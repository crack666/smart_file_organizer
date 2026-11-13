using SmartFileOrganizer.Application.Sensitivity;
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

        // A link is not entered by a rescan either: its content belongs to its target.
        if (_fs.GetLink(directoryPath) is { } link)
            throw new InvalidOperationException($"Directory '{directoryPath}' is a link ({HeuristicsEngine.DescribeLink(link)}) and is not entered.");

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
        ReadSize(file);
        file.LastWriteTime = _fs.GetLastWriteTime(file.FullPath);
        file.Extension = _fs.GetExtension(file.FullPath);
        file.FileType = FileTypeDetector.Detect(file.Extension);
        file.Status = FileNodeStatus.Discovered;
        file.ScannedAt = DateTime.UtcNow;
        SensitiveFileRules.Apply(file, job.StructureOnly ? null : maxBytes => _fs.ReadPrefix(file.FullPath, maxBytes));

        await _fileRepo.UpdateMetadataAsync(file, ct);
        return file;
    }

    /// <summary>Size of the file, or the cause why it could not be read (never a silent 0).</summary>
    private void ReadSize(FileNode file)
    {
        try
        {
            file.Size = _fs.GetFileSize(file.FullPath);
            file.ScanErrorCause = null;
            file.ScanError = null;
        }
        catch (Exception ex) when (ScanErrorCauses.IsReadError(ex))
        {
            file.Size = 0;
            file.ScanErrorCause = ScanErrorCauses.Of(ex).ToCode();
            file.ScanError = ex.Message;
        }
    }

    private IEnumerable<FileNode> BuildDirectFileNodes(ScanJob job, DirectoryNode dir, string directoryPath)
    {
        // ListFiles throws for an unreadable folder: a failed rescan must not store it as empty.
        foreach (var filePath in _fs.ListFiles(directoryPath))
        {
            if (_fs.GetLink(filePath) != null)
                continue;   // a link is no content; the scan records it in file_links
            var ext = _fs.GetExtension(filePath);
            var node = new FileNode
            {
                JobId = job.Id,
                FullPath = filePath,
                Name = _fs.GetFileName(filePath),
                ParentPath = directoryPath,
                RootPath = job.RootPath,
                RelativePath = _fs.GetRelativePath(job.RootPath, filePath),
                RelativeDir = _fs.GetRelativePath(job.RootPath, directoryPath),
                Depth = dir.Depth + 1,
                LastWriteTime = _fs.GetLastWriteTime(filePath),
                Extension = ext,
                FileType = FileTypeDetector.Detect(ext),
                Status = FileNodeStatus.Discovered,
                ScannedAt = DateTime.UtcNow
            };
            ReadSize(node);
            SensitiveFileRules.Apply(node, job.StructureOnly ? null : maxBytes => _fs.ReadPrefix(filePath, maxBytes));
            yield return node;
        }
    }
}