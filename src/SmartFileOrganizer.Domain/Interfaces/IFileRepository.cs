using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IFileRepository
{
    Task InsertBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default);
    Task<FileNode?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<FileNode>> GetByDirectoryAsync(long jobId, string parentPath, CancellationToken ct = default);
    Task<IReadOnlyList<FileNode>> GetPendingAiAnalysisAsync(long jobId, int batchSize, CancellationToken ct = default);
    Task<long> CountAiEligibleAsync(long jobId, CancellationToken ct = default);
    Task<long> CountPendingAiAnalysisAsync(long jobId, CancellationToken ct = default);
    Task UpdateStatusAsync(long id, Enums.FileNodeStatus status, CancellationToken ct = default);
    Task<long> CountByJobAsync(long jobId, CancellationToken ct = default);
    /// <summary>Returns lightweight name+size info for ALL files in a directory (used for Phase 1 prompt building).</summary>
    Task<IReadOnlyList<DirectoryFileInfo>> GetFileInfoForDirectoryAsync(long jobId, string parentPath, CancellationToken ct = default);
    /// <summary>Marks the given file IDs as Skipped if they are still in Discovered state.</summary>
    Task MarkAsSkippedAsync(IReadOnlyList<long> ids, CancellationToken ct = default);
    Task MarkAsProcessingAsync(IReadOnlyList<long> ids, CancellationToken ct = default);
    /// <summary>Resets any files stuck in Processing (e.g. from a previous cancelled run) back to Discovered.</summary>
    Task ResetStaleProcessingAsync(long jobId, CancellationToken ct = default);
}
