using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Query service for directory tree data (for the UI tree view).
/// </summary>
public class DirectoryQueryService
{
    private readonly IDirectoryRepository _dirRepo;

    public DirectoryQueryService(IDirectoryRepository dirRepo) => _dirRepo = dirRepo;

    public Task<IReadOnlyList<DirectoryNode>> GetRootsAsync(long jobId, CancellationToken ct = default)
        => _dirRepo.GetRootsAsync(jobId, ct);

    public Task<IReadOnlyList<DirectoryNode>> GetChildrenAsync(
        long jobId, string parentPath, CancellationToken ct = default)
        => _dirRepo.GetChildrenAsync(jobId, parentPath, ct);

    public Task<DirectoryNode?> GetDirectoryAsync(
        long jobId, string path, CancellationToken ct = default)
        => _dirRepo.GetByPathAsync(jobId, path, ct);
}
