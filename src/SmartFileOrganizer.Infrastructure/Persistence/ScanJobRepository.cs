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
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Children before parents: foreign keys are enforced, and most references were declared without
        // ON DELETE CASCADE. One transaction, so a failure leaves the job whole. Test cases and evaluation runs
        // outlive the job and only lose their reference to it; content_fingerprints belongs to paths, not jobs.
        // A table added later with a job, file or folder reference belongs in this list
        // (ScanJobDeletionTests seeds every table and fails otherwise).
        const string sql = BundleRepository.DeleteForJobSql + """

            DELETE FROM comparison_files    WHERE comparison_id IN (SELECT id FROM comparisons WHERE source_job_id = @id OR against_job_id = @id);
            DELETE FROM comparisons         WHERE source_job_id = @id OR against_job_id = @id;
            DELETE FROM user_overrides      WHERE file_node_id IN (SELECT id FROM file_nodes WHERE job_id = @id);
            DELETE FROM ai_results          WHERE file_node_id IN (SELECT id FROM file_nodes WHERE job_id = @id);
            DELETE FROM file_facts          WHERE file_node_id IN (SELECT id FROM file_nodes WHERE job_id = @id);
            DELETE FROM file_decisions      WHERE job_id = @id OR file_node_id IN (SELECT id FROM file_nodes WHERE job_id = @id);
            DELETE FROM result_checks       WHERE file_node_id IN (SELECT id FROM file_nodes WHERE job_id = @id);
            DELETE FROM ai_directory_results WHERE directory_node_id IN (SELECT id FROM directory_nodes WHERE job_id = @id);
            DELETE FROM directory_samples   WHERE directory_node_id IN (SELECT id FROM directory_nodes WHERE job_id = @id);
            DELETE FROM directory_verdicts  WHERE directory_node_id IN (SELECT id FROM directory_nodes WHERE job_id = @id);
            UPDATE eval_cases SET job_id = NULL, file_node_id = NULL WHERE job_id = @id;
            UPDATE eval_cases SET file_node_id = NULL WHERE file_node_id IN (SELECT id FROM file_nodes WHERE job_id = @id);
            UPDATE eval_runs  SET job_id = NULL WHERE job_id = @id;
            DELETE FROM library_advice      WHERE job_id = @id;
            DELETE FROM finding_decisions   WHERE job_id = @id;
            DELETE FROM plan_actions        WHERE plan_id IN (SELECT id FROM plans WHERE job_id = @id);
            DELETE FROM plans               WHERE job_id = @id;
            UPDATE plan_actions SET library_job_id = NULL WHERE library_job_id = @id;
            UPDATE plan_actions SET file_node_id = NULL WHERE file_node_id IN (SELECT id FROM file_nodes WHERE job_id = @id);
            UPDATE plan_actions SET directory_node_id = NULL WHERE directory_node_id IN (SELECT id FROM directory_nodes WHERE job_id = @id);
            DELETE FROM model_server_waits  WHERE job_id = @id;
            DELETE FROM file_nodes          WHERE job_id = @id;
            DELETE FROM file_links          WHERE job_id = @id;
            DELETE FROM directory_nodes     WHERE job_id = @id;
            DELETE FROM scan_jobs           WHERE id = @id;
            """;

        await conn.ExecuteAsync(new CommandDefinition(sql, new { id, jobId = id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
