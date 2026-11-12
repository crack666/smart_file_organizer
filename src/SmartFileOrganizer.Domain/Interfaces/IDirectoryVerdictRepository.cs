using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IDirectoryVerdictRepository
{
    /// <summary>Inserts or replaces the verdict of one directory.</summary>
    Task UpsertAsync(DirectoryVerdict verdict, CancellationToken ct = default);

    Task<DirectoryVerdict?> GetByNodeIdAsync(long directoryNodeId, CancellationToken ct = default);

    Task<DirectoryVerdict?> GetByDirectoryPathAsync(long jobId, string fullPath, CancellationToken ct = default);

    /// <summary>All verdicts and stored errors of a job, ordered by directory path.</summary>
    Task<IReadOnlyList<DirectoryVerdict>> GetForJobAsync(long jobId, CancellationToken ct = default);
}
