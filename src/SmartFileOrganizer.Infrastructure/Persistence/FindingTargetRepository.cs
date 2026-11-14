using System.Text.Json;
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
        public string DecidedBy { get; set; } = string.Empty;
    }

    private sealed class PartRow
    {
        public string FindingKey { get; set; } = string.Empty;
        public int PartIndex { get; set; }
        public string FileIds { get; set; } = "[]";
        public string Label { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public long? LibraryJobId { get; set; }
        public string? Path { get; set; }
        public string? RelativePath { get; set; }
        public string? Certainty { get; set; }
        public string ContentCheck { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string DecidedBy { get; set; } = string.Empty;
    }

    public async Task<IReadOnlyDictionary<string, FindingTargetRecord>> GetAllAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT job_id, finding_key, input_hash, status, library_job_id, path, relative_path, certainty, content_check,
                   content_status, reason, same_content_files, with_model, model, matched_at, decided_by
            FROM finding_targets WHERE job_id = @jobId
            """,
            new { jobId }, cancellationToken: ct));
        var result = new Dictionary<string, FindingTargetRecord>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            // A row with a code this version does not know counts as not matched.
            if (!VerdictCodes.TryParse<FindingTargetStatus>(r.Status, out var status)
                || !VerdictCodes.TryParse<TargetContentCheck>(r.ContentCheck, out var check)
                || !VerdictCodes.TryParse<ContentCheckStatus>(r.ContentStatus, out var contentStatus)
                || !VerdictCodes.TryParse<TargetDecidedBy>(r.DecidedBy, out var decidedBy))
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
                MatchedAt = r.MatchedAt,
                DecidedBy = decidedBy
            };
        }

        var parts = await conn.QueryAsync<PartRow>(new CommandDefinition(
            """
            SELECT finding_key, part_index, file_ids, label, status, library_job_id, path, relative_path, certainty, content_check, reason,
                   decided_by
            FROM finding_target_parts WHERE job_id = @jobId ORDER BY finding_key, part_index
            """,
            new { jobId }, cancellationToken: ct));
        foreach (var p in parts)
        {
            if (!result.TryGetValue(p.FindingKey, out var record)) continue;
            if (!VerdictCodes.TryParse<FindingTargetStatus>(p.Status, out var status)
                || !VerdictCodes.TryParse<TargetContentCheck>(p.ContentCheck, out var check)
                || !VerdictCodes.TryParse<TargetDecidedBy>(p.DecidedBy, out var partDecidedBy))
            {
                // A part this version cannot read: the whole match counts as not made.
                result.Remove(p.FindingKey);
                continue;
            }
            record.Parts.Add(new FindingTargetPart
            {
                Index = p.PartIndex,
                FileIds = JsonSerializer.Deserialize<List<long>>(p.FileIds) ?? [],
                Label = p.Label,
                Status = status,
                LibraryJobId = p.LibraryJobId,
                Path = p.Path,
                RelativePath = p.RelativePath,
                Certainty = VerdictCodes.ParseNullable<ResultCertainty>(p.Certainty),
                ContentCheck = check,
                Reason = p.Reason,
                DecidedBy = partDecidedBy
            });
        }
        return result;
    }

    public Task UpsertAsync(FindingTargetRecord record, CancellationToken ct = default) => ReplaceAsync(record.JobId, [record], [], ct);

    private static async Task WriteAsync(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction tx, FindingTargetRecord record, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM finding_target_parts WHERE job_id = @JobId AND finding_key = @FindingKey",
            new { record.JobId, record.FindingKey }, tx, cancellationToken: ct));
        foreach (var p in record.Parts)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO finding_target_parts (job_id, finding_key, part_index, file_ids, label, status, library_job_id, path,
                                                  relative_path, certainty, content_check, reason, decided_by)
                VALUES (@JobId, @FindingKey, @Index, @FileIds, @Label, @Status, @LibraryJobId, @Path, @RelativePath, @Certainty,
                        @ContentCheck, @Reason, @DecidedBy)
                """,
                new
                {
                    record.JobId,
                    record.FindingKey,
                    p.Index,
                    FileIds = JsonSerializer.Serialize(p.FileIds),
                    p.Label,
                    Status = p.Status.ToCode(),
                    p.LibraryJobId,
                    p.Path,
                    p.RelativePath,
                    Certainty = p.Certainty.ToCode(),
                    ContentCheck = p.ContentCheck.ToCode(),
                    p.Reason,
                    DecidedBy = p.DecidedBy.ToCode()
                }, tx, cancellationToken: ct));
        }
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO finding_targets (job_id, finding_key, input_hash, status, library_job_id, path, relative_path, certainty,
                                         content_check, content_status, reason, same_content_files, with_model, model, matched_at,
                                         decided_by)
            VALUES (@JobId, @FindingKey, @InputHash, @Status, @LibraryJobId, @Path, @RelativePath, @Certainty,
                    @ContentCheck, @ContentStatus, @Reason, @SameContentFiles, @WithModel, @Model, @MatchedAt, @DecidedBy)
            ON CONFLICT (job_id, finding_key) DO UPDATE SET
                input_hash = excluded.input_hash, status = excluded.status, library_job_id = excluded.library_job_id,
                path = excluded.path, relative_path = excluded.relative_path, certainty = excluded.certainty,
                content_check = excluded.content_check, content_status = excluded.content_status, reason = excluded.reason,
                same_content_files = excluded.same_content_files, with_model = excluded.with_model, model = excluded.model,
                matched_at = excluded.matched_at, decided_by = excluded.decided_by
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
                record.MatchedAt,
                DecidedBy = record.DecidedBy.ToCode()
            }, tx, cancellationToken: ct));
    }

    public async Task ReplaceAsync(
        long jobId, IReadOnlyList<FindingTargetRecord> records, IReadOnlyCollection<string> removeKeys, CancellationToken ct = default)
    {
        // All in one transaction: the decisions of one call of sfo findings place are stored together or not at all.
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var findingKey in removeKeys)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM finding_target_parts WHERE job_id = @jobId AND finding_key = @findingKey",
                new { jobId, findingKey }, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM finding_targets WHERE job_id = @jobId AND finding_key = @findingKey",
                new { jobId, findingKey }, tx, cancellationToken: ct));
        }
        foreach (var record in records)
            await WriteAsync(conn, tx, record, ct);
        await tx.CommitAsync(ct);
    }
}
