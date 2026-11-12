using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>A job together with its counters per phase.</summary>
public sealed record JobStatusReport(ScanJob Job, JobStatistics Statistics);

/// <summary>Reads where a job stands in scan and AI phases. Read-only apart from the job-status normalisation
/// that <see cref="ScanJobService.GetJobAsync"/> applies.</summary>
public class JobStatusService
{
    private readonly ScanJobService _jobs;
    private readonly IJobStatisticsRepository _statistics;

    public JobStatusService(ScanJobService jobs, IJobStatisticsRepository statistics)
    {
        _jobs = jobs;
        _statistics = statistics;
    }

    /// <summary>Returns null when the job does not exist.</summary>
    public async Task<JobStatusReport?> GetAsync(long jobId, int maxErrors = 20, CancellationToken ct = default)
    {
        var job = await _jobs.GetJobAsync(jobId, ct);
        if (job == null)
            return null;

        var stats = await _statistics.GetAsync(jobId, maxErrors, ct);
        return new JobStatusReport(job, stats);
    }
}
