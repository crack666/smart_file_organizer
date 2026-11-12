using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Phase 3 of the 3-phase AI pipeline: one summary per directory, bottom-up.
/// </summary>
/// <remarks>
/// The model is asked only where it adds something: for mixed directories (including uniform ones the
/// sample raised to mixed) and for directories without a valid verdict. A unit, system, empty or confirmed
/// uniform directory already carries purpose and reason from its verdict, and its files either were not
/// analysed or inherited one result; its summary is written from the verdict and the sample outcome without
/// a model call (<see cref="BuildVerdictSummary"/>), with <c>model_used</c> left empty.
/// </remarks>
public class DirectorySummaryService
{
    private readonly IDirectoryRepository _dirRepo;
    private readonly IFileRepository _fileRepo;
    private readonly IDirectoryClassificationRepository _dirClassRepo;
    private readonly IClassificationRepository _classRepo;
    private readonly IDirectoryVerdictRepository _verdictRepo;
    private readonly IDirectorySampleRepository _sampleRepo;
    private readonly IOllamaService _ollama;
    private readonly Func<int> _getMaxParallelRequests;
    private readonly ILogger<DirectorySummaryService> _logger;

    public DirectorySummaryService(
        IDirectoryRepository dirRepo,
        IFileRepository fileRepo,
        IDirectoryClassificationRepository dirClassRepo,
        IClassificationRepository classRepo,
        IDirectoryVerdictRepository verdictRepo,
        IDirectorySampleRepository sampleRepo,
        IOllamaService ollama,
        Func<int> getMaxParallelRequests,
        ILogger<DirectorySummaryService> logger)
    {
        _dirRepo = dirRepo;
        _fileRepo = fileRepo;
        _dirClassRepo = dirClassRepo;
        _classRepo = classRepo;
        _verdictRepo = verdictRepo;
        _sampleRepo = sampleRepo;
        _ollama = ollama;
        _getMaxParallelRequests = getMaxParallelRequests;
        _logger = logger;
    }

    public async Task<AiClassificationRunResult> RunAsync(
        long jobId,
        IProgress<AiClassificationProgress>? progress = null,
        Func<CancellationToken, Task>? waitIfPausedAsync = null,
        CancellationToken ct = default)
    {
        // Directories ordered deepest-first for bottom-up summarization
        var allDirs = await _dirRepo.GetAllForJobOrderedByDepthAsync(jobId, ct);
        var aiCandidateDirs = allDirs
            .Where(d => d.DirStatus != DirectoryStatus.Skip)
            .Where(d => !string.IsNullOrWhiteSpace(d.Name) && !d.Name.StartsWith(".", StringComparison.Ordinal))
            .ToList();
        var dirsDeepFirst = aiCandidateDirs.OrderByDescending(d => d.Depth).ThenBy(d => d.FullPath).ToList();

        if (dirsDeepFirst.Count == 0)
            return new AiClassificationRunResult(true, 0, 0, 0, "No AI-relevant directories found.");

        var summarizedIds = (await _dirClassRepo.GetAssessedNodeIdsAsync(jobId, "summary", ct)).ToHashSet();
        var pending = dirsDeepFirst.Where(d => !summarizedIds.Contains(d.Id)).ToList();

        if (pending.Count == 0)
        {
            progress?.Report(new AiClassificationProgress(jobId, dirsDeepFirst.Count, dirsDeepFirst.Count, 0,
                string.Empty, "Phase 3/3: All directories already summarized."));
            return new AiClassificationRunResult(true, dirsDeepFirst.Count, dirsDeepFirst.Count, 0,
                "All directories already summarized.");
        }

        long processed = 0;
        var errorCount = 0;
        var modelCalls = 0;
        var workerCount = Math.Clamp(_getMaxParallelRequests(), 1, 8);
        var verdicts = (await _verdictRepo.GetForJobAsync(jobId, ct)).ToDictionary(v => v.DirectoryNodeId);
        var samples = (await _sampleRepo.GetForJobAsync(jobId, ct)).ToDictionary(s => s.DirectoryNodeId);

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = workerCount, CancellationToken = ct },
            async (dir, token) =>
            {
                if (waitIfPausedAsync != null) await waitIfPausedAsync(token);

                var beforeProcessed = Interlocked.Read(ref processed);
                var beforeErrors = Volatile.Read(ref errorCount);
                progress?.Report(new AiClassificationProgress(
                    jobId, beforeProcessed, pending.Count, beforeErrors, dir.FullPath,
                    $"Phase 3/3: Summarizing directories… {beforeProcessed}/{pending.Count} (parallel={workerCount})"));

                // Skip dirs that have no pre-assessment (Phase 1 failed or was skipped for this dir)
                var preAssessment = await _dirClassRepo.GetByNodeAndPhaseAsync(dir.Id, "pre_assessment", token);
                if (preAssessment == null)
                {
                    var progressedNoPre = Interlocked.Increment(ref processed);
                    progress?.Report(new AiClassificationProgress(
                        jobId, progressedNoPre, pending.Count, Volatile.Read(ref errorCount), dir.FullPath,
                        $"Phase 3/3: Summarizing directories… {progressedNoPre}/{pending.Count} (parallel={workerCount})"));
                    return;
                }

                // Verdict and sample already say what the directory is: no model call.
                if (BuildVerdictSummary(dir, verdicts.GetValueOrDefault(dir.Id), samples.GetValueOrDefault(dir.Id), preAssessment)
                    is { } fromVerdict)
                {
                    await _dirClassRepo.UpsertAsync(fromVerdict, token);
                    var progressedVerdict = Interlocked.Increment(ref processed);
                    progress?.Report(new AiClassificationProgress(
                        jobId, progressedVerdict, pending.Count, Volatile.Read(ref errorCount), dir.FullPath,
                        $"Phase 3/3: Summarizing directories… {progressedVerdict}/{pending.Count} (parallel={workerCount})"));
                    return;
                }

                // Build summary lines from Phase 2 results
                var fileNodes = await _fileRepo.GetByDirectoryAsync(jobId, dir.FullPath, token);
                var summaryLines = new List<string>();
                var anomalyDescriptions = BuildAnomalyDescriptions(preAssessment);

                foreach (var node in fileNodes)
                    summaryLines.Add(BuildFileSummaryLine(node));

                // Only call Ollama if we have something meaningful to say
                if (summaryLines.Count == 0 && anomalyDescriptions.Count == 0)
                {
                    var progressedNoContent = Interlocked.Increment(ref processed);
                    progress?.Report(new AiClassificationProgress(
                        jobId, progressedNoContent, pending.Count, Volatile.Read(ref errorCount), dir.FullPath,
                        $"Phase 3/3: Summarizing directories… {progressedNoContent}/{pending.Count} (parallel={workerCount})"));
                    return;
                }

                var input = new DirectorySummaryInput
                {
                    Directory = dir,
                    PreAssessment = preAssessment,
                    FileSummaryLines = summaryLines,
                    AnomalyDescriptions = anomalyDescriptions
                };

                Interlocked.Increment(ref modelCalls);
                var result = await _ollama.SummarizeDirectoryAsync(input, token);
                result.DirectoryNodeId = dir.Id;
                result.SamplingStrategy = preAssessment.SamplingStrategy;
                result.SampleSize = preAssessment.SampleSize;

                if (result.Error != null)
                {
                    _logger.LogWarning("Summary error for {Path}: {Err}", dir.FullPath, result.Error);
                    Interlocked.Increment(ref errorCount);
                }

                await _dirClassRepo.UpsertAsync(result, token);
                var nowProcessed = Interlocked.Increment(ref processed);
                var nowErrors = Volatile.Read(ref errorCount);
                progress?.Report(new AiClassificationProgress(
                    jobId, nowProcessed, pending.Count, nowErrors, dir.FullPath,
                    $"Phase 3/3: Summarizing directories… {nowProcessed}/{pending.Count} (parallel={workerCount})"));
            });

        var completedMsg = errorCount > 0
            ? $"Phase 3/3 finished with {errorCount} error(s)."
            : "Phase 3/3: Directory summaries complete.";
        _logger.LogInformation("Phase 3 for job {JobId}: {Done} directories summarized, {Calls} by the model.",
            jobId, processed, modelCalls);

        progress?.Report(new AiClassificationProgress(
            jobId, processed, pending.Count, errorCount, string.Empty, completedMsg));

        return new AiClassificationRunResult(true, pending.Count, processed, errorCount, completedMsg);
    }

    /// <summary>
    /// The summary of a directory whose verdict already describes it, or null when the model should
    /// summarize: mixed directories (also raised ones), no or failed verdict, or a verdict without purpose
    /// and reason. A confirmed sample adds its outcome. A uniform directory that the sample later raises to
    /// mixed loses this summary (<see cref="IDirectorySampleRepository.EscalateAsync"/>), so the model
    /// summarizes it on the next run.
    /// </summary>
    public static DirectoryClassificationResult? BuildVerdictSummary(
        DirectoryNode dir, DirectoryVerdict? verdict, DirectorySample? sample, DirectoryClassificationResult preAssessment)
    {
        if (verdict?.Verdict is not { } kind || verdict.Error is not null) return null;
        if (kind == DirectoryVerdictKind.Gemischt) return null;

        var text = verdict.Purpose ?? verdict.Reason;
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (kind == DirectoryVerdictKind.Einheitlich && sample is { State: SampleState.Confirmed })
        {
            text += $" Sample of {sample.Members.Count} confirmed {sample.MajorityCategory}; " +
                    $"{sample.InheritedCount} file(s) inherited that result.";
        }

        return new DirectoryClassificationResult
        {
            DirectoryNodeId = dir.Id,
            Phase = "summary",
            Summary = text,
            Theme = kind.ToCode(),
            SamplingStrategy = preAssessment.SamplingStrategy,
            SampleSize = preAssessment.SampleSize,
            ModelUsed = null,
            AnalyzedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// One input line for the directory summary. The directory summary is stored and may later
    /// be shown outside the machine, so a marked file contributes its neutral summary, never the
    /// model summary of its content.
    /// </summary>
    public static string BuildFileSummaryLine(FileNode node)
    {
        var markers = SensitiveOutputGuard.GetMarkers(node);
        if (markers != SensitiveMarker.None)
            return $"{node.Name} — {SensitiveOutputGuard.NeutralSummary(markers, node.FileType)}";

        var category = node.Classification?.Category.ToString() ?? node.FileType.ToString();
        return node.Classification?.Summary is { } s
            ? $"{node.Name} — {category} — {(s.Length > 120 ? s[..120] + "\u2026" : s)}"
            : $"{node.Name} — {category}";
    }

    public async Task<DirectoryClassificationResult> SummarizeSingleAsync(
        long jobId,
        DirectoryNode dir,
        CancellationToken ct = default)
    {
        var preAssessment = await _dirClassRepo.GetByNodeAndPhaseAsync(dir.Id, "pre_assessment", ct)
            ?? new DirectoryClassificationResult
            {
                DirectoryNodeId = dir.Id,
                Phase = "pre_assessment",
                Summary = "No pre-assessment available.",
                SamplingStrategy = "analyze_all",
                SampleSize = 0,
                AnalyzedAt = DateTime.UtcNow
            };

        var fileNodes = await _fileRepo.GetByDirectoryAsync(jobId, dir.FullPath, ct);
        var summaryLines = new List<string>();
        var anomalyDescriptions = BuildAnomalyDescriptions(preAssessment);

        foreach (var node in fileNodes)
            summaryLines.Add(BuildFileSummaryLine(node));

        DirectoryClassificationResult result;
        if (summaryLines.Count == 0 && anomalyDescriptions.Count == 0)
        {
            result = new DirectoryClassificationResult
            {
                DirectoryNodeId = dir.Id,
                Phase = "summary",
                Summary = "Directory is currently empty.",
                Theme = "empty",
                SamplingStrategy = preAssessment.SamplingStrategy,
                SampleSize = preAssessment.SampleSize,
                AnalyzedAt = DateTime.UtcNow,
                Error = null
            };
        }
        else
        {
            result = await _ollama.SummarizeDirectoryAsync(new DirectorySummaryInput
            {
                Directory = dir,
                PreAssessment = preAssessment,
                FileSummaryLines = summaryLines,
                AnomalyDescriptions = anomalyDescriptions
            }, ct);

            result.DirectoryNodeId = dir.Id;
            result.Phase = "summary";
            result.SamplingStrategy = preAssessment.SamplingStrategy;
            result.SampleSize = preAssessment.SampleSize;
        }

        await _dirClassRepo.UpsertAsync(result, ct);
        return result;
    }

    private static List<string> BuildAnomalyDescriptions(DirectoryClassificationResult preAssessment)
    {
        if (string.IsNullOrWhiteSpace(preAssessment.AnomalousFileIds)) return [];
        try
        {
            using var doc = JsonDocument.Parse(preAssessment.AnomalousFileIds);
            var lines = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                var reason = item.TryGetProperty("reason", out var r) ? r.GetString() : null;
                if (name != null) lines.Add(reason != null ? $"{name}: {reason}" : name);
            }
            return lines;
        }
        catch
        {
            return [];
        }
    }
}
