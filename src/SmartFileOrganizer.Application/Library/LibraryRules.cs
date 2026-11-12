using System.Globalization;
using System.Text.RegularExpressions;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Library;

/// <summary>
/// The structure rules of <c>sfo library review</c>: findings from names, counts and sizes only, no model and
/// no file content. Dumps are found first and reported once; every other rule looks only at what lies outside
/// them. Findings are bundled per parent, and rules that compare siblings need
/// <see cref="LibraryReviewOptions.MinSiblings"/> of them. Rules give candidates where only judgement can
/// decide (same purpose twice, which pattern is the target); the decision belongs to the person or the model.
/// </summary>
public static partial class LibraryRules
{
    private static readonly FileType[] ContentTypes =
        [FileType.Image, FileType.Video, FileType.Audio, FileType.Document, FileType.Archive, FileType.Executable, FileType.Code, FileType.Database];

    /// <summary>Runs every rule. The tree must not have been through <see cref="DumpDetector.Mark"/> yet.</summary>
    public static LibraryRuleResult Run(LibraryTree tree, LibraryWords words, LibraryReviewOptions options)
    {
        var dumps = DumpDetector.Mark(tree, words);
        tree.ComputeLiveSums();
        var ctx = new Ctx(tree, words, options);

        var findings = new List<LibraryFinding>();
        findings.AddRange(Dumps(ctx, dumps));
        findings.AddRange(Sensitive(ctx));
        findings.AddRange(DominantFile(ctx));
        findings.AddRange(Empty(ctx));
        findings.AddRange(Placeholders(ctx));
        findings.AddRange(CameraNames(ctx));
        findings.AddRange(SavedWebPages(ctx));
        findings.AddRange(Naming(ctx));
        findings.AddRange(LetterCase(ctx));
        findings.AddRange(Variants(ctx));
        findings.AddRange(FlatAmongStructured(ctx));
        findings.AddRange(LooseFiles(ctx));
        findings.AddRange(VeryFull(ctx));
        findings.AddRange(VeryDeep(ctx));
        findings.AddRange(TypeMismatchByName(ctx));
        findings.AddRange(TypeMismatchByArea(ctx));
        findings.AddRange(SameNameElsewhere(ctx));
        findings.AddRange(SimilarNames(ctx));
        findings.AddRange(LikelyCopies(ctx));

        var excluded = 0;
        if (words.Exceptions.Count > 0)
        {
            var kept = new List<LibraryFinding>(findings.Count);
            foreach (var f in findings)
            {
                if (words.Exceptions.Any(e => e.Covers(f.Kind, f.RelativePath)))
                    excluded++;
                else
                    kept.Add(f);
            }
            findings = kept;
        }

        findings = Rollup(ctx, findings);

        findings.Sort(LibraryFinding.Compare);
        return new LibraryRuleResult(findings, dumps, excluded);
    }

    /// <summary>Kinds whose findings repeat with the structure (every event folder has an empty "edited" folder).</summary>
    private static readonly LibraryFindingKind[] RollupKinds =
    [
        LibraryFindingKind.Empty, LibraryFindingKind.PlaceholderName, LibraryFindingKind.CameraFolderName,
        LibraryFindingKind.SavedWebPage, LibraryFindingKind.CaseInconsistent
    ];

    /// <summary>
    /// Folds findings of one kind that occur below <see cref="LibraryReviewOptions.RollupMin"/> or more children of a
    /// folder into one finding at that folder, level by level, but never into the root: "empty folders in 25
    /// places below Photos" instead of 25 findings.
    /// </summary>
    private static List<LibraryFinding> Rollup(Ctx ctx, List<LibraryFinding> findings)
    {
        var byPath = ctx.Tree.All.ToDictionary(n => n.RelativePath, StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var kind in RollupKinds)
            {
                var candidates = findings.Where(f => f.Kind == kind && byPath.ContainsKey(f.RelativePath)).ToList();
                var groups = candidates
                    .Select(f => (Finding: f, Node: byPath[f.RelativePath]))
                    .Where(x => x.Node.Parent is { Depth: >= 1 })
                    .GroupBy(x => x.Node.Parent!)
                    .Where(g => g.Count() >= ctx.O.RollupMin)
                    .ToList();
                foreach (var group in groups)
                {
                    var parent = group.Key;
                    var members = group.Select(x => x.Finding)
                        .Concat(candidates.Where(f => f.RelativePath == parent.RelativePath))
                        .ToList();
                    var merged = ctx.Make(kind, members.Min(m => m.Impact), parent,
                        $"In {members.Count} places below this folder ({Count(members.Sum(m => m.Directories), "folder", "folders")}, "
                        + $"{Count(members.Sum(m => m.Files), "file", "files")}). For example at {members[0].RelativePath}: {members[0].Reason}",
                        members.Select(m => m.RelativePath).ToList(),
                        members.Sum(m => m.Directories), members.Sum(m => m.Files), members.Sum(m => m.Bytes),
                        new Dictionary<string, object?> { ["places"] = members.Count });
                    findings.RemoveAll(members.Contains);
                    findings.Add(merged);
                    changed = true;
                    break;   // groups were computed before this merge; recompute
                }
            }
        }
        return findings;
    }

    /// <summary>
    /// Whether a folder name says nothing about its content: <c>placeholder</c> (<c>Neuer Ordner</c>),
    /// <c>catch_all</c> (<c>Sonstiges</c>), <c>keyboard</c> (<c>asdf</c>), or null. Copy numbers are ignored.
    /// </summary>
    public static string? PlaceholderKind(string name, LibraryWords words)
    {
        var n = CopyNumberRegex().Replace(LibraryWords.Normalize(name), string.Empty).Trim();
        if (n.Length == 0) return null;
        if (words.PlaceholderNames.Contains(n)) return "placeholder";
        if (words.CatchAllNames.Contains(n)) return "catch_all";
        if (LibraryText.IsKeyboardMash(n)) return "keyboard";
        return null;
    }

    // ------------------------------------------------------------------ context and helpers

    private sealed class Ctx(LibraryTree tree, LibraryWords words, LibraryReviewOptions options)
    {
        public LibraryTree Tree { get; } = tree;
        public LibraryWords Words { get; } = words;
        public LibraryReviewOptions O { get; } = options;

        /// <summary>Directories outside every dump.</summary>
        public IReadOnlyList<LibraryNode> Live { get; } = tree.All.Where(n => !n.InDump).ToList();

        public IEnumerable<LibraryNode> LiveChildren(LibraryNode n) => n.Children.Where(c => !c.InDump);

        public LibraryFinding Make(
            LibraryFindingKind kind, FindingImpact impact, LibraryNode at, string reason,
            IReadOnlyList<string> items, int directories, long files, long bytes,
            Dictionary<string, object?>? details = null)
        {
            var cap = O.ItemsPerFinding;
            return new LibraryFinding(kind, impact, at.FullPath, at.RelativePath, reason,
                items.Take(cap).ToList(), Math.Max(0, items.Count - cap), directories, files, bytes,
                details ?? new Dictionary<string, object?>());
        }

        public bool IsPlaceholder(LibraryNode n) => PlaceholderKind(n.Name) is not null;

        /// <summary><c>placeholder</c>, <c>catch_all</c>, <c>keyboard</c> or null.</summary>
        public string? PlaceholderKind(string name) => LibraryRules.PlaceholderKind(name, Words);

        public string? VariantOf(string name, VariantGroup group) => group.Match(name.Trim());

        public bool IsVariantName(string name) => Words.VariantGroups.Any(g => g.Match(name.Trim()) is not null);
    }

    private static string Count(long n, string one, string many) =>
        n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    private static string Size(long bytes) => LibraryText.Size(bytes);

    private static string TypeCode(FileType t) => t.ToString().ToLowerInvariant();

    private static LibraryNode CommonAncestor(IReadOnlyList<LibraryNode> nodes)
    {
        var candidate = nodes[0].Parent ?? nodes[0];
        while (candidate.Parent is not null && !nodes.All(n => n == candidate || n.IsBelow(candidate)))
            candidate = candidate.Parent;
        return candidate;
    }

    private static string Rel(LibraryNode n) => n.RelativePath;

    /// <summary>The class a subtree mostly holds, among content types: (type, count, total), or null without content.</summary>
    private static (FileType Type, long Count, long Total)? Majority(long[] types)
    {
        long total = 0;
        var best = FileType.Unknown;
        long bestCount = 0;
        foreach (var t in ContentTypes)
        {
            var c = types[(int)t];
            total += c;
            if (c > bestCount)
            {
                bestCount = c;
                best = t;
            }
        }
        return total == 0 ? null : (best, bestCount, total);
    }

    /// <summary>Phone clips next to photos are fine; documents among photos are not.</summary>
    private static bool Compatible(FileType expected, FileType found) =>
        expected == found || (expected is FileType.Image or FileType.Video && found is FileType.Image or FileType.Video);

    // ------------------------------------------------------------------ dumps, sensitive files, one huge file

    private static IEnumerable<LibraryFinding> Dumps(Ctx ctx, IReadOnlyList<LibraryNode> dumps)
    {
        foreach (var group in dumps.GroupBy(d => (d.DumpKind, d.Parent)))
        {
            var members = group.ToList();
            var kind = group.Key.DumpKind!.Value;
            var dirs = members.Sum(m => m.TreeDirs + 1);
            var files = members.Sum(m => m.TreeFiles);
            var bytes = members.Sum(m => m.TreeBytes);
            var empty = members.Sum(m => new[] { m }.Concat(m.Descendants())
                .Count(d => d.IsScanned && d.SubdirCount == 0 && d.Files.Count == 0));
            var depth = members.Max(m => m.TreeMaxDepth - m.Depth);
            var details = new Dictionary<string, object?>
            {
                ["dump_kind"] = kind.ToCode(),
                ["empty_dirs"] = empty,
                ["levels"] = depth,
                ["share_of_dirs"] = Math.Round((double)dirs / Math.Max(1, ctx.Tree.All.Count), 3)
            };

            string reason;
            LibraryNode at;
            List<string> items;
            if (members.Count == 1)
            {
                var m = members[0];
                at = m;
                items = [];
                details["evidence"] = m.DumpReason;
                reason = $"{kind.ToCode()} dump ({m.DumpReason}): {Count(dirs, "folder", "folders")}, {Count(files, "file", "files")}, "
                         + $"{Size(bytes)}, {Count(empty, "empty folder", "empty folders")}, {depth} levels; left out of all other rules.";
            }
            else
            {
                at = group.Key.Parent ?? members[0];
                items = members.Select(m => m.Name).ToList();
                reason = $"{members.Count} {kind.ToCode()} dumps: {Count(dirs, "folder", "folders")}, {Count(files, "file", "files")}, "
                         + $"{Size(bytes)}, {Count(empty, "empty folder", "empty folders")}; left out of all other rules.";
            }
            yield return ctx.Make(LibraryFindingKind.Dump, FindingImpact.High, at, reason, items, dirs, files, bytes, details);
        }
    }

    private static IEnumerable<LibraryFinding> Sensitive(Ctx ctx)
    {
        foreach (var group in ctx.Tree.SensitiveFiles.GroupBy(s => s.Dir))
        {
            var dir = group.Key;
            var markers = group.Aggregate(SensitiveMarker.None, (m, s) => m | s.Markers);
            var tokens = SensitiveMarkerNames.ToTokens(markers);
            var items = group.Select(s => dir.RelativePath == "." ? s.File.Name : $"{dir.RelativePath}/{s.File.Name}").ToList();
            yield return ctx.Make(LibraryFindingKind.SensitiveLocation, FindingImpact.High, dir,
                $"{Count(items.Count, "file", "files")} with access data, keys or identity papers ({string.Join(", ", tokens)}); "
                + "only path and marker are shown.",
                items, 1, items.Count, group.Sum(s => s.File.Size),
                new Dictionary<string, object?> { ["markers"] = tokens, ["in_dump"] = dir.InDump });
        }
    }

    private static IEnumerable<LibraryFinding> DominantFile(Ctx ctx)
    {
        var root = ctx.Tree.Root;
        if (ctx.Tree.LargestFile is not { } largest || root.TreeFiles < 2 || root.TreeBytes <= 0)
            yield break;
        var share = (double)largest.File.Size / root.TreeBytes;
        if (share <= ctx.O.DominantFileShare)
            yield break;
        var path = largest.Dir.RelativePath == "." ? largest.File.Name : $"{largest.Dir.RelativePath}/{largest.File.Name}";
        yield return ctx.Make(LibraryFindingKind.DominantFile, FindingImpact.High, largest.Dir,
            $"One file holds {LibraryText.Percent(share)} of all bytes ({Size(largest.File.Size)} of {Size(root.TreeBytes)}).",
            [path], 1, 1, largest.File.Size,
            new Dictionary<string, object?> { ["share"] = Math.Round(share, 3) });
    }

    // ------------------------------------------------------------------ empty, placeholder, camera, web pages

    private static IEnumerable<LibraryFinding> Empty(Ctx ctx)
    {
        static bool EmptyBranch(LibraryNode n) => n.Depth > 0 && n.TreeComplete && n.TreeFiles == 0;

        var tops = ctx.Live.Where(n => EmptyBranch(n) && !(n.Parent is { } p && EmptyBranch(p))).ToList();
        foreach (var group in tops.GroupBy(n => n.Parent!))
        {
            var members = group.ToList();
            var dirs = members.Sum(m => m.TreeDirs + 1);
            var systemOnly = members.Sum(m => new[] { m }.Concat(m.Descendants()).Count(d => d.SystemFiles > 0));
            var reason = members.Count == 1 && dirs == 1
                ? "An empty folder" + (systemOnly > 0 ? " (only system files such as Thumbs.db)." : ".")
                : $"{Count(members.Count, "empty branch", "empty branches")} with {Count(dirs, "folder", "folders")} in total"
                  + (systemOnly > 0 ? $", {systemOnly} of them holding only system files." : ".");
            yield return ctx.Make(LibraryFindingKind.Empty, dirs >= 20 ? FindingImpact.Medium : FindingImpact.Low,
                group.Key, reason, members.Select(m => m.Name).ToList(), dirs, 0, 0,
                new Dictionary<string, object?> { ["branches"] = members.Count, ["only_system_files"] = systemOnly });
        }
    }

    private static IEnumerable<LibraryFinding> Placeholders(Ctx ctx)
    {
        var hits = ctx.Live.Where(n => n.Depth > 0)
            .Select(n => (Node: n, Kind: ctx.PlaceholderKind(n.Name)))
            .Where(x => x.Kind is not null)
            .Select(x => (x.Node, Kind: x.Kind!, Grown: x.Node.LiveFiles >= ctx.O.GrownFiles || x.Node.LiveBytes >= ctx.O.GrownBytes))
            .ToList();

        // Small ones under one parent are one finding; a grown one keeps its own, its size is the point.
        foreach (var group in hits.Where(h => !h.Grown).GroupBy(h => h.Node.Parent!).Where(g => g.Count() > 1))
        {
            var members = group.ToList();
            var files = members.Sum(m => m.Node.LiveFiles);
            yield return ctx.Make(LibraryFindingKind.PlaceholderName, files > 0 ? FindingImpact.Medium : FindingImpact.Low, group.Key,
                $"{members.Count} folders with placeholder or catch-all names, together {Count(files, "file", "files")} ({Size(members.Sum(m => m.Node.LiveBytes))}).",
                members.Select(m => $"{m.Node.Name} ({m.Node.LiveFiles} files)").ToList(),
                members.Sum(m => m.Node.TreeDirs + 1), files, members.Sum(m => m.Node.LiveBytes),
                new Dictionary<string, object?> { ["name_kind"] = members.Select(m => m.Kind).Distinct().ToList(), ["grown"] = false });
        }

        var bundled = hits.Where(h => !h.Grown).GroupBy(h => h.Node.Parent!).Where(g => g.Count() > 1).SelectMany(g => g).Select(h => h.Node).ToHashSet();
        foreach (var (n, kind, grown) in hits.Where(h => !bundled.Contains(h.Node)))
        {
            var impact = grown ? FindingImpact.High : n.LiveFiles > 0 ? FindingImpact.Medium : FindingImpact.Low;
            var what = kind switch
            {
                "catch_all" => "A catch-all name",
                "keyboard" => "A name typed at random",
                _ => "A placeholder name"
            };
            yield return ctx.Make(LibraryFindingKind.PlaceholderName, impact, n,
                $"{what}; it holds {Count(n.LiveFiles, "file", "files")} ({Size(n.LiveBytes)}) in {Count(n.TreeDirs, "subfolder", "subfolders")}"
                + (grown ? ", it has grown." : "."),
                [], n.TreeDirs + 1, n.LiveFiles, n.LiveBytes,
                new Dictionary<string, object?> { ["name_kind"] = new[] { kind }, ["grown"] = grown });
        }
    }

    private static IEnumerable<LibraryFinding> CameraNames(Ctx ctx)
    {
        foreach (var group in ctx.Live.Where(n => n.Depth > 0 && LibraryText.IsCameraFolder(n.Name)).GroupBy(n => n.Parent!))
        {
            var members = group.ToList();
            yield return ctx.Make(LibraryFindingKind.CameraFolderName, FindingImpact.Low, group.Key,
                $"{Count(members.Count, "folder is", "folders are")} still named by the camera; a name for the occasion is missing.",
                members.Select(m => m.Name).ToList(), members.Count, members.Sum(m => m.LiveFiles), members.Sum(m => m.LiveBytes));
        }
    }

    private static IEnumerable<LibraryFinding> SavedWebPages(Ctx ctx)
    {
        foreach (var parent in ctx.Live)
        {
            var pages = new List<LibraryNode>();
            foreach (var child in ctx.LiveChildren(parent))
            {
                var m = WebFilesRegex().Match(child.Name);
                if (!m.Success) continue;
                var stem = m.Groups["stem"].Value;
                if (parent.Files.Any(f => Path.GetFileNameWithoutExtension(f.Name).Equals(stem, StringComparison.OrdinalIgnoreCase)
                                          && f.Extension is ".htm" or ".html" or ".mht" or ".mhtml" or ".xhtml"))
                    pages.Add(child);
            }
            if (pages.Count == 0) continue;
            yield return ctx.Make(LibraryFindingKind.SavedWebPage, FindingImpact.Low, parent,
                $"{Count(pages.Count, "saved web page", "saved web pages")} (a page file with its _files folder).",
                pages.Select(p => p.Name).ToList(), pages.Count, pages.Sum(p => p.LiveFiles), pages.Sum(p => p.LiveBytes));
        }
    }

    // ------------------------------------------------------------------ sibling rules

    private static IEnumerable<LibraryFinding> Naming(Ctx ctx)
    {
        foreach (var parent in ctx.Live)
        {
            var siblings = ctx.LiveChildren(parent)
                .Where(c => !ctx.IsPlaceholder(c) && !LibraryText.IsCameraFolder(c.Name) && !ctx.IsVariantName(c.Name))
                .Select(c => (Node: c, Pattern: LibraryText.Classify(c.Name)))
                .Where(s => s.Pattern != NamePattern.Other)
                .ToList();
            if (siblings.Count < ctx.O.MinSiblings) continue;

            var dated = siblings.Where(s => NamePattern.IsDated(s.Pattern)).ToList();
            var patterns = siblings.GroupBy(s => s.Pattern)
                .Select(g => (Pattern: g.Key, Count: g.Count()))
                .OrderByDescending(p => p.Count).ThenBy(p => p.Pattern, StringComparer.Ordinal)
                .ToList();
            if (patterns.Count < 2 || dated.Count < 2) continue;

            var majority = patterns[0].Pattern;
            var majorityDated = patterns.First(p => NamePattern.IsDated(p.Pattern)).Pattern;
            var deviants = siblings.Where(s => NamePattern.IsDated(s.Pattern) && s.Pattern != majorityDated)
                .Concat(siblings.Where(s => !NamePattern.IsDated(s.Pattern)))
                .Select(s => s.Node.Name)
                .ToList();

            yield return ctx.Make(LibraryFindingKind.NamingInconsistent, FindingImpact.Medium, parent,
                $"{siblings.Count} subfolders use {patterns.Count} naming patterns ("
                + string.Join(", ", patterns.Select(p => $"{p.Pattern} {p.Count}"))
                + $"); the most common one with a date is {majorityDated}. Which pattern is the target is a decision, not a rule.",
                deviants, siblings.Count, siblings.Sum(s => s.Node.LiveFiles), siblings.Sum(s => s.Node.LiveBytes),
                new Dictionary<string, object?>
                {
                    ["patterns"] = patterns.ToDictionary(p => p.Pattern, p => (object?)p.Count),
                    ["majority"] = majority,
                    ["majority_dated"] = majorityDated
                });
        }
    }

    private static IEnumerable<LibraryFinding> LetterCase(Ctx ctx)
    {
        foreach (var parent in ctx.Live)
        {
            var siblings = ctx.LiveChildren(parent)
                .Where(c => c.Name.Length > 0 && char.IsLetter(c.Name[0]) && char.ToUpperInvariant(c.Name[0]) != char.ToLowerInvariant(c.Name[0]))
                .ToList();
            if (siblings.Count < ctx.O.MinSiblings) continue;

            var upper = siblings.Count(c => char.IsUpper(c.Name[0]));
            var lower = siblings.Count - upper;
            var majorityUpper = upper >= lower;
            var share = (double)Math.Max(upper, lower) / siblings.Count;
            var deviants = siblings.Where(c => char.IsUpper(c.Name[0]) != majorityUpper).ToList();
            if (deviants.Count == 0 || share < ctx.O.CaseMajorityShare) continue;

            yield return ctx.Make(LibraryFindingKind.CaseInconsistent, FindingImpact.Low, parent,
                $"{Count(deviants.Count, "name starts", "names start")} in {(majorityUpper ? "lower" : "upper")} case, "
                + $"{Math.Max(upper, lower)} of {siblings.Count} siblings in {(majorityUpper ? "upper" : "lower")} case.",
                deviants.Select(d => d.Name).ToList(), deviants.Count, deviants.Sum(d => d.LiveFiles), deviants.Sum(d => d.LiveBytes),
                new Dictionary<string, object?> { ["majority"] = majorityUpper ? "upper" : "lower" });
        }
    }

    private static IEnumerable<LibraryFinding> Variants(Ctx ctx)
    {
        foreach (var group in ctx.Words.VariantGroups)
        {
            var covered = new HashSet<LibraryNode>();

            // Across a collection: the series under one folder spell their seasons differently.
            foreach (var grand in ctx.Live)
            {
                var perParent = ctx.LiveChildren(grand)
                    .Select(p => (Parent: p, Ids: ctx.LiveChildren(p).Select(c => ctx.VariantOf(c.Name, group)).OfType<string>().ToList()))
                    .Where(x => x.Ids.Count > 0)
                    .ToList();
                if (perParent.Count < 2) continue;
                var counts = perParent.SelectMany(x => x.Ids).GroupBy(id => id)
                    .Select(g => (Id: g.Key, Count: g.Count()))
                    .OrderByDescending(x => x.Count).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
                if (counts.Count < 2) continue;

                var main = counts[0].Id;
                var items = perParent.Where(x => x.Ids.Any(id => id != main))
                    .Select(x => $"{x.Parent.Name}: {string.Join(", ", x.Ids.Distinct().Where(id => id != main))}")
                    .ToList();
                foreach (var x in perParent) covered.Add(x.Parent);
                yield return ctx.Make(LibraryFindingKind.VariantSpellings, FindingImpact.Medium, grand,
                    $"The {group.Role} level is spelled {counts.Count} ways across {perParent.Count} folders ("
                    + string.Join(", ", counts.Select(c => $"{c.Id} {c.Count}")) + $"); most use {main}.",
                    items, perParent.Count, perParent.Sum(x => x.Parent.LiveFiles), perParent.Sum(x => x.Parent.LiveBytes),
                    new Dictionary<string, object?>
                    {
                        ["role"] = group.Role,
                        ["variants"] = counts.ToDictionary(c => c.Id, c => (object?)c.Count),
                        ["mixed_within_one_folder"] = perParent.Count(x => x.Ids.Distinct().Count() > 1)
                    });
            }

            // Within one folder.
            foreach (var parent in ctx.Live.Where(p => !covered.Contains(p)))
            {
                var children = ctx.LiveChildren(parent)
                    .Select(c => (Node: c, Id: ctx.VariantOf(c.Name, group)))
                    .Where(x => x.Id is not null)
                    .ToList();
                var ids = children.Select(c => c.Id!).Distinct().ToList();
                if (ids.Count < 2) continue;
                yield return ctx.Make(LibraryFindingKind.VariantSpellings, FindingImpact.Medium, parent,
                    $"The {group.Role} level is spelled {ids.Count} ways in one folder ({string.Join(", ", ids)}).",
                    children.Select(c => c.Node.Name).ToList(), children.Count,
                    children.Sum(c => c.Node.LiveFiles), children.Sum(c => c.Node.LiveBytes),
                    new Dictionary<string, object?>
                    {
                        ["role"] = group.Role,
                        ["variants"] = children.GroupBy(c => c.Id!).ToDictionary(g => g.Key, g => (object?)g.Count())
                    });
            }
        }
    }

    private static IEnumerable<LibraryFinding> FlatAmongStructured(Ctx ctx)
    {
        foreach (var parent in ctx.Live)
        {
            var siblings = ctx.LiveChildren(parent).ToList();
            if (siblings.Count < ctx.O.MinSiblings) continue;
            var structured = siblings.Count(s => s.SubdirCount > 0);
            if ((double)structured / siblings.Count < ctx.O.StructuredShare) continue;
            var flat = siblings.Where(s => s.SubdirCount == 0 && s.Files.Count >= ctx.O.FlatMinFiles).ToList();
            if (flat.Count == 0) continue;
            yield return ctx.Make(LibraryFindingKind.FlatAmongStructured, FindingImpact.Medium, parent,
                $"{Count(flat.Count, "folder holds", "folders hold")} {ctx.O.FlatMinFiles} or more files flat, "
                + $"while {structured} of {siblings.Count} siblings are divided into subfolders.",
                flat.Select(f => $"{f.Name} ({f.Files.Count} files)").ToList(), flat.Count,
                flat.Sum(f => (long)f.Files.Count), flat.Sum(f => f.DirectBytes),
                new Dictionary<string, object?> { ["structured"] = structured, ["siblings"] = siblings.Count });
        }
    }

    private static IEnumerable<LibraryFinding> LooseFiles(Ctx ctx)
    {
        foreach (var n in ctx.Live.Where(n => n.Depth <= 1 && n.Files.Count > 0 && n.SubdirCount >= ctx.O.LooseMinSubdirs))
        {
            yield return ctx.Make(LibraryFindingKind.LooseFiles, n.Files.Count >= 10 ? FindingImpact.Medium : FindingImpact.Low, n,
                $"{Count(n.Files.Count, "loose file lies", "loose files lie")} next to {Count(n.SubdirCount, "folder", "folders")} at the top of the library.",
                n.Files.OrderByDescending(f => f.Size).Select(f => f.Name).ToList(), 1, n.Files.Count, n.DirectBytes,
                new Dictionary<string, object?> { ["subdirs"] = n.SubdirCount });
        }
    }

    // ------------------------------------------------------------------ size and depth

    private static IEnumerable<LibraryFinding> VeryFull(Ctx ctx)
    {
        foreach (var n in ctx.Live.Where(n => n.Files.Count > ctx.O.VeryFullFiles))
        {
            var series = n.Profile.NamePatterns.Where(p => p.Pattern is "camera" or "date-prefix" or "screenshot").Sum(p => p.Count);
            var raw = (double)series / n.Files.Count >= 0.5;
            yield return ctx.Make(LibraryFindingKind.VeryFull, FindingImpact.Medium, n,
                $"{Count(n.Files.Count, "file", "files")} directly in one folder"
                + (raw ? "; most are named by a camera or date, so this looks like raw material not yet sorted into occasions." : "."),
                [], 1, n.Files.Count, n.DirectBytes,
                new Dictionary<string, object?> { ["raw_material"] = raw });
        }
    }

    private static IEnumerable<LibraryFinding> VeryDeep(Ctx ctx)
    {
        var depths = ctx.Live.Select(n => n.Depth).OrderBy(d => d).ToList();
        if (depths.Count == 0) yield break;
        var median = depths[depths.Count / 2];
        var threshold = Math.Max(median + ctx.O.DeepMargin, ctx.O.DeepMinDepth);

        var branches = ctx.Live.Where(n => n.Depth > threshold)
            .GroupBy(n => n.Ancestors().First(a => a.Depth == threshold))
            .Select(g => (Top: g.Key, Max: g.Max(n => n.Depth), Count: g.Count()))
            .ToList();
        foreach (var byParent in branches.GroupBy(b => b.Top.Parent ?? b.Top))
        {
            var members = byParent.ToList();
            var max = members.Max(m => m.Max);
            var single = members.Count == 1;
            yield return ctx.Make(LibraryFindingKind.VeryDeep, FindingImpact.Medium, single ? members[0].Top : byParent.Key,
                $"{Count(members.Count, "branch reaches", "branches reach")} depth {max}, while half of all folders lie at depth {median} or less.",
                single ? [] : members.Select(m => m.Top.Name).ToList(), members.Sum(m => m.Count),
                members.Sum(m => m.Top.LiveFiles), members.Sum(m => m.Top.LiveBytes),
                new Dictionary<string, object?> { ["max_depth"] = max, ["median_depth"] = median, ["threshold"] = threshold });
        }
    }

    // ------------------------------------------------------------------ file types

    private static IEnumerable<LibraryFinding> TypeMismatchByName(Ctx ctx)
    {
        var reported = new List<LibraryNode>();
        foreach (var n in ctx.Live.Where(n => n.Depth > 0))
        {
            if (reported.Any(r => n.IsBelow(r))) continue;
            var promised = LibraryText.Tokens(LibraryWords.Normalize(n.Name))
                .Select(t => ctx.Words.TypeWords.TryGetValue(t, out var type) ? type : (FileType?)null)
                .FirstOrDefault(t => t is not null);
            if (promised is not { } expected) continue;
            if (Majority(n.LiveTypes) is not { } m || m.Total < ctx.O.MismatchMinFiles) continue;
            var share = (double)m.Count / m.Total;
            if (Compatible(expected, m.Type) || share < ctx.O.MismatchShare) continue;

            reported.Add(n);
            yield return ctx.Make(LibraryFindingKind.TypeMismatch, m.Total >= 100 ? FindingImpact.High : FindingImpact.Medium, n,
                $"The name promises {TypeCode(expected)} files, but {m.Count} of {m.Total} files below are {TypeCode(m.Type)} ({LibraryText.Percent(share)}).",
                [], 1 + n.TreeDirs, m.Total, n.LiveBytes,
                new Dictionary<string, object?>
                {
                    ["basis"] = "name",
                    ["expected"] = TypeCode(expected),
                    ["found"] = TypeCode(m.Type),
                    ["share"] = Math.Round(share, 3)
                });
        }
    }

    private static IEnumerable<LibraryFinding> TypeMismatchByArea(Ctx ctx)
    {
        foreach (var area in ctx.Live.Where(n => n.Depth == 1))
        {
            if (Majority(area.LiveTypes) is not { } dominant || dominant.Total < ctx.O.AreaMinFiles) continue;
            if ((double)dominant.Count / dominant.Total < ctx.O.AreaDominantShare) continue;

            var odd = new List<(LibraryNode Node, FileType Type, long Count, long Total)>();
            var stack = new Stack<LibraryNode>(ctx.LiveChildren(area));
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (Majority(n.LiveTypes) is { } m && m.Total >= ctx.O.AreaSubtreeMinFiles
                    && !Compatible(dominant.Type, m.Type) && (double)m.Count / m.Total >= ctx.O.AreaDominantShare)
                {
                    odd.Add((n, m.Type, m.Count, m.Total));
                    continue;   // report the topmost subtree only
                }
                foreach (var c in ctx.LiveChildren(n))
                    stack.Push(c);
            }
            if (odd.Count == 0) continue;

            odd.Sort((a, b) => b.Total.CompareTo(a.Total));
            var files = odd.Sum(o => o.Total);
            yield return ctx.Make(LibraryFindingKind.TypeMismatch, files >= 100 ? FindingImpact.High : FindingImpact.Medium, area,
                $"{Count(odd.Count, "subtree holds", "subtrees hold")} mostly other files ({Count(files, "file", "files")}) "
                + $"in an area of {TypeCode(dominant.Type)} files ({LibraryText.Percent((double)dominant.Count / dominant.Total)}).",
                odd.Select(o => $"{Rel(o.Node)} ({TypeCode(o.Type)} {o.Count} of {o.Total})").ToList(),
                odd.Sum(o => 1 + o.Node.TreeDirs), files, odd.Sum(o => o.Node.LiveBytes),
                new Dictionary<string, object?>
                {
                    ["basis"] = "area",
                    ["expected"] = TypeCode(dominant.Type),
                    ["found"] = odd.Select(o => TypeCode(o.Type)).Distinct().ToList()
                });
        }
    }

    // ------------------------------------------------------------------ same purpose, similar names, copies

    private static bool Comparable(Ctx ctx, LibraryNode n) =>
        n.Depth > 0 && !ctx.IsPlaceholder(n) && !LibraryText.IsCameraFolder(n.Name) && !ctx.IsVariantName(n.Name)
        && !ctx.Words.CommonChildNames.Contains(LibraryWords.Normalize(n.Name));

    private static IEnumerable<LibraryFinding> SameNameElsewhere(Ctx ctx)
    {
        var groups = ctx.Live.Where(n => Comparable(ctx, n))
            .Select(n => (Node: n, Key: LibraryText.NameKey(n.Name)))
            .Where(x => x.Key.Length >= 4 && !ctx.Words.CommonChildNames.Contains(x.Key))
            .GroupBy(x => x.Key);
        foreach (var group in groups)
        {
            var members = group.Select(x => x.Node).ToList();
            if (members.Count < 2 || members.Count > ctx.O.SameNameMaxGroup) continue;
            if (members.Select(m => m.Parent).Distinct().Count() < 2) continue;   // a series in one folder ("Trip 2015", "Trip 2016")

            // Below parents named alike it is a regular sub-structure: "2015/Invoices", "2016/Invoices".
            if (members.Select(m => LibraryText.NameKey(m.Parent?.Name ?? string.Empty)).Distinct().Count() == 1) continue;

            var at = CommonAncestor(members);
            yield return ctx.Make(LibraryFindingKind.SameNameElsewhere, FindingImpact.Medium, at,
                $"{members.Count} folders named alike ('{group.Key}') at different places; possibly the same purpose kept twice. A candidate, not a judgement.",
                members.Select(Rel).ToList(), members.Count, members.Sum(m => m.LiveFiles), members.Sum(m => m.LiveBytes),
                new Dictionary<string, object?> { ["key"] = group.Key });
        }
    }

    private static IEnumerable<LibraryFinding> SimilarNames(Ctx ctx)
    {
        var comparable = ctx.Live.Where(n => Comparable(ctx, n)).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Same words in another order or with an extra number: "Holiday Italy", "Italy Holiday 2".
        foreach (var group in comparable
                     .Select(n => (Node: n, Key: LibraryText.WordSetKey(n.Name, ctx.Words.StopWords)))
                     .Where(x => x.Key.Length >= 4)
                     .GroupBy(x => x.Key))
        {
            var members = group.Select(x => x.Node).ToList();
            if (members.Count < 2 || members.Count > ctx.O.SameNameMaxGroup) continue;
            if (members.Select(m => LibraryText.NameKey(m.Name)).Distinct().Count() < 2) continue;   // identical apart from numbers
            var years = members.Select(m => LibraryText.Years(m.Name)).ToList();
            if (years.All(y => y.Count > 0) && years.SelectMany(y => y).Distinct().Count() == members.Count) continue;   // one per year

            seen.Add(string.Join('|', members.Select(m => m.FullPath).OrderBy(p => p, StringComparer.Ordinal)));
            yield return Similar(ctx, members, "the same words in another order or with an extra number");
        }

        // Typing slips between siblings: "Rechnungen", "Rechnugen".
        foreach (var parent in ctx.Live)
        {
            var siblings = ctx.LiveChildren(parent).Where(c => Comparable(ctx, c))
                .Select(c => (Node: c, Key: LibraryText.NameKey(c.Name)))
                .Where(x => x.Key.Length >= 6)
                .Take(300)
                .ToList();
            for (var i = 0; i < siblings.Count; i++)
            {
                for (var j = i + 1; j < siblings.Count; j++)
                {
                    var (a, b) = (siblings[i], siblings[j]);
                    if (a.Key == b.Key || LibraryText.Distance(a.Key, b.Key, 1) > 1) continue;
                    var pair = new[] { a.Node, b.Node };
                    if (!seen.Add(string.Join('|', pair.Select(m => m.FullPath).OrderBy(p => p, StringComparer.Ordinal)))) continue;
                    yield return Similar(ctx, pair, "names one letter apart");
                }
            }
        }
    }

    private static LibraryFinding Similar(Ctx ctx, IReadOnlyList<LibraryNode> members, string how)
    {
        var at = CommonAncestor(members);
        return ctx.Make(LibraryFindingKind.SimilarNames, FindingImpact.Medium, at,
            $"{members.Count} folders with nearly equal names ({how}); possibly one purpose split in two.",
            members.Select(Rel).ToList(), members.Count, members.Sum(m => m.LiveFiles), members.Sum(m => m.LiveBytes),
            new Dictionary<string, object?> { ["names"] = members.Select(m => m.Name).ToList() });
    }

    private static IEnumerable<LibraryFinding> LikelyCopies(Ctx ctx)
    {
        // Whole subtrees outside dumps, or a whole dump (a phone copied twice).
        var candidates = ctx.Tree.All
            .Where(n => n.Depth > 0 && (!n.InDump || n.Dump == n) && n.TreeComplete
                        && n.TreeFiles >= ctx.O.CopyMinFiles && n.TreeBytes >= ctx.O.CopyMinBytes
                        && (n.InDump || n.LiveFiles == n.TreeFiles));
        foreach (var group in candidates.GroupBy(n => (n.TreeFiles, n.TreeBytes)))
        {
            var all = group.ToList();
            if (all.Count < 2) continue;
            // A folder holding nothing but one subfolder has the same totals; keep the innermost, it names the copy.
            var members = all.Where(m => !all.Any(o => o != m && o.IsBelow(m))).ToList();
            if (members.Count < 2) continue;

            var bytes = group.Key.TreeBytes;
            var duplicated = bytes * (members.Count - 1);
            yield return ctx.Make(LibraryFindingKind.LikelyCopy, duplicated >= ctx.O.GrownBytes ? FindingImpact.High : FindingImpact.Medium,
                CommonAncestor(members),
                $"{members.Count} subtrees with the same {Count(group.Key.TreeFiles, "file", "files")} and the same {Size(bytes)} each; "
                + $"probably copies ({Size(duplicated)} twice). A content comparison is the next step.",
                members.Select(Rel).ToList(), members.Sum(m => m.TreeDirs + 1), group.Key.TreeFiles * members.Count, duplicated,
                new Dictionary<string, object?> { ["files_each"] = group.Key.TreeFiles, ["bytes_each"] = bytes });
        }
    }

    // "Neuer Ordner (2)", "New folder 3"
    [GeneratedRegex(@"\s*(?:\(\d+\)|\d+)$")]
    private static partial Regex CopyNumberRegex();

    [GeneratedRegex(@"^(?<stem>.+?)(?:_files|-dateien|_archivos|_fichiers)$", RegexOptions.IgnoreCase)]
    private static partial Regex WebFilesRegex();
}

/// <summary>What the rules returned: findings in review order, the dump roots, and how many findings the person's exceptions removed.</summary>
public sealed record LibraryRuleResult(IReadOnlyList<LibraryFinding> Findings, IReadOnlyList<LibraryNode> Dumps, int ExcludedByExceptions);
