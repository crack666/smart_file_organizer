using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>Stores test cases in <c>eval_cases</c>; enum values as their codes (<see cref="VerdictCodes"/>).</summary>
public class EvalCaseRepository : IEvalCaseRepository
{
    private readonly DatabaseContext _db;

    public EvalCaseRepository(DatabaseContext db) => _db = db;

    public async Task UpsertAsync(EvalCase evalCase, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        evalCase.Id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO eval_cases
                (kind, origin, job_id, file_node_id, subject_path, relative_path, expected, observed,
                 observed_model, sensitive_markers, evidence_json, created_at, updated_at)
            VALUES
                (@Kind, @Origin, @JobId, @FileNodeId, @SubjectPath, @RelativePath, @Expected, @Observed,
                 @ObservedModel, @SensitiveMarkers, @EvidenceJson, @CreatedAt, @UpdatedAt)
            ON CONFLICT(kind, job_id, subject_path) DO UPDATE SET
                origin            = excluded.origin,
                file_node_id      = excluded.file_node_id,
                relative_path     = excluded.relative_path,
                expected          = excluded.expected,
                observed          = excluded.observed,
                observed_model    = excluded.observed_model,
                sensitive_markers = excluded.sensitive_markers,
                evidence_json     = excluded.evidence_json,
                updated_at        = excluded.updated_at
            RETURNING id
            """,
            new
            {
                Kind = evalCase.Kind.ToCode(),
                Origin = evalCase.Origin.ToCode(),
                evalCase.JobId,
                evalCase.FileNodeId,
                evalCase.SubjectPath,
                evalCase.RelativePath,
                evalCase.Expected,
                evalCase.Observed,
                evalCase.ObservedModel,
                SensitiveMarkers = SensitiveMarkerNames.Format(evalCase.SensitiveMarkers),
                evalCase.EvidenceJson,
                evalCase.CreatedAt,
                evalCase.UpdatedAt
            });
    }

    public async Task<IReadOnlyList<EvalCase>> GetAllAsync(EvalCaseKind? kind = null, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<CaseRow>(
            "SELECT * FROM eval_cases WHERE @kind IS NULL OR kind = @kind ORDER BY id",
            new { kind = kind.ToCode() });
        return rows.Select(r => new EvalCase
        {
            Id = r.Id,
            Kind = VerdictCodes.ParseNullable<EvalCaseKind>(r.Kind)!.Value,
            Origin = VerdictCodes.ParseNullable<EvalCaseOrigin>(r.Origin)!.Value,
            JobId = r.JobId,
            FileNodeId = r.FileNodeId,
            SubjectPath = r.SubjectPath,
            RelativePath = r.RelativePath,
            Expected = r.Expected,
            Observed = r.Observed,
            ObservedModel = r.ObservedModel,
            SensitiveMarkers = SensitiveMarkerNames.Parse(r.SensitiveMarkers),
            EvidenceJson = r.EvidenceJson,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt
        }).ToList();
    }

    private sealed class CaseRow
    {
        public long Id { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public long? JobId { get; set; }
        public long? FileNodeId { get; set; }
        public string SubjectPath { get; set; } = string.Empty;
        public string? RelativePath { get; set; }
        public string Expected { get; set; } = string.Empty;
        public string? Observed { get; set; }
        public string? ObservedModel { get; set; }
        public string? SensitiveMarkers { get; set; }
        public string? EvidenceJson { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}

/// <summary>Stores evaluation runs in <c>eval_runs</c>.</summary>
public class EvalRunRepository : IEvalRunRepository
{
    private readonly DatabaseContext _db;

    public EvalRunRepository(DatabaseContext db) => _db = db;

    public async Task InsertAsync(EvalRun run, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        run.Id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO eval_runs
                (mode, started_at, finished_at, subject, job_id, model, stub, prompt_fingerprint,
                 app_version, settings_json, report_json)
            VALUES
                (@Mode, @StartedAt, @FinishedAt, @Subject, @JobId, @Model, @Stub, @PromptFingerprint,
                 @AppVersion, @SettingsJson, @ReportJson)
            RETURNING id
            """,
            new
            {
                Mode = run.Mode.ToCode(),
                run.StartedAt,
                run.FinishedAt,
                run.Subject,
                run.JobId,
                run.Model,
                run.Stub,
                run.PromptFingerprint,
                run.AppVersion,
                run.SettingsJson,
                run.ReportJson
            });
    }

    public async Task<EvalRun?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RunRow>("SELECT * FROM eval_runs WHERE id = @id", new { id });
        return row is null ? null : FromRow(row);
    }

    public async Task<IReadOnlyList<EvalRun>> GetLatestAsync(EvalRunMode? mode, int count, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<RunRow>(
            "SELECT * FROM eval_runs WHERE @mode IS NULL OR mode = @mode ORDER BY id DESC LIMIT @count",
            new { mode = mode.ToCode(), count });
        return rows.Select(FromRow).ToList();
    }

    private static EvalRun FromRow(RunRow r) => new()
    {
        Id = r.Id,
        Mode = VerdictCodes.ParseNullable<EvalRunMode>(r.Mode)!.Value,
        StartedAt = r.StartedAt,
        FinishedAt = r.FinishedAt,
        Subject = r.Subject,
        JobId = r.JobId,
        Model = r.Model,
        Stub = r.Stub,
        PromptFingerprint = r.PromptFingerprint,
        AppVersion = r.AppVersion,
        SettingsJson = r.SettingsJson,
        ReportJson = r.ReportJson
    };

    private sealed class RunRow
    {
        public long Id { get; set; }
        public string Mode { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime FinishedAt { get; set; }
        public string Subject { get; set; } = string.Empty;
        public long? JobId { get; set; }
        public string? Model { get; set; }
        public string? Stub { get; set; }
        public string? PromptFingerprint { get; set; }
        public string? AppVersion { get; set; }
        public string SettingsJson { get; set; } = "{}";
        public string ReportJson { get; set; } = "{}";
    }
}
