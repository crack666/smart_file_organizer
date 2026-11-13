namespace SmartFileOrganizer.Domain.Models;

/// <summary>Counters per pipeline phase for one scan job, read from the database in one go.</summary>
public sealed class JobStatistics
{
    public long DirectoriesTotal { get; init; }

    /// <summary>Directories the AI phases consider: not skipped by the scan rules and not hidden (name starting with a dot).</summary>
    public long DirectoriesAiRelevant { get; init; }

    public long DirectoriesPreAssessed { get; init; }
    public long PreAssessmentErrors { get; init; }
    public long DirectoriesSummarized { get; init; }
    public long SummaryErrors { get; init; }

    /// <summary>
    /// AI-relevant directories that get no summary because none is needed: judged without a pre-assessment (a folder
    /// inside a unit or a dump part judged by rule), or pre-assessed without a file of their own.
    /// </summary>
    public long SummariesNotNeeded { get; init; }

    /// <summary>AI-relevant directories without a verdict that <c>sfo analyze --against</c> left out: every file below
    /// them is <c>backed_up</c> (<c>analysis_skip</c>).</summary>
    public long PreAssessmentBackedUp { get; init; }

    /// <summary>AI-relevant directories without a summary that were left out as <c>backed_up</c>; not part of
    /// <see cref="SummariesNotNeeded"/>.</summary>
    public long SummaryBackedUp { get; init; }

    /// <summary>Files left out of the classification because the library holds them (<c>analysis_skip</c>
    /// <c>backed_up</c>); part of <see cref="FilesAiSkipped"/>.</summary>
    public long FilesAiBackedUp { get; init; }

    public long FilesTotal { get; init; }

    /// <summary>Files of a type the classification phase handles (image, video, document).</summary>
    public long FilesAiEligible { get; init; }

    /// <summary>Eligible files still waiting for classification (status Discovered or a stale Processing).</summary>
    public long FilesAiPending { get; init; }

    public long FilesAiAnalyzed { get; init; }
    public long FilesAiErrors { get; init; }

    /// <summary>Eligible files not analysed because their directory is a unit or system data, or because the library
    /// holds them (<see cref="FilesAiBackedUp"/>).</summary>
    public long FilesAiSkipped { get; init; }

    /// <summary>Files per minute the model judged lately (the newest results, if recent); null when no run is going on.</summary>
    public double? FilesAiRatePerMinute { get; init; }

    /// <summary>Eligible files that inherited the result of the confirmed sample of their directory (not looked at).</summary>
    public long FilesAiInherited { get; init; }

    /// <summary>Eligible files waiting for the sample of their directory.</summary>
    public long FilesAiDeferred { get; init; }

    /// <summary>Of the analysed files, those whose result the model gave in a call for this file.</summary>
    public long FilesAiByModel { get; init; }

    /// <summary>Of the analysed files, those whose result was taken over from the content store without a model call
    /// (origin <c>reused</c>, https://github.com/crack666/smart_file_organizer/issues/38).</summary>
    public long FilesAiReused { get; init; }

    /// <summary>The most recent errors, newest first.</summary>
    public IReadOnlyList<JobErrorEntry> RecentErrors { get; init; } = [];

    /// <summary>Folders and files the scan could not read, grouped by cause (code of <see cref="Enums.ScanErrorCause"/>).</summary>
    public IReadOnlyList<ScanErrorGroup> ScanErrors { get; init; } = [];

    /// <summary>Links the scan found, grouped by kind (code of <see cref="Enums.LinkKind"/>).</summary>
    public IReadOnlyList<LinkGroup> Links { get; init; } = [];
}

/// <summary>Read errors of one cause: how many folders and files, and the first few by path.</summary>
public sealed record ScanErrorGroup(string Cause, long Directories, long Files, IReadOnlyList<ScanErrorExample> Examples);

public sealed record ScanErrorExample(string Path, string? Message);

/// <summary>Links of one kind: folders not entered, folders a <c>follow_link</c> rule entered, files that are links.</summary>
public sealed record LinkGroup(string Kind, long NotFollowed, long Followed, long Files, IReadOnlyList<LinkExample> Examples);

public sealed record LinkExample(string Path, string? Target, bool Followed);

/// <summary>One recorded failure. <see cref="Phase"/> is <c>classification</c>, <c>pre_assessment</c> or <c>summary</c>.</summary>
public sealed record JobErrorEntry(string Phase, string Path, string Message);
