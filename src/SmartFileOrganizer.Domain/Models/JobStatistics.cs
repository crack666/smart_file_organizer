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

    public long FilesTotal { get; init; }

    /// <summary>Files of a type the classification phase handles (image, video, document).</summary>
    public long FilesAiEligible { get; init; }

    /// <summary>Eligible files still waiting for classification (status Discovered or a stale Processing).</summary>
    public long FilesAiPending { get; init; }

    public long FilesAiAnalyzed { get; init; }
    public long FilesAiErrors { get; init; }

    /// <summary>Eligible files the pre-assessment chose not to sample.</summary>
    public long FilesAiSkipped { get; init; }

    /// <summary>The most recent errors, newest first.</summary>
    public IReadOnlyList<JobErrorEntry> RecentErrors { get; init; } = [];
}

/// <summary>One recorded failure. <see cref="Phase"/> is <c>classification</c>, <c>pre_assessment</c> or <c>summary</c>.</summary>
public sealed record JobErrorEntry(string Phase, string Path, string Message);
