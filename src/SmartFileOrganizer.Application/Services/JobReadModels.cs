using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>Why a read query could not answer.</summary>
public enum ReadFailure
{
    JobNotFound,
    PathNotFound
}

/// <summary>Thrown by <see cref="JobReadService"/> for an unknown job or path.</summary>
public sealed class JobReadException(ReadFailure failure, string message) : Exception(message)
{
    public ReadFailure Failure { get; } = failure;
}

public enum DirectorySort
{
    /// <summary>Largest subtree first.</summary>
    Size,

    /// <summary>Most files in the subtree first.</summary>
    Files,

    /// <summary>By full path, ordinal.</summary>
    Path
}

public enum FileSort
{
    Path,

    /// <summary>Largest first.</summary>
    Size
}

/// <summary>
/// Extra filter values next to the verdict codes from <see cref="VerdictCodes"/>:
/// <see cref="Error"/> matches a directory whose verdict is only a stored error,
/// <see cref="None"/> a directory Phase 1 has not reached (or, for retention, one without a value).
/// </summary>
public static class ReadFilterCodes
{
    public const string Error = "error";
    public const string None = "none";
}

/// <summary>One page of a list. <see cref="NextOffset"/> is null on the last page.</summary>
public sealed record ReadPage<T>(long Total, int Limit, int Offset, IReadOnlyList<T> Items)
{
    public int? NextOffset => Offset + Items.Count < Total ? Offset + Items.Count : null;
}

/// <summary>Filters and paging shared by directory and file listings.</summary>
public abstract record ReadQuery
{
    public long JobId { get; init; }

    /// <summary>Verdict codes, plus <see cref="ReadFilterCodes.Error"/> and <see cref="ReadFilterCodes.None"/>. Empty: no filter.
    /// For files the verdict of the directory that holds the file.</summary>
    public IReadOnlyCollection<string> Verdicts { get; init; } = [];

    /// <summary>Retention codes, plus <see cref="ReadFilterCodes.None"/>. Empty: no filter.</summary>
    public IReadOnlyCollection<string> Retentions { get; init; } = [];

    /// <summary>True: only marked files (directories holding marked files); false: only unmarked; null: no filter.</summary>
    public bool? Sensitive { get; init; }

    public int Limit { get; init; } = JobReadService.DefaultLimit;
    public int Offset { get; init; }

    /// <summary><c>--include-sensitive</c> lifts the protection; default protected.</summary>
    public SensitiveOutputOptions Output { get; init; } = SensitiveOutputOptions.Default;
}

public sealed record DirectoryListQuery : ReadQuery
{
    /// <summary>Only this directory and its subtree (full path, or relative to the job root).</summary>
    public string? Under { get; init; }

    /// <summary>Levels below <see cref="Under"/> (or the job root); 0 lists only that directory.</summary>
    public int? MaxDepth { get; init; }

    /// <summary>Minimum subtree size in bytes.</summary>
    public long? MinSize { get; init; }

    public DirectorySort Sort { get; init; } = DirectorySort.Path;
}

public sealed record FileListQuery : ReadQuery
{
    /// <summary>Only files in this directory (full path, or relative to the job root).</summary>
    public string? Directory { get; init; }

    /// <summary>With <see cref="Directory"/>: include its subdirectories.</summary>
    public bool Recursive { get; init; }

    /// <summary>Effective category as the output guard reports it; <see cref="FileCategory.Unknown"/> also matches unclassified files.</summary>
    public IReadOnlyCollection<FileCategory> Categories { get; init; } = [];

    public IReadOnlyCollection<FileNodeStatus> Statuses { get; init; } = [];
    public IReadOnlyCollection<FileType> Types { get; init; } = [];

    /// <summary>Only files with a reported confidence at or below this value.</summary>
    public double? MaxConfidence { get; init; }

    public FileSort Sort { get; init; } = FileSort.Path;
}

/// <summary>A directory as the read commands show it. Sizes and counts cover the whole subtree.</summary>
public sealed record DirectoryItem
{
    public long Id { get; init; }
    public string FullPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public int Depth { get; init; }
    public DirectoryStatus Status { get; init; }
    public long SizeBytes { get; init; }
    public long Files { get; init; }
    public long DirectFiles { get; init; }
    public int Subdirs { get; init; }

    /// <summary>Verdict code, null when there is none (see <see cref="VerdictError"/>).</summary>
    public string? Verdict { get; init; }

    public string? VerdictSource { get; init; }
    public string? VerdictRule { get; init; }
    public string? Retention { get; init; }
    public string? VerdictError { get; init; }
    public string? Purpose { get; init; }
    public string? Reason { get; init; }

    /// <summary>Phase 3 summary of the model.</summary>
    public string? Summary { get; init; }

    public string? SummaryError { get; init; }

    /// <summary>Marked files in the subtree.</summary>
    public long SensitiveFiles { get; init; }

    /// <summary>True when at least one model text was replaced by <see cref="DirectoryTextGuard.NeutralHint"/>.</summary>
    public bool TextWithheld { get; init; }

    /// <summary>The code a verdict filter compares with: verdict, <c>error</c> or <c>none</c>.</summary>
    public string VerdictFilterCode => Verdict ?? (VerdictError is not null ? ReadFilterCodes.Error : ReadFilterCodes.None);
}

/// <summary>A file through the output guard, with the verdict of its directory.</summary>
public sealed record FileItem(FileOutputRecord Record, string? DirectoryVerdict, string? DirectoryRetention);

/// <summary>One stored directory analysis (Phase 1 pre-assessment or Phase 3 summary), protected like <see cref="DirectoryItem"/>.</summary>
public sealed record DirectoryAnalysisItem
{
    public string Phase { get; init; } = string.Empty;
    public string? Summary { get; init; }
    public string? Theme { get; init; }
    public string? Homogeneity { get; init; }
    public string? DominantType { get; init; }
    public string? SamplingStrategy { get; init; }
    public int SampleSize { get; init; }
    public IReadOnlyList<AnomalyItem> Anomalies { get; init; } = [];
    public string? ModelUsed { get; init; }
    public DateTime? AnalyzedAt { get; init; }
    public string? Error { get; init; }
}

/// <summary>A file the model flagged as not fitting its directory. <see cref="Reason"/> is withheld for a marked file.</summary>
public sealed record AnomalyItem(string Name, string? Reason);

/// <summary>Everything known about one directory.</summary>
public sealed record DirectoryDetail
{
    public DirectoryItem Directory { get; init; } = new();

    /// <summary>For <c>einheit</c>: full path of the directory that is the whole.</summary>
    public string? UnitDirectory { get; init; }

    public DirectoryProfile? Profile { get; init; }
    public string? VerdictModel { get; init; }
    public DateTime? JudgedAt { get; init; }

    /// <summary>Raw model answer of the verdict; withheld like the other model texts.</summary>
    public string? VerdictRawResponse { get; init; }

    public IReadOnlyList<DirectoryAnalysisItem> Analyses { get; init; } = [];
}

/// <summary>Result of <see cref="JobReadService.ShowAsync"/>: exactly one of the two is set.</summary>
public sealed record PathDetail(DirectoryDetail? Directory, FileItem? File);

/// <summary>Overview of a job, bounded in size whatever the number of files.</summary>
public sealed record JobSummary
{
    public ScanJob Job { get; init; } = new();
    public long Directories { get; init; }
    public long Files { get; init; }
    public long TotalBytes { get; init; }

    /// <summary>Files the classification phase still has to look at.</summary>
    public long PendingFiles { get; init; }

    /// <summary>Files whose classification failed.</summary>
    public long ErrorFiles { get; init; }

    public long SensitiveFiles { get; init; }

    /// <summary>Counts by effective category (as the output guard reports it); <c>unclassified</c> for files without one.</summary>
    public IReadOnlyDictionary<string, long> ByCategory { get; init; } = new Dictionary<string, long>();

    public IReadOnlyDictionary<string, long> ByFileType { get; init; } = new Dictionary<string, long>();
    public IReadOnlyDictionary<string, long> ByStatus { get; init; } = new Dictionary<string, long>();
    public IReadOnlyDictionary<string, long> BySensitiveMarker { get; init; } = new Dictionary<string, long>();

    /// <summary>Directories by verdict code, plus <c>error</c> and <c>none</c>.</summary>
    public IReadOnlyDictionary<string, long> DirectoriesByVerdict { get; init; } = new Dictionary<string, long>();

    public IReadOnlyDictionary<string, long> DirectoriesByRetention { get; init; } = new Dictionary<string, long>();

    /// <summary>Largest subtrees below the job root, without model texts.</summary>
    public IReadOnlyList<DirectoryItem> LargestDirectories { get; init; } = [];
}
