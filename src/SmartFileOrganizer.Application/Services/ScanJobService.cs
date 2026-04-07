using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Orchestrates the lifecycle of scan jobs: Create, Start, Pause, Resume, Cancel.
/// The actual I/O scanning runs on a background task, and a CancellationTokenSource
/// is kept alive for the duration of the scan so it can be paused/cancelled.
/// </summary>
public class ScanJobService
{
    private readonly IScanJobRepository _jobRepo;
    private readonly IScanEngine _scanEngine;
    private readonly ILogger<ScanJobService> _logger;

    // Active scan state — one scan job at a time (MVP constraint).
    private CancellationTokenSource? _cts;
    private Task? _scanTask;
    private long _activeJobId;
    private bool _pauseRequested;

    public event EventHandler<ScanProgress>? ProgressChanged;
    public event EventHandler<ScanJob>? JobStateChanged;

    public ScanJobService(
        IScanJobRepository jobRepo,
        IScanEngine scanEngine,
        ILogger<ScanJobService> logger)
    {
        _jobRepo = jobRepo;
        _scanEngine = scanEngine;
        _logger = logger;
    }

    public async Task<ScanJob> CreateJobAsync(string rootPath, CancellationToken ct = default)
    {
        var job = new ScanJob
        {
            RootPath = rootPath,
            Status = JobStatus.Created,
            CreatedAt = DateTime.UtcNow
        };
        return await _jobRepo.CreateAsync(job, ct);
    }

    public async Task<IReadOnlyList<ScanJob>> GetAllJobsAsync(CancellationToken ct = default)
    {
        var jobs = await _jobRepo.GetAllAsync(ct);
        foreach (var job in jobs)
            await NormalizeCompletedIfIncompleteAsync(job, ct);
        return jobs;
    }

    public async Task DeleteJobAsync(long id, CancellationToken ct = default)
        => await _jobRepo.DeleteAsync(id, ct);

    public async Task<ScanJob?> GetJobAsync(long id, CancellationToken ct = default)
    {
        var job = await _jobRepo.GetByIdAsync(id, ct);
        if (job != null)
            await NormalizeCompletedIfIncompleteAsync(job, ct);
        return job;
    }

    /// <summary>Start or resume a scan job in the background.</summary>
    public async Task StartAsync(long jobId, CancellationToken appCt = default)
    {
        if (_scanTask != null && !_scanTask.IsCompleted)
        {
            _logger.LogWarning("A scan is already running (job {Active}). Stop it first.", _activeJobId);
            return;
        }

        var job = await _jobRepo.GetByIdAsync(jobId, appCt)
            ?? throw new InvalidOperationException($"Job {jobId} not found.");

        await NormalizeCompletedIfIncompleteAsync(job, appCt);

        if (job.Status is JobStatus.Completed or JobStatus.Failed)
        {
            _logger.LogWarning("Job {JobId} is already finished ({Status}).", jobId, job.Status);
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        _activeJobId = jobId;
        _pauseRequested = false;

        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressChanged?.Invoke(this, p);
        });

        _scanTask = Task.Run(async () =>
        {
            try
            {
                await _scanEngine.RunAsync(job, progress, _cts.Token);
                var updated = await _jobRepo.GetByIdAsync(jobId) ?? job;
                JobStateChanged?.Invoke(this, updated);
            }
            catch (OperationCanceledException)
            {
                job.Status = _pauseRequested ? JobStatus.Paused : JobStatus.Cancelled;
                _logger.LogInformation("Scan job {JobId} {State}.", jobId, _pauseRequested ? "paused" : "cancelled");
                await _jobRepo.UpdateAsync(job);
                JobStateChanged?.Invoke(this, job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan job {JobId} failed.", jobId);
                job.Status = JobStatus.Failed;
                job.LastError = ex.Message;
                await _jobRepo.UpdateAsync(job);
                JobStateChanged?.Invoke(this, job);
            }
        }, _cts.Token);
    }

    public void Pause()
    {
        _pauseRequested = true;
        _cts?.Cancel();
        _logger.LogInformation("Pause requested for job {JobId}.", _activeJobId);
    }

    public void Cancel()
    {
        _pauseRequested = false;
        _cts?.Cancel();
        _logger.LogInformation("Cancel requested for job {JobId}.", _activeJobId);
    }

    private async Task NormalizeCompletedIfIncompleteAsync(ScanJob job, CancellationToken ct)
    {
        if (job.Status != JobStatus.Completed)
            return;

        // Defensive migration: some interrupted runs were previously persisted as Completed.
        // If counters indicate unfinished work, reclassify as Paused so users can resume.
        var incompleteFiles = job.TotalFiles > 0 && job.ProcessedFiles < job.TotalFiles;
        var incompleteDirs = job.TotalDirectories > 0 && job.ProcessedDirectories < job.TotalDirectories;

        if (!incompleteFiles && !incompleteDirs)
            return;

        job.Status = JobStatus.Paused;
        job.CompletedAt = null;
        await _jobRepo.UpdateAsync(job, ct);
        _logger.LogInformation(
            "Normalized job {JobId} from Completed to Paused (files {ProcessedFiles}/{TotalFiles}, dirs {ProcessedDirs}/{TotalDirs}).",
            job.Id,
            job.ProcessedFiles,
            job.TotalFiles,
            job.ProcessedDirectories,
            job.TotalDirectories);
    }
}
