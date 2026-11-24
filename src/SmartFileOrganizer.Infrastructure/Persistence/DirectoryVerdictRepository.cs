using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// Stores directory verdicts in <c>directory_verdicts</c>. Enum values are written as their fixed
/// codes (<see cref="VerdictCodes"/>), the profile as snake_case JSON. Reading puts the correction of a person
/// (<c>directory_overrides</c>) over the stored verdict; writing never touches the correction.
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
               dn.id AS node_id, dn.full_path AS directory_full_path, dn.relative_path AS directory_relative_path,
               dov.verdict AS override_verdict, dov.retention AS override_retention, dov.note AS override_note,
               dov.overridden_at AS override_at
        FROM directory_nodes dn
        LEFT JOIN directory_verdicts dv ON dv.directory_node_id = dn.id
        LEFT JOIN directory_overrides dov ON dov.directory_node_id = dn.id
        WHERE (dv.id IS NOT NULL OR dov.id IS NOT NULL)
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
            SelectColumns + " AND dn.id = @directoryNodeId", new { directoryNodeId });
        return row is null ? null : FromRow(row);
    }

    public async Task<DirectoryVerdict?> GetByDirectoryPathAsync(long jobId, string fullPath, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            SelectColumns + " AND dn.job_id = @jobId AND dn.full_path = @fullPath", new { jobId, fullPath });
        return row is null ? null : FromRow(row);
    }

    public async Task<IReadOnlyList<DirectoryVerdict>> GetForJobAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(
            SelectColumns + " AND dn.job_id = @jobId ORDER BY dn.full_path", new { jobId });
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

    /// <summary>The verdict that counts: the stored one, or the correction of a person over it.</summary>
    private static DirectoryVerdict FromRow(Row r)
    {
        var stored = r.Id is null ? null : Stored(r);
        if (VerdictCodes.ParseNullable<DirectoryVerdictKind>(r.OverrideVerdict) is not { } verdict)
            return stored!;

        var correction = new DirectoryOverride
        {
            DirectoryNodeId = r.NodeId,
            Verdict = verdict,
            Retention = VerdictCodes.ParseNullable<RetentionValue>(r.OverrideRetention),
            Note = r.OverrideNote,
            OverriddenAt = r.OverrideAt ?? default
        };
        return new DirectoryVerdict
        {
            Id = stored?.Id ?? 0,
            DirectoryNodeId = r.NodeId,
            Verdict = verdict,
            Source = VerdictSource.Human,
            Purpose = stored?.Purpose,
            Retention = correction.Retention ?? stored?.Retention,
            // The stored reason argues for the verdict that no longer counts; see Replaced.
            Reason = correction.Note,
            UnitDirectoryNodeId = verdict == DirectoryVerdictKind.Unit ? r.NodeId : null,
            Profile = stored?.Profile ?? new DirectoryProfile(),
            JudgedAt = correction.OverriddenAt,
            DirectoryFullPath = r.DirectoryFullPath,
            DirectoryRelativePath = r.DirectoryRelativePath,
            Override = correction,
            Replaced = stored
        };
    }

    private static DirectoryVerdict Stored(Row r) => new()
    {
        Id = r.Id ?? 0,
        DirectoryNodeId = r.DirectoryNodeId,
        Verdict = VerdictCodes.ParseNullable<DirectoryVerdictKind>(r.Verdict),
        Source = VerdictCodes.ParseNullable<VerdictSource>(r.Source) ?? VerdictSource.Model,
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
        public long? Id { get; set; }
        public long NodeId { get; set; }
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
        public string? OverrideVerdict { get; set; }
        public string? OverrideRetention { get; set; }
        public string? OverrideNote { get; set; }
        public DateTime? OverrideAt { get; set; }
    }
}
