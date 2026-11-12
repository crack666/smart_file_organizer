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

    /// <summary>Creates a job scanned with the shipped rules (recorded at the job on its first run).</summary>
    public Task<ScanJob> CreateJobAsync(string rootPath, CancellationToken ct = default) =>
        CreateJobAsync(rootPath, rules: null, ScanRole.Source, ct);

    /// <summary>Creates a job scanned with <paramref name="rules"/>; null means the shipped rules.</summary>
    public Task<ScanJob> CreateJobAsync(string rootPath, ScanRulesProfile? rules, CancellationToken ct = default) =>
        CreateJobAsync(rootPath, rules, ScanRole.Source, ct);

    /// <summary>Creates a job with a fixed <paramref name="role"/> and the shipped rules.</summary>
    public Task<ScanJob> CreateJobAsync(string rootPath, ScanRole role, CancellationToken ct = default) =>
        CreateJobAsync(rootPath, rules: null, role, ct);

    /// <summary>
    /// Creates a job with <paramref name="rules"/> (null: the shipped rules) and a fixed <paramref name="role"/>;
    /// a <see cref="ScanRole.Library"/> job is only read, never analysed.
    /// </summary>
    public Task<ScanJob> CreateJobAsync(string rootPath, ScanRulesProfile? rules, ScanRole role, CancellationToken ct = default) =>
        CreateJobAsync(rootPath, rules, role, structureOnly: false, ct);

    /// <summary>
    /// Creates a job as above. With <paramref name="structureOnly"/> the scan records the tree without
    /// opening any file, not even for the sensitive-file header probe.
    /// </summary>
    public async Task<ScanJob> CreateJobAsync(string rootPath, ScanRulesProfile? rules, ScanRole role, bool structureOnly, CancellationToken ct = default)
    {
        var job = new ScanJob
        {
            RootPath = rootPath,
            Role = role,
            Status = JobStatus.Created,
            CreatedAt = DateTime.UtcNow,
            Rules = rules,
            StructureOnly = structureOnly
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

    /// <summary>
    /// Runs (or resumes) a scan in the caller's flow and returns the persisted job, for hosts without an
    /// event loop (CLI, tests). Independent of <see cref="StartAsync"/>: no <see cref="JobStateChanged"/> events.
    /// A finished job (Completed or Failed) is returned unchanged. A failure is persisted as
    /// <see cref="JobStatus.Failed"/> and returned. Cancelling <paramref name="ct"/> persists the job as
    /// <see cref="JobStatus.Paused"/>, so it can be resumed, and then throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task<ScanJob> RunAsync(long jobId, IProgress<ScanProgress>? progress = null, CancellationToken ct = default) =>
        RunAsync(jobId, rules: null, progress, ct);

    /// <summary>
    /// Like the overload without rules, but first switches the
    /// job to <paramref name="rules"/> if they differ from the stored ones (null keeps the stored rules).
    /// A switch reopens a finished job: the scan engine re-evaluates every folder stored as skipped or
    /// shallow and enters those the new rules let in. Data already collected is kept.
    /// </summary>
    public async Task<ScanJob> RunAsync(long jobId, ScanRulesProfile? rules, IProgress<ScanProgress>? progress, CancellationToken ct = default)
    {
        var job = await _jobRepo.GetByIdAsync(jobId, ct)
            ?? throw new InvalidOperationException($"Job {jobId} not found.");

        await NormalizeCompletedIfIncompleteAsync(job, ct);

        if (rules != null && rules.Hash != job.RulesHash)
        {
            _logger.LogInformation("Job {JobId}: scan rules changed from '{Old}' to '{New}' ({Source}); stored decisions are re-evaluated.",
                jobId, job.RulesName ?? "(none recorded)", rules.Name, rules.Source);
            job.Rules = rules;
            if (job.Status is JobStatus.Completed or JobStatus.Failed)
            {
                job.Status = JobStatus.Paused;
                job.CompletedAt = null;
            }
            await _jobRepo.UpdateAsync(job, ct);
        }

        if (job.Status is JobStatus.Completed or JobStatus.Failed)
            return job;

        try
        {
            await _scanEngine.RunAsync(job, progress ?? new Progress<ScanProgress>(), ct);
            return await _jobRepo.GetByIdAsync(jobId, CancellationToken.None) ?? job;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Status = JobStatus.Paused;
            await _jobRepo.UpdateAsync(job, CancellationToken.None);
            _logger.LogInformation("Scan job {JobId} paused.", jobId);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Scan job {JobId} failed.", jobId);
            job.Status = JobStatus.Failed;
            job.LastError = ex.Message;
            await _jobRepo.UpdateAsync(job, CancellationToken.None);
            return job;
        }
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
