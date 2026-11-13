using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Clear cases that are decided without calling the model. Every verdict from here carries
/// <see cref="VerdictSource.Rule"/>, the rule id in <see cref="DirectoryVerdict.Rule"/> and a readable reason.
/// </summary>
public static class DirectoryVerdictRules
{
    public const string InsideUnit = "inside-unit";
    public const string ScanSkip = "scan-skip";
    public const string Hidden = "hidden";
    public const string Marker = "marker";
    public const string Dump = "dump";
    public const string DumpNoise = "dump-noise";
    public const string Empty = "empty";
    public const string Container = "container";

    /// <summary>Dominant type key for a collection of units in <see cref="DirectoryProfile.SubdirectoryDominantType"/>.</summary>
    public const string UnitDominantType = "unit";

    /// <summary>Dominant type key for a collection of dumps (a <c>Users</c> folder with several profiles).</summary>
    public const string DumpDominantType = "dump";

    /// <summary>Where a directory lies inside the nearest dump above it.</summary>
    /// <param name="DumpPath">The dump root, relative to the scan root.</param>
    /// <param name="NoisePath">The <c>noise_paths</c> entry the directory lies at or below, or null for a part
    /// that is judged (a worth-keeping part such as <c>Documents</c>, or a folder no pattern names).</param>
    public sealed record DumpPlace(string DumpPath, string? NoisePath);

    public enum Next
    {
        /// <summary>A rule decided; the verdict is set.</summary>
        Decided,
        /// <summary>No direct files but subdirectories: decide from the subdirectory verdicts once they exist.</summary>
        FromSubdirectories,
        /// <summary>No rule applies; ask the model.</summary>
        AskModel
    }

    /// <summary>
    /// Rules that need only the directory itself, its nearest ancestor that is a unit and its place in the
    /// nearest dump above it. Order matters: being part of a unit wins over everything, because a unit is
    /// never split. Inside a dump only the noise is decided here; every other part goes on through the rules
    /// below and the model like any directory (https://github.com/crack666/smart_file_organizer/issues/59).
    /// </summary>
    /// <param name="dump">The directory recognised as a dump by <see cref="Bundles.BundlePatterns.Recognise"/>, the
    /// one recognition the bundles and the library review use as well; null when it is none. Unpacked software
    /// is a <c>unit</c>; a copy of a phone, computer, user profile or removable drive is a <c>dump</c>.</param>
    /// <param name="inDump">Where the directory lies inside the nearest dump above it; null outside every dump.</param>
    public static (Next Next, DirectoryVerdict? Verdict) Evaluate(
        DirectoryNode dir, DirectoryProfile profile, DirectoryVerdict? unitAncestor, string? unitAncestorPath,
        Bundles.DumpRecognition? dump = null, DumpPlace? inDump = null)
    {
        if (unitAncestor is not null)
        {
            return Decide(dir, profile, DirectoryVerdictKind.Unit, InsideUnit,
                $"Part of the unit '{unitAncestorPath}'; a unit is handled only as a whole.",
                unitAncestor.Retention,
                unitAncestor.UnitDirectoryNodeId ?? unitAncestor.DirectoryNodeId);
        }

        if (dir.DirStatus == DirectoryStatus.Skip)
        {
            return Decide(dir, profile, DirectoryVerdictKind.System, ScanSkip,
                $"Excluded by the scan rules ({dir.DirReason ?? "Skip"}); its content was not listed.",
                RetentionValue.Keep, null);
        }

        if (dir.Name.StartsWith('.'))
        {
            return Decide(dir, profile, DirectoryVerdictKind.System, Hidden,
                "Hidden directory (name starts with a dot): tool or application data, not user content.",
                RetentionValue.Keep, null);
        }

        if (inDump?.NoisePath is { } noisePath)
        {
            return Decide(dir, profile, DirectoryVerdictKind.System, DumpNoise,
                $"Noise of the dump '{inDump.DumpPath}': at or below '{noisePath}' (app data, caches, system); "
                + "its files are not analysed one by one.",
                RetentionValue.Review, null);
        }

        if (profile.UnitMarkers.Count > 0)
        {
            return Decide(dir, profile, DirectoryVerdictKind.Unit, Marker,
                $"Markers of a project, program, game or backup: {string.Join(", ", profile.UnitMarkers)}.",
                RetentionValue.Review, dir.Id);
        }

        if (dump is not null)
        {
            if (dump.Signature.Origin == DumpOrigin.Software)
            {
                return Decide(dir, profile, DirectoryVerdictKind.Unit, Dump,
                    $"Recognised as a {dump.Describe()}; unpacked software is handled as a whole.",
                    RetentionValue.Review, dir.Id);
            }

            return Decide(dir, profile, DirectoryVerdictKind.Dump, Dump,
                $"Recognised as a {dump.Describe()}. A copy of a device or computer is no unit: its parts are judged "
                + "on their own, only its noise (app data, caches, system) is skipped.",
                RetentionValue.Review, null);
        }

        if (profile.FileCount == 0 && dir.DirStatus == DirectoryStatus.Scanned)
        {
            if (profile.SubdirectoryCount == 0)
            {
                return Decide(dir, profile, DirectoryVerdictKind.Empty, Empty,
                    "No files and no subdirectories.", RetentionValue.Disposable, null);
            }

            return (Next.FromSubdirectories, null);
        }

        return (Next.AskModel, null);
    }

    /// <summary>
    /// A directory without direct files, judged from its subdirectories. Empty subdirectories are
    /// ignored, and so are system subdirectories next to real content.
    /// </summary>
    public static DirectoryVerdict EvaluateFromSubdirectories(
        DirectoryNode dir, DirectoryProfile profile, IReadOnlyList<DirectoryVerdict?> children)
    {
        var missing = children.Count(c => c is null || c.Verdict is null);
        if (missing > 0)
        {
            return new DirectoryVerdict
            {
                DirectoryNodeId = dir.Id,
                Source = VerdictSource.Rule,
                Rule = Container,
                Profile = profile,
                JudgedAt = DateTime.UtcNow,
                Error = $"{missing} of {children.Count} subdirectories have no verdict yet; retried on the next run."
            };
        }

        var judged = children.Select(c => c!).ToList();
        var content = judged.Where(c => c.Verdict != DirectoryVerdictKind.Empty).ToList();
        if (content.Count == 0)
        {
            return Decide(dir, profile, DirectoryVerdictKind.Empty, Container,
                $"No direct files and all {judged.Count} subdirectories are empty.", RetentionValue.Disposable, null).Verdict!;
        }

        var nonSystem = content.Where(c => c.Verdict != DirectoryVerdictKind.System).ToList();
        if (nonSystem.Count == 0)
        {
            return Decide(dir, profile, DirectoryVerdictKind.System, Container,
                $"No direct files and all {content.Count} non-empty subdirectories are system directories.",
                RetentionValue.Keep, null).Verdict!;
        }

        var keys = nonSystem.Select(DominantType).Distinct(StringComparer.Ordinal).ToList();
        var allAlike = nonSystem.All(c => c.Verdict is DirectoryVerdictKind.Unit or DirectoryVerdictKind.Uniform or DirectoryVerdictKind.Dump)
                       && keys.Count == 1 && keys[0] is not null;

        if (allAlike)
        {
            profile.SubdirectoryDominantType = keys[0];
            var what = keys[0] switch
            {
                UnitDominantType => "units (projects, programs, games or backups)",
                DumpDominantType => "dumps (copies of devices, computers or user profiles)",
                _ => $"collections of {keys[0]}"
            };
            return Decide(dir, profile, DirectoryVerdictKind.Uniform, Container,
                $"No direct files; all {nonSystem.Count} subdirectories with content are {what}.",
                RetentionValue.Review, null).Verdict!;
        }

        var summary = string.Join(", ", nonSystem
            .GroupBy(c => c.Verdict!.Value.ToCode())
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {g.Key}"));
        return Decide(dir, profile, DirectoryVerdictKind.Mixed, Container,
            $"No direct files; subdirectories differ ({summary}), so each is handled on its own.",
            RetentionValue.Review, null).Verdict!;
    }

    /// <summary>What a judged directory mainly holds, for comparing siblings: <c>unit</c>, <c>dump</c>, a file type name, or null.</summary>
    public static string? DominantType(DirectoryVerdict v)
    {
        if (v.Verdict == DirectoryVerdictKind.Unit) return UnitDominantType;
        if (v.Verdict == DirectoryVerdictKind.Dump) return DumpDominantType;
        if (v.Profile.FileCount > 0 && v.Profile.FileTypes.Count > 0) return v.Profile.FileTypes[0].Key;
        return v.Profile.SubdirectoryDominantType;
    }

    private static (Next Next, DirectoryVerdict? Verdict) Decide(
        DirectoryNode dir, DirectoryProfile profile, DirectoryVerdictKind kind, string rule,
        string reason, RetentionValue? retention, long? unitId) =>
        (Next.Decided, new DirectoryVerdict
        {
            DirectoryNodeId = dir.Id,
            Verdict = kind,
            Source = VerdictSource.Rule,
            Rule = rule,
            Retention = retention,
            Reason = reason,
            UnitDirectoryNodeId = kind == DirectoryVerdictKind.Unit ? unitId : null,
            Profile = profile,
            JudgedAt = DateTime.UtcNow
        });
}
