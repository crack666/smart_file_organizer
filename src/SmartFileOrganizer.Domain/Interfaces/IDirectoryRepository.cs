using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IDirectoryRepository
{
    Task InsertBatchAsync(IEnumerable<DirectoryNode> nodes, CancellationToken ct = default);
    Task<DirectoryNode?> GetByPathAsync(long jobId, string fullPath, CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryNode>> GetChildrenAsync(long jobId, string parentPath, CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryNode>> GetRootsAsync(long jobId, CancellationToken ct = default);
    Task UpdateAsync(DirectoryNode node, CancellationToken ct = default);
}
