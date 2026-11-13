using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Findings;

/// <summary>The recommended kind of action with its reason (rules in the comment on
/// https://github.com/crack666/smart_file_organizer/issues/12). Nothing is executed here.</summary>
/// <param name="SingleDecision">True for sensitive files: always decided one by one, never in a batch.</param>
public sealed record FindingAction(PlanActionKind Kind, string Reason, bool SingleDecision = false);

/// <summary>
/// A proposed place in the library. <see cref="SameContentFiles"/> counts files of the finding whose content already
/// lies there; <see cref="ContentCheck"/> says whether the content of the unit was compared with what the place holds.
/// </summary>
public sealed record FindingTarget(
    string Path,
    string RelativePath,
    ResultCertainty Certainty,
    string Reason,
    int SameContentFiles,
    TargetContentCheck ContentCheck);

/// <summary>
/// Identical files at several places: how many groups, how much is redundant, which copy to keep.
/// <see cref="Scope"/> is <c>files</c> for copies of single files, <c>folders</c> for a whole program or system folder
/// that exists twice (the directories are then the copied folders, not the folders of the files).
/// </summary>
public sealed record FindingDuplicates(
    int Groups,
    long RedundantFiles,
    long RedundantBytes,
    IReadOnlyList<string> Directories,
    int DirectoriesTotal,
    string Keep,
    string Scope = FindingDuplicates.FilesScope)
{
    public const string FilesScope = "files";
    public const string FoldersScope = "folders";
}

/// <summary>Earliest and latest time of the files, as the bundles record it (local wall-clock time).</summary>
public sealed record FindingPeriod(DateTime? From, DateTime? To, TimeSource? Source);

/// <summary>
/// One finding: a short, checkable statement about a part of a source, with the files it is about, a certainty
/// derived from the results it rests on, and a recommended action. Built from stored data only; see
/// <see cref="FindingRules"/>.
/// </summary>
public sealed class Finding
{
    /// <summary>Stable key: the same subject gives the same key in every run (<c>in_noise-4e1f0a9b2c</c>).</summary>
    public required string Key { get; init; }

    public required FindingKind Kind { get; init; }
    public required ResultCertainty Certainty { get; set; }

    /// <summary>One short line: what and where.</summary>
    public required string Title { get; init; }

    /// <summary>One sentence a person can check without looking anything up.</summary>
    public required string Reason { get; set; }

    /// <summary>The folder or bundle root the finding is about (full path); null for an event without a folder.</summary>
    public string? Path { get; init; }

    /// <summary><see cref="Path"/> relative to the job root, with <c>/</c>; <c>.</c> for the root.</summary>
    public string? RelativePath { get; init; }

    /// <summary>The bundle the finding is about, as built now (ids change with every <c>sfo bundles build</c>).</summary>
    public long? BundleId { get; init; }

    public BundleKind? BundleKind { get; init; }

    public long Files { get; init; }
    public long Bytes { get; set; }

    /// <summary>
    /// For <c>not_backed_up</c> and <c>in_noise</c>: the kind of files (<see cref="FindingRules.GroupOf"/>), so a mixed
    /// folder falls into one decision per kind. Null for the other kinds.
    /// </summary>
    public string? Group { get; init; }

    public IReadOnlyDictionary<string, long> ByFileType { get; init; } = new Dictionary<string, long>();
    public FindingPeriod? Period { get; init; }

    /// <summary>Comparison states of the files, when there is a comparison with a library.</summary>
    public IReadOnlyDictionary<string, long>? Comparison { get; init; }

    /// <summary>For <see cref="FindingKind.Sensitive"/>: marker codes and the ids of the rules that set them. Never content.</summary>
    public IReadOnlyList<string> Markers { get; set; } = [];

    public IReadOnlyList<string> SensitiveRules { get; set; } = [];

    public required FindingAction Action { get; set; }
    public FindingTarget? Target { get; set; }
    public FindingDuplicates? Duplicates { get; set; }

    /// <summary>A few file paths relative to the job root, so the finding can be checked at a glance.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];

    /// <summary>What a person should know beyond the reason: what is left out, what was not checked.</summary>
    public List<string> Notes { get; init; } = [];

    /// <summary>Files of the finding (ids of this job); for a skipped folder none.</summary>
    public IReadOnlyList<long> FileIds { get; init; } = [];

    /// <summary>Directories (full paths) the files lie in, or the marked folder itself; used by <c>--under</c>.</summary>
    public IReadOnlyList<string> Directories { get; set; } = [];

    // Set when read, from the stored decision.
    public FindingState State { get; set; } = FindingState.Open;
    public ResultOrigin? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }

    /// <summary>Position in the ordered list of the job (1 = most important).</summary>
    public int Rank { get; set; }

    /// <summary>Uncertain and review findings are listed apart, after the sure ones.</summary>
    public bool Unsure => Certainty is ResultCertainty.Uncertain or ResultCertainty.Review;
}

/// <summary>Whether a kind could be computed, and what is missing for it.</summary>
public sealed record KindCoverage(FindingCoverage Coverage, string? Note);

/// <summary>What the stored steps of the job offer the findings.</summary>
public sealed record FindingInputsInfo(
    long Files,
    long FilesJudged,
    long FilesPending,
    long Directories,
    long DirectoriesJudged,
    DateTime? BundlesBuiltAt,
    ComparisonHeader? LibraryComparison,
    ComparisonHeader? DuplicateComparison,
    long? LibraryJobId);

/// <summary>All findings of a job, ranked, with what they rest on.</summary>
public sealed record FindingReport(
    ScanJob Job,
    FindingInputsInfo Inputs,
    IReadOnlyDictionary<FindingKind, KindCoverage> Coverage,
    IReadOnlyList<string> MissingSteps,
    IReadOnlyList<Finding> Findings,
    IReadOnlyDictionary<FindingKind, long> BelowThreshold);
