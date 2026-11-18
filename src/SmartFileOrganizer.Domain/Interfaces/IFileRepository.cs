using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IFileRepository
{
    Task InsertBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default);
    Task ReplaceDirectoryFilesAsync(long jobId, string parentPath, IEnumerable<FileNode> nodes, CancellationToken ct = default);
    Task<FileNode?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<FileNode>> GetByDirectoryAsync(long jobId, string parentPath, CancellationToken ct = default);
    Task<IReadOnlyList<FileNode>> GetPendingAiAnalysisAsync(long jobId, int batchSize, CancellationToken ct = default);
    Task<long> CountAiEligibleAsync(long jobId, CancellationToken ct = default);
    Task<long> CountPendingAiAnalysisAsync(long jobId, CancellationToken ct = default);
    Task UpdateMetadataAsync(FileNode node, CancellationToken ct = default);
    Task UpdateStatusAsync(long id, Enums.FileNodeStatus status, CancellationToken ct = default);
    Task<long> CountByJobAsync(long jobId, CancellationToken ct = default);
    /// <summary>Returns lightweight name+size info for ALL files in a directory (used for Phase 1 prompt building).</summary>
    Task<IReadOnlyList<DirectoryFileInfo>> GetFileInfoForDirectoryAsync(long jobId, string parentPath, CancellationToken ct = default);
    /// <summary>Marks the given file IDs as Skipped if they are still in Discovered state.</summary>
    Task MarkAsSkippedAsync(IReadOnlyList<long> ids, CancellationToken ct = default);
    Task MarkAsProcessingAsync(IReadOnlyList<long> ids, CancellationToken ct = default);
    /// <summary>Resets any files stuck in Processing (e.g. from a previous cancelled run) back to Discovered.</summary>
    Task ResetStaleProcessingAsync(long jobId, CancellationToken ct = default);
    /// <summary>
    /// Sets failed files (status Error) of the job back to Discovered so Phase 2 tries them again; files a
    /// person corrected are left alone. Their failed result stays until the new attempt replaces it.
    /// Returns the number of files released.
    /// </summary>
    Task<int> ReleaseFailedAsync(long jobId, CancellationToken ct = default);
    /// <summary>
    /// Sets files back to Discovered that are Skipped although nothing skips them any more: no result, not left
    /// out as backed up (<c>analysis_skip</c>), and their directory judged neither <c>unit</c> nor <c>system</c>.
    /// Such rows come from databases written while the model chose a random sample (the files outside it were
    /// Skipped for good) and from a directory judged again with another verdict. Returns the number released.
    /// </summary>
    Task<int> ReleaseStaleSkippedAsync(long jobId, CancellationToken ct = default);
    /// <summary>Files of a job whose stored rule version is missing or lower than <paramref name="version"/>.</summary>
    Task<IReadOnlyList<FileNode>> GetBelowRulesVersionAsync(long jobId, int version, CancellationToken ct = default);
    /// <summary>The files of a job of one type, by id.</summary>
    Task<IReadOnlyList<FileNode>> GetByTypeAsync(long jobId, Enums.FileType type, CancellationToken ct = default);
    /// <summary>Writes markers, rule id and rule version of the given files; nothing else.</summary>
    Task UpdateSensitiveAsync(IReadOnlyList<FileNode> nodes, CancellationToken ct = default);
    /// <summary>Records files that are themselves links (<c>file_links</c>); a path already recorded is kept.</summary>
    Task InsertLinksAsync(IEnumerable<FileLinkNode> links, CancellationToken ct = default);
}
