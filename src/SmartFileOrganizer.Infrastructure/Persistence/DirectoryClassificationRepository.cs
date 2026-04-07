using Dapper;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

public class DirectoryClassificationRepository : IDirectoryClassificationRepository
{
    private readonly DatabaseContext _db;

    public DirectoryClassificationRepository(DatabaseContext db) => _db = db;

    public async Task UpsertAsync(DirectoryClassificationResult result, CancellationToken ct = default)
    {
        // Intentional: do not cancel this write mid-flight.
        // On "Stop AI" we still prefer a best-effort persistence of already computed
        // directory results instead of throwing TaskCanceledException from OpenAsync/ExecuteAsync.
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(CancellationToken.None);

        await conn.ExecuteAsync(
            """
            INSERT INTO ai_directory_results
                (directory_node_id, phase, summary, theme, homogeneity, dominant_type,
                 anomalous_file_ids, sampling_strategy, sample_size, model_used, analyzed_at, error)
            VALUES
                (@DirectoryNodeId, @Phase, @Summary, @Theme, @Homogeneity, @DominantType,
                 @AnomalousFileIds, @SamplingStrategy, @SampleSize, @ModelUsed, @AnalyzedAt, @Error)
            ON CONFLICT(directory_node_id, phase) DO UPDATE SET
                summary            = excluded.summary,
                theme              = excluded.theme,
                homogeneity        = excluded.homogeneity,
                dominant_type      = excluded.dominant_type,
                anomalous_file_ids = excluded.anomalous_file_ids,
                sampling_strategy  = excluded.sampling_strategy,
                sample_size        = excluded.sample_size,
                model_used         = excluded.model_used,
                analyzed_at        = excluded.analyzed_at,
                error              = excluded.error
            """,
            result);
    }

    public async Task<DirectoryClassificationResult?> GetByNodeAndPhaseAsync(
        long directoryNodeId, string phase, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DirectoryClassificationResult>(
            "SELECT * FROM ai_directory_results WHERE directory_node_id = @directoryNodeId AND phase = @phase",
            new { directoryNodeId, phase });
    }

    public async Task<IReadOnlyList<long>> GetAssessedNodeIdsAsync(
        long jobId, string phase, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var ids = await conn.QueryAsync<long>(
            """
            SELECT adr.directory_node_id
            FROM ai_directory_results adr
            JOIN directory_nodes dn ON dn.id = adr.directory_node_id
            WHERE dn.job_id = @jobId AND adr.phase = @phase
            """,
            new { jobId, phase });
        return ids.AsList();
    }

    public async Task<DirectoryClassificationResult?> GetByDirectoryPathAsync(
        long jobId, string fullPath, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DirectoryClassificationResult>(
            """
            SELECT adr.*
            FROM ai_directory_results adr
            JOIN directory_nodes dn ON dn.id = adr.directory_node_id
            WHERE dn.job_id = @jobId AND dn.full_path = @fullPath AND adr.phase = 'summary'
            """,
            new { jobId, fullPath });
    }
}
