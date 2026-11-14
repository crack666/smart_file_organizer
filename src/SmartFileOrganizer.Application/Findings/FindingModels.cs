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
/// <see cref="LibraryJobId"/> and <see cref="LibraryRoot"/> name the library the place lies in: a target never lies
/// outside a root scanned with the role <c>library</c> (https://github.com/crack666/smart_file_organizer/issues/74).
/// </summary>
public sealed record FindingTarget(
    string Path,
    string RelativePath,
    ResultCertainty Certainty,
    string Reason,
    int SameContentFiles,
    TargetContentCheck ContentCheck,
    long? LibraryJobId = null,
    string? LibraryRoot = null);

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

    /// <summary>
    /// For the kinds that need a place (<see cref="FindingRules.TargetKinds"/>): what the stored target match says
    /// (https://github.com/crack666/smart_file_organizer/issues/83), <c>not_matched</c> when none fits the finding as it
    /// is now. Null for the other kinds.
    /// </summary>
    public FindingTargetStatus? TargetStatus { get; set; }

    /// <summary>
    /// SHA-256 of what a target match of this finding rests on (its files, their description facts, the libraries); a
    /// stored match counts only while it is the same. Null for the kinds without a target.
    /// </summary>
    public string? TargetInput { get; set; }

    /// <summary>
    /// For target status <c>split</c> (Issue 85): the parts of the finding by content, each with its own place or why it
    /// has none. The plan moves each file to the place of its part. Empty otherwise.
    /// </summary>
    public IReadOnlyList<FindingTargetPart> TargetParts { get; set; } = [];

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
/// <param name="LibraryComparison">The comparison with the first library named (it stands for the set).</param>
/// <param name="LibraryJobId">The first library job; <paramref name="LibraryJobIds"/> lists all of them.</param>
public sealed record FindingInputsInfo(
    long Files,
    long FilesJudged,
    long FilesPending,
    long Directories,
    long DirectoriesJudged,
    DateTime? BundlesBuiltAt,
    ComparisonHeader? LibraryComparison,
    ComparisonHeader? DuplicateComparison,
    long? LibraryJobId,
    IReadOnlyList<long>? LibraryJobIds = null);

/// <summary>A number of files and their size.</summary>
public sealed record FileTally(long Files, long Bytes)
{
    public static FileTally Zero { get; } = new(0, 0);

    public FileTally Add(long bytes) => new(Files + 1, Bytes + bytes);
}

/// <summary>Files of the source one library holds by content.</summary>
public sealed record LibraryTally(long LibraryJobId, string RootPath, long Files, long Bytes);

/// <summary>
/// What lies on the source at a glance, file by file rather than finding by finding
/// (https://github.com/crack666/smart_file_organizer/issues/74). A marked file counts under <see cref="Sensitive"/> only,
/// never under worth; <see cref="BackedUp"/> counts every file the libraries hold, marked ones included.
/// </summary>
/// <param name="WorthKeepingUnknown">Worth keeping, but not compared or not readable: nobody knows whether it is safe.</param>
/// <param name="NotCompared">Files without a comparison state: no library, or the file could not be read.</param>
public sealed record FindingOverview(
    FileTally All,
    FileTally WorthKeeping,
    FileTally WorthKeepingNotBackedUp,
    FileTally WorthKeepingBackedUp,
    FileTally WorthKeepingUnknown,
    FileTally BackedUp,
    IReadOnlyList<LibraryTally> BackedUpByLibrary,
    FileTally Disposable,
    FileTally Sensitive,
    FileTally NotCompared);

/// <summary>
/// One library root as a target: how much is free there, how much the proposed targets in it need together.
/// <see cref="FreeBytes"/> is null when it cannot be determined; <see cref="Space"/> is then <c>unknown</c>.
/// </summary>
public sealed record RootStorage(long LibraryJobId, string RootPath, long? FreeBytes, long NeededBytes, int Findings, TargetSpace Space);

/// <summary>
/// Where the proposals of the findings go (https://github.com/crack666/smart_file_organizer/issues/74): only into
/// library roots. <see cref="Unplaced"/> counts files of findings that should move but have no target yet (worth keeping
/// and not in the library, or among noise); they need room in one of the roots too. <see cref="UnplacedByReason"/> says
/// why per <see cref="FindingTargetStatus"/> code: not matched yet, no fitting folder, content unclear, no model
/// (https://github.com/crack666/smart_file_organizer/issues/83).
/// </summary>
public sealed record FindingStorage(
    StorageStatus Status,
    string? Message,
    IReadOnlyList<RootStorage> Roots,
    int UnplacedFindings,
    FileTally Unplaced,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, int>? UnplacedByReason = null);

/// <summary>All findings of a job, ranked, with what they rest on.</summary>
public sealed record FindingReport(
    ScanJob Job,
    FindingInputsInfo Inputs,
    IReadOnlyDictionary<FindingKind, KindCoverage> Coverage,
    IReadOnlyList<string> MissingSteps,
    IReadOnlyList<Finding> Findings,
    IReadOnlyDictionary<FindingKind, long> BelowThreshold,
    FindingOverview? Overview = null,
    FindingStorage? Storage = null);
