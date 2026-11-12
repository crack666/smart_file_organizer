using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Query service for directory tree data and directory verdicts (UI tree view, CLI).
/// </summary>
public class DirectoryQueryService
{
    private readonly IDirectoryRepository _dirRepo;
    private readonly IDirectoryVerdictRepository _verdictRepo;

    public DirectoryQueryService(IDirectoryRepository dirRepo, IDirectoryVerdictRepository verdictRepo)
    {
        _dirRepo = dirRepo;
        _verdictRepo = verdictRepo;
    }

    public Task<IReadOnlyList<DirectoryNode>> GetRootsAsync(long jobId, CancellationToken ct = default)
        => _dirRepo.GetRootsAsync(jobId, ct);

    public Task<IReadOnlyList<DirectoryNode>> GetChildrenAsync(
        long jobId, string parentPath, CancellationToken ct = default)
        => _dirRepo.GetChildrenAsync(jobId, parentPath, ct);

    public Task<DirectoryNode?> GetDirectoryAsync(
        long jobId, string path, CancellationToken ct = default)
        => _dirRepo.GetByPathAsync(jobId, path, ct);

    /// <summary>Verdict (or stored error) of one directory, with its profile; null if Phase 1 has not reached it.</summary>
    public Task<DirectoryVerdict?> GetVerdictAsync(
        long jobId, string path, CancellationToken ct = default)
        => _verdictRepo.GetByDirectoryPathAsync(jobId, path, ct);

    /// <summary>All verdicts and stored errors of a job, ordered by directory path.</summary>
    public Task<IReadOnlyList<DirectoryVerdict>> GetVerdictsAsync(long jobId, CancellationToken ct = default)
        => _verdictRepo.GetForJobAsync(jobId, ct);
}
