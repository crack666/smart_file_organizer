namespace SmartFileOrganizer.Application.Findings;

/// <summary>
/// Thresholds of the findings, configuration section <c>Findings</c>
/// (https://github.com/crack666/smart_file_organizer/issues/26). Starting values, not measured ones; the reasons are
/// in docs/findings.md.
/// </summary>
public sealed class FindingOptions
{
    /// <summary>Files worth keeping (certain or likely) a unit needs for a <c>not_backed_up</c> or <c>in_noise</c> finding. Default 1:
    /// one personal document that is nowhere else is exactly what the person wants to hear about.</summary>
    public int MinFiles { get; set; } = 1;

    /// <summary>The same for the unsure part (uncertain or review). Default 1; these findings are listed apart, after the sure ones.</summary>
    public int MinUncertainFiles { get; set; } = 1;

    /// <summary>A folder needs this many judged files of the kinds compared (media, documents, audio) before a few
    /// other files in it count as <c>misplaced</c>. Default 5.</summary>
    public int MisplacedMinFolderFiles { get; set; } = 5;

    /// <summary>The share the main kind needs among those files. Default 0.8: four of five.</summary>
    public double MisplacedDominantShare { get; set; } = 0.8;

    /// <summary>A group of folders with identical files is a <c>duplicate</c> finding from this many redundant bytes. Default 1 MiB.</summary>
    public long DuplicateMinBytes { get; set; } = 1L << 20;

    /// <summary>... or from this many redundant copies, however small. Default 10.</summary>
    public int DuplicateMinFiles { get; set; } = 10;

    /// <summary>How many <c>misplaced</c> groups are matched against the library for a place. Default 50.</summary>
    public int MaxTargetLookups { get; set; } = 50;

    /// <summary>File paths shown per finding as examples. Default 3.</summary>
    public int Examples { get; set; } = 3;

    /// <summary>Throws <see cref="ArgumentException"/> for values the rules cannot work with, naming every problem.</summary>
    public void Validate()
    {
        var problems = new List<string>();
        if (MinFiles < 1) problems.Add("Findings:MinFiles must be at least 1.");
        if (MinUncertainFiles < 1) problems.Add("Findings:MinUncertainFiles must be at least 1.");
        if (MisplacedMinFolderFiles < 2) problems.Add("Findings:MisplacedMinFolderFiles must be at least 2.");
        if (MisplacedDominantShare is <= 0.5 or > 1) problems.Add("Findings:MisplacedDominantShare must be above 0.5 and at most 1.");
        if (DuplicateMinBytes < 0) problems.Add("Findings:DuplicateMinBytes must not be negative.");
        if (DuplicateMinFiles < 1) problems.Add("Findings:DuplicateMinFiles must be at least 1.");
        if (MaxTargetLookups < 0) problems.Add("Findings:MaxTargetLookups must not be negative.");
        if (Examples is < 0 or > 20) problems.Add("Findings:Examples must be between 0 and 20.");
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));
    }
}
