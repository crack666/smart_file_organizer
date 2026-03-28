using Dapper;
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
                 dominant_categories, suggested_area, scanned_at)
            VALUES
                (@JobId, @FullPath, @Name, @ParentPath, @RootPath, @RelativePath,
                 @Depth, @DirStatus, @DirReason, @TotalSize, @DirectFileCount,
                 @RecursiveFileCount, @SubdirCount, @DominantFileTypes,
                 @DominantCategories, @SuggestedArea, @ScannedAt)
            """;

        await conn.ExecuteAsync(sql, nodes, transaction: tx);
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
            WHERE job_id = @jobId AND parent_path = @parentPath
            ORDER BY name
            """,
            new { jobId, parentPath });
        return result.AsList();
    }

    public async Task<IReadOnlyList<DirectoryNode>> GetRootsAsync(
        long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var result = await conn.QueryAsync<DirectoryNode>(
            "SELECT * FROM directory_nodes WHERE job_id = @jobId AND depth = 0 ORDER BY name",
            new { jobId });
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
                suggested_area = @SuggestedArea
            WHERE id = @Id
            """;

        await conn.ExecuteAsync(sql, node);
    }
}
