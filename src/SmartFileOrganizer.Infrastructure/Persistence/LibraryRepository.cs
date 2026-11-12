using System.Runtime.CompilerServices;
using Dapper;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>Queries behind <c>sfo library</c>. Writes only to <c>library_advice</c>.</summary>
public class LibraryRepository : ILibraryRepository
{
    private readonly DatabaseContext _db;

    public LibraryRepository(DatabaseContext db) => _db = db;

    public async IAsyncEnumerable<StructureFileRow> StreamStructureFilesAsync(
        long jobId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        const string sql = """
            SELECT parent_path, name, size, file_type, extension, last_write_time, sensitive_markers
            FROM file_nodes
            WHERE job_id = @jobId
            """;
        await foreach (var row in conn.QueryUnbufferedAsync<StructureFileRow>(sql, new { jobId }).WithCancellation(ct))
            yield return row;
    }

    public async Task<LibraryAdviceRecord?> GetAdviceAsync(long jobId, string task, string inputHash, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<LibraryAdviceRecord>(new CommandDefinition(
            """
            SELECT * FROM library_advice
            WHERE job_id = @jobId AND task = @task AND input_hash = @inputHash
            ORDER BY id DESC
            LIMIT 1
            """,
            new { jobId, task, inputHash }, cancellationToken: ct));
    }

    public async Task SaveAdviceAsync(LibraryAdviceRecord record, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        record.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO library_advice (job_id, task, input_hash, model, created_at, content, raw_response, error)
            VALUES (@JobId, @Task, @InputHash, @Model, @CreatedAt, @Content, @RawResponse, @Error);
            SELECT last_insert_rowid();
            """,
            record, cancellationToken: ct));
    }
}
