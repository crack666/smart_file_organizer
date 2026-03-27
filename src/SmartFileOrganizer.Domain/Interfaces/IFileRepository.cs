using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IFileRepository
{
    Task InsertBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default);
    Task<FileNode?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<FileNode>> GetByDirectoryAsync(long jobId, string parentPath, CancellationToken ct = default);
    Task<IReadOnlyList<FileNode>> GetPendingAiAnalysisAsync(long jobId, int batchSize, CancellationToken ct = default);
    Task UpdateStatusAsync(long id, Enums.FileNodeStatus status, CancellationToken ct = default);
    Task<long> CountByJobAsync(long jobId, CancellationToken ct = default);
}
