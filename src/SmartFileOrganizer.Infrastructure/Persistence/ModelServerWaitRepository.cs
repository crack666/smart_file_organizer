using System.Globalization;
using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>Stores waiting runs in <c>model_server_waits</c>; times as ISO 8601 UTC, the cause as its code.</summary>
public class ModelServerWaitRepository : IModelServerWaitRepository
{
    private readonly DatabaseContext _db;

    public ModelServerWaitRepository(DatabaseContext db) => _db = db;

    public async Task SetAsync(ModelServerWait wait, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO model_server_waits (job_id, since, checked_at, cause) VALUES (@JobId, @Since, @CheckedAt, @Cause)
            ON CONFLICT(job_id) DO UPDATE SET since = excluded.since, checked_at = excluded.checked_at, cause = excluded.cause
            """,
            new { wait.JobId, Since = Format(wait.Since), CheckedAt = Format(wait.CheckedAt), Cause = wait.Cause.ToCode() },
            cancellationToken: ct));
    }

    public async Task TouchAsync(long jobId, DateTime checkedAt, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE model_server_waits SET checked_at = @checkedAt WHERE job_id = @jobId",
            new { jobId, checkedAt = Format(checkedAt) }, cancellationToken: ct));
    }

    public async Task ClearAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM model_server_waits WHERE job_id = @jobId", new { jobId }, cancellationToken: ct));
    }

    public async Task<ModelServerWait?> GetAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT job_id AS JobId, since AS Since, checked_at AS CheckedAt, cause AS Cause FROM model_server_waits WHERE job_id = @jobId",
            new { jobId }, cancellationToken: ct));
        if (row is null || !VerdictCodes.TryParse<ModelServerFailure>(row.Cause, out var cause)) return null;
        return new ModelServerWait { JobId = row.JobId, Since = Parse(row.Since), CheckedAt = Parse(row.CheckedAt), Cause = cause };
    }

    private static string Format(DateTime value) =>
        DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc)
            .ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Parse(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private sealed class Row
    {
        public long JobId { get; set; }
        public string Since { get; set; } = string.Empty;
        public string CheckedAt { get; set; } = string.Empty;
        public string Cause { get; set; } = string.Empty;
    }
}
