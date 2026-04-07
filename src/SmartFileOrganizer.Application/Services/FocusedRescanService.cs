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
    private readonly ILogger<FocusedRescanService> _logger;

    public FocusedRescanService(
        IScopedRefreshService refreshService,
        IFileRepository fileRepo,
        IDirectoryRepository dirRepo,
        IUserOverrideRepository overrideRepo,
        DirectoryPreAssessmentService preAssessmentService,
        ClassificationService classificationService,
        DirectorySummaryService summaryService,
        ILogger<FocusedRescanService> logger)
    {
        _refreshService = refreshService;
        _fileRepo = fileRepo;
        _dirRepo = dirRepo;
        _overrideRepo = overrideRepo;
        _preAssessmentService = preAssessmentService;
        _classificationService = classificationService;
        _summaryService = summaryService;
        _logger = logger;
    }

    public async Task RescanDirectoryAsync(long jobId, string directoryPath, CancellationToken ct = default)
    {
        var dir = await _refreshService.RefreshDirectoryAsync(jobId, directoryPath, ct);
        await _preAssessmentService.PreAssessSingleAsync(jobId, dir, ct);

        var directFiles = await _fileRepo.GetByDirectoryAsync(jobId, directoryPath, ct);
        foreach (var file in directFiles.Where(f => f.Status != FileNodeStatus.Skipped))
        {
            await _overrideRepo.DeleteByFileNodeIdAsync(file.Id, ct);
            await _classificationService.ClassifySingleAsync(file, ct);
        }

        await _summaryService.SummarizeSingleAsync(jobId, dir, ct);
        _logger.LogInformation("Rescanned directory {Path} in job {JobId}", directoryPath, jobId);
    }

    public async Task ReanalyzeFileAsync(long jobId, long fileNodeId, CancellationToken ct = default)
    {
        var file = await _refreshService.RefreshFileAsync(jobId, fileNodeId, ct);

        await _overrideRepo.DeleteByFileNodeIdAsync(file.Id, ct);
        await _classificationService.ClassifySingleAsync(file, ct);

        var dir = await _dirRepo.GetByPathAsync(jobId, file.ParentPath, ct);
        if (dir != null)
            await _summaryService.SummarizeSingleAsync(jobId, dir, ct);

        _logger.LogInformation("Reanalyzed file {Path} in job {JobId}", file.FullPath, jobId);
    }
}