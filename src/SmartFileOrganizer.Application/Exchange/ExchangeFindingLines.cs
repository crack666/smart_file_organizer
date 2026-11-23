using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Application.Exchange;

/// <summary>
/// One line of <c>findings.jsonl</c> (format 1.1, https://github.com/crack666/smart_file_organizer/issues/116): a finding
/// of a source as <c>sfo findings</c> computes it, in every state, with the ids of its files in <c>files.jsonl</c> and
/// of its folder in <c>directories.jsonl</c>.
/// </summary>
public sealed record ExchangeFinding
{
    /// <summary>Stable key: the same subject gives the same key in every run.</summary>
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;

    [JsonPropertyName("rank")] public int Rank { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;
    [JsonPropertyName("certainty")] public string Certainty { get; init; } = string.Empty;

    /// <summary><c>sure</c> or <c>unsure</c> (certainty uncertain or review): the part of the list it stands in.</summary>
    [JsonPropertyName("section")] public string Section { get; init; } = string.Empty;

    [JsonPropertyName("state")] public string State { get; init; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;

    /// <summary>The folder the finding is about, relative to the job root; null for an event without a folder.</summary>
    [JsonPropertyName("relative_path")] public string? RelativePath { get; init; }

    /// <summary>Id of that folder in <c>directories.jsonl</c>.</summary>
    [JsonPropertyName("directory_id")] public long? DirectoryId { get; init; }

    [JsonPropertyName("bundle_id")] public long? BundleId { get; init; }
    [JsonPropertyName("bundle_kind")] public string? BundleKind { get; init; }
    [JsonPropertyName("files")] public long Files { get; init; }
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("group")] public string? Group { get; init; }
    [JsonPropertyName("by_file_type")] public IReadOnlyDictionary<string, long> ByFileType { get; init; } = new Dictionary<string, long>();
    [JsonPropertyName("period")] public ExchangePeriod? Period { get; init; }
    [JsonPropertyName("comparison")] public IReadOnlyDictionary<string, long>? Comparison { get; init; }
    [JsonPropertyName("markers")] public IReadOnlyList<string> Markers { get; init; } = [];
    [JsonPropertyName("sensitive_rules")] public IReadOnlyList<string> SensitiveRules { get; init; } = [];
    [JsonPropertyName("action")] public ExchangeFindingAction Action { get; init; } = new();
    [JsonPropertyName("target")] public ExchangeFindingTarget? Target { get; init; }
    [JsonPropertyName("target_status")] public string? TargetStatus { get; init; }
    [JsonPropertyName("target_decided_by")] public string? TargetDecidedBy { get; init; }
    [JsonPropertyName("target_parts")] public IReadOnlyList<ExchangeTargetPart> TargetParts { get; init; } = [];
    [JsonPropertyName("duplicates")] public ExchangeDuplicates? Duplicates { get; init; }
    [JsonPropertyName("examples")] public IReadOnlyList<string> Examples { get; init; } = [];
    [JsonPropertyName("notes")] public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Ids of the files of the finding in <c>files.jsonl</c>, ascending.</summary>
    [JsonPropertyName("file_ids")] public IReadOnlyList<long> FileIds { get; init; } = [];

    [JsonPropertyName("decided_by")] public string? DecidedBy { get; init; }
    [JsonPropertyName("decided_at")] public DateTime? DecidedAt { get; init; }
    [JsonPropertyName("decision_note")] public string? DecisionNote { get; init; }
}

/// <summary>Earliest and latest time of the files of a finding: local wall-clock time without an offset, as the bundles record it.</summary>
public sealed record ExchangePeriod
{
    [JsonPropertyName("from_local")] public string? FromLocal { get; init; }
    [JsonPropertyName("to_local")] public string? ToLocal { get; init; }
    [JsonPropertyName("time_source")] public string? TimeSource { get; init; }
}

/// <summary>The recommended action of a finding.</summary>
public sealed record ExchangeFindingAction
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
    [JsonPropertyName("single_decision")] public bool SingleDecision { get; init; }
}

/// <summary>The proposed place of a finding in a library.</summary>
public sealed record ExchangeFindingTarget
{
    [JsonPropertyName("library_job_id")] public long? LibraryJobId { get; init; }
    [JsonPropertyName("library_root")] public string? LibraryRoot { get; init; }

    /// <summary>Full path of the place.</summary>
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;

    /// <summary>The place relative to the library root, with <c>/</c>.</summary>
    [JsonPropertyName("relative_path")] public string RelativePath { get; init; } = string.Empty;

    [JsonPropertyName("certainty")] public string Certainty { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
    [JsonPropertyName("same_content_files")] public int SameContentFiles { get; init; }
    [JsonPropertyName("content_check")] public string ContentCheck { get; init; } = string.Empty;
}

/// <summary>For target status <c>split</c>: one part of a finding by content, with its own place.</summary>
public sealed record ExchangeTargetPart
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("library_job_id")] public long? LibraryJobId { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("relative_path")] public string? RelativePath { get; init; }
    [JsonPropertyName("certainty")] public string? Certainty { get; init; }
    [JsonPropertyName("content_check")] public string ContentCheck { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
    [JsonPropertyName("decided_by")] public string? DecidedBy { get; init; }

    /// <summary>Ids of the files of this part in <c>files.jsonl</c>.</summary>
    [JsonPropertyName("file_ids")] public IReadOnlyList<long> FileIds { get; init; } = [];
}

/// <summary>Identical files at several places, for a <c>duplicate</c> finding.</summary>
public sealed record ExchangeDuplicates
{
    [JsonPropertyName("groups")] public int Groups { get; init; }
    [JsonPropertyName("redundant_files")] public long RedundantFiles { get; init; }
    [JsonPropertyName("redundant_bytes")] public long RedundantBytes { get; init; }

    /// <summary>Folders relative to the job root (at most a few; <see cref="DirectoriesTotal"/> counts all).</summary>
    [JsonPropertyName("directories")] public IReadOnlyList<string> Directories { get; init; } = [];

    [JsonPropertyName("directories_total")] public int DirectoriesTotal { get; init; }

    /// <summary>The copy to keep, relative to the job root.</summary>
    [JsonPropertyName("keep")] public string Keep { get; init; } = string.Empty;

    /// <summary><c>files</c> (copies of single files) or <c>folders</c> (a whole folder exists twice).</summary>
    [JsonPropertyName("scope")] public string Scope { get; init; } = string.Empty;
}

/// <summary>A plan of the job in <c>job.json</c>; its actions are the lines of <c>plan.jsonl</c>.</summary>
public sealed record ExchangePlan
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; init; }

    /// <summary>The library jobs whose roots its targets may lie in.</summary>
    [JsonPropertyName("library_job_ids")] public IReadOnlyList<long> LibraryJobIds { get; init; } = [];

    /// <summary>What the creation left out and why.</summary>
    [JsonPropertyName("notes")] public IReadOnlyList<string> Notes { get; init; } = [];

    [JsonPropertyName("actions")] public int Actions { get; init; }
}

/// <summary>
/// One line of <c>plan.jsonl</c> (format 1.1): an action of a plan of the job with its state, as <c>sfo plan show</c> lists
/// it, with the id of its file or folder in <c>files.jsonl</c> or <c>directories.jsonl</c> and the key of its finding.
/// </summary>
public sealed record ExchangePlanAction
{
    [JsonPropertyName("plan_id")] public long PlanId { get; init; }
    [JsonPropertyName("id")] public long Id { get; init; }

    /// <summary>Key of the finding in <c>findings.jsonl</c> the action comes from.</summary>
    [JsonPropertyName("finding_key")] public string FindingKey { get; init; } = string.Empty;

    [JsonPropertyName("finding_kind")] public string FindingKind { get; init; } = string.Empty;

    /// <summary>The title of the finding: what and where, in one line.</summary>
    [JsonPropertyName("unit")] public string Unit { get; init; } = string.Empty;

    [JsonPropertyName("source_kind")] public string SourceKind { get; init; } = string.Empty;

    /// <summary>The file or folder relative to the job root, with <c>/</c>.</summary>
    [JsonPropertyName("relative_path")] public string RelativePath { get; init; } = string.Empty;

    /// <summary>For a file action: its id in <c>files.jsonl</c>.</summary>
    [JsonPropertyName("file_id")] public long? FileId { get; init; }

    /// <summary>For a folder action: its id in <c>directories.jsonl</c>.</summary>
    [JsonPropertyName("directory_id")] public long? DirectoryId { get; init; }

    [JsonPropertyName("files")] public long Files { get; init; }
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;
    [JsonPropertyName("recommended")] public string Recommended { get; init; } = string.Empty;

    /// <summary>Full path at the target; for <c>delete_source</c> the place in the library that holds the content.</summary>
    [JsonPropertyName("target_path")] public string? TargetPath { get; init; }

    [JsonPropertyName("library_job_id")] public long? LibraryJobId { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("origin")] public string Origin { get; init; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; init; } = string.Empty;
    [JsonPropertyName("sensitive")] public bool Sensitive { get; init; }
    [JsonPropertyName("single_decision")] public bool SingleDecision { get; init; }
    [JsonPropertyName("decided_by")] public string? DecidedBy { get; init; }
    [JsonPropertyName("decided_at")] public DateTime? DecidedAt { get; init; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; init; }
}
