using Dapper;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>Scan rows for the duplicate report (https://github.com/crack666/smart_file_organizer/issues/87).</summary>
public sealed class DuplicateRepository : IDuplicateRepository
{
    private readonly DatabaseContext _db;

    public DuplicateRepository(DatabaseContext db) => _db = db;

    public async Task<IReadOnlyList<DuplicateCandidate>> GetSizeCandidatesAsync(IReadOnlyList<long> jobIds, long minSize, CancellationToken ct = default)
    {
        if (jobIds.Count == 0)
            return [];
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        // Index file_nodes(job_id, size) serves both the inner count and the outer lookup.
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT job_id, id, full_path, name, parent_path, size FROM file_nodes
            WHERE job_id IN @jobIds AND size >= @minSize
              AND size IN (SELECT size FROM file_nodes
                           WHERE job_id IN @jobIds AND size >= @minSize
                           GROUP BY size HAVING COUNT(*) > 1)
            ORDER BY size, full_path
            """,
            new { jobIds, minSize = Math.Max(1, minSize) }, cancellationToken: ct));
        return rows.Select(r => new DuplicateCandidate(r.JobId, new ComparableFile(r.Id, r.FullPath, r.Name, r.ParentPath, r.Size))).ToList();
    }

    public async Task<(long Files, long Bytes)> GetTotalsAsync(IReadOnlyList<long> jobIds, CancellationToken ct = default)
    {
        if (jobIds.Count == 0)
            return (0, 0);
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleAsync<(long Files, long Bytes)>(new CommandDefinition(
            "SELECT COUNT(*), COALESCE(SUM(size), 0) FROM file_nodes WHERE job_id IN @jobIds",
            new { jobIds }, cancellationToken: ct));
        return row;
    }

    public async Task<IReadOnlyList<FolderFileCount>> GetFolderFileCountsAsync(IReadOnlyList<long> jobIds, long minSize, CancellationToken ct = default)
    {
        if (jobIds.Count == 0)
            return [];
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long JobId, string ParentPath, long Files, long Bytes)>(new CommandDefinition(
            """
            SELECT job_id, parent_path, COUNT(*), COALESCE(SUM(size), 0) FROM file_nodes
            WHERE job_id IN @jobIds AND size >= @minSize
            GROUP BY job_id, parent_path
            """,
            new { jobIds, minSize = Math.Max(1, minSize) }, cancellationToken: ct));
        return rows.Select(r => new FolderFileCount(r.JobId, r.ParentPath, (int)r.Files, r.Bytes)).ToList();
    }

    private sealed class Row
    {
        public long JobId { get; set; }
        public long Id { get; set; }
        public string FullPath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ParentPath { get; set; } = string.Empty;
        public long Size { get; set; }
    }
}
