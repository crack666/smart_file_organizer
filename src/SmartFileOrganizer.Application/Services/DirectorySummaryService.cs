using System.Text.Json;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<DirectorySummaryService> _logger;

    public DirectorySummaryService(
        IDirectoryRepository dirRepo,
        IFileRepository fileRepo,
        IDirectoryClassificationRepository dirClassRepo,
        IClassificationRepository classRepo,
        IOllamaService ollama,
        ILogger<DirectorySummaryService> logger)
    {
        _dirRepo = dirRepo;
        _fileRepo = fileRepo;
        _dirClassRepo = dirClassRepo;
        _classRepo = classRepo;
        _ollama = ollama;
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
        var dirsDeepFirst = allDirs.OrderByDescending(d => d.Depth).ThenBy(d => d.FullPath).ToList();

        if (dirsDeepFirst.Count == 0)
            return new AiClassificationRunResult(true, 0, 0, 0, "No directories found.");

        var summarizedIds = (await _dirClassRepo.GetAssessedNodeIdsAsync(jobId, "summary", ct)).ToHashSet();
        var pending = dirsDeepFirst.Where(d => !summarizedIds.Contains(d.Id)).ToList();

        if (pending.Count == 0)
        {
            progress?.Report(new AiClassificationProgress(jobId, dirsDeepFirst.Count, dirsDeepFirst.Count, 0,
                string.Empty, "Phase 3/3: All directories already summarized."));
            return new AiClassificationRunResult(true, dirsDeepFirst.Count, dirsDeepFirst.Count, 0,
                "All directories already summarized.");
        }

        var processed = 0;
        var errorCount = 0;

        foreach (var dir in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (waitIfPausedAsync != null) await waitIfPausedAsync(ct);

            progress?.Report(new AiClassificationProgress(
                jobId, processed, pending.Count, errorCount, dir.FullPath,
                $"Phase 3/3: Summarizing directories… {processed}/{pending.Count}"));

            // Skip dirs that have no pre-assessment (Phase 1 failed or was skipped for this dir)
            var preAssessment = await _dirClassRepo.GetByNodeAndPhaseAsync(dir.Id, "pre_assessment", ct);
            if (preAssessment == null)
            {
                processed++;
                continue;
            }

            // Build summary lines from Phase 2 results
            var fileNodes = await _fileRepo.GetByDirectoryAsync(jobId, dir.FullPath, ct);
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
                processed++;
                continue;
            }

            var input = new DirectorySummaryInput
            {
                Directory = dir,
                PreAssessment = preAssessment,
                FileSummaryLines = summaryLines,
                AnomalyDescriptions = anomalyDescriptions
            };

            var result = await _ollama.SummarizeDirectoryAsync(input, ct);
            result.DirectoryNodeId = dir.Id;
            result.SamplingStrategy = preAssessment.SamplingStrategy;
            result.SampleSize = preAssessment.SampleSize;

            if (result.Error != null)
            {
                _logger.LogWarning("Summary error for {Path}: {Err}", dir.FullPath, result.Error);
                errorCount++;
            }

            await _dirClassRepo.UpsertAsync(result, ct);
            processed++;
        }

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
