using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// The verdict on one directory of a scan job: what it is, whether it can be handled as a
/// whole, who decided, and why. One row per directory in <c>directory_verdicts</c>.
/// </summary>
public sealed class DirectoryVerdict
{
    public long Id { get; set; }
    public long DirectoryNodeId { get; set; }

    /// <summary>Null only when <see cref="Error"/> is set.</summary>
    public DirectoryVerdictKind? Verdict { get; set; }

    public VerdictSource Source { get; set; }

    /// <summary>Id of the rule that decided, for example <c>marker</c> or <c>inside-unit</c>; null when the model decided.</summary>
    public string? Rule { get; set; }

    /// <summary>Purpose of the directory in one sentence.</summary>
    public string? Purpose { get; set; }

    public RetentionValue? Retention { get; set; }

    /// <summary>Readable justification of the verdict.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// For <c>einheit</c>: the directory that is the whole (itself, or the ancestor it belongs to).
    /// Null for all other verdicts.
    /// </summary>
    public long? UnitDirectoryNodeId { get; set; }

    public DirectoryProfile Profile { get; set; } = new();

    public string? ModelUsed { get; set; }
    public string? RawResponse { get; set; }
    public DateTime JudgedAt { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Set when the sample contradicted the verdict and the directory was raised to
    /// <see cref="DirectoryVerdictKind.Gemischt"/>: the verdict it had before (<see cref="DirectoryVerdictKind.Einheitlich"/>). The sample itself is in <c>directory_samples</c>.
    /// </summary>
    public DirectoryVerdictKind? EscalatedFrom { get; set; }

    /// <summary>Why the directory was raised to mixed, for example which share of the sample deviated.</summary>
    public string? EscalationReason { get; set; }

    // Filled when reading, from directory_nodes; not stored in directory_verdicts.
    public string? DirectoryFullPath { get; set; }
    public string? DirectoryRelativePath { get; set; }
}

/// <summary>What Phase 1 got back for one directory: the pre-assessment for sampling and the verdict.</summary>
public sealed record DirectoryPreAssessmentOutcome(DirectoryClassificationResult Assessment, DirectoryVerdict Verdict);
