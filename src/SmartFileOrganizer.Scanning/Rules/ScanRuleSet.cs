using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Scanning.Rules;

/// <summary>What a matching rule does with a folder. File format: <c>skip</c>, <c>shallow</c>, <c>user_data</c>.</summary>
public enum ScanRuleAction
{
    /// <summary>Do not enter the folder and do not list its files.</summary>
    Skip,

    /// <summary>List the files of the folder and count its subfolders, but do not enter them.
    /// With <see cref="ScanRule.MaxDepth"/>, only from that many levels below the matched folder.</summary>
    Shallow,

    /// <summary>Always scan in full: no depth limit and no content check (only the safety limit applies).</summary>
    UserData
}

/// <summary>One rule of a rule file.</summary>
/// <param name="Match">
/// One or more whole path segments, compared case-insensitively. <c>"bin"</c> matches the folder
/// <c>App/bin</c>, not <c>Robin</c>; <c>"ProgramData/Microsoft"</c> (either separator) matches the two
/// segments in a row.
/// </param>
/// <param name="MaxDepth">
/// Only for <see cref="ScanRuleAction.Shallow"/>: how many levels below the matched folder are still
/// scanned in full. 0 records the matched folder itself shallow.
/// </param>
/// <param name="Why">Free text, copied into the stored reason of every folder the rule decides.</param>
public sealed record ScanRule(string Match, ScanRuleAction Action, int MaxDepth = 0, string? Why = null)
{
    private string[]? _segments;

    /// <summary><see cref="Match"/> split into segments on both separators.</summary>
    public IReadOnlyList<string> Segments => _segments ??= PathSegments.Split(Match);
}

/// <summary>
/// A complete set of scan rules: what a rule file (a profile) contains after validation.
/// The rules shipped with the program are <see cref="ScanRulesFile.Default"/>.
/// </summary>
public sealed record ScanRuleSet
{
    public const int CurrentSchemaVersion = 1;

    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// From this depth below the scan root, a folder that no rule covers is recorded shallow if it looks
    /// like a program folder: it holds files of <see cref="ProgramTypes"/> and none of <see cref="UserTypes"/>.
    /// Every other folder is scanned in full.
    /// </summary>
    public int ShallowFrom { get; init; } = 6;

    /// <summary>From this depth every folder is recorded shallow, whatever it contains or which rule
    /// matches. Guards against loops through links; real folder trees stay well below it.</summary>
    public int SafetyLimit { get; init; } = 32;

    /// <summary>Skip folders whose name starts with a dot, unless a rule matches.</summary>
    public bool SkipHidden { get; init; } = true;

    /// <summary>File types that make a folder user data when found among its direct files.</summary>
    public IReadOnlyList<FileType> UserTypes { get; init; } =
        [FileType.Image, FileType.Video, FileType.Audio, FileType.Document];

    /// <summary>File types that mark a program folder, as long as no user file sits next to them.</summary>
    public IReadOnlyList<FileType> ProgramTypes { get; init; } = [FileType.Executable];

    public IReadOnlyList<ScanRule> Rules { get; init; } = [];
}

/// <summary>Splits paths and patterns into segments, independent of the OS the scan runs on.</summary>
public static class PathSegments
{
    private static readonly char[] Separators = ['\\', '/'];

    public static string[] Split(string path) =>
        path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
}
