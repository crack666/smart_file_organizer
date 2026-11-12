using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// Facts about one directory, computed from scan data without calling the model:
/// what lies directly in it, how old it is, how files are named, and which markers of a
/// project, program, game or backup it carries. Stored as JSON next to the verdict.
/// </summary>
public sealed class DirectoryProfile
{
    /// <summary>Number of files directly in the directory.</summary>
    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    /// <summary>Number of known subdirectories, including ones the scan skipped.</summary>
    public int SubdirectoryCount { get; set; }

    /// <summary>Distribution by lower-case extension (empty string for files without one), most files first.</summary>
    public List<ProfileBucket> Extensions { get; set; } = [];

    /// <summary>Distribution by detected <see cref="FileType"/> name, most files first.</summary>
    public List<ProfileBucket> FileTypes { get; set; } = [];

    /// <summary>Oldest last-write time of a direct file (UTC).</summary>
    public DateTime? OldestWrite { get; set; }

    /// <summary>Newest last-write time of a direct file (UTC).</summary>
    public DateTime? NewestWrite { get; set; }

    /// <summary>Days between <see cref="OldestWrite"/> and <see cref="NewestWrite"/>.</summary>
    public int SpanDays { get; set; }

    /// <summary>Recognized naming patterns (camera numbering, date prefixes, …) and how many files match.</summary>
    public List<NamePatternCount> NamePatterns { get; set; } = [];

    /// <summary>
    /// Only for a directory without direct files that was judged from its subdirectories: the dominant
    /// type all of them share (a <see cref="FileType"/> name, or <c>unit</c> for a collection of units).
    /// </summary>
    public string? SubdirectoryDominantType { get; set; }

    /// <summary>Markers of a whole that must not be split, for example <c>dir:.git</c> or <c>file:package.json</c>.</summary>
    public List<string> UnitMarkers { get; set; } = [];

    /// <summary>Verdict the facts point to, if any (<c>uniform</c> or <c>mixed</c>). A hint, not a decision.</summary>
    public DirectoryVerdictKind? Suggests { get; set; }

    /// <summary>Why <see cref="Suggests"/> was set, in one sentence.</summary>
    public string? SuggestionReason { get; set; }
}

/// <summary>Count and bytes of files sharing one key (extension or file type).</summary>
public sealed class ProfileBucket
{
    public string Key { get; set; } = string.Empty;
    public int Count { get; set; }
    public long Bytes { get; set; }
}

public sealed class NamePatternCount
{
    /// <summary>Stable pattern id, for example <c>camera</c> or <c>date-prefix</c>.</summary>
    public string Pattern { get; set; } = string.Empty;
    public int Count { get; set; }
}
