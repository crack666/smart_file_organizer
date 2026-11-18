using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IDirectoryRepository
{
    Task InsertBatchAsync(IEnumerable<DirectoryNode> nodes, CancellationToken ct = default);
    Task<DirectoryNode?> GetByPathAsync(long jobId, string fullPath, CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryNode>> GetChildrenAsync(long jobId, string parentPath, CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryNode>> GetRootsAsync(long jobId, CancellationToken ct = default);
    Task UpdateAsync(DirectoryNode node, CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryNode>> GetAllForJobOrderedByDepthAsync(long jobId, CancellationToken ct = default);

    /// <summary>
    /// Paths of the links a scan of the job entered through a <c>follow_link</c> rule: folders with a link kind
    /// whose status is not <c>link</c>. A resumed scan needs them to know which targets were entered already.
    /// </summary>
    Task<IReadOnlyList<string>> GetFollowedLinkPathsAsync(long jobId, CancellationToken ct = default);

    /// <summary>What a job's scan stored, counted from the rows: the counters a finished scan reports.</summary>
    Task<ScanTotals> GetScanTotalsAsync(long jobId, CancellationToken ct = default);
}

/// <summary>Stored rows of a scan: folders (every decision, links and errors included), files, and read errors.</summary>
public sealed record ScanTotals(long Directories, long Files, long DirectoryErrors, long FileErrors);
