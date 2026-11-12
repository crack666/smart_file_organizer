using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Handles targeted refresh operations inside an existing scan job.
/// Used for rescanning a single directory's direct files and for reanalyzing a single file.
/// </summary>
public class FocusedRescanService
{
    private readonly IScopedRefreshService _refreshService;
    private readonly IFileRepository _fileRepo;
    private readonly IDirectoryRepository _dirRepo;
    private readonly IUserOverrideRepository _overrideRepo;
    private readonly DirectoryPreAssessmentService _preAssessmentService;
    private readonly ClassificationService _classificationService;
    private readonly DirectorySummaryService _summaryService;
    private readonly DirectorySamplingService _sampling;
    private readonly IDirectoryVerdictRepository _verdictRepo;
    private readonly ILogger<FocusedRescanService> _logger;

    public FocusedRescanService(
        IScopedRefreshService refreshService,
        IFileRepository fileRepo,
        IDirectoryRepository dirRepo,
        IUserOverrideRepository overrideRepo,
        DirectoryPreAssessmentService preAssessmentService,
        ClassificationService classificationService,
        DirectorySummaryService summaryService,
        DirectorySamplingService sampling,
        IDirectoryVerdictRepository verdictRepo,
        ILogger<FocusedRescanService> logger)
    {
        _sampling = sampling;
        _verdictRepo = verdictRepo;
        _refreshService = refreshService;
        _fileRepo = fileRepo;
        _dirRepo = dirRepo;
        _overrideRepo = overrideRepo;
        _preAssessmentService = preAssessmentService;
        _classificationService = classificationService;
        _summaryService = summaryService;
        _logger = logger;
    }

    public async Task RescanDirectoryAsync(
        long jobId,
        string directoryPath,
        IProgress<FocusedRescanProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new FocusedRescanProgress("Rescan: refreshing directory metadata…", 0, 1, directoryPath));
        var dir = await _refreshService.RefreshDirectoryAsync(jobId, directoryPath, ct);
        progress?.Report(new FocusedRescanProgress("Rescan: pre-assessing directory…", 1, 2, directoryPath));

        // Judged again: the old sample no longer applies. A uniform directory gets a new one, so the rescan
        // spends the same calls as a run would; only Discovered files (sample members, mixed files) are judged.
        await _sampling.ForgetAsync(dir.Id, ct);
        await _preAssessmentService.PreAssessSingleAsync(jobId, dir, ct);
        await _sampling.PlanDirectoryAsync(jobId, dir.Id, ct);
        var verdict = await _verdictRepo.GetByNodeIdAsync(dir.Id, ct);
        var askKeepHint = verdict?.Verdict is DirectoryVerdictKind.Mixed or DirectoryVerdictKind.Uniform;

        var directFiles = await _fileRepo.GetByDirectoryAsync(jobId, directoryPath, ct);
        // As before the rescan judges every file not skipped, eligible or not, except those waiting for the sample.
        var filesToAnalyze = directFiles.Where(f => f.Status == FileNodeStatus.Discovered).ToList();
        var totalSteps = 3 + filesToAnalyze.Count;

        progress?.Report(new FocusedRescanProgress(
            $"Rescan: pre-assessment complete, analyzing {filesToAnalyze.Count} file(s)…",
            2,
            totalSteps,
            directoryPath));

        var processedSteps = 2;
        foreach (var file in filesToAnalyze)
        {
            await _overrideRepo.DeleteByFileNodeIdAsync(file.Id, ct);
            await _classificationService.ClassifySingleAsync(file, ct, askKeepHint);

            processedSteps++;
            progress?.Report(new FocusedRescanProgress(
                $"Rescan: analyzed {processedSteps - 2}/{filesToAnalyze.Count} file(s)…",
                processedSteps,
                totalSteps,
                file.FullPath));
        }

        // Inherit, or judge the released files one by one: raised to mixed, or confirmed without inheritance
        // because the model found a marker the rules had missed.
        if (await _sampling.ResolveDirectoryAsync(jobId, dir.Id, ct) is SampleState.Escalated or SampleState.Confirmed)
        {
            var released = (await _fileRepo.GetByDirectoryAsync(jobId, directoryPath, ct))
                .Where(f => f.Status == FileNodeStatus.Discovered && DirectorySamplingService.IsEligible(f.FileType))
                .ToList();
            foreach (var file in released)
            {
                await _overrideRepo.DeleteByFileNodeIdAsync(file.Id, ct);
                await _classificationService.ClassifySingleAsync(file, ct, askKeepHint: true);
            }
        }

        progress?.Report(new FocusedRescanProgress("Rescan: summarizing directory…", totalSteps - 1, totalSteps, directoryPath));
        await _summaryService.SummarizeSingleAsync(jobId, dir, ct);

        progress?.Report(new FocusedRescanProgress("Directory rescanned and reanalyzed.", totalSteps, totalSteps, directoryPath));
        _logger.LogInformation("Rescanned directory {Path} in job {JobId}", directoryPath, jobId);
    }

    public async Task ReanalyzeFileAsync(
        long jobId,
        long fileNodeId,
        IProgress<FocusedRescanProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new FocusedRescanProgress("Reanalysis: refreshing file metadata…", 0, 1, string.Empty));
        var file = await _refreshService.RefreshFileAsync(jobId, fileNodeId, ct);
        var totalSteps = 3;

        progress?.Report(new FocusedRescanProgress("Reanalysis: classifying file…", 1, totalSteps, file.FullPath));

        await _overrideRepo.DeleteByFileNodeIdAsync(file.Id, ct);
        await _classificationService.ClassifySingleAsync(file, ct);

        var dir = await _dirRepo.GetByPathAsync(jobId, file.ParentPath, ct);
        if (dir != null)
        {
            progress?.Report(new FocusedRescanProgress("Reanalysis: updating directory summary…", 2, totalSteps, dir.FullPath));
            await _summaryService.SummarizeSingleAsync(jobId, dir, ct);
        }
        else
        {
            totalSteps = 2;
        }

        progress?.Report(new FocusedRescanProgress("File reanalyzed.", totalSteps, totalSteps, file.FullPath));

        _logger.LogInformation("Reanalyzed file {Path} in job {JobId}", file.FullPath, jobId);
    }
}