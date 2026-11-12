using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// A correction by the human, kept as a test case: what the model answered, what is right, and
/// the evidence the model saw. One row per kind and subject in <c>eval_cases</c>; a second
/// correction of the same subject updates the row.
/// </summary>
/// <remarks>
/// Cases can carry private content and stay in the local database; they are never exported.
/// For a sensitive file only path and markers are stored, never an excerpt
/// (<see cref="EvidenceJson"/> is null).
/// </remarks>
public sealed class EvalCase
{
    public long Id { get; set; }
    public EvalCaseKind Kind { get; set; }
    public EvalCaseOrigin Origin { get; set; }

    public long? JobId { get; set; }
    public long? FileNodeId { get; set; }

    /// <summary>Full path of the file (or later: directory) the case is about.</summary>
    public string SubjectPath { get; set; } = string.Empty;

    /// <summary>Path relative to the scan root, with forward slashes.</summary>
    public string? RelativePath { get; set; }

    /// <summary>The right answer as a code of the kind, for example <c>invoice</c> for a category.</summary>
    public string Expected { get; set; } = string.Empty;

    /// <summary>What the model answered when the human corrected it.</summary>
    public string? Observed { get; set; }

    public string? ObservedModel { get; set; }

    /// <summary>Markers of the file at the time of the correction (rule and model). Set means: no evidence stored.</summary>
    public SensitiveMarker SensitiveMarkers { get; set; }

    /// <summary>The input the model saw, as JSON; null for sensitive files.</summary>
    public string? EvidenceJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool IsSensitive => SensitiveMarkers != SensitiveMarker.None;
}

/// <summary>
/// One evaluation run with everything needed to compare it with another: when, which model,
/// which prompts (fingerprint), which settings and thresholds, and the full report.
/// </summary>
public sealed class EvalRun
{
    public long Id { get; set; }
    public EvalRunMode Mode { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }

    /// <summary>What was measured: corpus name, generator version and seed, or <c>cases</c>.</summary>
    public string Subject { get; set; } = string.Empty;

    public long? JobId { get; set; }
    public string? Model { get; set; }

    /// <summary>File name of the scripted answers when the run used a stub instead of Ollama.</summary>
    public string? Stub { get; set; }

    public string? PromptFingerprint { get; set; }
    public string? AppVersion { get; set; }

    /// <summary>Settings and thresholds of the run, as a JSON object of strings.</summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>The report (scores and failures), as JSON.</summary>
    public string ReportJson { get; set; } = "{}";
}
