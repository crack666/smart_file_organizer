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
}
