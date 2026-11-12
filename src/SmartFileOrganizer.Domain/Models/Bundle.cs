using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// A group of files the person decides once (https://github.com/crack666/smart_file_organizer/issues/39).
/// Bundles form a tree per job: a dump has its keep and noise parts as children, a camera part its events.
/// Every file belongs to at most one bundle directly (<see cref="FileIds"/>); counts and sizes cover the
/// bundle and all its children.
/// </summary>
public sealed class Bundle
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public long? ParentId { get; set; }

    public BundleKind Kind { get; set; }

    /// <summary>For <see cref="BundleKind.Dump"/>: where it comes from.</summary>
    public DumpOrigin? Origin { get; set; }

    /// <summary>For <see cref="BundleKind.DumpKeep"/>: which part.</summary>
    public DumpPart? Part { get; set; }

    /// <summary>The directory that is the bundle (dump, folder, the first directory of a part); null for events.</summary>
    public string? RootPath { get; set; }

    public long? RootDirectoryNodeId { get; set; }

    /// <summary>Name proposed from period and existing folder names, without a model.</summary>
    public string SuggestedName { get; set; } = string.Empty;

    /// <summary>Name in the style of the library, proposed by a model later
    /// (https://github.com/crack666/smart_file_organizer/issues/25,
    /// https://github.com/crack666/smart_file_organizer/issues/29). Always null here.</summary>
    public string? StyledName { get; set; }

    /// <summary>Earliest and latest time of the files, local wall-clock time as recorded (no zone).</summary>
    public DateTime? FirstTime { get; set; }

    public DateTime? LastTime { get; set; }
    public TimeSource? TimeSource { get; set; }

    public long FileCount { get; set; }
    public long TotalBytes { get; set; }

    /// <summary>Directories in the subtree of <see cref="RootPath"/> (dumps and folders) or holding members (parts, events).</summary>
    public int DirectoryCount { get; set; }

    public int EmptyDirectoryCount { get; set; }

    /// <summary>Why the files form this bundle, in one sentence.</summary>
    public string Reason { get; set; } = string.Empty;

    public BundleFacts Facts { get; set; } = new();
    public DateTime BuiltAt { get; set; }

    /// <summary>Files that belong directly to this bundle (not to a child). Filled when building; not loaded by listings.</summary>
    public List<long> FileIds { get; set; } = [];

    /// <summary>Child bundles. Filled when building; listings return the tree flat with <see cref="ParentId"/>.</summary>
    public List<Bundle> Children { get; set; } = [];
}

/// <summary>Facts a bundle rests on, stored as JSON in <c>bundles.facts_json</c> (snake_case).</summary>
public sealed class BundleFacts
{
    /// <summary>File count per file type code (<c>image</c>, <c>document</c>, ...), bundle and children.</summary>
    public Dictionary<string, long> ByFileType { get; set; } = new();

    /// <summary>Files per time source code (<c>capture_date</c>, <c>file_name</c>, <c>modified_date</c>).</summary>
    public Dictionary<string, long> TimeSources { get; set; } = new();

    /// <summary>What recognised a dump, for example <c>dir:DCIM</c>, <c>dir:LOST.DIR</c>, <c>dir~package:com.whatsapp</c>.</summary>
    public List<string> Markers { get; set; } = [];

    /// <summary>Directories of a part or an event, relative to the job root; at most <see cref="MaxListedDirectories"/>.</summary>
    public List<string> Directories { get; set; } = [];

    public int DirectoriesTotal { get; set; }

    /// <summary>Camera or device all files share, when known from the metadata.</summary>
    public string? Device { get; set; }

    /// <summary>All times are modification times lying so close together that they are probably the time of a copy.</summary>
    public bool CopyTimeSuspected { get; set; }

    /// <summary>For a folder that holds several named folders: only its own files are in the bundle.</summary>
    public bool DirectFilesOnly { get; set; }

    public const int MaxListedDirectories = 20;
}

/// <summary>One run of the bundle builder for a job, stored in <c>bundle_builds</c>.</summary>
public sealed class BundleBuild
{
    public long JobId { get; set; }
    public DateTime BuiltAt { get; set; }

    /// <summary>Thresholds and pattern set used, as JSON.</summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary><see cref="BundleSplitStats"/> as JSON.</summary>
    public string StatsJson { get; set; } = "{}";
}

/// <summary>Statistics of a job with and without dumps.</summary>
public sealed class BundleSplitStats
{
    public PartStats All { get; set; } = new();
    public PartStats Dumps { get; set; } = new();
    public PartStats WithoutDumps { get; set; } = new();
    public int DumpCount { get; set; }
    public int BundleCount { get; set; }

    /// <summary>Files outside every bundle: not photo or video, lying loose in folders without a meaningful name.</summary>
    public long UnbundledFiles { get; set; }

    public sealed class PartStats
    {
        public long Directories { get; set; }
        public long EmptyDirectories { get; set; }
        public long Files { get; set; }
        public long TotalBytes { get; set; }
        public Dictionary<string, long> ByFileType { get; set; } = new();
    }
}

/// <summary>
/// When and with what a photo or video was taken, and whether it is a camera picture, a screenshot or a scan,
/// from the stored metadata facts (<c>taken_at</c>, <c>device</c>, <c>image_origin</c>). Each may be missing.
/// </summary>
public sealed record CaptureInfo(DateTime? TakenAt, string? Device, ImageOrigin? ImageOrigin = null);

/// <summary>A bundle as listings show it: the stored row plus counts computed when read.</summary>
public sealed class BundleReadRow
{
    public Bundle Bundle { get; init; } = new();
    public int Children { get; init; }

    /// <summary>Files of the bundle (and its children) marked sensitive, by rule or model.</summary>
    public long SensitiveFiles { get; set; }

    /// <summary>Counts per comparison state of the latest comparison with a library; null without one.</summary>
    public Dictionary<string, long>? Comparison { get; set; }
}

/// <summary>Counts per comparison state and bundle (direct members only) from the latest comparison with a library.</summary>
public sealed record BundleComparisonCounts(
    long ComparisonId,
    long AgainstJobId,
    DateTime ComparedAt,
    IReadOnlyList<(long BundleId, string State, long Files)> Counts);
