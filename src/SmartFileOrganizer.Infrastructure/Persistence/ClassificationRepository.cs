using Dapper;
using SmartFileOrganizer.Domain.Enums;
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

        await conn.ExecuteAsync(UpsertSql, ToParameters(c));
    }

    internal const string UpsertSql = """
        INSERT INTO ai_results
            (file_node_id, category, importance, confidence, summary,
             suggested_target, model_used, analyzed_at, raw_response, error, is_ai_result,
             sensitive_markers, origin, inherited_from_directory_node_id, keep_hint, keep_reason)
        VALUES
            (@FileNodeId, @Category, @Importance, @Confidence, @Summary,
             @SuggestedTarget, @ModelUsed, @AnalyzedAt, @RawResponse, @Error, @IsAiResult,
             @SensitiveMarkers, @Origin, @InheritedFromDirectoryNodeId, @KeepHint, @KeepReason)
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
            is_ai_result = excluded.is_ai_result,
            sensitive_markers = excluded.sensitive_markers,
            origin = excluded.origin,
            inherited_from_directory_node_id = excluded.inherited_from_directory_node_id,
            keep_hint = excluded.keep_hint,
            keep_reason = excluded.keep_reason
        """;

    /// <summary>
    /// Parameter object: markers as their token list and the coded enums as their codes; Dapper would
    /// bind every enum as an integer.
    /// </summary>
    internal static object ToParameters(FileClassification c) => new
    {
        c.FileNodeId,
        c.Category,
        c.Importance,
        c.Confidence,
        c.Summary,
        c.SuggestedTarget,
        c.ModelUsed,
        c.AnalyzedAt,
        c.RawResponse,
        c.Error,
        c.IsAiResult,
        SensitiveMarkers = SensitiveMarkerNames.Format(c.SensitiveMarkers),
        Origin = c.Origin.ToCode(),
        c.InheritedFromDirectoryNodeId,
        KeepHint = c.KeepHint.ToCode(),
        c.KeepReason
    };

    public async Task<FileClassification?> GetByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<FileClassification>(
            "SELECT * FROM ai_results WHERE file_node_id = @fileNodeId",
            new { fileNodeId });
    }
}
