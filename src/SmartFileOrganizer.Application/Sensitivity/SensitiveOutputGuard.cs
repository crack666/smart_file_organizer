using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Sensitivity;

/// <summary>
/// Options for everything that leaves the process (CLI output, exports).
/// </summary>
public sealed record SensitiveOutputOptions
{
    /// <summary>Protected output: marked files show path, type, marker and a neutral summary only.</summary>
    public static SensitiveOutputOptions Default { get; } = new();

    /// <summary>
    /// <c>--include-sensitive</c>: also show model summary, target, text excerpt and raw response
    /// of marked files. Only for a human at the local machine; never set it for an external caller.
    /// </summary>
    public bool IncludeSensitive { get; init; }
}

/// <summary>
/// One file as it may leave the machine. Built only by <see cref="SensitiveOutputGuard.Project"/>;
/// CLI commands and exports serialize this record instead of reading <see cref="FileNode"/> and
/// <see cref="FileClassification"/> themselves.
/// </summary>
public sealed record FileOutputRecord
{
    [JsonPropertyName("full_path")] public string FullPath { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("relative_dir")] public string RelativeDir { get; init; } = string.Empty;
    [JsonPropertyName("extension")] public string Extension { get; init; } = string.Empty;
    [JsonPropertyName("file_type")] public string FileType { get; init; } = string.Empty;
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("last_write_time")] public DateTime? LastWriteTime { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;

    /// <summary>Fixed marker tokens (<c>zugangsdaten</c>, <c>schluessel</c>, ...); empty when not sensitive.</summary>
    [JsonPropertyName("sensitive")] public IReadOnlyList<string> Sensitive { get; init; } = [];

    /// <summary>Ids of the rules that marked the file (<c>name:id_rsa</c>, <c>model</c>). No content.</summary>
    [JsonPropertyName("sensitive_rule")] public string? SensitiveRule { get; init; }

    /// <summary>True when content fields were withheld for this record.</summary>
    [JsonPropertyName("protected")] public bool Protected { get; init; }

    [JsonPropertyName("category")] public string? Category { get; init; }

    /// <summary><c>user</c>, <c>ai</c>, <c>sensitive</c> (forced to review because of a marker) or null.</summary>
    [JsonPropertyName("category_source")] public string? CategorySource { get; init; }

    [JsonPropertyName("importance")] public string? Importance { get; init; }
    [JsonPropertyName("confidence")] public double? Confidence { get; init; }

    /// <summary>Code <c>certain</c>, <c>likely</c>, <c>uncertain</c> or <c>review</c>; null without a result or for
    /// results written before schema step 10. Says nothing about the content, so it stays on protected records.</summary>
    [JsonPropertyName("certainty")] public string? Certainty { get; init; }

    /// <summary>How often the file went through Phase 2.</summary>
    [JsonPropertyName("attempts")] public int? Attempts { get; init; }

    /// <summary>Error of the attempt before the current result. Null for a protected record, like <see cref="Error"/>.</summary>
    [JsonPropertyName("previous_error")] public string? PreviousError { get; init; }

    /// <summary>Model summary, or for a protected record the neutral summary.</summary>
    [JsonPropertyName("summary")] public string? Summary { get; init; }

    [JsonPropertyName("suggested_target")] public string? SuggestedTarget { get; init; }
    [JsonPropertyName("model_used")] public string? ModelUsed { get; init; }
    [JsonPropertyName("analyzed_at")] public DateTime? AnalyzedAt { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }

    /// <summary>Text excerpt, if the caller supplied one. Always null for a protected record.</summary>
    [JsonPropertyName("text_excerpt")] public string? TextExcerpt { get; init; }

    /// <summary>Raw model response. Always null for a protected record.</summary>
    [JsonPropertyName("raw_response")] public string? RawResponse { get; init; }
}

/// <summary>
/// The single point through which file data leaves the process. Every CLI command and every
/// export builds its output with <see cref="Project"/>.
/// </summary>
/// <remarks>
/// <para>Boundary: only the local model may read the content of a sensitive file. A model or
/// person outside the machine sees, for a marked file, path, type, marker and a neutral summary
/// written here, never the model's own summary, a text excerpt, the raw response, the suggested
/// target or an error text (an Ollama error body can echo the prompt).</para>
/// <para>A file counts as marked if any source marks it: the stored rule markers
/// (<see cref="FileNode.SensitiveMarkers"/>), the model markers
/// (<see cref="FileClassification.SensitiveMarkers"/>), or the name rules evaluated again right
/// here, so that rows scanned before the rules existed are protected as well.</para>
/// <para>Marked files are never proposed as disposable: a model category
/// <see cref="FileCategory.TrashCandidate"/> becomes <see cref="FileCategory.ReviewNeeded"/>,
/// with or without <see cref="SensitiveOutputOptions.IncludeSensitive"/>. A user override is
/// a human decision and is shown as is.</para>
/// </remarks>
public static class SensitiveOutputGuard
{
    /// <summary>Rule id used when the local model set a marker.</summary>
    public const string ModelRule = "model";

    /// <summary>JSON settings for <see cref="FileOutputRecord"/>: indented off, nulls kept, umlauts readable.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>All markers of a file from every source.</summary>
    public static SensitiveMatch GetMatch(FileNode file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var result = new SensitiveMatch(file.SensitiveMarkers,
            file.SensitiveMarkers == SensitiveMarker.None ? null : file.SensitiveRule ?? "rule");

        var model = file.Classification?.SensitiveMarkers ?? SensitiveMarker.None;
        if (model != SensitiveMarker.None)
            result = result.Combine(new SensitiveMatch(model, ModelRule));

        // Name rules again: cheap, and they cover rows written before the rules existed.
        var byName = SensitiveFileRules.MatchName(file.Name, file.ParentPath);
        if ((byName.Markers & ~result.Markers) != 0)
            result = result.Combine(byName);

        return result;
    }

    public static SensitiveMarker GetMarkers(FileNode file) => GetMatch(file).Markers;

    public static bool IsSensitive(FileNode file) => GetMarkers(file) != SensitiveMarker.None;

    /// <summary>
    /// The category a model may propose for a file: <see cref="FileCategory.TrashCandidate"/>
    /// becomes <see cref="FileCategory.ReviewNeeded"/> when the file is marked.
    /// </summary>
    public static FileCategory ApplyReviewRule(FileCategory category, SensitiveMarker markers)
        => markers != SensitiveMarker.None && category == FileCategory.TrashCandidate
            ? FileCategory.ReviewNeeded
            : category;

    /// <summary>Builds the output record for one file.</summary>
    /// <param name="file">File with <see cref="FileNode.Classification"/> and <see cref="FileNode.Override"/> loaded.</param>
    /// <param name="options">Null means <see cref="SensitiveOutputOptions.Default"/> (protected).</param>
    /// <param name="textExcerpt">Optional text excerpt a command wants to show. Dropped for protected records.</param>
    public static FileOutputRecord Project(
        FileNode file, SensitiveOutputOptions? options = null, string? textExcerpt = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        options ??= SensitiveOutputOptions.Default;

        var match = GetMatch(file);
        var isProtected = match.IsMatch && !options.IncludeSensitive;
        var ai = file.Classification;
        var ovr = file.Override;

        var record = new FileOutputRecord
        {
            FullPath = file.FullPath,
            Name = file.Name,
            RelativeDir = file.RelativeDir,
            Extension = file.Extension,
            FileType = file.FileType.ToString(),
            SizeBytes = file.Size,
            LastWriteTime = file.LastWriteTime == default ? null : file.LastWriteTime,
            Status = file.Status.ToString(),
            Sensitive = SensitiveMarkerNames.ToTokens(match.Markers),
            SensitiveRule = match.Rule,
            Protected = isProtected,
            Certainty = ai?.Certainty.ToCode(),
            Attempts = ai?.Attempts
        };

        if (isProtected)
        {
            // Whitelist: nothing derived from the content except the markers themselves.
            return record with
            {
                Category = ovr is not null ? ovr.OverriddenCategory.ToString() : nameof(FileCategory.ReviewNeeded),
                CategorySource = ovr is not null ? "user" : "sensitive",
                Summary = NeutralSummary(match.Markers, file.FileType)
            };
        }

        string? category;
        string? categorySource;
        if (ovr is not null)
        {
            category = ovr.OverriddenCategory.ToString();
            categorySource = "user";
        }
        else if (ai is not null)
        {
            var effective = ApplyReviewRule(ai.Category, match.Markers);
            category = effective.ToString();
            categorySource = effective != ai.Category ? "sensitive" : "ai";
        }
        else
        {
            category = match.IsMatch ? nameof(FileCategory.ReviewNeeded) : null;
            categorySource = match.IsMatch ? "sensitive" : null;
        }

        return record with
        {
            Category = category,
            CategorySource = categorySource,
            Importance = ai?.Importance.ToString(),
            Confidence = ai?.Confidence,
            Summary = ai?.Summary,
            SuggestedTarget = ovr?.OverriddenTarget ?? ai?.SuggestedTarget,
            ModelUsed = ai?.ModelUsed,
            AnalyzedAt = ai is null || ai.AnalyzedAt == default ? null : ai.AnalyzedAt,
            Error = ai?.Error,
            PreviousError = ai?.PreviousError,
            TextExcerpt = textExcerpt,
            RawResponse = ai?.RawResponse
        };
    }

    /// <summary>Serializes one record as a single JSON object.</summary>
    public static string ToJson(FileOutputRecord record)
        => JsonSerializer.Serialize(record, JsonOptions);

    /// <summary>Serializes several records as a JSON array.</summary>
    public static string ToJson(IEnumerable<FileOutputRecord> records)
        => JsonSerializer.Serialize(records, JsonOptions);

    /// <summary>
    /// Summary built from markers and file type only. Never contains anything the model wrote
    /// or anything read from the file.
    /// </summary>
    public static string NeutralSummary(SensitiveMarker markers, FileType fileType)
    {
        var kinds = new List<string>();
        if (markers.HasFlag(SensitiveMarker.Zugangsdaten)) kinds.Add("credentials");
        if (markers.HasFlag(SensitiveMarker.Schluessel)) kinds.Add("private key material");
        if (markers.HasFlag(SensitiveMarker.PrivateNachricht)) kinds.Add("private messages");
        if (markers.HasFlag(SensitiveMarker.Ausweis)) kinds.Add("identity document");
        if (markers.HasFlag(SensitiveMarker.Finanzen)) kinds.Add("financial records");
        if (markers.HasFlag(SensitiveMarker.Gesundheit)) kinds.Add("health records");

        var what = kinds.Count == 0 ? "sensitive content" : string.Join(", ", kinds);
        return $"Sensitive {fileType} file ({what}). Content withheld; review locally.";
    }
}
