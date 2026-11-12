using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>
/// Read-only queries behind the CLI read commands (<c>dirs</c>, <c>files</c>, <c>show</c>, <c>summary</c>).
/// Nothing here writes to the database.
/// </summary>
public interface IJobReadRepository
{
    /// <summary>Every directory of the job with verdict codes and Phase 3 summary, ordered by full path.</summary>
    Task<IReadOnlyList<DirectoryReadRow>> GetDirectoriesAsync(long jobId, CancellationToken ct = default);

    /// <summary>Count and bytes of the files directly in each directory of the job.</summary>
    Task<IReadOnlyList<DirectoryFileTotals>> GetDirectoryFileTotalsAsync(long jobId, CancellationToken ct = default);

    /// <summary>
    /// Streams the files matching <paramref name="filter"/> with classification and override loaded,
    /// so a listing over a large job holds only one page in memory.
    /// </summary>
    IAsyncEnumerable<FileReadRow> StreamFilesAsync(FileListFilter filter, CancellationToken ct = default);

    /// <summary>One file by full path, with classification (including raw response) and override.</summary>
    Task<FileReadRow?> GetFileByPathAsync(long jobId, string fullPath, CancellationToken ct = default);

    /// <summary>The Phase 1 pre-assessment and Phase 3 summary rows of one directory (either may be missing).</summary>
    Task<IReadOnlyList<DirectoryClassificationResult>> GetDirectoryAnalysesAsync(long directoryNodeId, CancellationToken ct = default);

    /// <summary>The second checks of one file with both answers, oldest first.</summary>
    Task<IReadOnlyList<ResultCheck>> GetResultChecksAsync(long fileNodeId, CancellationToken ct = default);
}
