using System.Text.RegularExpressions;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Computes a <see cref="DirectoryProfile"/> from scan data alone: no model, no file contents.
/// Deterministic, so the same directory always yields the same profile.
/// </summary>
public static partial class DirectoryProfileBuilder
{
    /// <summary>Share of files of one type (or one naming scheme) from which a directory looks <c>uniform</c>.</summary>
    public const double UniformShare = 0.8;

    /// <summary>Below this share for the largest type, with at least <see cref="MixedMinTypes"/> types, a directory looks <c>mixed</c>.</summary>
    public const double MixedMaxShare = 0.5;

    public const int MixedMinTypes = 3;

    /// <summary>Patterns that say "these files belong together by how they were made".</summary>
    private static readonly string[] SeriesPatterns = ["camera", "date-prefix", "screenshot"];

    private static readonly (string Id, Regex Regex)[] NamePatternRules =
    [
        ("camera", CameraRegex()),
        ("date-prefix", DatePrefixRegex()),
        ("screenshot", ScreenshotRegex()),
        ("copy-suffix", CopySuffixRegex()),
    ];

    /// <summary>Subdirectory names that mark version control or a dependency tree.</summary>
    private static readonly string[] UnitMarkerDirectories =
        [".git", ".svn", ".hg", "node_modules", "WindowsImageBackup"];

    /// <summary>File names that mark a software project or a game.</summary>
    private static readonly string[] UnitMarkerFiles =
    [
        "package.json", "pyproject.toml", "setup.py", "Cargo.toml", "go.mod", "pom.xml",
        "build.gradle", "build.gradle.kts", "CMakeLists.txt", "composer.json", "Gemfile",
        "steam_appid.txt",
    ];

    /// <summary>Extensions of solution/project files and backup images.</summary>
    private static readonly string[] UnitMarkerExtensions =
    [
        ".sln", ".slnx", ".csproj", ".vbproj", ".fsproj", ".vcxproj",
        ".tib", ".tibx", ".vbk", ".vib", ".mrimg",
    ];

    public static DirectoryProfile Build(
        IReadOnlyList<DirectoryFileInfo> files,
        IReadOnlyList<string> subdirectoryNames)
    {
        var profile = new DirectoryProfile
        {
            FileCount = files.Count,
            TotalBytes = files.Sum(f => f.Size),
            SubdirectoryCount = subdirectoryNames.Count,
            Extensions = Bucket(files, f => NormalizeExtension(f)),
            FileTypes = Bucket(files, f => TypeName(f.FileType)),
        };

        var dated = files.Where(f => f.LastWriteTime > DateTime.MinValue).ToList();
        if (dated.Count > 0)
        {
            profile.OldestWrite = dated.Min(f => f.LastWriteTime);
            profile.NewestWrite = dated.Max(f => f.LastWriteTime);
            profile.SpanDays = (int)(profile.NewestWrite.Value - profile.OldestWrite.Value).TotalDays;
        }

        foreach (var (id, regex) in NamePatternRules)
        {
            var count = files.Count(f => regex.IsMatch(Path.GetFileNameWithoutExtension(f.Name)));
            if (count > 0)
                profile.NamePatterns.Add(new NamePatternCount { Pattern = id, Count = count });
        }

        profile.UnitMarkers = FindUnitMarkers(files, subdirectoryNames);
        Suggest(profile);
        return profile;
    }

    private static List<string> FindUnitMarkers(IReadOnlyList<DirectoryFileInfo> files, IReadOnlyList<string> subdirectoryNames)
    {
        var markers = new List<string>();

        foreach (var name in UnitMarkerDirectories)
        {
            if (subdirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                markers.Add($"dir:{name}");
        }

        foreach (var name in UnitMarkerFiles)
        {
            if (files.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                markers.Add($"file:{name}");
        }

        foreach (var ext in UnitMarkerExtensions)
        {
            if (files.Any(f => NormalizeExtension(f) == ext))
                markers.Add($"ext:{ext}");
        }

        var hasExe = files.Any(f => NormalizeExtension(f) == ".exe");
        if (hasExe && files.Any(f => NormalizeExtension(f) == ".dll"))
            markers.Add("program:exe+dll");

        // Unity games ship "<Game>.exe" next to "<Game>_Data".
        if (hasExe && subdirectoryNames.Any(n => n.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)))
            markers.Add("game:exe+_Data");

        return markers;
    }

    private static void Suggest(DirectoryProfile profile)
    {
        if (profile.FileCount == 0 || profile.FileTypes.Count == 0) return;

        var top = profile.FileTypes[0];
        var topShare = (double)top.Count / profile.FileCount;
        var series = profile.NamePatterns
            .Where(p => SeriesPatterns.Contains(p.Pattern))
            .OrderByDescending(p => p.Count)
            .FirstOrDefault();

        if (profile.FileCount >= 2 && topShare >= UniformShare)
        {
            profile.Suggests = DirectoryVerdictKind.Uniform;
            profile.SuggestionReason = $"{top.Count} of {profile.FileCount} files are {top.Key} ({topShare:P0}).";
            if (series is not null && (double)series.Count / profile.FileCount >= UniformShare)
                profile.SuggestionReason += $" {series.Count} follow the {series.Pattern} naming pattern.";
            return;
        }

        if (profile.FileCount >= 2 && series is not null && (double)series.Count / profile.FileCount >= UniformShare)
        {
            profile.Suggests = DirectoryVerdictKind.Uniform;
            profile.SuggestionReason = $"{series.Count} of {profile.FileCount} files follow the {series.Pattern} naming pattern.";
            return;
        }

        if (profile.FileCount >= MixedMinTypes && profile.FileTypes.Count >= MixedMinTypes && topShare < MixedMaxShare)
        {
            profile.Suggests = DirectoryVerdictKind.Mixed;
            profile.SuggestionReason =
                $"{profile.FileTypes.Count} file types, the largest ({top.Key}) only {topShare:P0} of {profile.FileCount} files.";
        }
    }

    private static List<ProfileBucket> Bucket(IReadOnlyList<DirectoryFileInfo> files, Func<DirectoryFileInfo, string> key) =>
        files.GroupBy(key)
            .Select(g => new ProfileBucket { Key = g.Key, Count = g.Count(), Bytes = g.Sum(f => f.Size) })
            .OrderByDescending(b => b.Count)
            .ThenByDescending(b => b.Bytes)
            .ThenBy(b => b.Key, StringComparer.Ordinal)
            .ToList();

    private static string NormalizeExtension(DirectoryFileInfo f)
    {
        var ext = string.IsNullOrEmpty(f.Extension) ? Path.GetExtension(f.Name) : f.Extension;
        return ext.ToLowerInvariant();
    }

    private static string TypeName(int fileType) =>
        Enum.IsDefined(typeof(FileType), fileType) ? ((FileType)fileType).ToString() : FileType.Unknown.ToString();

    // IMG_1234, DSC01234, DSCN0001, MVI_0042, VID_20240101_..., PXL_20240101_..., GOPR0001, GX010001, P1010001
    [GeneratedRegex(@"^(?:(?:IMG|DSC[NF]?|MVI|MOV|VID|PXL|GOPR|G[HX]\d{2}|SAM|DJI)[_-]?\d{3,}|P\d{7})", RegexOptions.IgnoreCase)]
    private static partial Regex CameraRegex();

    // 2024-05-17 ..., 20240517_..., 2024_05_17
    [GeneratedRegex(@"^(?:19|20)\d{2}[-_.]?(?:0[1-9]|1[0-2])[-_.]?(?:0[1-9]|[12]\d|3[01])", RegexOptions.IgnoreCase)]
    private static partial Regex DatePrefixRegex();

    [GeneratedRegex(@"^(?:Screenshot|Screen Shot|Bildschirmfoto)", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenshotRegex();

    // "report (1)", "report - Copy", "Bericht - Kopie"
    [GeneratedRegex(@"(?:\(\d+\)|[ _-](?:copy|kopie))$", RegexOptions.IgnoreCase)]
    private static partial Regex CopySuffixRegex();
}
