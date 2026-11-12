using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// Stores samples of uniform directories in <c>directory_samples</c>. Each state change runs in one
/// transaction with the file statuses and results it implies, so a stopped run never leaves a directory
/// half inherited. Writes are not cancelled mid-flight, like the other analysis writes.
/// </summary>
public class DirectorySampleRepository : IDirectorySampleRepository
{
    /// <summary>JSON shape of the member list: snake_case, roles and categories as strings.</summary>
    public static readonly JsonSerializerOptions MembersJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Ids per statement; keeps every statement far below the SQLite parameter limit.</summary>
    private const int IdChunk = 500;

    private const string SelectColumns = """
        SELECT s.id, s.directory_node_id, s.seed, s.state, s.eligible_count, s.members_json,
               s.max_deviation_share, s.majority_category, s.deviating_count, s.failed_count,
               s.deviation_share, s.inherited_count, s.inherited_confidence, s.reason,
               s.drawn_at, s.decided_at,
               dn.full_path AS directory_full_path, dn.relative_path AS directory_relative_path
        FROM directory_samples s
        JOIN directory_nodes dn ON dn.id = s.directory_node_id
        """;

    private const string UpsertSql = """
        INSERT INTO directory_samples
            (directory_node_id, seed, state, eligible_count, members_json, max_deviation_share,
             majority_category, deviating_count, failed_count, deviation_share, inherited_count,
             inherited_confidence, reason, drawn_at, decided_at)
        VALUES
            (@DirectoryNodeId, @Seed, @State, @EligibleCount, @MembersJson, @MaxDeviationShare,
             @MajorityCategory, @DeviatingCount, @FailedCount, @DeviationShare, @InheritedCount,
             @InheritedConfidence, @Reason, @DrawnAt, @DecidedAt)
        ON CONFLICT(directory_node_id) DO UPDATE SET
            seed                 = excluded.seed,
            state                = excluded.state,
            eligible_count       = excluded.eligible_count,
            members_json         = excluded.members_json,
            max_deviation_share  = excluded.max_deviation_share,
            majority_category    = excluded.majority_category,
            deviating_count      = excluded.deviating_count,
            failed_count         = excluded.failed_count,
            deviation_share      = excluded.deviation_share,
            inherited_count      = excluded.inherited_count,
            inherited_confidence = excluded.inherited_confidence,
            reason               = excluded.reason,
            drawn_at             = excluded.drawn_at,
            decided_at           = excluded.decided_at
        RETURNING id
        """;

    /// <summary>Deferred files of one directory, found through its node.</summary>
    private const string ReleaseDeferredSql = """
        UPDATE file_nodes SET status = @discovered
        WHERE status = @deferred
          AND job_id = (SELECT job_id FROM directory_nodes WHERE id = @dirId)
          AND parent_path = (SELECT full_path FROM directory_nodes WHERE id = @dirId)
        """;

    private readonly DatabaseContext _db;

    public DirectorySampleRepository(DatabaseContext db) => _db = db;

    public async Task CreateAsync(DirectorySample sample, IReadOnlyList<long> deferredFileIds, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(CancellationToken.None);
        await using var tx = await conn.BeginTransactionAsync(CancellationToken.None);

        sample.Id = await conn.ExecuteScalarAsync<long>(UpsertSql, ToRow(sample), tx);
        foreach (var chunk in deferredFileIds.Chunk(IdChunk))
        {
            await conn.ExecuteAsync(
                "UPDATE file_nodes SET status = @deferred WHERE id IN @ids AND status = @discovered",
                new { deferred = (int)FileNodeStatus.Deferred, discovered = (int)FileNodeStatus.Discovered, ids = chunk }, tx);
        }

        await tx.CommitAsync(CancellationToken.None);
    }

    public async Task<int> ConfirmAsync(DirectorySample sample, IReadOnlyList<FileClassification> inherited, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(CancellationToken.None);
        await using var tx = await conn.BeginTransactionAsync(CancellationToken.None);

        var count = 0;
        foreach (var c in inherited)
        {
            // Only files still waiting: a file judged in the meantime (focused rescan) keeps its own result.
            var claimed = await conn.ExecuteAsync(
                "UPDATE file_nodes SET status = @inheritedStatus WHERE id = @id AND status = @deferred",
                new { inheritedStatus = (int)FileNodeStatus.Inherited, id = c.FileNodeId, deferred = (int)FileNodeStatus.Deferred }, tx);
            if (claimed == 0) continue;

            await conn.ExecuteAsync(ClassificationRepository.UpsertSql, ClassificationRepository.ToParameters(c), tx);
            count++;
        }

        sample.InheritedCount = count;
        await conn.ExecuteScalarAsync<long>(UpsertSql, ToRow(sample), tx);
        await tx.CommitAsync(CancellationToken.None);
        return count;
    }

    public async Task EscalateAsync(DirectorySample sample, string escalationReason, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(CancellationToken.None);
        await using var tx = await conn.BeginTransactionAsync(CancellationToken.None);

        sample.Id = await conn.ExecuteScalarAsync<long>(UpsertSql, ToRow(sample), tx);
        await conn.ExecuteAsync(
            """
            UPDATE directory_verdicts
            SET escalated_from = verdict, verdict = @mixed, escalation_reason = @escalationReason
            WHERE directory_node_id = @dirId AND verdict = @uniform
            """,
            new
            {
                mixed = DirectoryVerdictKind.Gemischt.ToCode(),
                uniform = DirectoryVerdictKind.Einheitlich.ToCode(),
                escalationReason,
                dirId = sample.DirectoryNodeId
            }, tx);
        await conn.ExecuteAsync(ReleaseDeferredSql, ReleaseParameters(sample.DirectoryNodeId), tx);

        // A summary written while the directory was uniform no longer fits; Phase 3 writes a new one.
        await conn.ExecuteAsync(
            "DELETE FROM ai_directory_results WHERE directory_node_id = @dirId AND phase = 'summary'",
            new { dirId = sample.DirectoryNodeId }, tx);

        await tx.CommitAsync(CancellationToken.None);
    }

    public async Task DeleteAsync(long directoryNodeId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(CancellationToken.None);
        await using var tx = await conn.BeginTransactionAsync(CancellationToken.None);

        await conn.ExecuteAsync(ReleaseDeferredSql, ReleaseParameters(directoryNodeId), tx);
        await conn.ExecuteAsync("DELETE FROM directory_samples WHERE directory_node_id = @directoryNodeId", new { directoryNodeId }, tx);

        await tx.CommitAsync(CancellationToken.None);
    }

    public async Task<DirectorySample?> GetByNodeIdAsync(long directoryNodeId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            SelectColumns + " WHERE s.directory_node_id = @directoryNodeId", new { directoryNodeId });
        return row is null ? null : FromRow(row);
    }

    public async Task<IReadOnlyList<DirectorySample>> GetForJobAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(SelectColumns + " WHERE dn.job_id = @jobId ORDER BY dn.full_path", new { jobId });
        return rows.Select(FromRow).ToList();
    }

    private static object ReleaseParameters(long dirId) => new
    {
        discovered = (int)FileNodeStatus.Discovered,
        deferred = (int)FileNodeStatus.Deferred,
        dirId
    };

    private static Row ToRow(DirectorySample s) => new()
    {
        DirectoryNodeId = s.DirectoryNodeId,
        Seed = s.Seed,
        State = s.State.ToCode(),
        EligibleCount = s.EligibleCount,
        MembersJson = JsonSerializer.Serialize(s.Members, MembersJsonOptions),
        MaxDeviationShare = s.MaxDeviationShare,
        MajorityCategory = s.MajorityCategory?.ToString(),
        DeviatingCount = s.DeviatingCount,
        FailedCount = s.FailedCount,
        DeviationShare = s.DeviationShare,
        InheritedCount = s.InheritedCount,
        InheritedConfidence = s.InheritedConfidence,
        Reason = s.Reason,
        DrawnAt = s.DrawnAt,
        DecidedAt = s.DecidedAt
    };

    private static DirectorySample FromRow(Row r) => new()
    {
        Id = r.Id,
        DirectoryNodeId = r.DirectoryNodeId,
        Seed = r.Seed,
        State = VerdictCodes.ParseNullable<SampleState>(r.State) ?? SampleState.Open,
        EligibleCount = r.EligibleCount,
        Members = JsonSerializer.Deserialize<List<SampleMember>>(r.MembersJson, MembersJsonOptions) ?? [],
        MaxDeviationShare = r.MaxDeviationShare,
        MajorityCategory = Enum.TryParse<FileCategory>(r.MajorityCategory, out var c) ? c : null,
        DeviatingCount = r.DeviatingCount,
        FailedCount = r.FailedCount,
        DeviationShare = r.DeviationShare,
        InheritedCount = r.InheritedCount,
        InheritedConfidence = r.InheritedConfidence,
        Reason = r.Reason,
        DrawnAt = r.DrawnAt,
        DecidedAt = r.DecidedAt,
        DirectoryFullPath = r.DirectoryFullPath,
        DirectoryRelativePath = r.DirectoryRelativePath
    };

    /// <summary>Database shape: codes as strings, members as JSON.</summary>
    private sealed class Row
    {
        public long Id { get; set; }
        public long DirectoryNodeId { get; set; }
        public string Seed { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public int EligibleCount { get; set; }
        public string MembersJson { get; set; } = "[]";
        public double MaxDeviationShare { get; set; }
        public string? MajorityCategory { get; set; }
        public int DeviatingCount { get; set; }
        public int FailedCount { get; set; }
        public double? DeviationShare { get; set; }
        public int InheritedCount { get; set; }
        public double? InheritedConfidence { get; set; }
        public string? Reason { get; set; }
        public DateTime DrawnAt { get; set; }
        public DateTime? DecidedAt { get; set; }
        public string? DirectoryFullPath { get; set; }
        public string? DirectoryRelativePath { get; set; }
    }
}
