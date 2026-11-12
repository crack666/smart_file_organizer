using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// The sample of a directory judged uniform (<see cref="DirectoryVerdictKind.Uniform"/>): which files were drawn and why, and what came of it.
/// One row per directory in <c>directory_samples</c>.
/// </summary>
/// <remarks>
/// While <see cref="State"/> is <see cref="SampleState.Open"/> the other eligible files of the directory
/// wait as <see cref="FileNodeStatus.Deferred"/>. Confirming writes their inherited results, escalating
/// raises the verdict to mixed and releases them for single judgement; both happen in one
/// transaction, so no directory is left half inherited.
/// </remarks>
public sealed class DirectorySample
{
    public long Id { get; set; }
    public long DirectoryNodeId { get; set; }

    /// <summary>Start value of the draw, derived from job and directory path; the same input draws the same sample.</summary>
    public string Seed { get; set; } = string.Empty;

    public SampleState State { get; set; }

    /// <summary>Files of the directory the classification handles (image, video, document) when the sample was drawn.</summary>
    public int EligibleCount { get; set; }

    public List<SampleMember> Members { get; set; } = [];

    /// <summary>Threshold in force when the sample was evaluated: the largest share of deviating or failed members that still confirms.</summary>
    public double MaxDeviationShare { get; set; }

    /// <summary>Most frequent category in the sample; what the other files inherit when confirmed.</summary>
    public FileCategory? MajorityCategory { get; set; }

    /// <summary>Members judged into another category than <see cref="MajorityCategory"/>.</summary>
    public int DeviatingCount { get; set; }

    /// <summary>Members without a usable result (model error).</summary>
    public int FailedCount { get; set; }

    /// <summary>(<see cref="DeviatingCount"/> + <see cref="FailedCount"/>) / members; null while open.</summary>
    public double? DeviationShare { get; set; }

    public int InheritedCount { get; set; }

    /// <summary>Confidence written to every inherited result (capped).</summary>
    public double? InheritedConfidence { get; set; }

    /// <summary>Readable outcome: why confirmed or why raised.</summary>
    public string? Reason { get; set; }

    public DateTime DrawnAt { get; set; }
    public DateTime? DecidedAt { get; set; }

    // Filled when reading, from directory_nodes; not stored in directory_samples.
    public string? DirectoryFullPath { get; set; }
    public string? DirectoryRelativePath { get; set; }
}

/// <summary>One drawn file of a <see cref="DirectorySample"/>.</summary>
public sealed class SampleMember
{
    public long FileNodeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public SampleRole Role { get; set; }

    /// <summary>Category the model gave; null while open or when the call failed.</summary>
    public FileCategory? Category { get; set; }

    /// <summary>True when <see cref="Category"/> differs from the majority, or the call failed.</summary>
    public bool? Deviates { get; set; }

    public string? Error { get; set; }
}
