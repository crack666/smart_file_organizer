using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>Stores <see cref="FileFact"/> rows in <c>file_facts</c> with their English codes.</summary>
public class FileFactRepository : IFileFactRepository
{
    private readonly DatabaseContext _db;

    public FileFactRepository(DatabaseContext db) => _db = db;

    public async Task ReplaceAsync(long fileNodeId, IReadOnlyList<FileFact> facts, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync("DELETE FROM file_facts WHERE file_node_id = @fileNodeId", new { fileNodeId }, tx);

        var recordedAt = DateTime.UtcNow.ToString("O");
        // Last one wins when a key appears twice.
        var rows = facts
            .GroupBy(f => f.Key)
            .Select(g => g.Last())
            .Select(f => new
            {
                fileNodeId,
                fact = f.Key.ToCode(),
                value = f.Value,
                source = f.Source.ToCode(),
                recordedAt
            })
            .ToList();

        if (rows.Count > 0)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO file_facts (file_node_id, fact, value, source, recorded_at)
                VALUES (@fileNodeId, @fact, @value, @source, @recordedAt)
                """,
                rows, tx);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<FileFact>> GetByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string Fact, string Value, string Source)>(
            "SELECT fact, value, source FROM file_facts WHERE file_node_id = @fileNodeId ORDER BY fact",
            new { fileNodeId });

        return rows
            .Select(r => new FileFact(
                VerdictCodes.ParseNullable<FileFactKey>(r.Fact)!.Value,
                r.Value,
                VerdictCodes.ParseNullable<FactSource>(r.Source)!.Value))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<FileFact>>> GetByJobAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long FileNodeId, string Fact, string Value, string Source)>(new CommandDefinition(
            """
            SELECT ff.file_node_id, ff.fact, ff.value, ff.source
            FROM file_facts ff JOIN file_nodes f ON f.id = ff.file_node_id
            WHERE f.job_id = @jobId
            ORDER BY ff.file_node_id, ff.fact
            """, new { jobId }, cancellationToken: ct));

        var result = new Dictionary<long, IReadOnlyList<FileFact>>();
        foreach (var group in rows.GroupBy(r => r.FileNodeId))
        {
            // A code this build does not know is skipped rather than failing the whole job.
            result[group.Key] = group
                .Select(r => (Key: VerdictCodes.ParseNullable<FileFactKey>(r.Fact), r.Value, Source: VerdictCodes.ParseNullable<FactSource>(r.Source)))
                .Where(r => r.Key is not null && r.Source is not null)
                .Select(r => new FileFact(r.Key!.Value, r.Value, r.Source!.Value))
                .ToList();
        }
        return result;
    }
}
