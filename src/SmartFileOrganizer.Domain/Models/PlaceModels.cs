using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// A known place (https://github.com/crack666/smart_file_organizer/issues/86): a scanned root with what was learnt about
/// its drive and its last completed scan. Kept in <c>places</c>; it outlives its jobs, so a place stays listed with its
/// last numbers after the job is deleted or the drive is unplugged.
/// </summary>
public sealed class Place
{
    public long Id { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public ScanRole Role { get; set; }

    /// <summary>Label of the drive, as far as the system tells it; null when unknown.</summary>
    public string? VolumeLabel { get; set; }

    /// <summary>Serial number of the drive (Windows) or another stable mark; null when it cannot be read.</summary>
    public string? VolumeId { get; set; }

    /// <summary>File system of the drive (NTFS, exFAT, ext4, ...); null when unknown.</summary>
    public string? VolumeFormat { get; set; }

    /// <summary>The newest completed job of the place; null after it was deleted and no other is left.</summary>
    public long? LastJobId { get; set; }

    public DateTime? LastScanAt { get; set; }

    /// <summary>Files and bytes of the last completed scan.</summary>
    public long Files { get; set; }

    public long Bytes { get; set; }

    public DateTime FirstSeenAt { get; set; }
}

/// <summary>What could be learnt about the drive of a path; every part may be unknown.</summary>
public sealed record VolumeInfo(string? Label, string? Id, string? Format);

/// <summary>One move of the history (<c>moves</c>): an original that left its place through a plan.</summary>
public sealed record MoveRecord(
    long Id,
    long PlanId,
    long ActionId,
    string SourcePath,
    string? TargetPath,
    string? Hash,
    long Bytes,
    DateTime MovedAt,
    DateTime? ReturnedAt,
    DateTime? PurgedAt)
{
    public MoveState State => ReturnedAt is null ? MoveState.Moved : MoveState.Returned;
}

/// <summary>One file a refresh found new, changed, missing or moved (<c>scan_changes</c>).</summary>
public sealed record FileChangeRecord(
    long JobId,
    long PreviousJobId,
    string FullPath,
    FileChange Change,
    long Size,
    string? TargetPath,
    long? PlanId,
    DateTime? MovedAt);

/// <summary>One entry of a move notice that a scan found (<c>notice_entries</c>).</summary>
public sealed record NoticeEntryRecord(
    long JobId,
    string NoticePath,
    string SourcePath,
    string TargetPath,
    long PlanId,
    DateTime MovedAt);

/// <summary>Where a file was seen by a stored scan.</summary>
public sealed record SeenFile(long JobId, string FullPath, long Size, DateTime LastWriteTime, DateTime ScannedAt, ScanRole Role);
