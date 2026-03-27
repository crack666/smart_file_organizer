using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Scanning.Engine;

/// <summary>
/// Iterative scan engine. Processes directories using a work queue (not recursion).
/// Writes results to SQLite in configurable batches.
/// Supports cancellation and resume: directories that are already in the DB are skipped.
/// </summary>
public class ScanEngine : IScanEngine
{
    private readonly IFileSystemAccessor _fs;
    private readonly IHeuristicsEngine _heuristics;
    private readonly IFileRepository _fileRepo;
    private readonly IDirectoryRepository _dirRepo;
    private readonly IScanJobRepository _jobRepo;
    private readonly ILogger<ScanEngine> _logger;

    private const int BatchSize = 200;
    private const int JobUpdateIntervalMs = 2000;

    public ScanEngine(
        IFileSystemAccessor fs,
        IHeuristicsEngine heuristics,
        IFileRepository fileRepo,
        IDirectoryRepository dirRepo,
        IScanJobRepository jobRepo,
        ILogger<ScanEngine> logger)
    {
        _fs = fs;
        _heuristics = heuristics;
        _fileRepo = fileRepo;
        _dirRepo = dirRepo;
        _jobRepo = jobRepo;
        _logger = logger;
    }

    public async Task RunAsync(ScanJob job, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        job.Status = JobStatus.Running;
        job.StartedAt ??= DateTime.UtcNow;
        await _jobRepo.UpdateAsync(job, ct);

        _logger.LogInformation("Scan job {JobId} started at {Root}", job.Id, job.RootPath);

        var pendingDirs = new Queue<(string Path, int Depth)>();
        pendingDirs.Enqueue((job.RootPath, 0));

        var fileBatch = new List<FileNode>(BatchSize);
        var dirBatch = new List<DirectoryNode>(BatchSize);
        var lastJobSave = DateTime.UtcNow;

        while (pendingDirs.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (dirPath, depth) = pendingDirs.Dequeue();

            // Already scanned in a previous run? Skip.
            var existing = await _dirRepo.GetByPathAsync(job.Id, dirPath, ct);
            if (existing?.DirStatus == DirectoryStatus.Scanned ||
                existing?.DirStatus == DirectoryStatus.Shallow)
            {
                _logger.LogTrace("Skipping already-scanned dir: {Path}", dirPath);
                continue;
            }

            var decision = depth == 0
                ? ScanDecision.Descend   // always descend root
                : _heuristics.EvaluateDirectory(dirPath, depth);

            var dirNode = new DirectoryNode
            {
                JobId = job.Id,
                FullPath = dirPath,
                Name = _fs.GetFileName(dirPath),
                ParentPath = _fs.GetDirectoryName(dirPath),
                RootPath = job.RootPath,
                RelativePath = _fs.GetRelativePath(job.RootPath, dirPath),
                Depth = depth,
                DirStatus = DecisionToStatus(decision),
                DirReason = decision.ToString(),
                ScannedAt = DateTime.UtcNow
            };

            if (decision == ScanDecision.Skip)
            {
                dirBatch.Add(dirNode);
                await FlushDirBatchIfNeeded(dirBatch, ct);
                _logger.LogDebug("Skipped: {Path}", dirPath);
                continue;
            }

            // Enumerate files in this directory
            int fileCount = 0;
            long dirSize = 0;
            foreach (var filePath in _fs.EnumerateFiles(dirPath))
            {
                ct.ThrowIfCancellationRequested();

                var ext = _fs.GetExtension(filePath);
                var size = _fs.GetFileSize(filePath);
                dirSize += size;
                fileCount++;

                var fileNode = new FileNode
                {
                    JobId = job.Id,
                    FullPath = filePath,
                    Name = _fs.GetFileName(filePath),
                    ParentPath = dirPath,
                    RootPath = job.RootPath,
                    RelativePath = _fs.GetRelativePath(job.RootPath, filePath),
                    RelativeDir = _fs.GetRelativePath(job.RootPath, dirPath),
                    Depth = depth + 1,
                    Size = size,
                    LastWriteTime = _fs.GetLastWriteTime(filePath),
                    Extension = ext,
                    FileType = FileTypeDetector.Detect(ext),
                    Status = FileNodeStatus.Discovered,
                    ScannedAt = DateTime.UtcNow
                };

                fileBatch.Add(fileNode);
                job.TotalFiles++;

                if (fileBatch.Count >= BatchSize)
                {
                    await _fileRepo.InsertBatchAsync(fileBatch, ct);
                    job.ProcessedFiles += fileBatch.Count;
                    fileBatch.Clear();
                }
            }

            dirNode.DirectFileCount = fileCount;
            dirNode.TotalSize = dirSize;

            // Enumerate subdirectories
            if (decision == ScanDecision.Descend)
            {
                int subdirCount = 0;
                foreach (var subDir in _fs.EnumerateDirectories(dirPath))
                {
                    pendingDirs.Enqueue((subDir, depth + 1));
                    subdirCount++;
                    job.TotalDirectories++;
                }
                dirNode.SubdirCount = subdirCount;
            }

            dirBatch.Add(dirNode);
            job.ProcessedDirectories++;

            await FlushDirBatchIfNeeded(dirBatch, ct);

            // Report progress
            var currentProgress = new ScanProgress(
                job.Id, job.ProcessedFiles, job.TotalFiles,
                job.ProcessedDirectories, dirPath, job.ErrorCount);
            progress.Report(currentProgress);

            // Persist job counters periodically
            if ((DateTime.UtcNow - lastJobSave).TotalMilliseconds > JobUpdateIntervalMs)
            {
                await _jobRepo.UpdateAsync(job, ct);
                lastJobSave = DateTime.UtcNow;
            }
        }

        // Flush remaining batches
        if (fileBatch.Count > 0)
        {
            await _fileRepo.InsertBatchAsync(fileBatch, ct);
            job.ProcessedFiles += fileBatch.Count;
            fileBatch.Clear();
        }

        if (dirBatch.Count > 0)
        {
            await _dirRepo.InsertBatchAsync(dirBatch, ct);
            dirBatch.Clear();
        }

        job.Status = ct.IsCancellationRequested ? JobStatus.Cancelled : JobStatus.Completed;
        job.CompletedAt = DateTime.UtcNow;
        await _jobRepo.UpdateAsync(job, ct);

        _logger.LogInformation(
            "Scan job {JobId} finished with status {Status}. Files: {Files}, Dirs: {Dirs}",
            job.Id, job.Status, job.ProcessedFiles, job.ProcessedDirectories);
    }

    private async Task FlushDirBatchIfNeeded(List<DirectoryNode> batch, CancellationToken ct)
    {
        if (batch.Count >= BatchSize)
        {
            await _dirRepo.InsertBatchAsync(batch, ct);
            batch.Clear();
        }
    }

    private static DirectoryStatus DecisionToStatus(ScanDecision d) => d switch
    {
        ScanDecision.Descend => DirectoryStatus.Scanned,
        ScanDecision.Shallow => DirectoryStatus.Shallow,
        ScanDecision.Skip    => DirectoryStatus.Skip,
        _                    => DirectoryStatus.Pending
    };
}
