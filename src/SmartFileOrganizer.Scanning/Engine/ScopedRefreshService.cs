using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Scanning.Engine;

public class ScopedRefreshService : IScopedRefreshService
{
    private readonly IScanJobRepository _jobRepo;
    private readonly IFileRepository _fileRepo;
    private readonly IDirectoryRepository _dirRepo;
    private readonly IFileSystemAccessor _fs;

    public ScopedRefreshService(
        IScanJobRepository jobRepo,
        IFileRepository fileRepo,
        IDirectoryRepository dirRepo,
        IFileSystemAccessor fs)
    {
        _jobRepo = jobRepo;
        _fileRepo = fileRepo;
        _dirRepo = dirRepo;
        _fs = fs;
    }

    public async Task<DirectoryNode> RefreshDirectoryAsync(long jobId, string directoryPath, CancellationToken ct = default)
    {
        var job = await _jobRepo.GetByIdAsync(jobId, ct)
            ?? throw new InvalidOperationException($"Job {jobId} not found.");

        var dir = await _dirRepo.GetByPathAsync(jobId, directoryPath, ct)
            ?? throw new InvalidOperationException($"Directory '{directoryPath}' is not part of job {jobId}.");

        if (!_fs.DirectoryExists(directoryPath))
            throw new DirectoryNotFoundException($"Directory '{directoryPath}' does not exist anymore.");

        var refreshedFiles = BuildDirectFileNodes(job, dir, directoryPath).ToList();
        await _fileRepo.ReplaceDirectoryFilesAsync(jobId, directoryPath, refreshedFiles, ct);

        dir.DirectFileCount = refreshedFiles.Count;
        dir.TotalSize = refreshedFiles.Sum(f => f.Size);
        dir.SubdirCount = _fs.EnumerateDirectories(directoryPath)
            .Count(p => !Path.GetFileName(p).StartsWith(".", StringComparison.Ordinal));
        dir.DirStatus = DirectoryStatus.Scanned;
        dir.DirReason = "Manual directory rescan";
        dir.ScannedAt = DateTime.UtcNow;
        await _dirRepo.UpdateAsync(dir, ct);

        return dir;
    }

    public async Task<FileNode> RefreshFileAsync(long jobId, long fileNodeId, CancellationToken ct = default)
    {
        var job = await _jobRepo.GetByIdAsync(jobId, ct)
            ?? throw new InvalidOperationException($"Job {jobId} not found.");

        var file = await _fileRepo.GetByIdAsync(fileNodeId, ct)
            ?? throw new InvalidOperationException($"File node {fileNodeId} not found.");

        if (file.JobId != jobId)
            throw new InvalidOperationException($"File node {fileNodeId} does not belong to job {jobId}.");

        if (!_fs.FileExists(file.FullPath))
            throw new FileNotFoundException("The selected file does not exist anymore.", file.FullPath);

        file.Name = _fs.GetFileName(file.FullPath);
        file.ParentPath = _fs.GetDirectoryName(file.FullPath) ?? file.ParentPath;
        file.RelativePath = _fs.GetRelativePath(job.RootPath, file.FullPath);
        file.RelativeDir = _fs.GetRelativePath(job.RootPath, file.ParentPath);
        file.Size = _fs.GetFileSize(file.FullPath);
        file.LastWriteTime = _fs.GetLastWriteTime(file.FullPath);
        file.Extension = _fs.GetExtension(file.FullPath);
        file.FileType = FileTypeDetector.Detect(file.Extension);
        file.Status = FileNodeStatus.Discovered;
        file.ScannedAt = DateTime.UtcNow;

        await _fileRepo.UpdateMetadataAsync(file, ct);
        return file;
    }

    private IEnumerable<FileNode> BuildDirectFileNodes(ScanJob job, DirectoryNode dir, string directoryPath)
    {
        foreach (var filePath in _fs.EnumerateFiles(directoryPath))
        {
            var ext = _fs.GetExtension(filePath);
            yield return new FileNode
            {
                JobId = job.Id,
                FullPath = filePath,
                Name = _fs.GetFileName(filePath),
                ParentPath = directoryPath,
                RootPath = job.RootPath,
                RelativePath = _fs.GetRelativePath(job.RootPath, filePath),
                RelativeDir = _fs.GetRelativePath(job.RootPath, directoryPath),
                Depth = dir.Depth + 1,
                Size = _fs.GetFileSize(filePath),
                LastWriteTime = _fs.GetLastWriteTime(filePath),
                Extension = ext,
                FileType = FileTypeDetector.Detect(ext),
                Status = FileNodeStatus.Discovered,
                ScannedAt = DateTime.UtcNow
            };
        }
    }
}