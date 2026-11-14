using System.Text.Json;
using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>Stores plans in <c>plans</c> and <c>plan_actions</c>; enum values as their codes.</summary>
public class PlanRepository : IPlanRepository
{
    private const string ActionColumns = """
        id, plan_id, finding_key, finding_kind, unit, source_kind, source_path, relative_path, file_node_id,
        directory_node_id, files, bytes, kind, recommended, target_path, library_job_id, reason, note, origin, state,
        sensitive, single_decision, decided_by, decided_at, updated_at
        """;

    private readonly DatabaseContext _db;

    public PlanRepository(DatabaseContext db) => _db = db;

    public async Task<Plan> CreateAsync(Plan plan, IReadOnlyList<PlanAction> actions, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        plan.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO plans (job_id, created_at, libraries, notes) VALUES (@JobId, @CreatedAt, @Libraries, @Notes);
            SELECT last_insert_rowid();
            """,
            new { plan.JobId, plan.CreatedAt, Libraries = JsonSerializer.Serialize(plan.Libraries), Notes = JsonSerializer.Serialize(plan.Notes) },
            tx, cancellationToken: ct));

        foreach (var a in actions)
        {
            a.PlanId = plan.Id;
            a.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO plan_actions (plan_id, finding_key, finding_kind, unit, source_kind, source_path, relative_path,
                    file_node_id, directory_node_id, files, bytes, kind, recommended, target_path, library_job_id, reason, note,
                    origin, state, sensitive, single_decision, decided_by, decided_at, updated_at)
                VALUES (@PlanId, @FindingKey, @FindingKind, @Unit, @SourceKind, @SourcePath, @RelativePath,
                    @FileNodeId, @DirectoryNodeId, @Files, @Bytes, @Kind, @Recommended, @TargetPath, @LibraryJobId, @Reason, @Note,
                    @Origin, @State, @Sensitive, @SingleDecision, @DecidedBy, @DecidedAt, @UpdatedAt);
                SELECT last_insert_rowid();
                """,
                Params(a), tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return plan;
    }

    public async Task<Plan?> GetAsync(long planId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<PlanRow>(new CommandDefinition(
            "SELECT id, job_id, created_at, libraries, notes FROM plans WHERE id = @planId", new { planId }, cancellationToken: ct));
        return row?.ToPlan();
    }

    public async Task<IReadOnlyList<Plan>> GetByJobAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<PlanRow>(new CommandDefinition(
            "SELECT id, job_id, created_at, libraries, notes FROM plans WHERE job_id = @jobId ORDER BY id DESC", new { jobId }, cancellationToken: ct));
        return rows.Select(r => r.ToPlan()).ToList();
    }

    public async Task<IReadOnlyList<string>> GetWrittenForwardSourcesAsync(long planId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return (await conn.QueryAsync<string>(new CommandDefinition(
            """
            SELECT s.source_path FROM library_entries e JOIN plan_steps s ON s.id = e.step_id
            WHERE e.plan_id = @planId AND e.undone_at IS NULL AND e.comparison_id IS NOT NULL
            """, new { planId }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<PlanAction>> GetActionsAsync(long planId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<ActionRow>(new CommandDefinition(
            $"SELECT {ActionColumns} FROM plan_actions WHERE plan_id = @planId ORDER BY id", new { planId }, cancellationToken: ct));
        return rows.Select(r => r.ToAction()).ToList();
    }

    public async Task UpdateActionsAsync(IReadOnlyList<PlanAction> actions, CancellationToken ct = default)
    {
        if (actions.Count == 0) return;
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var a in actions)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE plan_actions SET kind = @Kind, target_path = @TargetPath, library_job_id = @LibraryJobId,
                    reason = @Reason, note = @Note, origin = @Origin, state = @State, decided_by = @DecidedBy,
                    decided_at = @DecidedAt, updated_at = @UpdatedAt
                WHERE id = @Id
                """,
                Params(a), tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    private static object Params(PlanAction a) => new
    {
        a.Id,
        a.PlanId,
        a.FindingKey,
        FindingKind = a.FindingKind.ToCode(),
        a.Unit,
        SourceKind = a.SourceKind.ToCode(),
        a.SourcePath,
        a.RelativePath,
        a.FileNodeId,
        a.DirectoryNodeId,
        a.Files,
        a.Bytes,
        Kind = a.Kind.ToCode(),
        Recommended = a.Recommended.ToCode(),
        a.TargetPath,
        a.LibraryJobId,
        a.Reason,
        a.Note,
        Origin = a.Origin.ToCode(),
        State = a.State.ToCode(),
        Sensitive = a.Sensitive ? 1 : 0,
        SingleDecision = a.SingleDecision ? 1 : 0,
        DecidedBy = a.DecidedBy?.ToCode(),
        a.DecidedAt,
        a.UpdatedAt
    };

    private sealed class PlanRow
    {
        public long Id { get; set; }
        public long JobId { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Libraries { get; set; } = "[]";
        public string Notes { get; set; } = "[]";

        public Plan ToPlan() => new()
        {
            Id = Id,
            JobId = JobId,
            CreatedAt = DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc),
            Libraries = JsonSerializer.Deserialize<List<long>>(Libraries) ?? [],
            Notes = JsonSerializer.Deserialize<List<string>>(Notes) ?? []
        };
    }

    private sealed class ActionRow
    {
        public long Id { get; set; }
        public long PlanId { get; set; }
        public string FindingKey { get; set; } = string.Empty;
        public string FindingKind { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public string SourceKind { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long? FileNodeId { get; set; }
        public long? DirectoryNodeId { get; set; }
        public long Files { get; set; }
        public long Bytes { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Recommended { get; set; } = string.Empty;
        public string? TargetPath { get; set; }
        public long? LibraryJobId { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string? Note { get; set; }
        public string Origin { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public long Sensitive { get; set; }
        public long SingleDecision { get; set; }
        public string? DecidedBy { get; set; }
        public DateTime? DecidedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public PlanAction ToAction() => new()
        {
            Id = Id,
            PlanId = PlanId,
            FindingKey = FindingKey,
            FindingKind = VerdictCodes.ParseNullable<FindingKind>(FindingKind)!.Value,
            Unit = Unit,
            SourceKind = VerdictCodes.ParseNullable<PlanSourceKind>(SourceKind)!.Value,
            SourcePath = SourcePath,
            RelativePath = RelativePath,
            FileNodeId = FileNodeId,
            DirectoryNodeId = DirectoryNodeId,
            Files = Files,
            Bytes = Bytes,
            Kind = VerdictCodes.ParseNullable<PlanActionKind>(Kind)!.Value,
            Recommended = VerdictCodes.ParseNullable<PlanActionKind>(Recommended)!.Value,
            TargetPath = TargetPath,
            LibraryJobId = LibraryJobId,
            Reason = Reason,
            Note = Note,
            Origin = VerdictCodes.ParseNullable<PlanActionOrigin>(Origin)!.Value,
            State = VerdictCodes.ParseNullable<PlanActionState>(State)!.Value,
            Sensitive = Sensitive != 0,
            SingleDecision = SingleDecision != 0,
            DecidedBy = VerdictCodes.ParseNullable<PlanActionOrigin>(DecidedBy),
            DecidedAt = DecidedAt is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null,
            UpdatedAt = DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)
        };
    }
}
