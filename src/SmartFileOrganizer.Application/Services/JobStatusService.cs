using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>A job together with its counters per phase.</summary>
/// <param name="Wait">The analysis of the job waits for the model server, or null.</param>
/// <param name="WaitIsStale">The waiting run has not probed for much longer than its interval: it was probably
/// killed, and nothing is waiting any more.</param>
public sealed record JobStatusReport(ScanJob Job, JobStatistics Statistics, ModelServerWait? Wait = null, bool WaitIsStale = false);

/// <summary>Reads where a job stands in scan and AI phases. Read-only apart from the job-status normalisation
/// that <see cref="ScanJobService.GetJobAsync"/> applies.</summary>
public class JobStatusService
{
    private readonly ScanJobService _jobs;
    private readonly IJobStatisticsRepository _statistics;
    private readonly IModelServerWaitRepository? _waits;
    private readonly ModelServerOptions _serverOptions;
    private readonly TimeProvider _time;

    public JobStatusService(
        ScanJobService jobs,
        IJobStatisticsRepository statistics,
        IModelServerWaitRepository? waits = null,
        ModelServerOptions? serverOptions = null,
        TimeProvider? time = null)
    {
        _jobs = jobs;
        _statistics = statistics;
        _waits = waits;
        _serverOptions = serverOptions ?? new ModelServerOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Returns null when the job does not exist.</summary>
    public async Task<JobStatusReport?> GetAsync(long jobId, int maxErrors = 20, CancellationToken ct = default)
    {
        var job = await _jobs.GetJobAsync(jobId, ct);
        if (job == null)
            return null;

        var stats = await _statistics.GetAsync(jobId, maxErrors, ct);
        var wait = _waits is null ? null : await _waits.GetAsync(jobId, ct);
        return new JobStatusReport(job, stats, wait, wait is not null && IsStale(wait));
    }

    /// <summary>A probe is due every interval; three missed ones, and at least a minute, mean the run is gone.</summary>
    private bool IsStale(ModelServerWait wait)
    {
        var allowed = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(1).Ticks, 3 * _serverOptions.ProbeInterval.Ticks));
        return _time.GetUtcNow().UtcDateTime - wait.CheckedAt > allowed;
    }
}
