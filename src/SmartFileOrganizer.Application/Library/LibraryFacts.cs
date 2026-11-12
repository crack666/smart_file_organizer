using System.Globalization;
using System.Text;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Library;

/// <summary>
/// The compact description of a tree sent to the local model: an outline of the upper levels with counts,
/// types, years and the naming patterns of the children, then the findings. Folder names only, never file
/// names, so marked files are not named. Bounded in size whatever the tree holds.
/// </summary>
public static class LibraryFacts
{
    /// <summary>Most outline lines sent; deeper or further folders are summarised per parent.</summary>
    public const int MaxOutlineLines = 250;

    /// <summary>Most children listed under one parent before the rest is summarised.</summary>
    public const int MaxChildrenPerParent = 20;

    public const int MaxFindings = 40;

    /// <summary>Levels below the root shown in the outline.</summary>
    public const int OutlineDepth = 3;

    public static string ForReview(LibraryTree tree, LibraryRuleResult rules)
    {
        var sb = new StringBuilder();
        var root = tree.Root;
        sb.AppendLine($"Library: {Num(root.TreeDirs + 1)} folders, {Num(root.TreeFiles)} files, {Size(root.TreeBytes)}.");
        sb.AppendLine($"Outside dumps: {Num(tree.All.Count(n => !n.InDump))} folders, {Num(root.LiveFiles)} files.");
        if (rules.Dumps.Count > 0)
        {
            sb.AppendLine("Dumps (device, computer or package copies; left out of the outline):");
            foreach (var d in rules.Dumps)
                sb.AppendLine($"- [{d.DumpKind!.Value.ToCode()}] {d.RelativePath}: {Num(d.TreeDirs + 1)} folders, {Num(d.TreeFiles)} files, {Size(d.TreeBytes)}");
        }

        sb.AppendLine();
        sb.AppendLine("Outline (path | subfolders | files and main types | years of the files | how the subfolders are named):");
        AppendOutline(sb, tree, includeVerdicts: false);

        sb.AppendLine();
        sb.AppendLine($"Findings of the structure rules (most important first, {Math.Min(MaxFindings, rules.Findings.Count)} of {rules.Findings.Count}):");
        foreach (var f in rules.Findings.Where(f => f.Kind != LibraryFindingKind.SensitiveLocation).Take(MaxFindings))
        {
            var items = f.Items.Count == 0 ? string.Empty : $" [{string.Join("; ", f.Items)}{(f.More > 0 ? $"; +{f.More}" : string.Empty)}]";
            sb.AppendLine($"- {f.Kind.ToCode()} @ {f.RelativePath}: {f.Reason}{items}");
        }
        var sensitive = rules.Findings.Count(f => f.Kind == LibraryFindingKind.SensitiveLocation);
        if (sensitive > 0)
            sb.AppendLine($"- sensitive_location: {sensitive} folder(s) hold access data or keys (names withheld).");
        return sb.ToString();
    }

    public static string ForProposal(LibraryTree tree, LibraryRuleResult rules)
    {
        var sb = new StringBuilder();
        var root = tree.Root;
        sb.AppendLine($"Source: {Num(root.TreeDirs + 1)} folders, {Num(root.TreeFiles)} files, {Size(root.TreeBytes)}.");
        sb.AppendLine("Content by file type (outside dumps): " + Types(root.LiveTypes, root.LiveFiles, all: true));
        if (rules.Dumps.Count > 0)
        {
            sb.AppendLine("Dumps (device, computer or package copies):");
            foreach (var d in rules.Dumps)
                sb.AppendLine($"- [{d.DumpKind!.Value.ToCode()}] {d.RelativePath}: {Num(d.TreeFiles)} files");
        }

        var occasions = tree.All
            .Where(n => !n.InDump && n.Files.Count >= 5 && Share(n, FileType.Image) + Share(n, FileType.Video) >= 0.8)
            .OrderByDescending(n => n.Files.Count)
            .Take(30)
            .ToList();
        if (occasions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Photo and video folders (name | files | dates of the files):");
            foreach (var n in occasions)
                sb.AppendLine($"- {n.RelativePath} | {n.Files.Count} | {Span(n.Profile.OldestWrite, n.Profile.NewestWrite)}");
        }

        sb.AppendLine();
        sb.AppendLine("Outline (path | subfolders | files and main types | years | naming of subfolders | verdict: purpose, where judged):");
        AppendOutline(sb, tree, includeVerdicts: true);
        return sb.ToString();
    }

    private static void AppendOutline(StringBuilder sb, LibraryTree tree, bool includeVerdicts)
    {
        var lines = 0;
        var queue = new Queue<LibraryNode>();
        queue.Enqueue(tree.Root);
        var years = YearSpans(tree);
        while (queue.Count > 0 && lines < MaxOutlineLines)
        {
            var n = queue.Dequeue();
            sb.Append(n.RelativePath).Append(" | ").Append(n.SubdirCount).Append(" | ")
              .Append(Num(n.LiveFiles)).Append(": ").Append(Types(n.LiveTypes, n.LiveFiles, all: false))
              .Append(" | ").Append(years.TryGetValue(n, out var y) ? y : "-")
              .Append(" | ").Append(ChildPatterns(n));
            if (includeVerdicts && n.Verdict is not null)
                sb.Append(" | ").Append(n.Verdict).Append(n.Purpose is null ? string.Empty : ": " + n.Purpose);
            sb.AppendLine();
            lines++;

            if (n.Depth >= OutlineDepth) continue;
            var children = n.Children.Where(c => !c.InDump).ToList();
            foreach (var c in children.OrderByDescending(c => c.LiveFiles).Take(MaxChildrenPerParent))
                queue.Enqueue(c);
            if (children.Count > MaxChildrenPerParent)
            {
                sb.AppendLine($"{n.RelativePath}/... | {children.Count - MaxChildrenPerParent} more subfolders not listed");
                lines++;
            }
        }
        if (queue.Count > 0)
            sb.AppendLine($"... {queue.Count} more folders not listed");
    }

    private static string ChildPatterns(LibraryNode n)
    {
        var children = n.Children.Where(c => !c.InDump).ToList();
        if (children.Count == 0) return "-";
        return string.Join(", ", children.GroupBy(c => LibraryText.Classify(c.Name))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} {g.Count()}"));
    }

    private static Dictionary<LibraryNode, string> YearSpans(LibraryTree tree)
    {
        var min = new Dictionary<LibraryNode, int>();
        var max = new Dictionary<LibraryNode, int>();
        for (var i = tree.All.Count - 1; i >= 0; i--)
        {
            var n = tree.All[i];
            if (n.InDump) continue;
            foreach (var f in n.Files.Where(f => f.LastWriteTime.Year > 1970))
            {
                min[n] = Math.Min(min.GetValueOrDefault(n, int.MaxValue), f.LastWriteTime.Year);
                max[n] = Math.Max(max.GetValueOrDefault(n, int.MinValue), f.LastWriteTime.Year);
            }
            if (n.Parent is { } p && min.TryGetValue(n, out var lo))
            {
                min[p] = Math.Min(min.GetValueOrDefault(p, int.MaxValue), lo);
                max[p] = Math.Max(max.GetValueOrDefault(p, int.MinValue), max[n]);
            }
        }
        return min.ToDictionary(kv => kv.Key, kv => kv.Value == max[kv.Key] ? $"{kv.Value}" : $"{kv.Value}-{max[kv.Key]}");
    }

    private static string Types(long[] types, long total, bool all)
    {
        if (total == 0) return "none";
        var parts = Enum.GetValues<FileType>()
            .Select(t => (Type: t, Count: types[(int)t]))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .Take(all ? 11 : 3)
            .Select(x => all
                ? $"{x.Type.ToString().ToLowerInvariant()} {Num(x.Count)}"
                : $"{x.Type.ToString().ToLowerInvariant()} {LibraryText.Percent((double)x.Count / total)}");
        return string.Join(", ", parts);
    }

    private static double Share(LibraryNode n, FileType type) =>
        n.Files.Count == 0 ? 0 : (double)n.Files.Count(f => f.FileType == (int)type) / n.Files.Count;

    private static string Span(DateTime? from, DateTime? to) =>
        from is null || to is null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}");

    private static string Num(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Size(long bytes) => LibraryText.Size(bytes);
}
