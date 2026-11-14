using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Duplicates;

/// <summary>What <see cref="DuplicateService.ReportAsync"/> is asked for.</summary>
/// <param name="MinSize">Files below this many bytes are left out; empty files always are.</param>
/// <param name="Verify">Stage 3: read the whole of every file of a group that agrees after stage 2.</param>
public sealed record DuplicateOptions(long MinSize = 1, bool Verify = false);

/// <param name="Stage"><c>sample</c> (stage 2) or <c>verify</c> (stage 3).</param>
public sealed record DuplicateProgress(string Stage, long Processed, long Total, long BytesRead, string? CurrentPath);

/// <summary>One copy in a group.</summary>
/// <param name="Managed">The file lies in a managed area (<c>Plan:ManagedAreas</c>).</param>
public sealed record DuplicateMember(long FileNodeId, long JobId, ScanRole Role, string FullPath, bool Managed);

/// <summary>One folder of a folder group.</summary>
/// <param name="Files">Files of the folder and below it that the report looks at (at least the minimum size).</param>
/// <param name="MatchedFiles">Of those, the files with a copy in another folder of the group.</param>
public sealed record DuplicateFolder(long JobId, ScanRole Role, string Path, int Files, int MatchedFiles, bool Managed);

/// <summary>
/// One group of the report: files with the same content, or folders that are wholly or mostly the same.
/// </summary>
/// <param name="Size">Bytes of one copy: of the file, or of the identical files of one folder.</param>
/// <param name="Copies">Files of a file group, folders of a folder group.</param>
/// <param name="WastedBytes">Bytes a single copy would free: size times copies minus one, per identical file.</param>
/// <param name="Hash">The full SHA-256 of a certain file group; null otherwise.</param>
/// <param name="Files">Every file of the group; for a folder group the identical files inside its folders.</param>
public sealed record DuplicateGroupReport(
    DuplicateScope Scope,
    DuplicateCertainty Certainty,
    long Size,
    int Copies,
    long WastedBytes,
    string? Hash,
    IReadOnlyList<DuplicateMember> Files,
    IReadOnlyList<DuplicateFolder> Folders,
    DuplicateAdvice Advice)
{
    public IReadOnlyList<long> JobIds => Files.Select(f => f.JobId).Distinct().Order().ToList();
    public bool AcrossJobs => JobIds.Count > 1;
    public bool InLibrary => Files.Any(f => f.Role == ScanRole.Library);
}

/// <summary>What each stage looked at and what was read.</summary>
/// <param name="SizeCandidates">Files whose size occurs more than once (stage 1, nothing read).</param>
/// <param name="SampleGroups">Files that still agree after the sample checksum (stage 2).</param>
/// <param name="SampleComputed">Sample checksums read in this run.</param>
/// <param name="FullComputed">Full checksums read in this run.</param>
/// <param name="Reused">Files whose checksum came from <c>content_fingerprints</c>.</param>
/// <param name="FilesRead">Files opened in this run.</param>
/// <param name="BytesRead">Bytes read in this run.</param>
public sealed record DuplicateStages(
    int SizeCandidates,
    long SizeCandidateBytes,
    int SampleGroups,
    int SampleComputed,
    int FullComputed,
    int Reused,
    int FilesRead,
    long BytesRead);

/// <summary>A file that could not be read or changed since its scan; it is in no group.</summary>
public sealed record DuplicateIssue(long JobId, string Path, string Message);

public sealed record DuplicateReport(
    IReadOnlyList<ScanJob> Jobs,
    bool Verified,
    long MinSize,
    long TotalFiles,
    long TotalBytes,
    DuplicateStages Stages,
    IReadOnlyList<DuplicateGroupReport> Groups,
    IReadOnlyList<DuplicateIssue> Errors)
{
    public bool Complete => Errors.Count == 0;
    public long WastedBytes => Groups.Sum(g => g.WastedBytes);
}
