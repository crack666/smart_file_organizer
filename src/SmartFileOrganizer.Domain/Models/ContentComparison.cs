using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// Cached content identity of one file, keyed by its full path (<c>content_fingerprints</c>).
/// Valid only while <see cref="Size"/> and <see cref="LastWriteTicks"/> still match the file;
/// otherwise it is recomputed. Shared by all jobs, so a rescan does not hash the library again.
/// <see cref="SampleHash"/> is the sample checksum (stage 2 of the comparison and the duplicate report),
/// <see cref="FullHash"/> the SHA-256 of the whole file.
/// </summary>
public sealed record ContentFingerprint(
    string FullPath,
    long Size,
    long LastWriteTicks,
    string? SampleHash,
    string? FullHash,
    DateTime ComputedAt,
    DateTime? FullHashAt);

/// <summary>Current size and modification time of a file, or why they could not be read.</summary>
public sealed record FileStat(bool Exists, long Size, long LastWriteTicks, string? Error)
{
    public static FileStat Missing(string error) => new(false, 0, 0, error);
}

/// <summary>Result of the sample checksum; <see cref="FullHash"/> is set when the whole file was read.</summary>
/// <param name="BytesRead">Bytes read from the file for this checksum.</param>
public sealed record SampleResult(string SampleHash, string? FullHash, long BytesRead);

/// <summary>The columns of a scanned file that the comparison needs.</summary>
public sealed record ComparableFile(long Id, string FullPath, string Name, string ParentPath, long Size);

/// <summary>A directory the scan could not read (<c>directory_nodes.dir_status = Error</c>).</summary>
public sealed record ScanDirectoryError(string FullPath, string? Reason);

/// <summary>One stored comparison of a source with one library (<c>comparisons</c>).</summary>
/// <param name="RunId">The comparisons stored by one <c>sfo compare</c> with several libraries share it (the id of the
/// first of them); a comparison with one library has its own id. Null before it is stored.</param>
public sealed record ComparisonHeader(
    long Id,
    long SourceJobId,
    long? AgainstJobId,
    DateTime ComparedAt,
    bool Complete,
    int ErrorCount,
    long? RunId = null);

/// <summary>
/// Outcome for one source file (<c>comparison_files</c>). <see cref="State"/> is null only when
/// the comparison ran without a library and the file could be read.
/// </summary>
/// <remarks>
/// For <c>backed_up</c>, <see cref="MatchHash"/> is the full SHA-256 that source and
/// <see cref="MatchPath"/> share, and <see cref="MatchVerifiedAt"/> the older of the two times this
/// checksum was computed from the files (it may come from the cache of an earlier run). Anything
/// that deletes the source file must read both files again and compare with <see cref="MatchHash"/>
/// first; this record is evidence of a past state, not a guarantee.
/// <see cref="MatchJobId"/> is the library job <see cref="MatchPath"/> belongs to (set when read back).
/// </remarks>
public sealed record FileComparison(
    long FileNodeId,
    string FullPath,
    long Size,
    ContentState? State,
    long? MatchFileNodeId,
    string? MatchPath,
    int MatchCount,
    int? DuplicateGroup,
    string? Error,
    string? MatchHash = null,
    DateTime? MatchVerifiedAt = null,
    long? MatchJobId = null);
