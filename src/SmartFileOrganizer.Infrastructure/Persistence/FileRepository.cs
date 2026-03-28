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
        var node = await conn.QuerySingleOrDefaultAsync<FileNode>(
            "SELECT * FROM file_nodes WHERE id = @id", new { id });
        if (node == null) return null;

        node.Classification = await conn.QuerySingleOrDefaultAsync<FileClassification>(
            "SELECT * FROM ai_results WHERE file_node_id = @id", new { id });
        node.Override = await conn.QuerySingleOrDefaultAsync<UserOverride>(
            "SELECT * FROM user_overrides WHERE file_node_id = @id", new { id });
        return node;
    }

    public async Task<IReadOnlyList<FileNode>> GetByDirectoryAsync(
        long jobId, string parentPath, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);

        // Step 1: load file nodes (plain, no ambiguous JOINs)
        System.Diagnostics.Debug.WriteLine($"[DB QUERY] GetByDirectoryAsync jobId={jobId} parentPath='{parentPath}'");
        var nodes = (await conn.QueryAsync<FileNode>(
            """SELECT * FROM file_nodes WHERE job_id = @jobId AND parent_path = @parentPath ORDER BY name""",
            new { jobId, parentPath })).AsList();
        System.Diagnostics.Debug.WriteLine($"[DB QUERY] → {nodes.Count} row(s) found");

        if (nodes.Count == 0) return nodes;

        // Step 2: load classifications for these files in one query
        var ids = nodes.Select(n => n.Id).ToList();
        var classifications = (await conn.QueryAsync<FileClassification>(
            "SELECT * FROM ai_results WHERE file_node_id IN @ids",
            new { ids })).ToDictionary(c => c.FileNodeId);

        // Step 3: load user overrides
        var overrides = (await conn.QueryAsync<UserOverride>(
            "SELECT * FROM user_overrides WHERE file_node_id IN @ids",
            new { ids })).ToDictionary(u => u.FileNodeId);

        // Step 4: attach
        foreach (var node in nodes)
        {
            if (classifications.TryGetValue(node.Id, out var cls)) node.Classification = cls;
            if (overrides.TryGetValue(node.Id, out var uo)) node.Override = uo;
        }

        return nodes;
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
