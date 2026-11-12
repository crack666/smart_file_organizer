using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Clear cases that are decided without calling the model. Every verdict from here carries
/// <see cref="VerdictSource.Regel"/>, the rule id in <see cref="DirectoryVerdict.Rule"/> and a readable reason.
/// </summary>
public static class DirectoryVerdictRules
{
    public const string InsideUnit = "inside-unit";
    public const string ScanSkip = "scan-skip";
    public const string Hidden = "hidden";
    public const string Marker = "marker";
    public const string Empty = "empty";
    public const string Container = "container";

    /// <summary>Dominant type key for a collection of units in <see cref="DirectoryProfile.SubdirectoryDominantType"/>.</summary>
    public const string UnitDominantType = "unit";

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
    /// Rules that need only the directory itself and its nearest ancestor that is a unit.
    /// Order matters: being part of a unit wins over everything, because a unit is never split.
    /// </summary>
    public static (Next Next, DirectoryVerdict? Verdict) Evaluate(
        DirectoryNode dir, DirectoryProfile profile, DirectoryVerdict? unitAncestor, string? unitAncestorPath)
    {
        if (unitAncestor is not null)
        {
            return Decide(dir, profile, DirectoryVerdictKind.Einheit, InsideUnit,
                $"Part of the unit '{unitAncestorPath}'; a unit is handled only as a whole.",
                unitAncestor.Retention,
                unitAncestor.UnitDirectoryNodeId ?? unitAncestor.DirectoryNodeId);
        }

        if (dir.DirStatus == DirectoryStatus.Skip)
        {
            return Decide(dir, profile, DirectoryVerdictKind.System, ScanSkip,
                $"Excluded by the scan rules ({dir.DirReason ?? "Skip"}); its content was not listed.",
                RetentionValue.Behalten, null);
        }

        if (dir.Name.StartsWith('.'))
        {
            return Decide(dir, profile, DirectoryVerdictKind.System, Hidden,
                "Hidden directory (name starts with a dot): tool or application data, not user content.",
                RetentionValue.Behalten, null);
        }

        if (profile.UnitMarkers.Count > 0)
        {
            return Decide(dir, profile, DirectoryVerdictKind.Einheit, Marker,
                $"Markers of a project, program, game or backup: {string.Join(", ", profile.UnitMarkers)}.",
                RetentionValue.Pruefen, dir.Id);
        }

        if (profile.FileCount == 0 && dir.DirStatus == DirectoryStatus.Scanned)
        {
            if (profile.SubdirectoryCount == 0)
            {
                return Decide(dir, profile, DirectoryVerdictKind.Leer, Empty,
                    "No files and no subdirectories.", RetentionValue.Entbehrlich, null);
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
                Source = VerdictSource.Regel,
                Rule = Container,
                Profile = profile,
                JudgedAt = DateTime.UtcNow,
                Error = $"{missing} of {children.Count} subdirectories have no verdict yet; retried on the next run."
            };
        }

        var judged = children.Select(c => c!).ToList();
        var content = judged.Where(c => c.Verdict != DirectoryVerdictKind.Leer).ToList();
        if (content.Count == 0)
        {
            return Decide(dir, profile, DirectoryVerdictKind.Leer, Container,
                $"No direct files and all {judged.Count} subdirectories are empty.", RetentionValue.Entbehrlich, null).Verdict!;
        }

        var nonSystem = content.Where(c => c.Verdict != DirectoryVerdictKind.System).ToList();
        if (nonSystem.Count == 0)
        {
            return Decide(dir, profile, DirectoryVerdictKind.System, Container,
                $"No direct files and all {content.Count} non-empty subdirectories are system directories.",
                RetentionValue.Behalten, null).Verdict!;
        }

        var keys = nonSystem.Select(DominantType).Distinct(StringComparer.Ordinal).ToList();
        var allAlike = nonSystem.All(c => c.Verdict is DirectoryVerdictKind.Einheit or DirectoryVerdictKind.Einheitlich)
                       && keys.Count == 1 && keys[0] is not null;

        if (allAlike)
        {
            profile.SubdirectoryDominantType = keys[0];
            var what = keys[0] == UnitDominantType ? "units (projects, programs, games or backups)" : $"collections of {keys[0]}";
            return Decide(dir, profile, DirectoryVerdictKind.Einheitlich, Container,
                $"No direct files; all {nonSystem.Count} subdirectories with content are {what}.",
                RetentionValue.Pruefen, null).Verdict!;
        }

        var summary = string.Join(", ", nonSystem
            .GroupBy(c => c.Verdict!.Value.ToCode())
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {g.Key}"));
        return Decide(dir, profile, DirectoryVerdictKind.Gemischt, Container,
            $"No direct files; subdirectories differ ({summary}), so each is handled on its own.",
            RetentionValue.Pruefen, null).Verdict!;
    }

    /// <summary>What a judged directory mainly holds, for comparing siblings: <c>unit</c>, a file type name, or null.</summary>
    public static string? DominantType(DirectoryVerdict v)
    {
        if (v.Verdict == DirectoryVerdictKind.Einheit) return UnitDominantType;
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
            Source = VerdictSource.Regel,
            Rule = rule,
            Retention = retention,
            Reason = reason,
            UnitDirectoryNodeId = kind == DirectoryVerdictKind.Einheit ? unitId : null,
            Profile = profile,
            JudgedAt = DateTime.UtcNow
        });
}
