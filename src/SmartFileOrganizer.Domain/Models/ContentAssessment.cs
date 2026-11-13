using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// The model and prompt version a file assessment was made with (https://github.com/crack666/smart_file_organizer/issues/38).
/// An assessment of the content store is taken over only when both are equal; a new model or a changed prompt makes
/// older entries invalid without deleting them.
/// </summary>
/// <param name="Model">Model name as sent to the server.</param>
/// <param name="PromptVersion">Derived from the prompts themselves (system message, templates, answer schemas), so a
/// changed prompt gives another version without anyone remembering to raise a number.</param>
public sealed record AssessmentIdentity(string Model, string PromptVersion);

/// <summary>The content a store entry belongs to: full SHA-256 and size. Only these two say "the same content".</summary>
public sealed record ContentKey(string Hash, long Size);

/// <summary>
/// What the model said about one content, kept across jobs in <c>content_assessments</c>. Only what belongs to the
/// content: never a target, a folder verdict, a finding or a decision, which depend on where the file lies.
/// </summary>
public sealed class ContentAssessment
{
    public long Id { get; init; }
    public required ContentKey Key { get; init; }
    public required AssessmentIdentity Identity { get; init; }

    /// <summary>Whether the call asked for the keep hint (files of mixed and uniform folders); part of the lookup,
    /// because the prompt differs.</summary>
    public bool KeepHintAsked { get; init; }

    public FileCategory Category { get; init; }
    public Importance Importance { get; init; }
    public double Confidence { get; init; }
    public ResultCertainty? Certainty { get; init; }
    public string? Summary { get; init; }
    public KeepHint? KeepHint { get; init; }
    public string? KeepReason { get; init; }

    /// <summary>Markers the model found in the content.</summary>
    public SensitiveMarker SensitiveMarkers { get; init; }

    /// <summary>The description of stage 1 (source <c>model</c>); empty for marked content.</summary>
    public IReadOnlyList<FileFact> DescribedFacts { get; init; } = [];

    public DateTime CreatedAt { get; init; }
    public DateTime? LastUsedAt { get; init; }

    /// <summary>How often the entry was taken over instead of a model call.</summary>
    public long UseCount { get; init; }
}

/// <summary>A category a person set for one content (<c>content_corrections</c>); it follows the content into every job.</summary>
public sealed record ContentCorrection(ContentKey Key, FileCategory Category, string? Note, DateTime CorrectedAt);

/// <summary>Counts of the content store for <c>sfo cache stats</c>.</summary>
/// <param name="Groups">Entries per model and prompt version, the current one first.</param>
public sealed record ContentStoreStats(
    long Entries,
    long Current,
    long Stale,
    long Reuses,
    long Corrections,
    long Fingerprints,
    IReadOnlyList<ContentStoreGroup> Groups);

/// <summary>Entries made with one model and prompt version.</summary>
public sealed record ContentStoreGroup(string Model, string PromptVersion, long Entries, long Reuses, DateTime? LastUsedAt);
