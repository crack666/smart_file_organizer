using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IDirectoryClassificationRepository
{
    Task UpsertAsync(DirectoryClassificationResult result, CancellationToken ct = default);
    Task<DirectoryClassificationResult?> GetByNodeAndPhaseAsync(long directoryNodeId, string phase, CancellationToken ct = default);
    Task<IReadOnlyList<long>> GetAssessedNodeIdsAsync(long jobId, string phase, CancellationToken ct = default);
    /// <summary>Returns the summary-phase result for a directory identified by job + full path.</summary>
    Task<DirectoryClassificationResult?> GetByDirectoryPathAsync(long jobId, string fullPath, CancellationToken ct = default);
}
