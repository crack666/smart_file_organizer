using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary><c>folder_descriptions</c>: what a sample of a library folder shows, kept per job, path and source.</summary>
public sealed class FolderDescriptionRepository : IFolderDescriptionRepository
{
    private readonly DatabaseContext _db;

    public FolderDescriptionRepository(DatabaseContext db) => _db = db;

    private sealed class Row
    {
        public long Id { get; set; }
        public long JobId { get; set; }
        public string RelativePath { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string SampleJson { get; set; } = "[]";
        public string FactsJson { get; set; } = "{}";
        public int Sampled { get; set; }
        public int Described { get; set; }
        public string? Model { get; set; }
        public DateTime DescribedAt { get; set; }
    }

    public async Task<FolderDescriptionRecord?> GetAsync(long jobId, string relativePath, FolderDescriptionSource source, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT id, job_id, relative_path, source, sample_json, facts_json, sampled, described, model, described_at
            FROM folder_descriptions
            WHERE job_id = @jobId AND relative_path = @relativePath AND source = @source
            """,
            new { jobId, relativePath, source = source.ToCode() }, cancellationToken: ct));
        if (row is null) return null;
        VerdictCodes.TryParse<FolderDescriptionSource>(row.Source, out var parsed);
        return new FolderDescriptionRecord
        {
            Id = row.Id,
            JobId = row.JobId,
            RelativePath = row.RelativePath,
            Source = parsed,
            SampleJson = row.SampleJson,
            FactsJson = row.FactsJson,
            Sampled = row.Sampled,
            Described = row.Described,
            Model = row.Model,
            DescribedAt = row.DescribedAt
        };
    }

    public async Task SaveAsync(FolderDescriptionRecord record, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        record.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO folder_descriptions (job_id, relative_path, source, sample_json, facts_json, sampled, described, model, described_at)
            VALUES (@JobId, @RelativePath, @Source, @SampleJson, @FactsJson, @Sampled, @Described, @Model, @DescribedAt)
            ON CONFLICT (job_id, relative_path, source) DO UPDATE SET
                sample_json = excluded.sample_json, facts_json = excluded.facts_json, sampled = excluded.sampled,
                described = excluded.described, model = excluded.model, described_at = excluded.described_at;
            SELECT id FROM folder_descriptions WHERE job_id = @JobId AND relative_path = @RelativePath AND source = @Source;
            """,
            new
            {
                record.JobId, record.RelativePath, Source = record.Source.ToCode(), record.SampleJson, record.FactsJson,
                record.Sampled, record.Described, record.Model, record.DescribedAt
            }, cancellationToken: ct));
    }
}
