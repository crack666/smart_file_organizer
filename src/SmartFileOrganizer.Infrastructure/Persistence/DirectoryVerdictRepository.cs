using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// Stores directory verdicts in <c>directory_verdicts</c>. Enum values are written as their fixed
/// codes (<see cref="VerdictCodes"/>), the profile as snake_case JSON.
/// </summary>
public class DirectoryVerdictRepository : IDirectoryVerdictRepository
{
    /// <summary>JSON shape of <see cref="DirectoryProfile"/> in the database.</summary>
    public static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private const string SelectColumns = """
        SELECT dv.id, dv.directory_node_id, dv.verdict, dv.source, dv.rule, dv.purpose, dv.retention,
               dv.reason, dv.unit_directory_node_id, dv.profile_json, dv.model_used, dv.raw_response,
               dv.judged_at, dv.error, dv.escalated_from, dv.escalation_reason,
               dn.full_path AS directory_full_path, dn.relative_path AS directory_relative_path
        FROM directory_verdicts dv
        JOIN directory_nodes dn ON dn.id = dv.directory_node_id
        """;

    private readonly DatabaseContext _db;

    public DirectoryVerdictRepository(DatabaseContext db) => _db = db;

    public async Task UpsertAsync(DirectoryVerdict verdict, CancellationToken ct = default)
    {
        // Like the other analysis writes: finish a computed result even when the run is being stopped.
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(CancellationToken.None);

        var id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO directory_verdicts
                (directory_node_id, verdict, source, rule, purpose, retention, reason,
                 unit_directory_node_id, profile_json, model_used, raw_response, judged_at, error,
                 escalated_from, escalation_reason)
            VALUES
                (@DirectoryNodeId, @Verdict, @Source, @Rule, @Purpose, @Retention, @Reason,
                 @UnitDirectoryNodeId, @ProfileJson, @ModelUsed, @RawResponse, @JudgedAt, @Error,
                 @EscalatedFrom, @EscalationReason)
            ON CONFLICT(directory_node_id) DO UPDATE SET
                verdict                = excluded.verdict,
                source                 = excluded.source,
                rule                   = excluded.rule,
                purpose                = excluded.purpose,
                retention              = excluded.retention,
                reason                 = excluded.reason,
                unit_directory_node_id = excluded.unit_directory_node_id,
                profile_json           = excluded.profile_json,
                model_used             = excluded.model_used,
                raw_response           = excluded.raw_response,
                judged_at              = excluded.judged_at,
                error                  = excluded.error,
                escalated_from         = excluded.escalated_from,
                escalation_reason      = excluded.escalation_reason
            RETURNING id
            """,
            ToRow(verdict));
        verdict.Id = id;
    }

    public async Task<DirectoryVerdict?> GetByNodeIdAsync(long directoryNodeId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            SelectColumns + " WHERE dv.directory_node_id = @directoryNodeId", new { directoryNodeId });
        return row is null ? null : FromRow(row);
    }

    public async Task<DirectoryVerdict?> GetByDirectoryPathAsync(long jobId, string fullPath, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            SelectColumns + " WHERE dn.job_id = @jobId AND dn.full_path = @fullPath", new { jobId, fullPath });
        return row is null ? null : FromRow(row);
    }

    public async Task<IReadOnlyList<DirectoryVerdict>> GetForJobAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(
            SelectColumns + " WHERE dn.job_id = @jobId ORDER BY dn.full_path", new { jobId });
        return rows.Select(FromRow).ToList();
    }

    private static Row ToRow(DirectoryVerdict v) => new()
    {
        DirectoryNodeId = v.DirectoryNodeId,
        Verdict = v.Verdict.ToCode(),
        Source = v.Source.ToCode(),
        Rule = v.Rule,
        Purpose = v.Purpose,
        Retention = v.Retention.ToCode(),
        Reason = v.Reason,
        UnitDirectoryNodeId = v.UnitDirectoryNodeId,
        ProfileJson = JsonSerializer.Serialize(v.Profile, ProfileJsonOptions),
        ModelUsed = v.ModelUsed,
        RawResponse = v.RawResponse,
        JudgedAt = v.JudgedAt,
        Error = v.Error,
        EscalatedFrom = v.EscalatedFrom.ToCode(),
        EscalationReason = v.EscalationReason
    };

    private static DirectoryVerdict FromRow(Row r) => new()
    {
        Id = r.Id,
        DirectoryNodeId = r.DirectoryNodeId,
        Verdict = VerdictCodes.ParseNullable<DirectoryVerdictKind>(r.Verdict),
        Source = VerdictCodes.ParseNullable<VerdictSource>(r.Source) ?? VerdictSource.Modell,
        Rule = r.Rule,
        Purpose = r.Purpose,
        Retention = VerdictCodes.ParseNullable<RetentionValue>(r.Retention),
        Reason = r.Reason,
        UnitDirectoryNodeId = r.UnitDirectoryNodeId,
        Profile = string.IsNullOrWhiteSpace(r.ProfileJson)
            ? new DirectoryProfile()
            : JsonSerializer.Deserialize<DirectoryProfile>(r.ProfileJson, ProfileJsonOptions) ?? new DirectoryProfile(),
        ModelUsed = r.ModelUsed,
        RawResponse = r.RawResponse,
        JudgedAt = r.JudgedAt,
        Error = r.Error,
        EscalatedFrom = VerdictCodes.ParseNullable<DirectoryVerdictKind>(r.EscalatedFrom),
        EscalationReason = r.EscalationReason,
        DirectoryFullPath = r.DirectoryFullPath,
        DirectoryRelativePath = r.DirectoryRelativePath
    };

    /// <summary>Database shape: codes as strings, profile as JSON.</summary>
    private sealed class Row
    {
        public long Id { get; set; }
        public long DirectoryNodeId { get; set; }
        public string? Verdict { get; set; }
        public string Source { get; set; } = string.Empty;
        public string? Rule { get; set; }
        public string? Purpose { get; set; }
        public string? Retention { get; set; }
        public string? Reason { get; set; }
        public long? UnitDirectoryNodeId { get; set; }
        public string ProfileJson { get; set; } = "{}";
        public string? ModelUsed { get; set; }
        public string? RawResponse { get; set; }
        public DateTime JudgedAt { get; set; }
        public string? Error { get; set; }
        public string? EscalatedFrom { get; set; }
        public string? EscalationReason { get; set; }
        public string? DirectoryFullPath { get; set; }
        public string? DirectoryRelativePath { get; set; }
    }
}
