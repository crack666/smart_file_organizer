using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

public interface IScopedRefreshService
{
    Task<DirectoryNode> RefreshDirectoryAsync(long jobId, string directoryPath, CancellationToken ct = default);
    Task<FileNode> RefreshFileAsync(long jobId, long fileNodeId, CancellationToken ct = default);
}