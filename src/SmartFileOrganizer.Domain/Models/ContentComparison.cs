using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// Cached content identity of one file, keyed by its full path (<c>content_fingerprints</c>).
/// Valid only while <see cref="Size"/> and <see cref="LastWriteTicks"/> still match the file;
/// otherwise it is recomputed. Shared by all jobs, so a rescan does not hash the archive again.
/// </summary>
public sealed record ContentFingerprint(
    string FullPath,
    long Size,
    long LastWriteTicks,
    string? HeadTailHash,
    string? FullHash,
    DateTime ComputedAt);

/// <summary>Current size and modification time of a file, or why they could not be read.</summary>
public sealed record FileStat(bool Exists, long Size, long LastWriteTicks, string? Error)
{
    public static FileStat Missing(string error) => new(false, 0, 0, error);
}

/// <summary>Result of the head-and-tail checksum; <see cref="FullHash"/> is set when the whole file was read.</summary>
public sealed record HeadTailResult(string HeadTailHash, string? FullHash);

/// <summary>The columns of a scanned file that the comparison needs.</summary>
public sealed record ComparableFile(long Id, string FullPath, string Name, string ParentPath, long Size);

/// <summary>A directory the scan could not read (<c>directory_nodes.dir_status = Error</c>).</summary>
public sealed record ScanDirectoryError(string FullPath, string? Reason);

/// <summary>One stored comparison run (<c>comparisons</c>).</summary>
public sealed record ComparisonHeader(
    long Id,
    long SourceJobId,
    long? AgainstJobId,
    DateTime ComparedAt,
    bool Complete,
    int ErrorCount);

/// <summary>
/// Outcome for one source file (<c>comparison_files</c>). <see cref="State"/> is null only when
/// the comparison ran without an archive and the file could be read.
/// </summary>
public sealed record FileComparison(
    long FileNodeId,
    string FullPath,
    long Size,
    ContentState? State,
    long? MatchFileNodeId,
    string? MatchPath,
    int MatchCount,
    int? DuplicateGroup,
    string? Error);
