using Dapper;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

public class ClassificationRepository : IClassificationRepository
{
    private readonly DatabaseContext _db;

    public ClassificationRepository(DatabaseContext db) => _db = db;

    public async Task UpsertAsync(FileClassification c, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            INSERT INTO ai_results
                (file_node_id, category, importance, confidence, summary,
                 suggested_target, model_used, analyzed_at, raw_response, error, is_ai_result)
            VALUES
                (@FileNodeId, @Category, @Importance, @Confidence, @Summary,
                 @SuggestedTarget, @ModelUsed, @AnalyzedAt, @RawResponse, @Error, @IsAiResult)
            ON CONFLICT(file_node_id) DO UPDATE SET
                category = excluded.category,
                importance = excluded.importance,
                confidence = excluded.confidence,
                summary = excluded.summary,
                suggested_target = excluded.suggested_target,
                model_used = excluded.model_used,
                analyzed_at = excluded.analyzed_at,
                raw_response = excluded.raw_response,
                error = excluded.error,
                is_ai_result = excluded.is_ai_result
            """;

        await conn.ExecuteAsync(sql, c);
    }

    public async Task<FileClassification?> GetByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<FileClassification>(
            "SELECT * FROM ai_results WHERE file_node_id = @fileNodeId",
            new { fileNodeId });
    }
}
