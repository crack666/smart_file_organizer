using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>What a refresh carried over from the previous job, file by file, and what it found changed.</summary>
/// <param name="Unchanged">Files with the same path, size and modification time as in the previous job.</param>
/// <param name="Checksums">Unchanged files whose stored checksum still holds (<c>content_fingerprints</c>); no file is read again.</param>
/// <param name="Results">Unchanged files whose analysis result was carried over: no model is asked for them again.</param>
/// <param name="Facts">Facts carried over (rows of <c>file_facts</c>).</param>
/// <param name="Corrections">Corrections of a person carried over (rows of <c>user_overrides</c>).</param>
public sealed record RefreshCounts(
    long PreviousJobId,
    long Unchanged,
    long New,
    long Changed,
    long Missing,
    long Moved,
    long Checksums,
    long Results,
    long Facts,
    long Corrections);

/// <summary>
/// The memory of the tool (https://github.com/crack666/smart_file_organizer/issues/86): known places, what a refresh
/// changed, the move notices a scan found, and the questions of <c>sfo where</c> over the move history and the scans.
/// </summary>
public interface IPlaceRepository
{
    /// <summary>Records a completed scan at its place: creates the place or moves its last job, numbers and drive on.</summary>
    Task<Place> RecordScanAsync(ScanJob job, VolumeInfo? volume, CancellationToken ct = default);

    /// <summary>All known places, the most recently scanned first.</summary>
    Task<IReadOnlyList<Place>> GetAllAsync(CancellationToken ct = default);

    /// <summary>The newest completed job of <paramref name="rootPath"/> and <paramref name="role"/> other than <paramref name="exceptJobId"/>.</summary>
    Task<long?> GetPreviousJobIdAsync(string rootPath, ScanRole role, long exceptJobId, CancellationToken ct = default);

    /// <summary>
    /// Compares <paramref name="jobId"/> with <paramref name="previousJobId"/> by path, size and modification time,
    /// stores new, changed, missing and moved files in <c>scan_changes</c>, and carries the analysis results, facts and
    /// corrections of unchanged files over to the new job. Reads no file.
    /// </summary>
    Task<RefreshCounts> RefreshAsync(long previousJobId, long jobId, CancellationToken ct = default);

    /// <summary>The changes a refresh stored for <paramref name="jobId"/>, by path.</summary>
    Task<IReadOnlyList<FileChangeRecord>> GetChangesAsync(long jobId, CancellationToken ct = default);

    /// <summary>Stores the entries of move notices a scan found.</summary>
    Task AddNoticeEntriesAsync(IReadOnlyList<NoticeEntryRecord> entries, CancellationToken ct = default);

    /// <summary>Moves whose original was <paramref name="path"/> or lay below it.</summary>
    Task<IReadOnlyList<MoveRecord>> GetMovesFromAsync(string path, CancellationToken ct = default);

    /// <summary>Moves whose target is <paramref name="path"/> or lies below it.</summary>
    Task<IReadOnlyList<MoveRecord>> GetMovesToAsync(string path, CancellationToken ct = default);

    /// <summary>Moves of content with this full checksum.</summary>
    Task<IReadOnlyList<MoveRecord>> GetMovesByHashAsync(string hash, CancellationToken ct = default);

    /// <summary>Entries of found notices for an original at <paramref name="path"/> or below it.</summary>
    Task<IReadOnlyList<NoticeEntryRecord>> GetNoticeEntriesAsync(string path, CancellationToken ct = default);

    /// <summary>The newest scan of every job that saw a file at <paramref name="path"/>.</summary>
    Task<IReadOnlyList<SeenFile>> GetSeenAsync(string path, CancellationToken ct = default);

    /// <summary>Paths whose stored full checksum is <paramref name="hash"/>.</summary>
    Task<IReadOnlyList<string>> GetPathsByHashAsync(string hash, CancellationToken ct = default);

    /// <summary>The full checksum stored for a path, if any.</summary>
    Task<string?> GetHashOfPathAsync(string path, CancellationToken ct = default);
}

/// <summary>Learns what it can about the drive of a path: label, serial number, file system.</summary>
public interface IVolumeProbe
{
    VolumeInfo? Probe(string path);
}
