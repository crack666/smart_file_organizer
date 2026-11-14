using Dapper;
using Microsoft.Data.Sqlite;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// Places, refresh changes, found move notices and the questions of <c>sfo where</c>
/// (https://github.com/crack666/smart_file_organizer/issues/86). Paths are compared without case on Windows.
/// </summary>
public class PlaceRepository : IPlaceRepository
{
    private static readonly string Collate = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;

    private readonly DatabaseContext _db;

    public PlaceRepository(DatabaseContext db) => _db = db;

    public async Task<Place> RecordScanAsync(ScanJob job, VolumeInfo? volume, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var root = Normalize(job.RootPath);
        var bytes = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(SUM(size), 0) FROM file_nodes WHERE job_id = @id", new { id = job.Id }, cancellationToken: ct));
        var now = DateTime.UtcNow;
        var existing = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            $"SELECT id FROM places WHERE root_path = @root{Collate}", new { root }, cancellationToken: ct));
        var args = new
        {
            root,
            role = job.Role.ToValue(),
            label = volume?.Label,
            vid = volume?.Id,
            format = volume?.Format,
            job = job.Id,
            at = job.CompletedAt ?? now,
            files = job.TotalFiles,
            bytes,
            now,
            id = existing
        };
        if (existing is null)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO places (root_path, role, volume_label, volume_id, volume_format, last_job_id, last_scan_at, files, bytes, first_seen_at)
                VALUES (@root, @role, @label, @vid, @format, @job, @at, @files, @bytes, @now)
                """, args, cancellationToken: ct));
        }
        else
        {
            // A drive that cannot be read this time keeps what was learnt about it before.
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE places SET role = @role, volume_label = COALESCE(@label, volume_label), volume_id = COALESCE(@vid, volume_id),
                    volume_format = COALESCE(@format, volume_format), last_job_id = @job, last_scan_at = @at, files = @files, bytes = @bytes
                WHERE id = @id
                """, args, cancellationToken: ct));
        }
        return (await GetAllAsync(conn, ct)).First(p => string.Equals(p.RootPath, root, PathComparison));
    }

    public async Task<IReadOnlyList<Place>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await GetAllAsync(conn, ct);
    }

    private static async Task<IReadOnlyList<Place>> GetAllAsync(SqliteConnection conn, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<PlaceRow>(new CommandDefinition(
            """
            SELECT id, root_path, role, volume_label, volume_id, volume_format, last_job_id, last_scan_at, files, bytes, first_seen_at
            FROM places ORDER BY last_scan_at DESC, id DESC
            """, cancellationToken: ct));
        return rows.Select(r => new Place
        {
            Id = r.Id,
            RootPath = r.RootPath,
            Role = ScanRoleValues.Parse(r.Role),
            VolumeLabel = r.VolumeLabel,
            VolumeId = r.VolumeId,
            VolumeFormat = r.VolumeFormat,
            LastJobId = r.LastJobId,
            LastScanAt = Utc(r.LastScanAt),
            Files = r.Files,
            Bytes = r.Bytes,
            FirstSeenAt = Utc(r.FirstSeenAt)!.Value
        }).ToList();
    }

    public async Task<long?> GetPreviousJobIdAsync(string rootPath, ScanRole role, long exceptJobId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            $"""
            SELECT id FROM scan_jobs
            WHERE root_path = @root{Collate} AND role = @role AND id <> @except AND status = @completed
            ORDER BY id DESC LIMIT 1
            """,
            new { root = Normalize(rootPath), role = role.ToValue(), except = exceptJobId, completed = (int)JobStatus.Completed },
            cancellationToken: ct));
    }

    public async Task<RefreshCounts> RefreshAsync(long previousJobId, long jobId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var p = new { prev = previousJobId, job = jobId };

        // Pairs of the same file in both jobs: same path, size and time, and read without error both times.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            DROP TABLE IF EXISTS temp.refresh_pairs;
            CREATE TEMP TABLE refresh_pairs (new_id INTEGER PRIMARY KEY, old_id INTEGER NOT NULL);
            INSERT INTO refresh_pairs (new_id, old_id)
            SELECT n.id, o.id FROM file_nodes n
            JOIN file_nodes o ON o.job_id = @prev AND o.full_path = n.full_path
            WHERE n.job_id = @job AND n.size = o.size AND n.last_write_time = o.last_write_time
              AND n.scan_error_cause IS NULL AND o.scan_error_cause IS NULL;
            """, p, tx, cancellationToken: ct));

        var unchanged = await conn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM refresh_pairs", transaction: tx, cancellationToken: ct));
        var checksums = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*) FROM refresh_pairs r JOIN file_nodes n ON n.id = r.new_id
            JOIN content_fingerprints f ON f.full_path = n.full_path AND f.size = n.size AND f.full_hash IS NOT NULL
            """, transaction: tx, cancellationToken: ct));

        var results = await CopyRowsAsync(conn, tx, "ai_results", ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE file_nodes SET status = (SELECT o.status FROM refresh_pairs r JOIN file_nodes o ON o.id = r.old_id WHERE r.new_id = file_nodes.id)
            WHERE id IN (SELECT r.new_id FROM refresh_pairs r JOIN ai_results a ON a.file_node_id = r.new_id)
            """, transaction: tx, cancellationToken: ct));
        var facts = await CopyRowsAsync(conn, tx, "file_facts", ct);
        var corrections = await CopyRowsAsync(conn, tx, "user_overrides", ct);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM scan_changes WHERE job_id = @job;
            INSERT INTO scan_changes (job_id, previous_job_id, full_path, change, size)
            SELECT @job, @prev, n.full_path, CASE WHEN o.id IS NULL THEN 'new' ELSE 'changed' END, n.size
            FROM file_nodes n LEFT JOIN file_nodes o ON o.job_id = @prev AND o.full_path = n.full_path
            WHERE n.job_id = @job AND n.id NOT IN (SELECT new_id FROM refresh_pairs);
            """, p, tx, cancellationToken: ct));

        // Gone: moved when the move history says a plan took it away and it has not come back.
        var gone = (await conn.QueryAsync<GoneRow>(new CommandDefinition(
            """
            SELECT o.full_path, o.size FROM file_nodes o
            WHERE o.job_id = @prev AND NOT EXISTS (SELECT 1 FROM file_nodes n WHERE n.job_id = @job AND n.full_path = o.full_path)
            """, p, tx, cancellationToken: ct))).ToList();
        long moved = 0;
        foreach (var g in gone)
        {
            var move = await conn.QuerySingleOrDefaultAsync<MoveRow>(new CommandDefinition(
                $"""
                SELECT id, plan_id, action_id, source_path, target_path, hash, bytes, moved_at, returned_at, purged_at FROM moves
                WHERE source_path = @path{Collate} AND returned_at IS NULL ORDER BY id DESC LIMIT 1
                """, new { path = g.FullPath }, tx, cancellationToken: ct));
            if (move is not null) moved++;
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO scan_changes (job_id, previous_job_id, full_path, change, size, target_path, plan_id, moved_at)
                VALUES (@job, @prev, @path, @change, @size, @target, @plan, @at)
                """,
                new
                {
                    job = jobId, prev = previousJobId, path = g.FullPath, size = g.Size,
                    change = (move is null ? FileChange.Missing : FileChange.Moved).ToCode(),
                    target = move?.TargetPath, plan = move?.PlanId, at = move?.MovedAt
                }, tx, cancellationToken: ct));
        }

        var counts = (await conn.QueryAsync<(string Change, long Count)>(new CommandDefinition(
            "SELECT change, COUNT(*) FROM scan_changes WHERE job_id = @job GROUP BY change", p, tx, cancellationToken: ct)))
            .ToDictionary(c => c.Change, c => c.Count);
        await conn.ExecuteAsync(new CommandDefinition("DROP TABLE temp.refresh_pairs", transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);

        long Of(FileChange c) => counts.GetValueOrDefault(c.ToCode());
        return new RefreshCounts(previousJobId, unchanged, Of(FileChange.New), Of(FileChange.Changed), Of(FileChange.Missing), moved,
            checksums, results, facts, corrections);
    }

    /// <summary>
    /// Copies the rows of <paramref name="table"/> that belong to the old file of each pair onto the new file. A reference
    /// to a folder of the old job is moved to the folder with the same path in the new job (or null).
    /// </summary>
    private static async Task<long> CopyRowsAsync(SqliteConnection conn, System.Data.Common.DbTransaction tx, string table, CancellationToken ct)
    {
        var columns = (await conn.QueryAsync<string>(new CommandDefinition(
            $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid", transaction: tx, cancellationToken: ct)))
            .Where(c => c is not "id" and not "file_node_id")
            .ToList();
        var targets = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var values = string.Join(", ", columns.Select(c => c.EndsWith("directory_node_id", StringComparison.Ordinal)
            ? $"(SELECT nd.id FROM directory_nodes od JOIN directory_nodes nd ON nd.full_path = od.full_path " +
              $"AND nd.job_id = (SELECT job_id FROM file_nodes WHERE id = r.new_id) WHERE od.id = t.\"{c}\")"
            : $"t.\"{c}\""));
        return await conn.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO {table} (file_node_id, {targets})
            SELECT r.new_id, {values} FROM refresh_pairs r JOIN {table} t ON t.file_node_id = r.old_id
            WHERE NOT EXISTS (SELECT 1 FROM {table} x WHERE x.file_node_id = r.new_id)
            """, transaction: tx, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<FileChangeRecord>> GetChangesAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<ChangeRow>(new CommandDefinition(
            """
            SELECT job_id, previous_job_id, full_path, change, size, target_path, plan_id, moved_at
            FROM scan_changes WHERE job_id = @jobId ORDER BY full_path
            """, new { jobId }, cancellationToken: ct));
        return rows.Select(r => new FileChangeRecord(r.JobId, r.PreviousJobId ?? 0, r.FullPath,
            VerdictCodes.ParseNullable<FileChange>(r.Change)!.Value, r.Size, r.TargetPath, r.PlanId, Utc(r.MovedAt))).ToList();
    }

    public async Task AddNoticeEntriesAsync(IReadOnlyList<NoticeEntryRecord> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var e in entries)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO notice_entries (job_id, notice_path, source_path, target_path, plan_id, moved_at)
                VALUES (@JobId, @NoticePath, @SourcePath, @TargetPath, @PlanId, @MovedAt)
                """, e, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    public Task<IReadOnlyList<MoveRecord>> GetMovesFromAsync(string path, CancellationToken ct = default) =>
        GetMovesAsync("source_path", path, ct);

    public Task<IReadOnlyList<MoveRecord>> GetMovesToAsync(string path, CancellationToken ct = default) =>
        GetMovesAsync("target_path", path, ct);

    private async Task<IReadOnlyList<MoveRecord>> GetMovesAsync(string column, string path, CancellationToken ct)
    {
        var (exact, prefix) = Bounds(path);
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<MoveRow>(new CommandDefinition(
            $"""
            SELECT id, plan_id, action_id, source_path, target_path, hash, bytes, moved_at, returned_at, purged_at FROM moves
            WHERE {column} = @exact{Collate} OR substr({column}, 1, length(@prefix)) = @prefix{Collate}
            ORDER BY id
            """, new { exact, prefix }, cancellationToken: ct));
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<MoveRecord>> GetMovesByHashAsync(string hash, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<MoveRow>(new CommandDefinition(
            """
            SELECT id, plan_id, action_id, source_path, target_path, hash, bytes, moved_at, returned_at, purged_at FROM moves
            WHERE hash = @hash ORDER BY id
            """, new { hash = hash.ToLowerInvariant() }, cancellationToken: ct));
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<NoticeEntryRecord>> GetNoticeEntriesAsync(string path, CancellationToken ct = default)
    {
        var (exact, prefix) = Bounds(path);
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<NoticeRow>(new CommandDefinition(
            $"""
            SELECT job_id, notice_path, source_path, target_path, plan_id, moved_at FROM notice_entries
            WHERE source_path = @exact{Collate} OR substr(source_path, 1, length(@prefix)) = @prefix{Collate}
            ORDER BY id
            """, new { exact, prefix }, cancellationToken: ct));
        return rows.Select(r => new NoticeEntryRecord(r.JobId, r.NoticePath, r.SourcePath, r.TargetPath, r.PlanId, Utc(r.MovedAt)!.Value)).ToList();
    }

    public async Task<IReadOnlyList<SeenFile>> GetSeenAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<SeenRow>(new CommandDefinition(
            $"""
            SELECT f.job_id, f.full_path, f.size, f.last_write_time, f.scanned_at, j.role FROM file_nodes f
            JOIN scan_jobs j ON j.id = f.job_id
            WHERE f.full_path = @path{Collate}
            ORDER BY f.job_id DESC
            """, new { path = Normalize(path) }, cancellationToken: ct));
        return rows.Select(r => new SeenFile(r.JobId, r.FullPath, r.Size, Utc(r.LastWriteTime)!.Value, Utc(r.ScannedAt)!.Value,
            ScanRoleValues.Parse(r.Role))).ToList();
    }

    public async Task<IReadOnlyList<string>> GetPathsByHashAsync(string hash, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT full_path FROM content_fingerprints WHERE full_hash = @hash ORDER BY full_path",
            new { hash = hash.ToLowerInvariant() }, cancellationToken: ct))).ToList();
    }

    public async Task<string?> GetHashOfPathAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT full_hash FROM content_fingerprints WHERE full_path = @path{Collate} AND full_hash IS NOT NULL LIMIT 1",
            new { path = Normalize(path) }, cancellationToken: ct));
    }

    // ------------------------------------------------------------------ helpers

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The form a root is stored in: full, without a trailing separator (except for a drive root).</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return trimmed.Length == 0 ? full : trimmed;
    }

    private static (string Exact, string Prefix) Bounds(string path)
    {
        var exact = Normalize(path);
        return (exact, Path.EndsInDirectorySeparator(exact) ? exact : exact + Path.DirectorySeparatorChar);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return conn;
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private sealed class PlaceRow
    {
        public long Id { get; set; }
        public string RootPath { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string? VolumeLabel { get; set; }
        public string? VolumeId { get; set; }
        public string? VolumeFormat { get; set; }
        public long? LastJobId { get; set; }
        public DateTime? LastScanAt { get; set; }
        public long Files { get; set; }
        public long Bytes { get; set; }
        public DateTime FirstSeenAt { get; set; }
    }

    private sealed class GoneRow
    {
        public string FullPath { get; set; } = string.Empty;
        public long Size { get; set; }
    }

    private sealed class MoveRow
    {
        public long Id { get; set; }
        public long PlanId { get; set; }
        public long ActionId { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public string? TargetPath { get; set; }
        public string? Hash { get; set; }
        public long Bytes { get; set; }
        public DateTime MovedAt { get; set; }
        public DateTime? ReturnedAt { get; set; }
        public DateTime? PurgedAt { get; set; }

        public MoveRecord ToRecord() => new(Id, PlanId, ActionId, SourcePath, TargetPath, Hash, Bytes,
            DateTime.SpecifyKind(MovedAt, DateTimeKind.Utc), Utc(ReturnedAt), Utc(PurgedAt));
    }

    private sealed class ChangeRow
    {
        public long JobId { get; set; }
        public long? PreviousJobId { get; set; }
        public string FullPath { get; set; } = string.Empty;
        public string Change { get; set; } = string.Empty;
        public long Size { get; set; }
        public string? TargetPath { get; set; }
        public long? PlanId { get; set; }
        public DateTime? MovedAt { get; set; }
    }

    private sealed class NoticeRow
    {
        public long JobId { get; set; }
        public string NoticePath { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;
        public long PlanId { get; set; }
        public DateTime MovedAt { get; set; }
    }

    private sealed class SeenRow
    {
        public long JobId { get; set; }
        public string FullPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime LastWriteTime { get; set; }
        public DateTime ScannedAt { get; set; }
        public string Role { get; set; } = string.Empty;
    }
}
