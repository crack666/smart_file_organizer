using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Query service for retrieving file nodes (for the UI file table).
/// </summary>
public class FileQueryService
{
    private readonly IFileRepository _fileRepo;

    public FileQueryService(IFileRepository fileRepo) => _fileRepo = fileRepo;

    public Task<IReadOnlyList<FileNode>> GetFilesInDirectoryAsync(
        long jobId, string directoryPath, CancellationToken ct = default)
        => _fileRepo.GetByDirectoryAsync(jobId, directoryPath, ct);

    public Task<FileNode?> GetFileByIdAsync(long id, CancellationToken ct = default)
        => _fileRepo.GetByIdAsync(id, ct);

    public Task<long> CountFilesAsync(long jobId, CancellationToken ct = default)
        => _fileRepo.CountByJobAsync(jobId, ct);
}
