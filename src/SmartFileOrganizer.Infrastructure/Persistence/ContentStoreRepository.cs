using System.Text.Json;
using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// <c>content_assessments</c> and <c>content_corrections</c> (step 22). Codes as in <c>ai_results</c>: category and
/// importance as numbers, certainty and keep hint as codes, markers as tokens, facts as a JSON list of
/// <c>{"key","value","source"}</c> codes.
/// </summary>
public class ContentStoreRepository : IContentStoreRepository
{
    private readonly DatabaseContext _db;

    public ContentStoreRepository(DatabaseContext db) => _db = db;

    public async Task<ContentAssessment?> FindAsync(ContentKey key, AssessmentIdentity identity, bool keepHintAsked, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT * FROM content_assessments
            WHERE content_hash = @Hash AND size = @Size AND model = @Model AND prompt_version = @PromptVersion
              AND keep_hint_asked = @keepHintAsked
            """,
            new { key.Hash, key.Size, identity.Model, identity.PromptVersion, keepHintAsked = keepHintAsked ? 1 : 0 },
            cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task SaveAsync(ContentAssessment a, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO content_assessments
                (content_hash, size, model, prompt_version, keep_hint_asked, category, importance, confidence, certainty,
                 summary, keep_hint, keep_reason, sensitive_markers, facts_json, created_at)
            VALUES
                (@Hash, @Size, @Model, @PromptVersion, @KeepHintAsked, @Category, @Importance, @Confidence, @Certainty,
                 @Summary, @KeepHint, @KeepReason, @SensitiveMarkers, @FactsJson, @CreatedAt)
            ON CONFLICT (content_hash, size, model, prompt_version, keep_hint_asked) DO UPDATE SET
                category = excluded.category,
                importance = excluded.importance,
                confidence = excluded.confidence,
                certainty = excluded.certainty,
                summary = excluded.summary,
                keep_hint = excluded.keep_hint,
                keep_reason = excluded.keep_reason,
                sensitive_markers = excluded.sensitive_markers,
                facts_json = excluded.facts_json,
                created_at = excluded.created_at
            """,
            new
            {
                a.Key.Hash, a.Key.Size, a.Identity.Model, a.Identity.PromptVersion,
                KeepHintAsked = a.KeepHintAsked ? 1 : 0,
                Category = (int)a.Category, Importance = (int)a.Importance, a.Confidence,
                Certainty = a.Certainty.ToCode(), a.Summary, KeepHint = a.KeepHint.ToCode(), a.KeepReason,
                SensitiveMarkers = SensitiveMarkerNames.Format(a.SensitiveMarkers),
                FactsJson = FormatFacts(a.DescribedFacts),
                CreatedAt = a.CreatedAt.ToString("O")
            }, cancellationToken: ct));
    }

    public async Task MarkUsedAsync(long id, DateTime usedAt, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE content_assessments SET use_count = use_count + 1, last_used_at = @at WHERE id = @id",
            new { id, at = usedAt.ToString("O") }, cancellationToken: ct));
    }

    public async Task<ContentCorrection?> FindCorrectionAsync(ContentKey key, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<CorrectionRow>(new CommandDefinition(
            "SELECT category, note, corrected_at AS correctedat FROM content_corrections WHERE content_hash = @Hash AND size = @Size",
            new { key.Hash, key.Size }, cancellationToken: ct));
        return row is null ? null : new ContentCorrection(key, (FileCategory)row.Category, row.Note, AsUtc(row.CorrectedAt));
    }

    public async Task SaveCorrectionAsync(ContentCorrection c, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO content_corrections (content_hash, size, category, note, corrected_at)
            VALUES (@Hash, @Size, @Category, @Note, @CorrectedAt)
            ON CONFLICT (content_hash, size) DO UPDATE SET
                category = excluded.category, note = excluded.note, corrected_at = excluded.corrected_at
            """,
            new { c.Key.Hash, c.Key.Size, Category = (int)c.Category, c.Note, CorrectedAt = c.CorrectedAt.ToString("O") },
            cancellationToken: ct));
    }

    public async Task DeleteCorrectionAsync(ContentKey key, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM content_corrections WHERE content_hash = @Hash AND size = @Size",
            new { key.Hash, key.Size }, cancellationToken: ct));
    }

    public async Task<ContentStoreStats> GetStatsAsync(AssessmentIdentity? current, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var groups = (await conn.QueryAsync<GroupRow>(new CommandDefinition(
            """
            SELECT model, prompt_version AS promptversion, COUNT(*) AS entries, COALESCE(SUM(use_count), 0) AS reuses,
                   MAX(last_used_at) AS lastusedat
            FROM content_assessments
            GROUP BY model, prompt_version
            """, cancellationToken: ct))).ToList();
        var corrections = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM content_corrections", cancellationToken: ct));
        var fingerprints = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM content_fingerprints WHERE full_hash IS NOT NULL", cancellationToken: ct));

        bool IsCurrent(GroupRow g) => current is not null && g.Model == current.Model && g.PromptVersion == current.PromptVersion;
        var ordered = groups
            .OrderByDescending(IsCurrent)
            .ThenByDescending(g => g.LastUsedAt ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(g => g.Model, StringComparer.Ordinal)
            .ThenBy(g => g.PromptVersion, StringComparer.Ordinal)
            .Select(g => new ContentStoreGroup(g.Model, g.PromptVersion, g.Entries, g.Reuses,
                g.LastUsedAt is null ? null : AsUtc(DateTime.Parse(g.LastUsedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind))))
            .ToList();
        var entries = groups.Sum(g => g.Entries);
        var currentEntries = groups.Where(IsCurrent).Sum(g => g.Entries);
        return new ContentStoreStats(entries, currentEntries, entries - currentEntries, groups.Sum(g => g.Reuses),
            corrections, fingerprints, ordered);
    }

    public async Task<(long Entries, long Corrections)> ClearAsync(AssessmentIdentity? current, bool keepCurrent, bool corrections,
        CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        long entries = keepCurrent && current is not null
            ? await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM content_assessments WHERE NOT (model = @Model AND prompt_version = @PromptVersion)",
                new { current.Model, current.PromptVersion }, tx, cancellationToken: ct))
            : await conn.ExecuteAsync(new CommandDefinition("DELETE FROM content_assessments", transaction: tx, cancellationToken: ct));
        long removedCorrections = corrections
            ? await conn.ExecuteAsync(new CommandDefinition("DELETE FROM content_corrections", transaction: tx, cancellationToken: ct))
            : 0;
        await tx.CommitAsync(ct);
        return (entries, removedCorrections);
    }

    private sealed record FactJson(string Key, string Value, string Source);

    private static string FormatFacts(IReadOnlyList<FileFact> facts) =>
        JsonSerializer.Serialize(facts.Select(f => new FactJson(f.Key.ToCode(), f.Value, f.Source.ToCode())));

    /// <summary>A code this build no longer knows is left out rather than failing the take-over.</summary>
    private static IReadOnlyList<FileFact> ParseFacts(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        return (JsonSerializer.Deserialize<List<FactJson>>(json) ?? [])
            .Select(f => (Key: VerdictCodes.ParseNullable<FileFactKey>(f.Key), f.Value, Source: VerdictCodes.ParseNullable<FactSource>(f.Source)))
            .Where(f => f.Key is not null && f.Source is not null)
            .Select(f => new FileFact(f.Key!.Value, f.Value, f.Source!.Value))
            .ToList();
    }

    private static DateTime AsUtc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc)
    };

    private sealed class CorrectionRow
    {
        public long Category { get; set; }
        public string? Note { get; set; }
        public DateTime CorrectedAt { get; set; }
    }

    private sealed class GroupRow
    {
        public string Model { get; set; } = string.Empty;
        public string PromptVersion { get; set; } = string.Empty;
        public long Entries { get; set; }
        public long Reuses { get; set; }
        public string? LastUsedAt { get; set; }
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public string Content_Hash { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Model { get; set; } = string.Empty;
        public string Prompt_Version { get; set; } = string.Empty;
        public long Keep_Hint_Asked { get; set; }
        public long Category { get; set; }
        public long Importance { get; set; }
        public double Confidence { get; set; }
        public string? Certainty { get; set; }
        public string? Summary { get; set; }
        public string? Keep_Hint { get; set; }
        public string? Keep_Reason { get; set; }
        public string? Sensitive_Markers { get; set; }
        public string? Facts_Json { get; set; }
        public DateTime Created_At { get; set; }
        public DateTime? Last_Used_At { get; set; }
        public long Use_Count { get; set; }

        public ContentAssessment ToModel() => new()
        {
            Id = Id,
            Key = new ContentKey(Content_Hash, Size),
            Identity = new AssessmentIdentity(Model, Prompt_Version),
            KeepHintAsked = Keep_Hint_Asked != 0,
            Category = (FileCategory)Category,
            Importance = (Importance)Importance,
            Confidence = Confidence,
            Certainty = VerdictCodes.ParseNullable<ResultCertainty>(Certainty),
            Summary = Summary,
            KeepHint = VerdictCodes.ParseNullable<KeepHint>(Keep_Hint),
            KeepReason = Keep_Reason,
            SensitiveMarkers = SensitiveMarkerNames.Parse(Sensitive_Markers),
            DescribedFacts = ParseFacts(Facts_Json),
            CreatedAt = AsUtc(Created_At),
            LastUsedAt = Last_Used_At is { } u ? AsUtc(u) : null,
            UseCount = Use_Count
        };
    }
}
