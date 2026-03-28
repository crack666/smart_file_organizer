using Dapper;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

public class ScanJobRepository : IScanJobRepository
{
    private readonly DatabaseContext _db;

    public ScanJobRepository(DatabaseContext db) => _db = db;

    public async Task<ScanJob> CreateAsync(ScanJob job, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            INSERT INTO scan_jobs
                (root_path, status, created_at, started_at, completed_at,
                 total_files, processed_files, total_directories, processed_directories,
                 error_count, last_error)
            VALUES
                (@RootPath, @Status, @CreatedAt, @StartedAt, @CompletedAt,
                 @TotalFiles, @ProcessedFiles, @TotalDirectories, @ProcessedDirectories,
                 @ErrorCount, @LastError);
            SELECT last_insert_rowid();
            """;

        job.Id = await conn.ExecuteScalarAsync<long>(sql, job);
        return job;
    }

    public async Task<ScanJob?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ScanJob>(
            "SELECT * FROM scan_jobs WHERE id = @id", new { id });
    }

    public async Task<IReadOnlyList<ScanJob>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var result = await conn.QueryAsync<ScanJob>("SELECT * FROM scan_jobs ORDER BY created_at DESC");
        return result.AsList();
    }

    public async Task UpdateAsync(ScanJob job, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);

        var sql = """
            UPDATE scan_jobs SET
                status = @Status,
                started_at = @StartedAt,
                completed_at = @CompletedAt,
                total_files = @TotalFiles,
                processed_files = @ProcessedFiles,
                total_directories = @TotalDirectories,
                processed_directories = @ProcessedDirectories,
                error_count = @ErrorCount,
                last_error = @LastError
            WHERE id = @Id
            """;

        await conn.ExecuteAsync(sql, job);
    }
}
