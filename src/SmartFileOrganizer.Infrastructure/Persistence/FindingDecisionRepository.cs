using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>Stores decisions about findings in <c>finding_decisions</c>; enum values as their codes.</summary>
public class FindingDecisionRepository : IFindingDecisionRepository
{
    private readonly DatabaseContext _db;

    public FindingDecisionRepository(DatabaseContext db) => _db = db;

    public async Task<IReadOnlyDictionary<string, FindingDecision>> GetAllAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT job_id, finding_key, kind, state, origin, note, decided_at, snapshot_json
            FROM finding_decisions WHERE job_id = @jobId
            """,
            new { jobId }, cancellationToken: ct));
        return rows.Select(r => new FindingDecision
        {
            JobId = r.JobId,
            FindingKey = r.FindingKey,
            Kind = VerdictCodes.ParseNullable<FindingKind>(r.Kind)!.Value,
            State = VerdictCodes.ParseNullable<FindingState>(r.State)!.Value,
            Origin = VerdictCodes.ParseNullable<ResultOrigin>(r.Origin)!.Value,
            Note = r.Note,
            DecidedAt = r.DecidedAt,
            SnapshotJson = r.SnapshotJson
        }).ToDictionary(d => d.FindingKey, StringComparer.Ordinal);
    }

    public async Task UpsertAsync(FindingDecision decision, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO finding_decisions (job_id, finding_key, kind, state, origin, note, decided_at, snapshot_json)
            VALUES (@JobId, @FindingKey, @Kind, @State, @Origin, @Note, @DecidedAt, @SnapshotJson)
            ON CONFLICT(job_id, finding_key) DO UPDATE SET
                kind          = excluded.kind,
                state         = excluded.state,
                origin        = excluded.origin,
                note          = excluded.note,
                decided_at    = excluded.decided_at,
                snapshot_json = excluded.snapshot_json
            """,
            new
            {
                decision.JobId,
                decision.FindingKey,
                Kind = decision.Kind.ToCode(),
                State = decision.State.ToCode(),
                Origin = decision.Origin.ToCode(),
                decision.Note,
                decision.DecidedAt,
                decision.SnapshotJson
            },
            cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(long jobId, string findingKey, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM finding_decisions WHERE job_id = @jobId AND finding_key = @findingKey",
            new { jobId, findingKey }, cancellationToken: ct));
        return n > 0;
    }

    private sealed class Row
    {
        public long JobId { get; set; }
        public string FindingKey { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public string? Note { get; set; }
        public DateTime DecidedAt { get; set; }
        public string SnapshotJson { get; set; } = "{}";
    }
}
