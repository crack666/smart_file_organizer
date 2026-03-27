using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

public class FileRepository : IFileRepository
{
    private readonly DatabaseContext _db;

    public FileRepository(DatabaseContext db) => _db = db;

    public async Task InsertBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string sql = """
            INSERT OR IGNORE INTO file_nodes
                (job_id, full_path, name, parent_path, root_path, relative_path,
                 relative_dir, depth, size, last_write_time, extension,
                 file_type, status, scanned_at)
            VALUES
                (@JobId, @FullPath, @Name, @ParentPath, @RootPath, @RelativePath,
                 @RelativeDir, @Depth, @Size, @LastWriteTime, @Extension,
                 @FileType, @Status, @ScannedAt)
            """;

        await conn.ExecuteAsync(sql, nodes, transaction: tx);
        await tx.CommitAsync(ct);
    }

    public async Task<FileNode?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<FileNode>(
            "SELECT * FROM file_nodes WHERE id = @id", new { id });
    }

    public async Task<IReadOnlyList<FileNode>> GetByDirectoryAsync(
        long jobId, string parentPath, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);

        var nodes = await conn.QueryAsync<FileNode>(
            """
            SELECT f.*, a.id as ai_id, a.category, a.importance, a.confidence,
                   a.summary, a.suggested_target, a.model_used, a.analyzed_at,
                   a.raw_response, a.error, a.is_ai_result,
                   u.id as uo_id, u.overridden_category, u.overridden_target, u.note, u.overridden_at
            FROM file_nodes f
            LEFT JOIN ai_results a ON a.file_node_id = f.id
            LEFT JOIN user_overrides u ON u.file_node_id = f.id
            WHERE f.job_id = @jobId AND f.parent_path = @parentPath
            ORDER BY f.name
            """,
            new { jobId, parentPath });

        return nodes.AsList();
    }

    public async Task<IReadOnlyList<FileNode>> GetPendingAiAnalysisAsync(
        long jobId, int batchSize, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);

        var nodes = await conn.QueryAsync<FileNode>(
            """
            SELECT * FROM file_nodes
            WHERE job_id = @jobId
              AND status = @status
              AND file_type IN (1, 2, 4)  -- Image=1, Video=2, Document=4
            ORDER BY id
            LIMIT @batchSize
            """,
            new { jobId, status = (int)FileNodeStatus.Discovered, batchSize });

        return nodes.AsList();
    }

    public async Task UpdateStatusAsync(long id, FileNodeStatus status, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE file_nodes SET status = @status WHERE id = @id",
            new { status = (int)status, id });
    }

    public async Task<long> CountByJobAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM file_nodes WHERE job_id = @jobId", new { jobId });
    }
}
