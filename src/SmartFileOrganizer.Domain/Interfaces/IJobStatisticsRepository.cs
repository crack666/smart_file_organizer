using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IJobStatisticsRepository
{
    /// <summary>Counts directories and files per phase and returns up to <paramref name="maxErrors"/> recent errors.</summary>
    Task<JobStatistics> GetAsync(long jobId, int maxErrors, CancellationToken ct = default);
}
