namespace SmartFileOrganizer.Application.Library;

/// <summary>
/// Thresholds and weights of <c>sfo library show</c> and <c>sfo library match</c>
/// (https://github.com/crack666/smart_file_organizer/issues/25). Configuration section <c>LibraryMatch</c>.
/// The defaults are starting values, not measured ones; docs/library-match.md gives the reasons.
/// </summary>
public sealed class LibraryMatchOptions
{
    /// <summary>A target below this score is no fitting target; then a new folder is proposed instead.</summary>
    public double MinScore { get; set; } = 0.35;

    /// <summary>When the runner-up scores within this distance of the best, the best is one certainty level lower.</summary>
    public double Margin { get; set; } = 0.10;

    /// <summary>Share of the files whose content already lies in a folder from which that counts as a signal.</summary>
    public double MinContentShare { get; set; } = 0.10;

    /// <summary>Share of the files whose content already lies in a folder at which the signal is at full strength.</summary>
    public double FullContentShare { get; set; } = 0.50;

    /// <summary>Days by which two periods may miss each other and still count as overlapping (time zones, a late copy).</summary>
    public int PeriodToleranceDays { get; set; } = 2;

    /// <summary>Share of the words of the source's name found in the folder's name from which names agree.</summary>
    public double MinNameShare { get; set; } = 0.5;

    /// <summary>Share of a folder's subfolders that must be of the source's kind for it to count as "next to its kind".</summary>
    public double MinSiblingShare { get; set; } = 0.5;

    /// <summary>Share of one kind of files (photos and videos, documents, ...) from which a folder has that role.</summary>
    public double RoleShare { get; set; } = 0.6;

    /// <summary>Proposals listed per match unless <c>--limit</c> says otherwise.</summary>
    public int MaxCandidates { get; set; } = 3;

    /// <summary>Folders down to this depth get a purpose from the model in <c>library show</c>.</summary>
    public int ModelMaxDepth { get; set; } = 2;

    /// <summary>At most this many folders are described to the model at once.</summary>
    public int ModelMaxFolders { get; set; } = 150;

    // Weights for a folder the files go into. They add up to 1.
    public double WeightContent { get; set; } = 0.40;
    public double WeightPeriod { get; set; } = 0.25;
    public double WeightName { get; set; } = 0.20;
    public double WeightType { get; set; } = 0.15;

    // Weights for a folder a named source folder goes into as a whole. They add up to 1.
    public double SubfolderWeightContent { get; set; } = 0.35;
    public double SubfolderWeightSiblings { get; set; } = 0.30;
    public double SubfolderWeightType { get; set; } = 0.15;
    public double SubfolderWeightPeriod { get; set; } = 0.10;
    public double SubfolderWeightName { get; set; } = 0.10;

    // Content (https://github.com/crack666/smart_file_organizer/issues/56).

    /// <summary>Added to the score of a candidate whose content fits, so it outranks one that only shares the period.</summary>
    public double WeightContentFit { get; set; } = 0.40;

    /// <summary>Candidates per library whose content is checked (best first); each costs a sample of the folder once.</summary>
    public int MaxContentCandidates { get; set; } = 5;

    /// <summary>Files of a candidate folder looked at for its description, spread over its period.</summary>
    public int FolderSampleSize { get; set; } = 4;

    /// <summary>Described files of the unit its description is built from, spread over its period.</summary>
    public int UnitSampleSize { get; set; } = 12;

    /// <summary>
    /// Folder for the patterns files (one per library job, editable by hand). Set by the host to
    /// <c>library-patterns</c> next to the database; null keeps the patterns in memory only.
    /// </summary>
    public string? PatternsDirectory { get; set; }

    /// <exception cref="ArgumentException">A value is out of range; the message names every problem.</exception>
    public void Validate()
    {
        var problems = new List<string>();
        void Share(string name, double value)
        {
            if (value is < 0 or > 1 || double.IsNaN(value)) problems.Add($"{name} must be between 0 and 1");
        }
        Share(nameof(MinScore), MinScore);
        Share(nameof(Margin), Margin);
        Share(nameof(MinContentShare), MinContentShare);
        Share(nameof(FullContentShare), FullContentShare);
        Share(nameof(MinNameShare), MinNameShare);
        Share(nameof(MinSiblingShare), MinSiblingShare);
        Share(nameof(RoleShare), RoleShare);
        if (FullContentShare < MinContentShare || FullContentShare <= 0)
            problems.Add($"{nameof(FullContentShare)} must be above 0 and at least {nameof(MinContentShare)}");
        if (PeriodToleranceDays is < 0 or > 365) problems.Add($"{nameof(PeriodToleranceDays)} must be between 0 and 365");
        if (MaxCandidates is < 1 or > 50) problems.Add($"{nameof(MaxCandidates)} must be between 1 and 50");
        if (ModelMaxDepth is < 0 or > 10) problems.Add($"{nameof(ModelMaxDepth)} must be between 0 and 10");
        if (ModelMaxFolders is < 1 or > 1000) problems.Add($"{nameof(ModelMaxFolders)} must be between 1 and 1000");
        if (MaxContentCandidates is < 0 or > 20) problems.Add($"{nameof(MaxContentCandidates)} must be between 0 and 20");
        if (FolderSampleSize is < 1 or > 10) problems.Add($"{nameof(FolderSampleSize)} must be between 1 and 10");
        if (UnitSampleSize is < 1 or > 100) problems.Add($"{nameof(UnitSampleSize)} must be between 1 and 100");
        Share(nameof(WeightContentFit), WeightContentFit);
        foreach (var (name, w) in new[]
                 {
                     (nameof(WeightContent), WeightContent), (nameof(WeightPeriod), WeightPeriod), (nameof(WeightName), WeightName),
                     (nameof(WeightType), WeightType), (nameof(SubfolderWeightContent), SubfolderWeightContent),
                     (nameof(SubfolderWeightSiblings), SubfolderWeightSiblings), (nameof(SubfolderWeightType), SubfolderWeightType),
                     (nameof(SubfolderWeightPeriod), SubfolderWeightPeriod), (nameof(SubfolderWeightName), SubfolderWeightName)
                 })
        {
            Share(name, w);
        }
        if (problems.Count > 0)
            throw new ArgumentException("LibraryMatch: " + string.Join("; ", problems));
    }
}
