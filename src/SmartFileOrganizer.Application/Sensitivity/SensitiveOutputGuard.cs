using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Sensitivity;

/// <summary>
/// How much of a marked file leaves the process, setting <c>Sensitive:Disclosure</c>
/// (https://github.com/crack666/smart_file_organizer/issues/99, 14.11.2026).
/// </summary>
public enum SensitiveDisclosure
{
    /// <summary><c>open</c> (default): a marker is a hint, nothing is hidden, except the literal text of a file with an access data marker.</summary>
    Open,

    /// <summary><c>restricted</c>: the behaviour before 14.11.2026; a marked file shows path, type and marker only, and a folder
    /// holding one lists no names, no subfolders and no description.</summary>
    Restricted
}

/// <summary>
/// Options for everything that leaves the process (CLI output, exports), and the one place that decides what of a marked
/// file or folder is shown (https://github.com/crack666/smart_file_organizer/issues/99). Every command asks these
/// methods instead of deciding per command.
/// </summary>
public sealed record SensitiveOutputOptions
{
    /// <summary>Configuration section of the setting.</summary>
    public const string Section = "Sensitive";

    /// <summary>Codes of <see cref="SensitiveDisclosure"/> in the settings.</summary>
    public const string OpenCode = "open";

    public const string RestrictedCode = "restricted";

    /// <summary>
    /// The markers that mean access data: <c>credentials</c> (passwords, logins, tokens, API keys, <c>.env</c>) and <c>keys</c>
    /// (private keys, wallets, seed phrases). The literal text of such a file is never shown, whatever the setting.
    /// </summary>
    public const SensitiveMarker AccessDataMarkers = SensitiveMarker.Credentials | SensitiveMarker.Keys;

    /// <summary>The default of the setting: <see cref="SensitiveDisclosure.Open"/>.</summary>
    public static SensitiveOutputOptions Default { get; } = new();

    /// <summary>The behaviour before 14.11.2026.</summary>
    public static SensitiveOutputOptions Restricted { get; } = new() { Disclosure = SensitiveDisclosure.Restricted };

    /// <summary>Setting <c>Sensitive:Disclosure</c>.</summary>
    public SensitiveDisclosure Disclosure { get; init; } = SensitiveDisclosure.Open;

    /// <summary>
    /// <c>--include-sensitive</c>: with <see cref="SensitiveDisclosure.Restricted"/>, also show model summary, text excerpt
    /// and raw response of marked files. Only for a human at the local machine. It never shows the literal text of a file
    /// with an access data marker (<see cref="AccessDataMarkers"/>).
    /// </summary>
    public bool IncludeSensitive { get; init; }

    /// <summary>True when <paramref name="markers"/> hold an access data marker.</summary>
    public static bool IsAccessData(SensitiveMarker markers) => (markers & AccessDataMarkers) != 0;

    /// <summary>
    /// Names, subfolders, purpose and stored descriptions of a folder that is marked or holds a marked file, and the
    /// name of a marked file in a listing of names or in a model text about a folder. Withheld only with
    /// <see cref="SensitiveDisclosure.Restricted"/> without <see cref="IncludeSensitive"/>.
    /// </summary>
    public bool ShowsStructure(SensitiveMarker markers) =>
        markers == SensitiveMarker.None || Disclosure == SensitiveDisclosure.Open || IncludeSensitive;

    /// <summary>
    /// The short classification of a marked file: category, importance, confidence and the stored description facts
    /// with codes and short words (document kind, issuer or service, year, occasion, setting, season, people).
    /// </summary>
    public bool ShowsClassification(SensitiveMarker markers) =>
        markers == SensitiveMarker.None || Disclosure == SensitiveDisclosure.Open || IncludeSensitive;

    /// <summary>
    /// Fields that can carry the literal content of a marked file: the model summary, a text excerpt, the raw model
    /// response, error texts, free description texts (<c>scene</c>, <c>activity</c>) and model texts naming the file.
    /// Never for access data; otherwise withheld only with <see cref="SensitiveDisclosure.Restricted"/> without
    /// <see cref="IncludeSensitive"/>.
    /// </summary>
    public bool ShowsContent(SensitiveMarker markers) =>
        markers == SensitiveMarker.None
        || (!IsAccessData(markers) && (Disclosure == SensitiveDisclosure.Open || IncludeSensitive));

    /// <summary>
    /// The description facts of a file that may be shown: all of them when <see cref="ShowsContent"/>, the short ones
    /// (codes, issuer, year; not the free texts <c>scene</c> and <c>activity</c>) when only
    /// <see cref="ShowsClassification"/>, none otherwise.
    /// </summary>
    public IReadOnlyList<FileFact> ShownFacts(SensitiveMarker markers, IReadOnlyList<FileFact> facts)
    {
        if (ShowsContent(markers)) return facts;
        if (!ShowsClassification(markers)) return [];
        return facts.Where(f => f.Key is not (FileFactKey.Scene or FileFactKey.Activity or FileFactKey.Title
            or FileFactKey.Author or FileFactKey.Artist or FileFactKey.Album)).ToList();
    }

    /// <summary>
    /// Reads <c>Sensitive:Disclosure</c>: <c>open</c> (also when not set) or <c>restricted</c>, case ignored.
    /// </summary>
    /// <exception cref="ArgumentException">Any other value.</exception>
    public static SensitiveOutputOptions FromSetting(string? disclosure) => disclosure?.Trim().ToLowerInvariant() switch
    {
        null or "" or OpenCode => Default,
        RestrictedCode => Restricted,
        _ => throw new ArgumentException($"Sensitive:Disclosure must be '{OpenCode}' or '{RestrictedCode}', not '{disclosure}'.")
    };
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

    /// <summary>Fixed marker tokens (<c>credentials</c>, <c>keys</c>, ...); empty when not sensitive.</summary>
    [JsonPropertyName("sensitive")] public IReadOnlyList<string> Sensitive { get; init; } = [];

    /// <summary>Ids of the rules that marked the file (<c>name:id_rsa</c>, <c>model</c>). No content.</summary>
    [JsonPropertyName("sensitive_rule")] public string? SensitiveRule { get; init; }

    /// <summary>True when content fields were withheld for this record.</summary>
    [JsonPropertyName("protected")] public bool Protected { get; init; }

    [JsonPropertyName("category")] public string? Category { get; init; }

    /// <summary>A <see cref="ResultOrigin"/> code: <c>human</c> (an override), <c>model</c>, <c>rule</c> (forced to review because of a marker) or null.</summary>
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
/// <para>What a marked file shows is decided by <see cref="SensitiveOutputOptions"/>
/// (https://github.com/crack666/smart_file_organizer/issues/99, 14.11.2026). With <c>Sensitive:Disclosure</c>
/// <c>open</c> (default) a marked file is shown like any other, with its markers as a hint. A file with an access data
/// marker (<see cref="SensitiveOutputOptions.AccessDataMarkers"/>) keeps its classification but never shows the model's
/// own summary, a text excerpt, the raw response or an error text (an Ollama error body can echo the prompt); its
/// summary is the neutral one written here. With <c>restricted</c>, the behaviour before that date, every marked file
/// shows path, type, marker and the neutral summary only.</para>
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
    /// <param name="options">Null means <see cref="SensitiveOutputOptions.Default"/> (<c>open</c>).</param>
    /// <param name="textExcerpt">Optional text excerpt a command wants to show. Dropped for protected records.</param>
    public static FileOutputRecord Project(
        FileNode file, SensitiveOutputOptions? options = null, string? textExcerpt = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        options ??= SensitiveOutputOptions.Default;

        var match = GetMatch(file);
        var isProtected = !options.ShowsContent(match.Markers);
        var classified = options.ShowsClassification(match.Markers);
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

        if (isProtected && !classified)
        {
            // Restricted: nothing derived from the content except the markers themselves.
            return record with
            {
                Category = ovr is not null ? ovr.OverriddenCategory.ToString() : nameof(FileCategory.ReviewNeeded),
                CategorySource = (ovr is not null ? ResultOrigin.Human : ResultOrigin.Rule).ToCode(),
                Summary = NeutralSummary(match.Markers, file.FileType)
            };
        }

        string? category;
        string? categorySource;
        if (ovr is not null)
        {
            category = ovr.OverriddenCategory.ToString();
            categorySource = ResultOrigin.Human.ToCode();
        }
        else if (ai is not null)
        {
            var effective = ApplyReviewRule(ai.Category, match.Markers);
            category = effective.ToString();
            categorySource = (effective != ai.Category ? ResultOrigin.Rule : ResultOrigin.Model).ToCode();
        }
        else
        {
            category = match.IsMatch ? nameof(FileCategory.ReviewNeeded) : null;
            categorySource = match.IsMatch ? ResultOrigin.Rule.ToCode() : null;
        }

        if (isProtected)
        {
            // Access data: the classification, but no field that can carry the literal text of the file (model summary,
            // excerpt, raw response, error texts, which can echo the prompt).
            return record with
            {
                Category = category,
                CategorySource = categorySource,
                Importance = ai?.Importance.ToString(),
                Confidence = ai?.Confidence,
                Summary = NeutralSummary(match.Markers, file.FileType),
                ModelUsed = ai?.ModelUsed,
                AnalyzedAt = ai is null || ai.AnalyzedAt == default ? null : ai.AnalyzedAt
            };
        }

        return record with
        {
            Category = category,
            CategorySource = categorySource,
            Importance = ai?.Importance.ToString(),
            Confidence = ai?.Confidence,
            Summary = ai?.Summary,
            // Deprecated (https://github.com/crack666/smart_file_organizer/issues/74): the old free-text target per file
            // was made up by the model; it is never shown as a target again. The key stays, always empty.
            SuggestedTarget = null,
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
        if (markers.HasFlag(SensitiveMarker.Credentials)) kinds.Add("credentials");
        if (markers.HasFlag(SensitiveMarker.Keys)) kinds.Add("private key material");
        if (markers.HasFlag(SensitiveMarker.PrivateMessage)) kinds.Add("private messages");
        if (markers.HasFlag(SensitiveMarker.Identity)) kinds.Add("identity document");
        if (markers.HasFlag(SensitiveMarker.Finance)) kinds.Add("financial records");
        if (markers.HasFlag(SensitiveMarker.Health)) kinds.Add("health records");

        var what = kinds.Count == 0 ? "sensitive content" : string.Join(", ", kinds);
        return $"Sensitive {fileType} file ({what}). Content withheld; review locally.";
    }
}
