using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>Why a comparison could not start.</summary>
public enum ComparisonFailure
{
    JobNotFound,
    ScanNotCompleted,
    WrongRole,
    RootUnreachable
}

/// <summary>Thrown by <see cref="ContentComparisonService.CompareAsync"/> before any file is read.</summary>
public sealed class ComparisonException(ComparisonFailure failure, string message) : Exception(message)
{
    public ComparisonFailure Failure { get; } = failure;
}

public sealed record ComparisonProgress(long Processed, long Total, string? CurrentPath);

/// <summary>Where an error was found.</summary>
public enum ComparisonIssueKind
{
    /// <summary>The scan could not read a directory; its files are missing from the job.</summary>
    Scan,

    /// <summary>A file could not be read during the comparison.</summary>
    Read
}

/// <summary>An error the comparison reports instead of guessing. <see cref="Role"/> says on which side it happened.</summary>
public sealed record ComparisonIssue(ScanRole Role, ComparisonIssueKind Kind, string Path, string Message);

/// <summary>Source files with identical content (full checksum). Numbered from 1 per comparison, ordered by first path.</summary>
public sealed record DuplicateGroup(int Group, long Size, IReadOnlyList<string> Paths);

/// <summary>
/// Counts for one source directory including all subdirectories. A directory with
/// <see cref="NotBackedUp"/> = 0 needs no model analysis; otherwise only the files that are not
/// <c>backed_up</c> do.
/// </summary>
public sealed record DirectoryComparison(string Path, int Files, int BackedUp, int New, int Differs, int Errors)
{
    public int NotBackedUp => Files - BackedUp;
    public double BackedUpShare => Files == 0 ? 1.0 : Math.Round((double)BackedUp / Files, 4);
}

/// <summary>How many checksums a run computed and how many it took from the cache.</summary>
/// <param name="HeadTailComputed">Sample checksums computed (stage 2). The name, and the JSON key <c>head_tail_computed</c>,
/// stay from the time stage 2 read head and tail only.</param>
public sealed record HashingStats(int HeadTailComputed, int FullComputed, int Reused);

public sealed record ComparisonReport(
    ComparisonHeader Header,
    ScanJob Source,
    ScanJob? Against,
    IReadOnlyList<FileComparison> Files,
    IReadOnlyList<DuplicateGroup> DuplicateGroups,
    IReadOnlyList<DirectoryComparison> Directories,
    IReadOnlyList<ComparisonIssue> Errors,
    HashingStats Hashing,
    IReadOnlyList<LibraryComparisonPart>? Libraries = null)
{
    public int Count(ContentState state) => Files.Count(f => f.State == state);
}

/// <summary>
/// One library of a comparison with several (https://github.com/crack666/smart_file_organizer/issues/74): its job, its
/// stored comparison, and how many source files it holds by content. A file held by several libraries counts at the
/// first one named, as in the joined result.
/// </summary>
public sealed record LibraryComparisonPart(ScanJob Library, ComparisonHeader Header, int BackedUpFiles, long BackedUpBytes);
