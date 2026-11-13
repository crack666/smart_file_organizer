using Dapper;
using SmartFileOrganizer.Domain.Enums;
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
                (root_path, role, status, created_at, started_at, completed_at,
                 total_files, processed_files, total_directories, processed_directories,
                 error_count, last_error,
                 rules_name, rules_source, rules_hash, rules_json, structure_only)
            VALUES
                (@RootPath, @RoleValue, @Status, @CreatedAt, @StartedAt, @CompletedAt,
                 @TotalFiles, @ProcessedFiles, @TotalDirectories, @ProcessedDirectories,
                 @ErrorCount, @LastError,
                 @RulesName, @RulesSource, @RulesHash, @RulesJson, @StructureOnly);
            SELECT last_insert_rowid();
            """;

        // Dapper writes enums as integers; the role column holds the fixed string.
        var parameters = new DynamicParameters(job);
        parameters.Add("RoleValue", job.Role.ToValue());
        job.Id = await conn.ExecuteScalarAsync<long>(sql, parameters);
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
                last_error = @LastError,
                rules_name = @RulesName,
                rules_source = @RulesSource,
                rules_hash = @RulesHash,
                rules_json = @RulesJson
            WHERE id = @Id
            """;

        await conn.ExecuteAsync(sql, job);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);

        // Cascade manually (SQLite FK cascade requires ON DELETE CASCADE in DDL;
        // we delete in dependency order instead).
        const string sql = BundleRepository.DeleteForJobSql + """

            DELETE FROM comparison_files    WHERE comparison_id IN (SELECT id FROM comparisons WHERE source_job_id = @id OR against_job_id = @id);
            DELETE FROM comparisons         WHERE source_job_id = @id OR against_job_id = @id;
            DELETE FROM user_overrides     WHERE file_node_id IN (SELECT id FROM file_nodes WHERE job_id = @id);
            DELETE FROM ai_results          WHERE file_node_id IN (SELECT id FROM file_nodes WHERE job_id = @id);
            DELETE FROM ai_directory_results WHERE directory_node_id IN (SELECT id FROM directory_nodes WHERE job_id = @id);
            DELETE FROM library_advice      WHERE job_id = @id;
            DELETE FROM finding_decisions   WHERE job_id = @id;
            DELETE FROM file_nodes         WHERE job_id = @id;
            DELETE FROM file_links          WHERE job_id = @id;
            DELETE FROM directory_nodes     WHERE job_id = @id;
            DELETE FROM scan_jobs           WHERE id = @id;
            """;

        await conn.ExecuteAsync(sql, new { id, jobId = id });
    }
}
