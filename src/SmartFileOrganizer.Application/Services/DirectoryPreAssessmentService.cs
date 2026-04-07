using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Phase 1 of the 3-phase AI pipeline.
/// For each directory, asks Ollama to assess content homogeneity, spot anomalies,
/// and recommend a sampling strategy. Non-sampled files are marked Skipped so
/// Phase 2 (ClassificationService) skips them automatically.
/// </summary>
public class DirectoryPreAssessmentService
{
    private readonly IDirectoryRepository _dirRepo;
    private readonly IFileRepository _fileRepo;
    private readonly IDirectoryClassificationRepository _dirClassRepo;
    private readonly IOllamaService _ollama;
    private readonly Func<int> _getMaxParallelRequests;
    private readonly DirectoryAnalysisOptions _options;
    private readonly ILogger<DirectoryPreAssessmentService> _logger;

    public DirectoryPreAssessmentService(
        IDirectoryRepository dirRepo,
        IFileRepository fileRepo,
        IDirectoryClassificationRepository dirClassRepo,
        IOllamaService ollama,
        Func<int> getMaxParallelRequests,
        DirectoryAnalysisOptions options,
        ILogger<DirectoryPreAssessmentService> logger)
    {
        _dirRepo = dirRepo;
        _fileRepo = fileRepo;
        _dirClassRepo = dirClassRepo;
        _ollama = ollama;
        _getMaxParallelRequests = getMaxParallelRequests;
        _options = options;
        _logger = logger;
    }

    public async Task<AiClassificationRunResult> RunAsync(
        long jobId,
        IProgress<AiClassificationProgress>? progress = null,
        Func<CancellationToken, Task>? waitIfPausedAsync = null,
        CancellationToken ct = default)
    {
        var allDirs = await _dirRepo.GetAllForJobOrderedByDepthAsync(jobId, ct);
        var aiCandidateDirs = allDirs
            .Where(d => d.DirStatus != DirectoryStatus.Skip)
            .Where(d => !string.IsNullOrWhiteSpace(d.Name) && !d.Name.StartsWith(".", StringComparison.Ordinal))
            .ToList();

        if (aiCandidateDirs.Count == 0)
        {
            return new AiClassificationRunResult(true, 0, 0, 0, "No AI-relevant directories found.");
        }

        var assessedIds = (await _dirClassRepo.GetAssessedNodeIdsAsync(jobId, "pre_assessment", ct)).ToHashSet();
        var pending = aiCandidateDirs.Where(d => !assessedIds.Contains(d.Id)).ToList();

        if (pending.Count == 0)
        {
            progress?.Report(new AiClassificationProgress(jobId, aiCandidateDirs.Count, aiCandidateDirs.Count, 0, string.Empty,
                "Phase 1/3: All directories already assessed."));
            return new AiClassificationRunResult(true, aiCandidateDirs.Count, aiCandidateDirs.Count, 0, "All directories already assessed.");
        }

        // Build subdir lookup once (avoids N extra DB calls)
        var childrenByParent = aiCandidateDirs
            .GroupBy(d => d.ParentPath)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Name).ToList());

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
                    $"Phase 1/3: Assessing directories… {beforeProcessed}/{pending.Count} (parallel={workerCount})"));

                var fileInfos = await _fileRepo.GetFileInfoForDirectoryAsync(jobId, dir.FullPath, token);
                var subdirNames = childrenByParent.TryGetValue(dir.FullPath, out var ch) ? ch : [];

                var result = await BuildPreAssessmentAsync(dir, fileInfos, subdirNames, token);
                if (result.Error != null)
                {
                    _logger.LogWarning("Pre-assessment error for {Path}: {Err}", dir.FullPath, result.Error);
                    Interlocked.Increment(ref errorCount);
                }

                await _dirClassRepo.UpsertAsync(result, token);
                var nowProcessed = Interlocked.Increment(ref processed);
                var nowErrors = Volatile.Read(ref errorCount);
                progress?.Report(new AiClassificationProgress(
                    jobId, nowProcessed, pending.Count, nowErrors, dir.FullPath,
                    $"Phase 1/3: Assessing directories… {nowProcessed}/{pending.Count} (parallel={workerCount})"));
            });

        var completedMsg = errorCount > 0
            ? $"Phase 1/3 finished with {errorCount} error(s)."
            : "Phase 1/3: Directory pre-assessment complete.";

        progress?.Report(new AiClassificationProgress(
            jobId, processed, pending.Count, errorCount, string.Empty, completedMsg));

        return new AiClassificationRunResult(true, pending.Count, processed, errorCount, completedMsg);
    }

    public async Task<DirectoryClassificationResult> PreAssessSingleAsync(
        long jobId,
        DirectoryNode dir,
        CancellationToken ct = default)
    {
        var fileInfos = await _fileRepo.GetFileInfoForDirectoryAsync(jobId, dir.FullPath, ct);
        var allDirs = await _dirRepo.GetAllForJobOrderedByDepthAsync(jobId, ct);
        var subdirNames = allDirs
            .Where(d => string.Equals(d.ParentPath, dir.FullPath, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.DirStatus != DirectoryStatus.Skip)
            .Where(d => !string.IsNullOrWhiteSpace(d.Name) && !d.Name.StartsWith(".", StringComparison.Ordinal))
            .Select(d => d.Name)
            .ToList();

        var result = await BuildPreAssessmentAsync(dir, fileInfos, subdirNames, ct);
        await _dirClassRepo.UpsertAsync(result, ct);
        return result;
    }

    private async Task<DirectoryClassificationResult> BuildPreAssessmentAsync(
        DirectoryNode dir,
        IReadOnlyList<DirectoryFileInfo> fileInfos,
        IReadOnlyList<string> subdirNames,
        CancellationToken ct)
    {
        if (fileInfos.Count == 0)
        {
            return new DirectoryClassificationResult
            {
                DirectoryNodeId = dir.Id,
                Phase = "pre_assessment",
                Summary = "Directory contains no direct files.",
                Homogeneity = "high",
                DominantType = "empty",
                SamplingStrategy = "analyze_all",
                SampleSize = 0,
                AnalyzedAt = DateTime.UtcNow,
                Error = null
            };
        }

        var input = new DirectoryPreAssessmentInput
        {
            Directory = dir,
            DirectFiles = fileInfos,
            SubdirectoryNames = subdirNames
        };

        var result = await _ollama.PreAssessDirectoryAsync(input, ct);
        result.DirectoryNodeId = dir.Id;

        if (result.Error == null)
            await ApplySamplingAsync(dir, fileInfos, result, ct);

        return result;
    }

    private async Task ApplySamplingAsync(
        DirectoryNode dir,
        IReadOnlyList<DirectoryFileInfo> fileInfos,
        DirectoryClassificationResult assessment,
        CancellationToken ct)
    {
        if (fileInfos.Count == 0) return;

        var strategy = assessment.SamplingStrategy ?? "analyze_all";

        if (strategy == "skip")
        {
            // Mark all AI-eligible files in this dir as Skipped
            var allIds = fileInfos.Select(f => f.Id).ToList();
            await _fileRepo.MarkAsSkippedAsync(allIds, ct);
            _logger.LogDebug("Skipped all {Count} files in {Path} (strategy=skip)", allIds.Count, dir.FullPath);
            return;
        }

        if (strategy == "analyze_all") return; // Nothing to skip

        // random_sample: keep anomalies + random sample up to cap
        var anomalousNames = ParseAnomalousNames(assessment.AnomalousFileIds);
        var anomalousIds = fileInfos
            .Where(f => anomalousNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
            .Select(f => f.Id)
            .ToHashSet();

        var cap = Math.Min(
            assessment.SampleSize > 0 ? assessment.SampleSize : _options.MaxSamplesPerDirectory,
            _options.MaxSamplesPerDirectory);

        var nonAnomalous = fileInfos.Where(f => !anomalousIds.Contains(f.Id)).ToList();
        var sampleCount = Math.Max(0, cap - anomalousIds.Count);

        var sampledIds = nonAnomalous
            .OrderBy(_ => Guid.NewGuid())
            .Take(sampleCount)
            .Select(f => f.Id)
            .ToHashSet();

        var keepIds = anomalousIds.Union(sampledIds).ToHashSet();
        var skipIds = fileInfos.Select(f => f.Id).Where(id => !keepIds.Contains(id)).ToList();

        if (skipIds.Count > 0)
        {
            await _fileRepo.MarkAsSkippedAsync(skipIds, ct);
            _logger.LogDebug(
                "Sampled {Keep} / {Total} files in {Path} (strategy={Strategy}), skipped {Skip}",
                keepIds.Count, fileInfos.Count, dir.FullPath, strategy, skipIds.Count);
        }

        // Also update stored AnomalousFileIds to resolve names → IDs for later use
        if (anomalousNames.Count > 0)
        {
            var resolved = fileInfos
                .Where(f => anomalousNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
                .Select(f => new { id = f.Id, name = f.Name })
                .ToList();
            assessment.AnomalousFileIds = JsonSerializer.Serialize(resolved);
        }
    }

    private static HashSet<string> ParseAnomalousNames(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var nameProp))
                    names.Add(nameProp.GetString() ?? string.Empty);
            }
            return names;
        }
        catch
        {
            return [];
        }
    }
}
