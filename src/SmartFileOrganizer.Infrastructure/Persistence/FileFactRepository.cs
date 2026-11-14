using Dapper;
using Microsoft.Data.Sqlite;
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
        await InsertAsync(conn, tx, fileNodeId, facts);

        await tx.CommitAsync(ct);
    }

    public async Task ReplaceKeysAsync(long fileNodeId, IReadOnlyCollection<FileFactKey> keys, IReadOnlyList<FileFact> facts, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var codes = keys.Concat(facts.Select(f => f.Key)).Distinct().Select(k => k.ToCode()).ToList();
        if (codes.Count > 0)
            await conn.ExecuteAsync("DELETE FROM file_facts WHERE file_node_id = @fileNodeId AND fact IN @codes", new { fileNodeId, codes }, tx);
        await InsertAsync(conn, tx, fileNodeId, facts);

        await tx.CommitAsync(ct);
    }

    private static async Task InsertAsync(SqliteConnection conn, System.Data.Common.DbTransaction tx, long fileNodeId, IReadOnlyList<FileFact> facts)
    {
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
                certainty = f.Certainty?.ToCode(),
                recordedAt
            })
            .ToList();

        if (rows.Count > 0)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO file_facts (file_node_id, fact, value, source, certainty, recorded_at)
                VALUES (@fileNodeId, @fact, @value, @source, @certainty, @recordedAt)
                """,
                rows, tx);
        }
    }

    public async Task<IReadOnlyList<FileFact>> GetByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string Fact, string Value, string Source, string? Certainty)>(
            "SELECT fact, value, source, certainty FROM file_facts WHERE file_node_id = @fileNodeId ORDER BY fact",
            new { fileNodeId });

        return rows.Select(r => ToFact(r.Fact, r.Value, r.Source, r.Certainty)).OfType<FileFact>().ToList();
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<FileFact>>> GetByJobAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long FileNodeId, string Fact, string Value, string Source, string? Certainty)>(new CommandDefinition(
            """
            SELECT ff.file_node_id, ff.fact, ff.value, ff.source, ff.certainty
            FROM file_facts ff JOIN file_nodes f ON f.id = ff.file_node_id
            WHERE f.job_id = @jobId
            ORDER BY ff.file_node_id, ff.fact
            """, new { jobId }, cancellationToken: ct));

        var result = new Dictionary<long, IReadOnlyList<FileFact>>();
        foreach (var group in rows.GroupBy(r => r.FileNodeId))
            result[group.Key] = group.Select(r => ToFact(r.Fact, r.Value, r.Source, r.Certainty)).OfType<FileFact>().ToList();
        return result;
    }

    /// <summary>A code this build does not know is skipped rather than failing the whole read; an unknown certainty is dropped.</summary>
    private static FileFact? ToFact(string fact, string value, string source, string? certainty)
    {
        var key = VerdictCodes.ParseNullable<FileFactKey>(fact);
        var src = VerdictCodes.ParseNullable<FactSource>(source);
        if (key is null || src is null) return null;
        return new FileFact(key.Value, value, src.Value, VerdictCodes.ParseNullable<ResultCertainty>(certainty));
    }
}
