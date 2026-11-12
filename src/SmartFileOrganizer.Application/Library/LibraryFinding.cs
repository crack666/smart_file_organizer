using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Library;

/// <summary>
/// One finding of the structure rules. It bundles all cases of one kind under one place: "6 empty folders
/// under X", not six findings. <see cref="Items"/> names up to <see cref="LibraryReviewOptions.ItemsPerFinding"/>
/// of them (child names, or relative paths for findings that span the tree); <see cref="More"/> counts the rest.
/// </summary>
/// <param name="Path">Full path of the place the finding is about (the parent for bundled findings).</param>
/// <param name="Directories">Directories the finding covers.</param>
/// <param name="Files">Content files the finding covers (subtree sums where the finding is about subtrees).</param>
/// <param name="Bytes">Bytes the finding covers.</param>
/// <param name="Details">Kind-specific facts with <c>snake_case</c> keys, for example the pattern distribution.</param>
public sealed record LibraryFinding(
    LibraryFindingKind Kind,
    FindingImpact Impact,
    string Path,
    string RelativePath,
    string Reason,
    IReadOnlyList<string> Items,
    int More,
    int Directories,
    long Files,
    long Bytes,
    IReadOnlyDictionary<string, object?> Details)
{
    /// <summary>Order of a review: impact first, then what the finding covers, then path.</summary>
    public static int Compare(LibraryFinding a, LibraryFinding b)
    {
        var c = a.Impact.CompareTo(b.Impact);
        if (c != 0) return c;
        c = (b.Files + 10L * b.Directories).CompareTo(a.Files + 10L * a.Directories);
        if (c != 0) return c;
        c = b.Bytes.CompareTo(a.Bytes);
        if (c != 0) return c;
        c = a.Kind.CompareTo(b.Kind);
        return c != 0 ? c : string.CompareOrdinal(a.RelativePath, b.RelativePath);
    }
}

/// <summary>
/// Thresholds of the structure rules. The defaults follow a manual review of a real home library
/// (comment on https://github.com/crack666/smart_file_organizer/issues/29); docs/library-review.md lists them.
/// </summary>
public sealed record LibraryReviewOptions
{
    /// <summary>Sibling rules (naming, letter case, flat among structured) need at least this many siblings.</summary>
    public int MinSiblings { get; init; } = 5;

    /// <summary>A folder without subfolders counts as flat from this many direct files.</summary>
    public int FlatMinFiles { get; init; } = 50;

    /// <summary>Share of siblings with subfolders from which the parent counts as structured.</summary>
    public double StructuredShare { get; init; } = 0.5;

    /// <summary>Loose files are reported at depth 0 and 1 when the folder also has at least this many subfolders.</summary>
    public int LooseMinSubdirs { get; init; } = 3;

    public int VeryFullFiles { get; init; } = 500;

    /// <summary>A branch is very deep beyond the median depth plus this margin, and never above <see cref="DeepMinDepth"/>.</summary>
    public int DeepMargin { get; init; } = 4;
    public int DeepMinDepth { get; init; } = 8;

    /// <summary>Subtrees are copy candidates from this many files and bytes.</summary>
    public int CopyMinFiles { get; init; } = 3;
    public long CopyMinBytes { get; init; } = 1L << 20;

    /// <summary>A name that occurs more often than this is structure, not a duplicate purpose.</summary>
    public int SameNameMaxGroup { get; init; } = 8;

    /// <summary>A name promising a type is checked from this many content files in its subtree.</summary>
    public int MismatchMinFiles { get; init; } = 10;

    /// <summary>Share a foreign class must reach to count as the majority.</summary>
    public double MismatchShare { get; init; } = 0.5;

    /// <summary>A top-level area has a dominant class from this many files and this share.</summary>
    public int AreaMinFiles { get; init; } = 50;
    public double AreaDominantShare { get; init; } = 0.6;

    /// <summary>A subtree inside an area is checked from this many files.</summary>
    public int AreaSubtreeMinFiles { get; init; } = 20;

    /// <summary>A single file above this share of all bytes is reported.</summary>
    public double DominantFileShare { get; init; } = 0.5;

    /// <summary>Letter case is reported when this share of siblings agrees.</summary>
    public double CaseMajorityShare { get; init; } = 0.7;

    public int ItemsPerFinding { get; init; } = 5;

    /// <summary>Findings of a repeating kind below this many children of one folder are folded into one finding there.</summary>
    public int RollupMin { get; init; } = 3;

    /// <summary>A placeholder or catch-all folder counts as grown from this many files or bytes (impact high).</summary>
    public int GrownFiles { get; init; } = 100;
    public long GrownBytes { get; init; } = 1L << 30;
}
