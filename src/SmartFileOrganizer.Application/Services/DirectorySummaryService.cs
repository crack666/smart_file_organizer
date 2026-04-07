using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Phase 3 of the 3-phase AI pipeline.
/// After Phase 2 has classified sampled files, this service builds a bottom-up
/// directory summary by asking Ollama to synthesize file results into a single
/// directory-level description.
/// </summary>
public class DirectorySummaryService
{
    private readonly IDirectoryRepository _dirRepo;
    private readonly IFileRepository _fileRepo;
    private readonly IDirectoryClassificationRepository _dirClassRepo;
    private readonly IClassificationRepository _classRepo;
    private readonly IOllamaService _ollama;
    private readonly Func<int> _getMaxParallelRequests;
    private readonly ILogger<DirectorySummaryService> _logger;

    public DirectorySummaryService(
        IDirectoryRepository dirRepo,
        IFileRepository fileRepo,
        IDirectoryClassificationRepository dirClassRepo,
        IClassificationRepository classRepo,
        IOllamaService ollama,
        Func<int> getMaxParallelRequests,
        ILogger<DirectorySummaryService> logger)
    {
        _dirRepo = dirRepo;
        _fileRepo = fileRepo;
        _dirClassRepo = dirClassRepo;
        _classRepo = classRepo;
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
        var workerCount = Math.Clamp(_getMaxParallelRequests(), 1, 8);

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

                // Build summary lines from Phase 2 results
                var fileNodes = await _fileRepo.GetByDirectoryAsync(jobId, dir.FullPath, token);
                var summaryLines = new List<string>();
                var anomalyDescriptions = BuildAnomalyDescriptions(preAssessment);

                foreach (var node in fileNodes)
                {
                    var category = node.Classification?.Category.ToString() ?? node.FileType.ToString();
                    var line = node.Classification?.Summary is { } s
                        ? $"{node.Name} — {category} — {(s.Length > 120 ? s[..120] + "\u2026" : s)}"
                        : $"{node.Name} — {category}";
                    summaryLines.Add(line);
                }

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

        progress?.Report(new AiClassificationProgress(
            jobId, processed, pending.Count, errorCount, string.Empty, completedMsg));

        return new AiClassificationRunResult(true, pending.Count, processed, errorCount, completedMsg);
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
