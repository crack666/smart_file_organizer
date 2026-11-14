using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

public class ContentComparisonRepository : IContentComparisonRepository
{
    /// <summary>Keeps <c>IN (...)</c> lists well below SQLite's parameter limit.</summary>
    private const int ChunkSize = 500;

    private const string FileColumns = "id, full_path, name, parent_path, size";

    private readonly DatabaseContext _db;

    public ContentComparisonRepository(DatabaseContext db) => _db = db;

    public async Task<IReadOnlyList<ComparableFile>> GetFilesAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<FileRow>(new CommandDefinition(
            $"SELECT {FileColumns} FROM file_nodes WHERE job_id = @jobId ORDER BY full_path",
            new { jobId }, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<ComparableFile>> GetFilesWithSourceSizesAsync(long againstJobId, long sourceJobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<FileRow>(new CommandDefinition(
            $"""
            SELECT {FileColumns} FROM file_nodes
            WHERE job_id = @againstJobId
              AND size IN (SELECT DISTINCT size FROM file_nodes WHERE job_id = @sourceJobId)
            ORDER BY full_path
            """,
            new { againstJobId, sourceJobId }, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<ComparableFile>> GetFilesWithSourceNamesAsync(long againstJobId, long sourceJobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<FileRow>(new CommandDefinition(
            $"""
            SELECT {FileColumns} FROM file_nodes
            WHERE job_id = @againstJobId
              AND name COLLATE NOCASE IN (SELECT name FROM file_nodes WHERE job_id = @sourceJobId)
            ORDER BY full_path
            """,
            new { againstJobId, sourceJobId }, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<string>> GetSkippedDirectoriesAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT full_path FROM directory_nodes WHERE job_id = @jobId AND dir_status = @skip ORDER BY full_path",
            new { jobId, skip = (int)DirectoryStatus.Skip }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ScanDirectoryError>> GetDirectoryErrorsAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string FullPath, string? DirReason)>(new CommandDefinition(
            "SELECT full_path, dir_reason FROM directory_nodes WHERE job_id = @jobId AND dir_status = @error ORDER BY full_path",
            new { jobId, error = (int)DirectoryStatus.Error }, cancellationToken: ct));
        return rows.Select(r => new ScanDirectoryError(r.FullPath, r.DirReason)).ToList();
    }

    public async Task<IReadOnlyDictionary<string, ContentFingerprint>> GetFingerprintsAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ContentFingerprint>(StringComparer.Ordinal);
        if (paths.Count == 0)
            return result;

        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        foreach (var chunk in paths.Distinct(StringComparer.Ordinal).Chunk(ChunkSize))
        {
            var rows = await conn.QueryAsync<FingerprintRow>(new CommandDefinition(
                """
                SELECT full_path, size, last_write_ticks, sample_hash, full_hash, computed_at, full_hash_at
                FROM content_fingerprints WHERE full_path IN @paths
                """,
                new { paths = chunk }, cancellationToken: ct));
            foreach (var row in rows)
            {
                result[row.FullPath] = new ContentFingerprint(
                    row.FullPath, row.Size, row.LastWriteTicks, row.SampleHash, row.FullHash, row.ComputedAt, row.FullHashAt);
            }
        }

        return result;
    }

    public async Task UpsertFingerprintsAsync(IReadOnlyCollection<ContentFingerprint> fingerprints, CancellationToken ct = default)
    {
        if (fingerprints.Count == 0)
            return;

        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO content_fingerprints (full_path, size, last_write_ticks, sample_hash, full_hash, computed_at, full_hash_at)
            VALUES (@FullPath, @Size, @LastWriteTicks, @SampleHash, @FullHash, @ComputedAt, @FullHashAt)
            ON CONFLICT(full_path) DO UPDATE SET
                size = excluded.size,
                last_write_ticks = excluded.last_write_ticks,
                sample_hash = excluded.sample_hash,
                full_hash = excluded.full_hash,
                computed_at = excluded.computed_at,
                full_hash_at = excluded.full_hash_at
            """,
            fingerprints, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<long> SaveComparisonAsync(ComparisonHeader header, IReadOnlyList<FileComparison> files, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM comparison_files WHERE comparison_id IN (
                SELECT id FROM comparisons WHERE source_job_id = @SourceJobId AND against_job_id IS @AgainstJobId);
            DELETE FROM comparisons WHERE source_job_id = @SourceJobId AND against_job_id IS @AgainstJobId;
            """,
            new { header.SourceJobId, header.AgainstJobId }, transaction: tx, cancellationToken: ct));

        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO comparisons (source_job_id, against_job_id, compared_at, complete, error_count, run_id)
            VALUES (@SourceJobId, @AgainstJobId, @ComparedAt, @Complete, @ErrorCount, @RunId);
            UPDATE comparisons SET run_id = id WHERE id = last_insert_rowid() AND run_id IS NULL;
            SELECT id FROM comparisons WHERE rowid = last_insert_rowid();
            """,
            new { header.SourceJobId, header.AgainstJobId, header.ComparedAt, Complete = header.Complete ? 1 : 0, header.ErrorCount, header.RunId },
            transaction: tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO comparison_files
                (comparison_id, file_node_id, state, match_file_node_id, match_count, duplicate_group, error,
                 match_hash, match_verified_at)
            VALUES
                (@ComparisonId, @FileNodeId, @State, @MatchFileNodeId, @MatchCount, @DuplicateGroup, @Error,
                 @MatchHash, @MatchVerifiedAt)
            """,
            files.Select(f => new
            {
                ComparisonId = id,
                f.FileNodeId,
                State = f.State?.ToValue(),
                f.MatchFileNodeId,
                f.MatchCount,
                f.DuplicateGroup,
                f.Error,
                f.MatchHash,
                f.MatchVerifiedAt
            }),
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<ComparisonHeader?> GetComparisonAsync(long sourceJobId, long? againstJobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<HeaderRow>(new CommandDefinition(
            """
            SELECT id, source_job_id, against_job_id, compared_at, complete, error_count, run_id
            FROM comparisons WHERE source_job_id = @sourceJobId AND against_job_id IS @againstJobId
            """,
            new { sourceJobId, againstJobId }, cancellationToken: ct));
        return row == null
            ? null
            : row.ToModel();
    }

    public async Task<ComparisonHeader?> GetLatestWithLibraryAsync(long sourceJobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<HeaderRow>(new CommandDefinition(
            """
            SELECT id, source_job_id, against_job_id, compared_at, complete, error_count, run_id
            FROM comparisons WHERE source_job_id = @sourceJobId AND against_job_id IS NOT NULL
            ORDER BY compared_at DESC, id DESC LIMIT 1
            """,
            new { sourceJobId }, cancellationToken: ct));
        return row == null
            ? null
            : row.ToModel();
    }

    public async Task<IReadOnlyList<ComparisonHeader>> GetLatestLibraryRunAsync(long sourceJobId, CancellationToken ct = default)
    {
        var latest = await GetLatestWithLibraryAsync(sourceJobId, ct);
        if (latest is null)
            return [];

        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<HeaderRow>(new CommandDefinition(
            """
            SELECT id, source_job_id, against_job_id, compared_at, complete, error_count, run_id
            FROM comparisons
            WHERE source_job_id = @sourceJobId AND against_job_id IS NOT NULL AND COALESCE(run_id, id) = @run
            ORDER BY id
            """,
            new { sourceJobId, run = latest.RunId ?? latest.Id }, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<FileComparison>> GetFileComparisonsAsync(long comparisonId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<FileComparisonRow>(new CommandDefinition(
            """
            SELECT cf.file_node_id, f.full_path, f.size, cf.state, cf.match_file_node_id,
                   m.full_path AS match_path, cf.match_count, cf.duplicate_group, cf.error,
                   cf.match_hash, cf.match_verified_at, m.job_id AS match_job_id
            FROM comparison_files cf
            JOIN file_nodes f ON f.id = cf.file_node_id
            LEFT JOIN file_nodes m ON m.id = cf.match_file_node_id
            WHERE cf.comparison_id = @comparisonId
            ORDER BY f.full_path
            """,
            new { comparisonId }, cancellationToken: ct));
        return rows.Select(r => new FileComparison(
            r.FileNodeId, r.FullPath, r.Size,
            r.State == null ? null : ContentStateValues.Parse(r.State),
            r.MatchFileNodeId, r.MatchPath, (int)r.MatchCount,
            r.DuplicateGroup == null ? null : (int)r.DuplicateGroup.Value,
            r.Error, r.MatchHash, r.MatchVerifiedAt, r.MatchJobId)).ToList();
    }

    public async Task<(int Files, int Directories)> SetAnalysisSkipAsync(long jobId, IReadOnlyCollection<long> fileIds,
        IReadOnlyCollection<long> directoryIds, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var statuses = new
        {
            jobId,
            reason = ContentStateValues.BackedUp,
            skipped = (int)FileNodeStatus.Skipped,
            discovered = (int)FileNodeStatus.Discovered,
            deferred = (int)FileNodeStatus.Deferred
        };

        await conn.ExecuteAsync(
            """
            UPDATE file_nodes SET status = CASE WHEN status = @skipped THEN @discovered ELSE status END, analysis_skip = NULL
            WHERE job_id = @jobId AND analysis_skip IS NOT NULL
            """, statuses, tx);
        await conn.ExecuteAsync(
            "UPDATE directory_nodes SET analysis_skip = NULL WHERE job_id = @jobId AND analysis_skip IS NOT NULL",
            new { jobId }, tx);

        var files = 0;
        foreach (var chunk in fileIds.Chunk(ChunkSize))
        {
            files += await conn.ExecuteAsync(
                """
                UPDATE file_nodes SET status = @skipped, analysis_skip = @reason
                WHERE job_id = @jobId AND id IN @ids AND status IN (@discovered, @deferred)
                """, new { statuses.jobId, statuses.reason, statuses.skipped, statuses.discovered, statuses.deferred, ids = chunk }, tx);
        }

        var directories = 0;
        foreach (var chunk in directoryIds.Chunk(ChunkSize))
        {
            directories += await conn.ExecuteAsync(
                "UPDATE directory_nodes SET analysis_skip = @reason WHERE job_id = @jobId AND id IN @ids",
                new { jobId, reason = ContentStateValues.BackedUp, ids = chunk }, tx);
        }

        await tx.CommitAsync(ct);
        return (files, directories);
    }

    // Dapper row types: plain settable properties, so mapping does not depend on constructor matching.

    private sealed class FileRow
    {
        public long Id { get; set; }
        public string FullPath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ParentPath { get; set; } = string.Empty;
        public long Size { get; set; }

        public ComparableFile ToModel() => new(Id, FullPath, Name, ParentPath, Size);
    }

    private sealed class FingerprintRow
    {
        public string FullPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public long LastWriteTicks { get; set; }
        public string? SampleHash { get; set; }
        public string? FullHash { get; set; }
        public DateTime ComputedAt { get; set; }
        public DateTime? FullHashAt { get; set; }
    }

    private sealed class HeaderRow
    {
        public long Id { get; set; }
        public long SourceJobId { get; set; }
        public long? AgainstJobId { get; set; }
        public DateTime ComparedAt { get; set; }
        public long Complete { get; set; }
        public long ErrorCount { get; set; }
        public long? RunId { get; set; }

        public ComparisonHeader ToModel() =>
            new(Id, SourceJobId, AgainstJobId, ComparedAt, Complete != 0, (int)ErrorCount, RunId ?? Id);
    }

    private sealed class FileComparisonRow
    {
        public long FileNodeId { get; set; }
        public string FullPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public string? State { get; set; }
        public long? MatchFileNodeId { get; set; }
        public string? MatchPath { get; set; }
        public long MatchCount { get; set; }
        public long? DuplicateGroup { get; set; }
        public string? Error { get; set; }
        public string? MatchHash { get; set; }
        public DateTime? MatchVerifiedAt { get; set; }
        public long? MatchJobId { get; set; }
    }
}
