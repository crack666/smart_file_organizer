using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Scanning.Rules;

namespace SmartFileOrganizer.Scanning.Engine;

/// <summary>
/// Iterative scan engine. Processes directories using a work queue (not recursion).
/// Writes results to SQLite in configurable batches.
/// Scans with the rules stored at the job (<see cref="ScanJob.Rules"/>); a job without stored rules
/// gets the shipped rules recorded on its first run.
/// Supports cancellation and resume: directories already scanned are not read again. Directories stored
/// as skipped or shallow are re-evaluated with the job's current rules and entered if those let them in,
/// so resuming after a rule change (or after an update of the shipped rules) does not keep stale skips.
/// </summary>
public class ScanEngine : IScanEngine
{
    private readonly IFileSystemAccessor _fs;
    private readonly IFileRepository _fileRepo;
    private readonly IDirectoryRepository _dirRepo;
    private readonly IScanJobRepository _jobRepo;
    private readonly ILogger<ScanEngine> _logger;

    private const int BatchSize = 200;
    private const int JobUpdateIntervalMs = 2000;

    public ScanEngine(
        IFileSystemAccessor fs,
        IFileRepository fileRepo,
        IDirectoryRepository dirRepo,
        IScanJobRepository jobRepo,
        ILogger<ScanEngine> logger)
    {
        _fs = fs;
        _fileRepo = fileRepo;
        _dirRepo = dirRepo;
        _jobRepo = jobRepo;
        _logger = logger;
    }

    public async Task RunAsync(ScanJob job, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        if (job.Rules == null)
        {
            job.Rules = ScanRulesFile.DefaultProfile;
            _logger.LogInformation("Scan job {JobId} has no rules recorded; using the shipped rules '{Name}'", job.Id, job.RulesName);
        }
        var heuristics = new HeuristicsEngine(ScanRulesFile.FromProfile(job.Rules));

        job.Status = JobStatus.Running;
        job.StartedAt ??= DateTime.UtcNow;
        await _jobRepo.UpdateAsync(job, ct);

        _logger.LogInformation("Scan job {JobId} started at {Root} with rules '{Rules}'", job.Id, job.RootPath, job.RulesName);

        var pendingDirs = new Queue<(string Path, int Depth)>();
        pendingDirs.Enqueue((job.RootPath, 0));

        var fileBatch = new List<FileNode>(BatchSize);
        var dirBatch = new List<DirectoryNode>(BatchSize);
        var lastJobSave = DateTime.UtcNow;

        while (pendingDirs.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (dirPath, depth) = pendingDirs.Dequeue();

            // Resume behavior:
            // If a directory itself is already scanned/shallow, we still may need to walk through
            // its children to discover descendants that were queued but not processed before pause.
            // So we do a lightweight traversal for existing Scanned dirs, but avoid reprocessing files.
            var existing = await _dirRepo.GetByPathAsync(job.Id, dirPath, ct);
            if (existing?.DirStatus == DirectoryStatus.Scanned)
            {
                foreach (var subDir in _fs.EnumerateDirectories(dirPath))
                {
                    pendingDirs.Enqueue((subDir, depth + 1));
                }

                _logger.LogTrace("Traversed children of already-scanned dir: {Path}", dirPath);
                continue;
            }

            // Listed at most once: for the content check (only when a decision depends on it) and for the rows.
            // The listing throws on access or I/O errors, so an unreadable directory is recorded as an error
            // rather than looking empty; the comparison with a library must not mistake missing files for absent ones.
            List<string>? files = null;
            Exception? listError = null;
            List<string> Files()
            {
                if (files != null)
                    return files;
                try { files = [.. _fs.ListFiles(dirPath)]; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { listError = ex; files = []; }
                return files;
            }

            var evaluation = depth == 0
                ? new DirectoryEvaluation(ScanDecision.Descend, "Descend: scan root")   // always descend root
                : heuristics.EvaluateDirectory(job.RootPath, dirPath, depth,
                    () => new DirectoryContent(Files().Select(f => FileTypeDetector.Detect(_fs.GetExtension(f)))));
            var decision = evaluation.Decision;
            var status = DecisionToStatus(decision);

            DirectoryNode dirNode;
            bool listFiles;
            if (existing != null)
            {
                // Stored as skipped or shallow by an earlier run, possibly under other rules. Only a wider
                // decision is applied; a narrower one never throws away what was already collected.
                if (Coverage(status) <= Coverage(existing.DirStatus))
                {
                    // A skip stored before folders were marked gets its markers now, still without entering it.
                    if (existing.DirStatus == DirectoryStatus.Skip && existing.SensitiveMarkers == SensitiveMarker.None)
                    {
                        SensitiveFileRules.ApplyToSkippedFolder(existing);
                        if (existing.SensitiveMarkers != SensitiveMarker.None)
                            await _dirRepo.UpdateAsync(existing, ct);
                    }
                    _logger.LogTrace("Kept stored decision for {Path}: {Reason}", dirPath, existing.DirReason);
                    continue;
                }

                _logger.LogDebug("Re-evaluated {Path}: {Old} -> {New}", dirPath, existing.DirReason, evaluation.Reason);
                listFiles = existing.DirStatus != DirectoryStatus.Shallow;   // a shallow folder's files are stored
                dirNode = existing;
                dirNode.DirStatus = status;
                dirNode.DirReason = $"{evaluation.Reason} (re-evaluated on resume; before: {existing.DirReason})";
                dirNode.ScannedAt = DateTime.UtcNow;
            }
            else
            {
                listFiles = true;
                dirNode = new DirectoryNode
                {
                    JobId = job.Id,
                    FullPath = dirPath,
                    Name = _fs.GetFileName(dirPath),
                    ParentPath = _fs.GetDirectoryName(dirPath),
                    RootPath = job.RootPath,
                    RelativePath = _fs.GetRelativePath(job.RootPath, dirPath),
                    Depth = depth,
                    DirStatus = status,
                    DirReason = evaluation.Reason,
                    ScannedAt = DateTime.UtcNow
                };
                // Every stored decision counts as processed, skips included; otherwise a finished scan with
                // two or more skipped folders reads as unfinished (processed < total) and is reported as paused.
                job.ProcessedDirectories++;
            }

            IReadOnlyList<string> subDirs = [];
            if (listError == null && decision != ScanDecision.Skip)
            {
                if (listFiles)
                    Files();
                if (listError == null && decision == ScanDecision.Descend)
                {
                    try { subDirs = _fs.ListDirectories(dirPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { listError = ex; }
                }
            }

            if (listError != null)
            {
                dirNode.DirStatus = DirectoryStatus.Error;
                dirNode.DirReason = $"Read error: {listError.Message}";
                job.ErrorCount++;
                job.LastError = $"{dirPath}: {listError.Message}";
                if (existing != null)
                    await _dirRepo.UpdateAsync(dirNode, ct);
                else
                    dirBatch.Add(dirNode);
                await FlushDirBatchIfNeeded(dirBatch, ct);
                _logger.LogWarning("Could not read directory {Path}: {Message}", dirPath, listError.Message);
                continue;
            }

            if (decision == ScanDecision.Skip)
            {
                // No rule sees the files of a skipped folder, so the folder itself carries the marker
                // (.ssh, .gnupg, .aws, AppData, ...). Name and path only: the folder is not entered.
                SensitiveFileRules.ApplyToSkippedFolder(dirNode);
                dirBatch.Add(dirNode);
                await FlushDirBatchIfNeeded(dirBatch, ct);
                _logger.LogDebug("Skipped: {Path} ({Reason})", dirPath, evaluation.Reason);
                continue;
            }

            int fileCount = 0;
            long dirSize = 0;
            foreach (var filePath in listFiles ? Files() : [])
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

                // Sensitive-file rules run here, not in the AI phase, so they also cover files
                // the model never sees. Only small, non-media files are probed for a header, and
                // a structure-only job opens no file at all: name, extension and folder rules only.
                SensitiveFileRules.Apply(fileNode, job.StructureOnly ? null : maxBytes => _fs.ReadPrefix(filePath, maxBytes));

                fileBatch.Add(fileNode);
                job.TotalFiles++;

                if (fileBatch.Count >= BatchSize)
                {
                    await _fileRepo.InsertBatchAsync(fileBatch, ct);
                    job.ProcessedFiles += fileBatch.Count;
                    fileBatch.Clear();
                }
            }

            if (listFiles)
            {
                dirNode.DirectFileCount = fileCount;
                dirNode.TotalSize = dirSize;
            }

            // Enumerate subdirectories
            if (decision == ScanDecision.Descend)
            {
                int subdirCount = 0;
                foreach (var subDir in subDirs)
                {
                    pendingDirs.Enqueue((subDir, depth + 1));
                    subdirCount++;
                    job.TotalDirectories++;
                }
                dirNode.SubdirCount = subdirCount;
            }
            else
            {
                // Shallow: count the subfolders without entering them, so the stored node shows
                // that there is more below it instead of looking like a leaf.
                dirNode.SubdirCount = _fs.EnumerateDirectories(dirPath).Count();
            }

            if (existing != null)
                await _dirRepo.UpdateAsync(dirNode, ct);
            else
                dirBatch.Add(dirNode);

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

    /// <summary>How much of a folder a status covers: skipped &lt; shallow &lt; scanned.</summary>
    private static int Coverage(DirectoryStatus status) => status switch
    {
        DirectoryStatus.Scanned => 2,
        DirectoryStatus.Shallow => 1,
        _ => 0
    };

    private static DirectoryStatus DecisionToStatus(ScanDecision d) => d switch
    {
        ScanDecision.Descend => DirectoryStatus.Scanned,
        ScanDecision.Shallow => DirectoryStatus.Shallow,
        ScanDecision.Skip    => DirectoryStatus.Skip,
        _                    => DirectoryStatus.Pending
    };
}
