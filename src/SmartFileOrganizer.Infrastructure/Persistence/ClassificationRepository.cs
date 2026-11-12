using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
             sensitive_markers, origin, inherited_from_directory_node_id, keep_hint, keep_reason,
             evidence, certainty, attempts)
        VALUES
            (@FileNodeId, @Category, @Importance, @Confidence, @Summary,
             @SuggestedTarget, @ModelUsed, @AnalyzedAt, @RawResponse, @Error, @IsAiResult,
             @SensitiveMarkers, @Origin, @InheritedFromDirectoryNodeId, @KeepHint, @KeepReason,
             @Evidence, @Certainty, 1)
        ON CONFLICT(file_node_id) DO UPDATE SET
            attempts = ai_results.attempts + 1,
            previous_error = CASE WHEN ai_results.error IS NOT NULL THEN ai_results.error ELSE ai_results.previous_error END,
            certainty = excluded.certainty,
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
            keep_reason = excluded.keep_reason,
            evidence = excluded.evidence
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
        c.KeepReason,
        c.Evidence,
        Certainty = c.Certainty.ToCode()
    };

    public async Task SaveAsync(FileClassification c, ResultCheck? check, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(UpsertSql, ToParameters(c), tx);
        if (check is not null)
        {
            check.FileNodeId = c.FileNodeId;
            check.Id = await conn.ExecuteScalarAsync<long>(InsertCheckSql, ToCheckParameters(check), tx);
        }
        await tx.CommitAsync(ct);
    }

    private const string InsertCheckSql = """
        INSERT INTO result_checks (file_node_id, reasons, outcome, rule_note, first_answer, second_answer, checked_at)
        VALUES (@FileNodeId, @Reasons, @Outcome, @RuleNote, @FirstAnswer, @SecondAnswer, @CheckedAt);
        SELECT last_insert_rowid();
        """;

    private static readonly JsonSerializerOptions AnswerJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static object ToCheckParameters(ResultCheck check) => new
    {
        check.FileNodeId,
        Reasons = string.Join(",", check.Reasons.Select(r => r.ToCode())),
        Outcome = check.Outcome.ToCode(),
        check.RuleNote,
        FirstAnswer = SerializeAnswer(check.First),
        SecondAnswer = check.Second is null ? null : SerializeAnswer(check.Second),
        check.CheckedAt
    };

    /// <summary>Answers as JSON; the markers as their token list, like the column of <c>ai_results</c>.</summary>
    internal static string SerializeAnswer(CheckAnswer a)
    {
        var node = JsonSerializer.SerializeToNode(a, AnswerJson)!.AsObject();
        node["sensitive_markers"] = SensitiveMarkerNames.Format(a.SensitiveMarkers);
        if (a.KeepHint is { } hint) node["keep_hint"] = hint.ToCode();
        return node.ToJsonString();
    }

    internal static CheckAnswer? DeserializeAnswer(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var node = JsonNode.Parse(json)!.AsObject();
        var markers = SensitiveMarkerNames.Parse(node["sensitive_markers"]?.GetValue<string>());
        var keep = VerdictCodes.ParseNullable<KeepHint>(node["keep_hint"]?.GetValue<string>());
        node.Remove("sensitive_markers");
        node.Remove("keep_hint");
        var answer = node.Deserialize<CheckAnswer>(AnswerJson)!;
        answer.SensitiveMarkers = markers;
        answer.KeepHint = keep;
        return answer;
    }

    public async Task<IReadOnlyList<ResultCheck>> GetChecksAsync(long fileNodeId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<CheckRow>(
            """
            SELECT id, file_node_id, reasons, outcome, rule_note, first_answer, second_answer, checked_at
            FROM result_checks WHERE file_node_id = @fileNodeId ORDER BY id
            """,
            new { fileNodeId });
        return rows.Select(r => new ResultCheck
        {
            Id = r.Id,
            FileNodeId = r.FileNodeId,
            Reasons = (r.Reasons ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(code => VerdictCodes.ParseNullable<CheckReason>(code)!.Value).ToList(),
            Outcome = VerdictCodes.ParseNullable<CheckOutcome>(r.Outcome)!.Value,
            RuleNote = r.RuleNote,
            First = DeserializeAnswer(r.FirstAnswer) ?? new CheckAnswer(),
            Second = DeserializeAnswer(r.SecondAnswer),
            CheckedAt = r.CheckedAt
        }).ToList();
    }

    private sealed class CheckRow
    {
        public long Id { get; set; }
        public long FileNodeId { get; set; }
        public string? Reasons { get; set; }
        public string? Outcome { get; set; }
        public string? RuleNote { get; set; }
        public string? FirstAnswer { get; set; }
        public string? SecondAnswer { get; set; }
        public DateTime CheckedAt { get; set; }
    }

    public async Task<FileClassification?> GetByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            SelectSql + " WHERE file_node_id = @fileNodeId",
            new { fileNodeId });
        return row?.Finish();
    }

    /// <summary>
    /// Reads <c>ai_results</c>. The coded columns are renamed so Dapper does not map them onto the nullable
    /// enums itself: it ignores type handlers for nullable enums and would try <c>Enum.Parse</c> on the code.
    /// The same holds for the marker list: <c>Enum.Parse</c> threw for <c>private_message</c>
    /// (found by the evaluation corpus of https://github.com/crack666/smart_file_organizer/issues/36).
    /// </summary>
    internal const string SelectSql = """
        SELECT id, file_node_id, category, importance, confidence, summary, suggested_target, model_used,
               analyzed_at, raw_response, error, is_ai_result, sensitive_markers AS sensitive_markers_code,
               origin AS origin_code, inherited_from_directory_node_id, keep_hint AS keep_hint_code, keep_reason,
               evidence, certainty AS certainty_code, attempts, previous_error
        FROM ai_results
        """;

    /// <summary>Database shape of a classification: the codes as strings, turned into enums by <see cref="Finish"/>.</summary>
    internal sealed class Row : FileClassification
    {
        public string? OriginCode { get; set; }
        public string? KeepHintCode { get; set; }
        public string? SensitiveMarkersCode { get; set; }
        public string? CertaintyCode { get; set; }

        public FileClassification Finish()
        {
            SensitiveMarkers = SensitiveMarkerNames.Parse(SensitiveMarkersCode);
            Origin = VerdictCodes.ParseNullable<ResultOrigin>(OriginCode);
            KeepHint = VerdictCodes.ParseNullable<KeepHint>(KeepHintCode);
            Certainty = VerdictCodes.ParseNullable<ResultCertainty>(CertaintyCode);
            return this;
        }
    }
}
