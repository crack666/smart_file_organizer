using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary><c>finding_targets</c>: the stored target match per finding; enum values as their codes.</summary>
public sealed class FindingTargetRepository : IFindingTargetRepository
{
    private readonly DatabaseContext _db;

    public FindingTargetRepository(DatabaseContext db) => _db = db;

    private sealed class Row
    {
        public long JobId { get; set; }
        public string FindingKey { get; set; } = string.Empty;
        public string InputHash { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public long? LibraryJobId { get; set; }
        public string? Path { get; set; }
        public string? RelativePath { get; set; }
        public string? Certainty { get; set; }
        public string ContentCheck { get; set; } = string.Empty;
        public string ContentStatus { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public int SameContentFiles { get; set; }
        public bool WithModel { get; set; }
        public string? Model { get; set; }
        public DateTime MatchedAt { get; set; }
    }

    public async Task<IReadOnlyDictionary<string, FindingTargetRecord>> GetAllAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT job_id, finding_key, input_hash, status, library_job_id, path, relative_path, certainty, content_check,
                   content_status, reason, same_content_files, with_model, model, matched_at
            FROM finding_targets WHERE job_id = @jobId
            """,
            new { jobId }, cancellationToken: ct));
        var result = new Dictionary<string, FindingTargetRecord>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            // A row with a code this version does not know counts as not matched.
            if (!VerdictCodes.TryParse<FindingTargetStatus>(r.Status, out var status)
                || !VerdictCodes.TryParse<TargetContentCheck>(r.ContentCheck, out var check)
                || !VerdictCodes.TryParse<ContentCheckStatus>(r.ContentStatus, out var contentStatus))
                continue;
            result[r.FindingKey] = new FindingTargetRecord
            {
                JobId = r.JobId,
                FindingKey = r.FindingKey,
                InputHash = r.InputHash,
                Status = status,
                LibraryJobId = r.LibraryJobId,
                Path = r.Path,
                RelativePath = r.RelativePath,
                Certainty = VerdictCodes.ParseNullable<ResultCertainty>(r.Certainty),
                ContentCheck = check,
                ContentStatus = contentStatus,
                Reason = r.Reason,
                SameContentFiles = r.SameContentFiles,
                WithModel = r.WithModel,
                Model = r.Model,
                MatchedAt = r.MatchedAt
            };
        }
        return result;
    }

    public async Task UpsertAsync(FindingTargetRecord record, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO finding_targets (job_id, finding_key, input_hash, status, library_job_id, path, relative_path, certainty,
                                         content_check, content_status, reason, same_content_files, with_model, model, matched_at)
            VALUES (@JobId, @FindingKey, @InputHash, @Status, @LibraryJobId, @Path, @RelativePath, @Certainty,
                    @ContentCheck, @ContentStatus, @Reason, @SameContentFiles, @WithModel, @Model, @MatchedAt)
            ON CONFLICT (job_id, finding_key) DO UPDATE SET
                input_hash = excluded.input_hash, status = excluded.status, library_job_id = excluded.library_job_id,
                path = excluded.path, relative_path = excluded.relative_path, certainty = excluded.certainty,
                content_check = excluded.content_check, content_status = excluded.content_status, reason = excluded.reason,
                same_content_files = excluded.same_content_files, with_model = excluded.with_model, model = excluded.model,
                matched_at = excluded.matched_at
            """,
            new
            {
                record.JobId,
                record.FindingKey,
                record.InputHash,
                Status = record.Status.ToCode(),
                record.LibraryJobId,
                record.Path,
                record.RelativePath,
                Certainty = record.Certainty.ToCode(),
                ContentCheck = record.ContentCheck.ToCode(),
                ContentStatus = record.ContentStatus.ToCode(),
                record.Reason,
                record.SameContentFiles,
                record.WithModel,
                record.Model,
                record.MatchedAt
            }, cancellationToken: ct));
    }
}
