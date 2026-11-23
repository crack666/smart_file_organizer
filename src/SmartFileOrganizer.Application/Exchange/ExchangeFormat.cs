using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Exchange;

/// <summary>
/// The open exchange format of a job (https://github.com/crack666/smart_file_organizer/issues/34): <c>job.json</c>,
/// <c>directories.jsonl</c> and <c>files.jsonl</c>, one entry per line, each line readable on its own. The schemas are
/// in <c>schemas/exchange/v1/</c> of the repository and embedded in this assembly (<see cref="ExchangeSchemas"/>); the
/// rules for changes and a full example are in <c>docs/data-format.md</c>.
/// </summary>
public static class ExchangeFormat
{
    /// <summary>Value of <c>format</c> in <c>job.json</c>.</summary>
    public const string Name = "sfo-exchange";

    /// <summary>
    /// Major and minor version. New fields raise the minor version; a field that changes meaning or type, or a code
    /// that is removed, raises the major version and gets a new schema folder.
    /// </summary>
    public const string Version = "1.1";

    /// <summary>The major version of <see cref="Version"/>; the schema folder is <c>v{Major}</c>.</summary>
    public const int Major = 1;

    public const string JobFile = "job.json";
    public const string DirectoriesFile = "directories.jsonl";
    public const string FilesFile = "files.jsonl";

    /// <summary>Since 1.1: the findings of a source (https://github.com/crack666/smart_file_organizer/issues/116).</summary>
    public const string FindingsFile = "findings.jsonl";

    /// <summary>Since 1.1: the actions of the plans of a source.</summary>
    public const string PlanFile = "plan.jsonl";

    /// <summary>
    /// Writing options: snake_case keys as declared, nulls kept (a reader sees every key), umlauts readable, one line per
    /// entry. Lines end with <c>\n</c> on every system.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    /// <summary>Like <see cref="JsonOptions"/>, indented, for <c>job.json</c>.</summary>
    public static JsonSerializerOptions IndentedOptions { get; } = new(JsonOptions) { WriteIndented = true };

    /// <summary>
    /// Code of an enum value whose code is its name in <c>snake_case</c> (<see cref="FileCategory"/>,
    /// <see cref="Importance"/>, <see cref="FileType"/>, <see cref="FileNodeStatus"/>, <see cref="JobStatus"/>), as the
    /// JSON of the CLI and the truth of the evaluation write them.
    /// </summary>
    public static string Snake<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>All <see cref="Snake{TEnum}"/> codes of an enum, in declaration order.</summary>
    public static IReadOnlyList<string> SnakeCodes<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(Snake).ToList();

    /// <summary>Reads a <see cref="Snake{TEnum}"/> code; false for anything else.</summary>
    public static bool TryParseSnake<TEnum>(string? code, out TEnum value) where TEnum : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(Snake(candidate), code, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>A stored relative path with <c>/</c> as separator on every system; <c>.</c> for the job root.</summary>
    public static string PortablePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath == ".") return ".";
        return relativePath.Replace('\\', '/').Trim('/');
    }

    /// <summary>
    /// A stored time as UTC. sfo stores UTC everywhere (scan times from <c>GetLastWriteTimeUtc</c>, results from
    /// <c>UtcNow</c>), but SQLite gives them back without a kind; the format writes them with <c>Z</c>.
    /// </summary>
    public static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    /// <summary>Joins a portable directory path and a name.</summary>
    public static string Join(string directory, string name) => directory == "." ? name : $"{directory}/{name}";
}

/// <summary><c>job.json</c>: what was exported, from which job, in which version of the format.</summary>
public sealed record ExchangeJob
{
    [JsonPropertyName("format")] public string Format { get; init; } = ExchangeFormat.Name;
    [JsonPropertyName("format_version")] public string FormatVersion { get; init; } = ExchangeFormat.Version;
    [JsonPropertyName("exported_at")] public DateTime ExportedAt { get; init; }

    /// <summary><c>open</c> or <c>restricted</c>, the setting <c>Sensitive:Disclosure</c> the export ran with.</summary>
    [JsonPropertyName("disclosure")] public string Disclosure { get; init; } = "open";

    [JsonPropertyName("include_sensitive")] public bool IncludeSensitive { get; init; }
    [JsonPropertyName("job_id")] public long JobId { get; init; }
    [JsonPropertyName("root_path")] public string RootPath { get; init; } = string.Empty;
    [JsonPropertyName("role")] public string Role { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("structure_only")] public bool StructureOnly { get; init; }
    [JsonPropertyName("rules_name")] public string? RulesName { get; init; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; init; }
    [JsonPropertyName("completed_at")] public DateTime? CompletedAt { get; init; }
    [JsonPropertyName("directories")] public int Directories { get; init; }
    [JsonPropertyName("files")] public int Files { get; init; }

    /// <summary>Lines of <c>findings.jsonl</c>; null when it was not written (a library job). Since 1.1.</summary>
    [JsonPropertyName("findings")] public int? Findings { get; init; }

    /// <summary>Lines of <c>plan.jsonl</c>; null when it was not written (a library job). Since 1.1.</summary>
    [JsonPropertyName("plan_actions")] public int? PlanActions { get; init; }

    /// <summary>The plans of the job, oldest first; their actions are the lines of <c>plan.jsonl</c>. Since 1.1.</summary>
    [JsonPropertyName("plans")] public IReadOnlyList<ExchangePlan> Plans { get; init; } = [];

    /// <summary>The line files next to <c>job.json</c>, in reading order.</summary>
    [JsonPropertyName("parts")] public IReadOnlyList<string> Parts { get; init; } = [ExchangeFormat.DirectoriesFile, ExchangeFormat.FilesFile];
}

/// <summary>One line of <c>directories.jsonl</c>.</summary>
public sealed record ExchangeDirectory
{
    [JsonPropertyName("id")] public long Id { get; init; }

    /// <summary>Id of the parent directory; null for the job root.</summary>
    [JsonPropertyName("parent_id")] public long? ParentId { get; init; }

    /// <summary>Path relative to the job root with <c>/</c>; <c>.</c> for the root.</summary>
    [JsonPropertyName("relative_path")] public string RelativePath { get; init; } = ".";

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("depth")] public int Depth { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("scan_reason")] public string? ScanReason { get; init; }
    [JsonPropertyName("link_kind")] public string? LinkKind { get; init; }
    [JsonPropertyName("link_target")] public string? LinkTarget { get; init; }
    [JsonPropertyName("error_cause")] public string? ErrorCause { get; init; }
    [JsonPropertyName("direct_files")] public long DirectFiles { get; init; }
    [JsonPropertyName("files")] public long Files { get; init; }
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("subdirs")] public int Subdirs { get; init; }
    [JsonPropertyName("sensitive")] public IReadOnlyList<string> Sensitive { get; init; } = [];
    [JsonPropertyName("sensitive_rule")] public string? SensitiveRule { get; init; }

    /// <summary>Marked files in the subtree.</summary>
    [JsonPropertyName("sensitive_files")] public long SensitiveFiles { get; init; }

    /// <summary>True when a model text about the folder was replaced because it named a marked file.</summary>
    [JsonPropertyName("text_withheld")] public bool TextWithheld { get; init; }

    [JsonPropertyName("verdict")] public ExchangeVerdict? Verdict { get; init; }

    /// <summary>Phase 3 summary of the folder.</summary>
    [JsonPropertyName("summary")] public string? Summary { get; init; }
}

/// <summary>The verdict of a directory inside its line.</summary>
public sealed record ExchangeVerdict
{
    /// <summary>Verdict code; null when Phase 1 failed (<see cref="Error"/>).</summary>
    [JsonPropertyName("verdict")] public string? Verdict { get; init; }

    [JsonPropertyName("source")] public string Source { get; init; } = string.Empty;
    [JsonPropertyName("rule")] public string? Rule { get; init; }
    [JsonPropertyName("retention")] public string? Retention { get; init; }
    [JsonPropertyName("purpose")] public string? Purpose { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }

    /// <summary>For <c>unit</c>: the directory that is the whole.</summary>
    [JsonPropertyName("unit_directory_id")] public long? UnitDirectoryId { get; init; }

    [JsonPropertyName("escalated_from")] public string? EscalatedFrom { get; init; }
    [JsonPropertyName("escalation_reason")] public string? EscalationReason { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("judged_at")] public DateTime? JudgedAt { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }

    /// <summary>What the scan counted in the folder before it was judged (extensions, types, dates, name patterns).</summary>
    [JsonPropertyName("profile")] public JsonElement? Profile { get; init; }
}

/// <summary>One line of <c>files.jsonl</c>; also the input of <c>sfo import-results</c>.</summary>
public sealed record ExchangeFile
{
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("directory_id")] public long? DirectoryId { get; init; }

    /// <summary>Path relative to the job root with <c>/</c>. The key an import matches on.</summary>
    [JsonPropertyName("relative_path")] public string RelativePath { get; init; } = string.Empty;

    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("extension")] public string? Extension { get; init; }
    [JsonPropertyName("file_type")] public string? FileType { get; init; }
    [JsonPropertyName("size_bytes")] public long? SizeBytes { get; init; }
    [JsonPropertyName("last_write_time")] public DateTime? LastWriteTime { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("analysis_skip")] public string? AnalysisSkip { get; init; }
    [JsonPropertyName("scan_error_cause")] public string? ScanErrorCause { get; init; }
    [JsonPropertyName("sensitive")] public IReadOnlyList<string> Sensitive { get; init; } = [];
    [JsonPropertyName("sensitive_rule")] public string? SensitiveRule { get; init; }

    /// <summary>True when fields that can carry the text of the file were withheld.</summary>
    [JsonPropertyName("protected")] public bool Protected { get; init; }

    [JsonPropertyName("result")] public ExchangeResult? Result { get; init; }
    [JsonPropertyName("facts")] public IReadOnlyList<ExchangeFact> Facts { get; init; } = [];
    [JsonPropertyName("content")] public ExchangeContent? Content { get; init; }
}

/// <summary>The judgement of a file inside its line.</summary>
public sealed record ExchangeResult
{
    [JsonPropertyName("category")] public string Category { get; init; } = string.Empty;

    /// <summary>Who set <see cref="Category"/>: <c>human</c> (a correction), <c>rule</c> (raised to review because of a
    /// marker), else the origin of the result.</summary>
    [JsonPropertyName("category_source")] public string? CategorySource { get; init; }

    /// <summary>Origin of the result: <c>rule</c>, <c>model</c>, <c>inherited</c>, <c>human</c>, <c>agent</c>,
    /// <c>external</c>, <c>reused</c>.</summary>
    [JsonPropertyName("origin")] public string? Origin { get; init; }

    [JsonPropertyName("importance")] public string? Importance { get; init; }
    [JsonPropertyName("confidence")] public double? Confidence { get; init; }
    [JsonPropertyName("certainty")] public string? Certainty { get; init; }
    [JsonPropertyName("summary")] public string? Summary { get; init; }
    [JsonPropertyName("keep_hint")] public string? KeepHint { get; init; }
    [JsonPropertyName("keep_reason")] public string? KeepReason { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("analyzed_at")] public DateTime? AnalyzedAt { get; init; }
    [JsonPropertyName("inherited_from_directory_id")] public long? InheritedFromDirectoryId { get; init; }
    [JsonPropertyName("attempts")] public int? Attempts { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }

    /// <summary>The answer of the model as it came; null for a protected file.</summary>
    [JsonPropertyName("raw_response")] public string? RawResponse { get; init; }
}

/// <summary>One stored fact of a file.</summary>
public sealed record ExchangeFact
{
    [JsonPropertyName("fact")] public string Fact { get; init; } = string.Empty;
    [JsonPropertyName("value")] public string Value { get; init; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; init; } = string.Empty;
    [JsonPropertyName("certainty")] public string? Certainty { get; init; }
}

/// <summary>The comparison of a file with the libraries of the newest <c>sfo compare</c>.</summary>
public sealed record ExchangeContent
{
    [JsonPropertyName("state")] public string? State { get; init; }

    /// <summary>The library job that holds the same content; null unless <c>backed_up</c>.</summary>
    [JsonPropertyName("library_job_id")] public long? LibraryJobId { get; init; }
}
