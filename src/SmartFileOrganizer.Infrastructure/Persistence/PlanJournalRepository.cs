using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// The journal of the execution in <c>plan_steps</c>, what the scan saw of a source (<c>file_nodes</c>), and
/// <c>scan_jobs.keep_source</c>. Each call is its own short transaction, so a step that is started is on disk before
/// the file system is touched.
/// </summary>
public class PlanJournalRepository : IPlanJournal
{
    private const string Columns =
        "id, plan_id, action_id, kind, state, source_path, target_path, hold_path, bytes, hash, detail, started_at, finished_at";

    private readonly DatabaseContext _db;

    public PlanJournalRepository(DatabaseContext db) => _db = db;

    public async Task<PlanStep> StartAsync(PlanStep step, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        step.State = PlanStepState.Started;
        step.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO plan_steps (plan_id, action_id, kind, state, source_path, target_path, hold_path, bytes, hash, detail, started_at)
            VALUES (@PlanId, @ActionId, @Kind, @State, @SourcePath, @TargetPath, @HoldPath, @Bytes, @Hash, @Detail, @StartedAt);
            SELECT last_insert_rowid();
            """,
            Params(step), cancellationToken: ct));
        return step;
    }

    public async Task FinishAsync(PlanStep step, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE plan_steps SET state = @State, target_path = @TargetPath, hold_path = @HoldPath, bytes = @Bytes,
                hash = @Hash, detail = @Detail, finished_at = @FinishedAt
            WHERE id = @Id
            """,
            Params(step), tx, cancellationToken: ct));

        // The move history (https://github.com/crack666/smart_file_organizer/issues/86) is written with the step that
        // moves an original, in the same transaction, and outlives the plan and the job.
        if (step.State == PlanStepState.Done)
        {
            var sql = step.Kind switch
            {
                PlanStepKind.Hold => """
                    INSERT INTO moves (plan_id, action_id, source_path, target_path, hash, bytes, moved_at)
                    VALUES (@PlanId, @ActionId, @SourcePath, @TargetPath, @Hash, @Bytes, @FinishedAt)
                    """,
                PlanStepKind.Restore => """
                    UPDATE moves SET returned_at = @FinishedAt
                    WHERE plan_id = @PlanId AND action_id = @ActionId AND source_path = @SourcePath AND returned_at IS NULL
                    """,
                PlanStepKind.Purge => """
                    UPDATE moves SET purged_at = @FinishedAt
                    WHERE plan_id = @PlanId AND action_id = @ActionId AND source_path = @SourcePath AND returned_at IS NULL AND purged_at IS NULL
                    """,
                _ => null
            };
            if (sql is not null)
                await conn.ExecuteAsync(new CommandDefinition(sql, Params(step), tx, cancellationToken: ct));

            // The holding of the library is written forward with the copy and taken back with it.
            if (step.Kind is PlanStepKind.Copy or PlanStepKind.Present)
                await LibraryLedger.RecordAsync(conn, tx, step, ct);
            else if (step.Kind == PlanStepKind.Uncopy)
                await LibraryLedger.RevertAsync(conn, tx, step, ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<PlanStep>> GetStepsAsync(long planId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<StepRow>(new CommandDefinition(
            $"SELECT {Columns} FROM plan_steps WHERE plan_id = @planId ORDER BY id", new { planId }, cancellationToken: ct));
        return rows.Select(r => r.ToStep()).ToList();
    }

    public async Task<IReadOnlyList<ScannedFile>> GetScannedFilesAsync(long jobId, string path, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var prefix = trimmed + Path.DirectorySeparatorChar;
        var rows = await conn.QueryAsync<ScannedRow>(new CommandDefinition(
            """
            SELECT full_path, size, last_write_time FROM file_nodes
            WHERE job_id = @jobId AND (full_path = @trimmed OR substr(full_path, 1, length(@prefix)) = @prefix)
            ORDER BY full_path
            """,
            new { jobId, trimmed, prefix }, cancellationToken: ct));
        return rows.Select(r => new ScannedFile(r.FullPath, r.Size, DateTime.SpecifyKind(r.LastWriteTime, DateTimeKind.Utc))).ToList();
    }

    public async Task<bool> GetKeepSourceAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT keep_source FROM scan_jobs WHERE id = @jobId", new { jobId }, cancellationToken: ct)) is > 0;
    }

    public async Task SetKeepSourceAsync(long jobId, bool keep, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE scan_jobs SET keep_source = @keep WHERE id = @jobId", new { jobId, keep = keep ? 1 : 0 }, cancellationToken: ct));
    }

    private static object Params(PlanStep s) => new
    {
        s.Id,
        s.PlanId,
        s.ActionId,
        Kind = s.Kind.ToCode(),
        State = s.State.ToCode(),
        s.SourcePath,
        s.TargetPath,
        s.HoldPath,
        s.Bytes,
        s.Hash,
        s.Detail,
        s.StartedAt,
        s.FinishedAt
    };

    private sealed class ScannedRow
    {
        public string FullPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime LastWriteTime { get; set; }
    }

    private sealed class StepRow
    {
        public long Id { get; set; }
        public long PlanId { get; set; }
        public long ActionId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string? TargetPath { get; set; }
        public string? HoldPath { get; set; }
        public long Bytes { get; set; }
        public string? Hash { get; set; }
        public string? Detail { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }

        public PlanStep ToStep() => new()
        {
            Id = Id,
            PlanId = PlanId,
            ActionId = ActionId,
            Kind = VerdictCodes.ParseNullable<PlanStepKind>(Kind)!.Value,
            State = VerdictCodes.ParseNullable<PlanStepState>(State)!.Value,
            SourcePath = SourcePath,
            TargetPath = TargetPath,
            HoldPath = HoldPath,
            Bytes = Bytes,
            Hash = Hash,
            Detail = Detail,
            StartedAt = DateTime.SpecifyKind(StartedAt, DateTimeKind.Utc),
            FinishedAt = FinishedAt is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null
        };
    }
}
