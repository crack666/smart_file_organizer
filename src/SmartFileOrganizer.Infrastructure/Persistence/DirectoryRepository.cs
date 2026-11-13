using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

public class DirectoryRepository : IDirectoryRepository
{
    private readonly DatabaseContext _db;

    public DirectoryRepository(DatabaseContext db) => _db = db;

    public async Task InsertBatchAsync(IEnumerable<DirectoryNode> nodes, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string sql = """
            INSERT OR IGNORE INTO directory_nodes
                (job_id, full_path, name, parent_path, root_path, relative_path,
                 depth, dir_status, dir_reason, total_size, direct_file_count,
                 recursive_file_count, subdir_count, dominant_file_types,
                 dominant_categories, suggested_area, scanned_at,
                 sensitive_markers, sensitive_rule, link_kind, link_target, scan_error_cause)
            VALUES
                (@JobId, @FullPath, @Name, @ParentPath, @RootPath, @RelativePath,
                 @Depth, @DirStatus, @DirReason, @TotalSize, @DirectFileCount,
                 @RecursiveFileCount, @SubdirCount, @DominantFileTypes,
                 @DominantCategories, @SuggestedArea, @ScannedAt,
                 @SensitiveMarkers, @SensitiveRule, @LinkKind, @LinkTarget, @ScanErrorCause)
            """;

        await conn.ExecuteAsync(sql, nodes.Select(ToParameters), transaction: tx);
        await tx.CommitAsync(ct);
    }

    public async Task<DirectoryNode?> GetByPathAsync(
        long jobId, string fullPath, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DirectoryNode>(
            "SELECT * FROM directory_nodes WHERE job_id = @jobId AND full_path = @fullPath",
            new { jobId, fullPath });
    }

    public async Task<IReadOnlyList<DirectoryNode>> GetChildrenAsync(
        long jobId, string parentPath, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var result = await conn.QueryAsync<DirectoryNode>(
            """
            SELECT * FROM directory_nodes
            WHERE job_id = @jobId
              AND parent_path = @parentPath
              AND dir_status != @skip
              AND name NOT LIKE '.%'
            ORDER BY name
            """,
            new { jobId, parentPath, skip = (int)DirectoryStatus.Skip });
        return result.AsList();
    }

    public async Task<IReadOnlyList<DirectoryNode>> GetRootsAsync(
        long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var result = await conn.QueryAsync<DirectoryNode>(
            """
            SELECT * FROM directory_nodes
            WHERE job_id = @jobId
              AND depth = 0
              AND dir_status != @skip
              AND name NOT LIKE '.%'
            ORDER BY name
            """,
            new { jobId, skip = (int)DirectoryStatus.Skip });
        return result.AsList();
    }

    public async Task UpdateAsync(DirectoryNode node, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            UPDATE directory_nodes SET
                dir_status = @DirStatus,
                dir_reason = @DirReason,
                total_size = @TotalSize,
                direct_file_count = @DirectFileCount,
                recursive_file_count = @RecursiveFileCount,
                subdir_count = @SubdirCount,
                dominant_file_types = @DominantFileTypes,
                dominant_categories = @DominantCategories,
                suggested_area = @SuggestedArea,
                scanned_at = @ScannedAt,
                sensitive_markers = @SensitiveMarkers,
                sensitive_rule = @SensitiveRule,
                link_kind = @LinkKind,
                link_target = @LinkTarget,
                scan_error_cause = @ScanErrorCause
            WHERE id = @Id
            """;

        await conn.ExecuteAsync(sql, ToParameters(node));
    }

    public async Task<IReadOnlyList<DirectoryNode>> GetAllForJobOrderedByDepthAsync(
        long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var result = await conn.QueryAsync<DirectoryNode>(
            "SELECT * FROM directory_nodes WHERE job_id = @jobId ORDER BY depth ASC, full_path ASC",
            new { jobId });
        return result.AsList();
    }

    public async Task<ScanTotals> GetScanTotalsAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleAsync<TotalsRow>(new CommandDefinition(
            """
            SELECT
                (SELECT COUNT(*) FROM directory_nodes WHERE job_id = @jobId) AS directories,
                (SELECT COUNT(*) FROM file_nodes WHERE job_id = @jobId) AS files,
                (SELECT COUNT(*) FROM directory_nodes WHERE job_id = @jobId AND dir_status = @error) AS directory_errors,
                (SELECT COUNT(*) FROM file_nodes WHERE job_id = @jobId AND scan_error_cause IS NOT NULL) AS file_errors
            """,
            new { jobId, error = (int)DirectoryStatus.Error }, cancellationToken: ct));
        return new ScanTotals(row.Directories, row.Files, row.DirectoryErrors, row.FileErrors);
    }

    private sealed class TotalsRow
    {
        public long Directories { get; set; }
        public long Files { get; set; }
        public long DirectoryErrors { get; set; }
        public long FileErrors { get; set; }
    }

    /// <summary>
    /// Parameter object for inserts and updates. Dapper would bind <see cref="SensitiveMarker"/> as its
    /// integer value; the column holds the fixed token list instead.
    /// </summary>
    private static object ToParameters(DirectoryNode n) => new
    {
        n.Id,
        n.JobId,
        n.FullPath,
        n.Name,
        n.ParentPath,
        n.RootPath,
        n.RelativePath,
        n.Depth,
        n.DirStatus,
        n.DirReason,
        n.TotalSize,
        n.DirectFileCount,
        n.RecursiveFileCount,
        n.SubdirCount,
        n.DominantFileTypes,
        n.DominantCategories,
        n.SuggestedArea,
        n.ScannedAt,
        SensitiveMarkers = SensitiveMarkerNames.Format(n.SensitiveMarkers),
        n.SensitiveRule,
        n.LinkKind,
        n.LinkTarget,
        n.ScanErrorCause
    };
}
