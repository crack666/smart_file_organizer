using System.Globalization;
using System.Text.Json;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Bundles;

/// <summary>Bundles of a job and its statistics with and without dumps.</summary>
public sealed record BundleBuildResult(List<Bundle> Roots, BundleSplitStats Stats);

/// <summary>
/// Turns the scanned tree of a job into bundles, from scan data and stored metadata only, without a model
/// (https://github.com/crack666/smart_file_organizer/issues/39):
/// <list type="number">
/// <item>Dumps: from the root down, a directory whose subdirectory and file names match a
/// <see cref="DumpSignature"/> is a dump; the outermost match wins and nothing below it is looked at again.</item>
/// <item>A split dump (phone, computer) gets one <see cref="BundleKind.DumpKeep"/> child per worth-keeping part
/// and one <see cref="BundleKind.DumpNoise"/> child for the rest; the camera part gets its events as children.</item>
/// <item>Outside dumps, a folder with a meaningful name is a <see cref="BundleKind.Folder"/> bundle with its whole
/// subtree, unless it holds two or more named folders with files: then those are bundles of their own.</item>
/// <item>Photos and videos left loose (in the root or in folders with generic names) are grouped into events.</item>
/// </list>
/// Other loose files belong to no bundle and are counted as unbundled.
/// </summary>
public sealed class BundleBuilder
{
    private readonly BundlePatterns _patterns;
    private readonly BundleOptions _options;

    public BundleBuilder(BundlePatterns patterns, BundleOptions options)
    {
        options.Validate();
        _patterns = patterns;
        _options = options;
    }

    public BundleBuildResult Build(
        string rootPath,
        IReadOnlyList<DirectoryReadRow> directories,
        IReadOnlyList<FileNode> files,
        IReadOnlyDictionary<long, CaptureInfo> captures)
    {
        _partDirectories.Clear();
        var tree = new Tree(rootPath, directories, files);
        var times = new Dictionary<long, FileTime>();
        FileTime TimeOf(FileNode f)
        {
            if (!times.TryGetValue(f.Id, out var t))
                times[f.Id] = t = EventGrouper.TimeOf(f, captures);
            return t;
        }

        var dumps = FindDumps(tree);
        var dumpOf = new Dictionary<string, Bundle>(StringComparer.Ordinal);
        var roots = new List<Bundle>();

        foreach (var dump in dumps)
        {
            var bundle = BuildDump(tree, dump, TimeOf);
            roots.Add(bundle);
            foreach (var d in tree.Subtree(dump.Directory))
                dumpOf[d.FullPath] = bundle;
        }

        var loose = new List<FileNode>();
        long unbundled = 0;
        CollectFolders(tree, dumpOf, roots, loose, ref unbundled);

        foreach (var group in EventGrouper.Group(loose, TimeOf, _options))
            roots.Add(group);

        foreach (var bundle in roots)
            Finish(bundle, tree, TimeOf);

        roots.Sort((a, b) => Order(a).CompareTo(Order(b)) is var c and not 0
            ? c
            : Nullable.Compare(a.FirstTime, b.FirstTime) is var t and not 0 ? t
            : string.CompareOrdinal(a.RootPath ?? a.SuggestedName, b.RootPath ?? b.SuggestedName));

        var stats = Statistics(tree, dumpOf, roots, unbundled);
        return new BundleBuildResult(roots, stats);
    }

    /// <summary>The settings a build used, stored with it.</summary>
    public string SettingsJson() => JsonSerializer.Serialize(new
    {
        event_gap_hours = _options.EventGapHours,
        max_event_days = _options.MaxEventDays,
        min_event_files = _options.MinEventFiles,
        patterns = new { name = _patterns.Name, source = _patterns.Source, hash = _patterns.Hash }
    });

    private static int Order(Bundle b) => b.Kind switch
    {
        BundleKind.Dump => 0,
        BundleKind.Folder => 1,
        BundleKind.Event => 2,
        _ => 3
    };

    // ---------------------------------------------------------------- dumps

    private sealed record DumpMatch(DirectoryReadRow Directory, DumpRecognition Recognition)
    {
        public DumpSignature Signature => Recognition.Signature;
        public List<string> Markers => Recognition.Markers;
    }

    private List<DumpMatch> FindDumps(Tree tree)
    {
        var found = new List<DumpMatch>();
        var queue = new Queue<DirectoryReadRow>();
        queue.Enqueue(tree.Root);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            if (Detect(tree, dir) is { } match)
            {
                found.Add(match with { Directory = Lift(tree, dir) });
                continue;
            }
            foreach (var child in tree.ChildrenOf(dir))
                queue.Enqueue(child);
        }
        return found;
    }

    private DumpMatch? Detect(Tree tree, DirectoryReadRow dir)
    {
        var recognition = _patterns.Recognise(
            tree.ChildrenOf(dir).Select(c => c.Name).ToList(),
            tree.FilesIn(dir).Select(f => f.Name).ToList(),
            isRoot: ReferenceEquals(dir, tree.Root),
            mediaShare: () =>
            {
                var total = tree.SubtreeFiles(dir);
                return total == 0 ? 0 : (double)tree.SubtreeMedia(dir) / total;
            });
        return recognition is null ? null : new DumpMatch(dir, recognition);
    }

    /// <summary>A dump nested in folders that hold nothing else (<c>Phone/Internal storage/DCIM</c>) starts at the outermost of them.</summary>
    private static DirectoryReadRow Lift(Tree tree, DirectoryReadRow dir)
    {
        while (tree.ParentOf(dir) is { } parent
               && !ReferenceEquals(parent, tree.Root)
               && tree.ChildrenOf(parent).Count == 1
               && tree.FilesIn(parent).Count == 0)
        {
            dir = parent;
        }
        return dir;
    }

    private Bundle BuildDump(Tree tree, DumpMatch match, Func<FileNode, FileTime> timeOf)
    {
        var dir = match.Directory;
        var dump = new Bundle
        {
            Kind = BundleKind.Dump,
            Origin = match.Signature.Origin,
            RootPath = dir.FullPath,
            RootDirectoryNodeId = dir.Id,
            Reason = $"Recognised as {match.Recognition.Describe()}.",
            Facts = { Markers = match.Markers }
        };

        if (!match.Signature.Split)
        {
            foreach (var d in tree.Subtree(dir))
                dump.FileIds.AddRange(tree.FilesIn(d).Select(f => f.Id));
            return dump;
        }

        // Each directory of the dump belongs to one part, or to the noise (null).
        var partOf = new Dictionary<string, DumpPart?>(StringComparer.Ordinal);
        foreach (var d in tree.Subtree(dir))
        {
            var segments = ReferenceEquals(d, dir)
                ? []
                : BundlePatterns.SplitPath(Path.GetRelativePath(dir.FullPath, d.FullPath));
            partOf[d.FullPath] = Classify(segments);
        }

        var parts = new Dictionary<DumpPart, Bundle>();
        var noise = new Bundle { Kind = BundleKind.DumpNoise };
        foreach (var d in tree.Subtree(dir))
        {
            var part = partOf[d.FullPath];
            Bundle target;
            if (part is { } p)
            {
                if (!parts.TryGetValue(p, out target!))
                {
                    parts[p] = target = new Bundle { Kind = BundleKind.DumpKeep, Part = p, RootPath = d.FullPath, RootDirectoryNodeId = d.Id };
                }
                // The first directories of a part, not every folder below them.
                var parentPart = tree.ParentOf(d) is { } parent && partOf.TryGetValue(parent.FullPath, out var pp) ? pp : null;
                if (parentPart != p)
                    target.Facts.Directories.Add(tree.Relative(d));
            }
            else
            {
                target = noise;
            }

            target.FileIds.AddRange(tree.FilesIn(d).Select(f => f.Id));
        }

        foreach (var (part, bundle) in parts.OrderBy(p => p.Key))
        {
            bundle.Facts.DirectoriesTotal = bundle.Facts.Directories.Count;
            bundle.Facts.Directories = bundle.Facts.Directories.Order(StringComparer.Ordinal).Take(BundleFacts.MaxListedDirectories).ToList();
            bundle.Reason = $"Worth keeping: the {part.ToCode()} folders of the dump ({string.Join(", ", bundle.Facts.Directories)}).";

            if (part == DumpPart.Camera)
            {
                // The camera roll is decided as one, but shown by occasion below it.
                var members = bundle.FileIds.Select(tree.File).ToList();
                var media = members.Where(Tree.IsMedia).ToList();
                bundle.FileIds = members.Where(f => !Tree.IsMedia(f)).Select(f => f.Id).ToList();
                bundle.Children.AddRange(EventGrouper.Group(media, timeOf, _options));
            }

            dump.Children.Add(bundle);
        }

        noise.Reason = "Everything in the dump outside the worth-keeping parts: app data, caches, system and empty folders.";
        if (noise.FileIds.Count > 0 || tree.Subtree(dir).Any(d => partOf[d.FullPath] is null && !ReferenceEquals(d, dir)))
            dump.Children.Add(noise);

        // Remember which directories each child covers, for directory counts.
        foreach (var child in dump.Children)
            child.RootPath ??= child.Kind == BundleKind.DumpNoise ? dir.FullPath : null;
        _partDirectories[noise] = tree.Subtree(dir).Where(d => partOf[d.FullPath] is null).ToList();
        foreach (var (part, bundle) in parts)
            _partDirectories[bundle] = tree.Subtree(dir).Where(d => partOf[d.FullPath] == part).ToList();

        return dump;
    }

    private readonly Dictionary<Bundle, List<DirectoryReadRow>> _partDirectories = new(ReferenceEqualityComparer.Instance);

    /// <summary>The part of a directory inside a dump by its path below the dump root; see <see cref="BundlePatterns.PartOf"/>.</summary>
    public DumpPart? Classify(IReadOnlyList<string> segments) => _patterns.PartOf(segments);

    // ---------------------------------------------------------------- folders and loose files

    private void CollectFolders(Tree tree, Dictionary<string, Bundle> dumpOf, List<Bundle> roots, List<FileNode> loose, ref long unbundled)
    {
        // Files per subtree without dumps, to see which named folders carry anything.
        var filesOutsideDumps = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var d in tree.BottomUp())
        {
            if (dumpOf.ContainsKey(d.FullPath)) continue;
            var count = tree.FilesIn(d).Count + tree.ChildrenOf(d).Sum(c => filesOutsideDumps.GetValueOrDefault(c.FullPath));
            filesOutsideDumps[d.FullPath] = count;
        }

        var stack = new Stack<DirectoryReadRow>();
        stack.Push(tree.Root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (dumpOf.ContainsKey(dir.FullPath)) continue;
            var children = tree.ChildrenOf(dir).Where(c => !dumpOf.ContainsKey(c.FullPath)).ToList();

            var meaningful = !ReferenceEquals(dir, tree.Root) && !_patterns.IsGenericName(dir.Name);
            if (!meaningful)
            {
                foreach (var file in tree.FilesIn(dir))
                {
                    if (Tree.IsMedia(file)) loose.Add(file);
                    else unbundled++;
                }
                foreach (var child in children)
                    stack.Push(child);
                continue;
            }

            var namedChildren = children
                .Where(c => !_patterns.IsGenericName(c.Name) && filesOutsideDumps.GetValueOrDefault(c.FullPath) > 0)
                .ToList();

            if (namedChildren.Count >= 2)
            {
                // A folder of occasions ("Family" with "Christmas 2015" and "Easter 2016"): each is a bundle.
                if (tree.FilesIn(dir).Count > 0)
                {
                    var own = NewFolder(dir, tree.FilesIn(dir).Select(f => f.Id), [dir]);
                    own.Facts.DirectFilesOnly = true;
                    own.Reason = "Existing folder with a meaningful name, taken as it is: only its own files, "
                                 + $"because its {namedChildren.Count} named subfolders are bundles of their own.";
                    roots.Add(own);
                }
                foreach (var child in children)
                    stack.Push(child);
                continue;
            }

            // A named folder without files outside dumps: nothing to decide (dumps below it are bundles already).
            if (filesOutsideDumps.GetValueOrDefault(dir.FullPath) == 0)
                continue;

            var covered = tree.Subtree(dir).Where(d => !dumpOf.ContainsKey(d.FullPath)).ToList();
            var folder = NewFolder(dir, covered.SelectMany(d => tree.FilesIn(d)).Select(f => f.Id), covered);
            folder.Reason = "Existing folder with a meaningful name, taken as it is with everything below it.";
            roots.Add(folder);
        }
    }

    private Bundle NewFolder(DirectoryReadRow dir, IEnumerable<long> fileIds, List<DirectoryReadRow> covered)
    {
        var folder = new Bundle
        {
            Kind = BundleKind.Folder,
            RootPath = dir.FullPath,
            RootDirectoryNodeId = dir.Id,
            SuggestedName = dir.Name,
            FileIds = fileIds.ToList()
        };
        _partDirectories[folder] = covered;
        return folder;
    }

    // ---------------------------------------------------------------- counts, times, names

    /// <summary>Fills counts, times and names of a bundle and its children; returns the files of its subtree.</summary>
    private List<FileNode> Finish(Bundle bundle, Tree tree, Func<FileNode, FileTime> timeOf)
    {
        var all = bundle.FileIds.Select(tree.File).ToList();
        foreach (var child in bundle.Children)
            all.AddRange(Finish(child, tree, timeOf));

        bundle.FileCount = all.Count;
        bundle.TotalBytes = all.Sum(f => f.Size);
        bundle.Facts.ByFileType = Count(all.Select(f => TypeCode(f.FileType)));

        var fileTimes = all.Select(timeOf).ToList();
        var dated = fileTimes.Where(t => t.Time is not null).ToList();
        bundle.Facts.TimeSources = Count(dated.Select(t => t.Source.ToCode()));
        if (dated.Count > 0)
        {
            bundle.FirstTime = dated.Min(t => t.Time);
            bundle.LastTime = dated.Max(t => t.Time);
            var sources = dated.Select(t => t.Source).Distinct().ToList();
            bundle.TimeSource = sources.Count == 1 ? sources[0] : TimeSource.Mixed;
        }

        // The period of a split dump is that of its worth-keeping parts: app data and caches carry the time of
        // the last copy, which would stretch "phone 2013" to "phone 2013-2026".
        var kept = bundle.Children.Where(c => c.Kind == BundleKind.DumpKeep && c.FirstTime is not null).ToList();
        if (bundle.Kind == BundleKind.Dump && kept.Count > 0)
        {
            bundle.FirstTime = kept.Min(c => c.FirstTime);
            bundle.LastTime = kept.Max(c => c.LastTime);
            var keptSources = kept.Select(c => c.TimeSource).Distinct().ToList();
            bundle.TimeSource = keptSources.Count == 1 ? keptSources[0] : TimeSource.Mixed;
        }

        var devices = fileTimes.Select(t => t.Device).Distinct().ToList();
        if (devices.Count == 1 && devices[0] is { } device)
            bundle.Facts.Device = device;

        // Directories a bundle covers: its subtree (dump, folder), its classified directories (parts),
        // or the directories holding its files (events).
        List<DirectoryReadRow> covered;
        if (_partDirectories.TryGetValue(bundle, out var list))
            covered = list;
        else if (bundle.Kind == BundleKind.Dump && bundle.RootPath is not null)
            covered = tree.Subtree(tree.Directory(bundle.RootPath)!).ToList();
        else
            covered = all.Select(f => f.ParentPath).Distinct(StringComparer.Ordinal)
                .Select(tree.Directory).Where(d => d is not null).Select(d => d!).ToList();
        bundle.DirectoryCount = covered.Count;
        bundle.EmptyDirectoryCount = covered.Count(tree.IsEmpty);

        if (bundle.Kind is BundleKind.Event or BundleKind.Scattered)
        {
            var relative = covered.Select(tree.Relative).Order(StringComparer.Ordinal).ToList();
            bundle.Facts.DirectoriesTotal = relative.Count;
            bundle.Facts.Directories = relative.Take(BundleFacts.MaxListedDirectories).ToList();
        }

        Name(bundle, tree, fileTimes);
        return all;
    }

    private void Name(Bundle bundle, Tree tree, List<FileTime> fileTimes)
    {
        var gap = _options.EventGapHours.ToString(CultureInfo.InvariantCulture);
        switch (bundle.Kind)
        {
            case BundleKind.Dump:
                var dumpName = tree.Directory(bundle.RootPath!)?.Name ?? "dump";
                bundle.SuggestedName = $"{dumpName} ({bundle.Origin.ToCode()}{Years(bundle)})";
                foreach (var child in bundle.Children)
                {
                    child.SuggestedName = child.Kind == BundleKind.DumpNoise
                        ? $"{dumpName}: noise"
                        : $"{dumpName}: {child.Part.ToCode()}";
                }
                break;

            case BundleKind.Event:
                bundle.SuggestedName = Period(bundle);
                bundle.Facts.CopyTimeSuspected = fileTimes.All(t => t.Source == TimeSource.ModifiedDate)
                                                 && bundle.FileCount >= 20
                                                 && bundle.LastTime - bundle.FirstTime <= TimeSpan.FromSeconds(bundle.FileCount);
                bundle.Reason = $"{bundle.FileCount} photos and videos from {Period(bundle)} with no pause longer than {gap} h"
                                + $" (times from {bundle.TimeSource.ToCode()})"
                                + (bundle.Facts.Device is { } d ? $", all taken with {d}" : string.Empty)
                                + (bundle.Facts.CopyTimeSuspected
                                    ? "; the modification times lie within seconds, so they are probably the time of a copy, not of the occasion."
                                    : ".");
                break;

            case BundleKind.Scattered:
                bundle.Reason = $"Photos and videos of {(bundle.FirstTime is { } t ? t.Year.ToString(CultureInfo.InvariantCulture) : "unknown date")} "
                                + $"in groups of fewer than {_options.MinEventFiles} files.";
                break;
        }
    }

    private static string Years(Bundle b) =>
        b.FirstTime is not { } first || b.LastTime is not { } last ? string.Empty
        : first.Year == last.Year ? $" {first.Year.ToString(CultureInfo.InvariantCulture)}"
        : $" {first.Year.ToString(CultureInfo.InvariantCulture)}-{last.Year.ToString(CultureInfo.InvariantCulture)}";

    private static string Period(Bundle b)
    {
        if (b.FirstTime is not { } first || b.LastTime is not { } last) return "unknown date";
        var from = first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return from == to ? from : $"{from} to {to}";
    }

    // ---------------------------------------------------------------- statistics

    private BundleSplitStats Statistics(Tree tree, Dictionary<string, Bundle> dumpOf, List<Bundle> roots, long unbundled)
    {
        var all = new BundleSplitStats.PartStats();
        var dumps = new BundleSplitStats.PartStats();
        var without = new BundleSplitStats.PartStats();

        foreach (var d in tree.All)
        {
            var inDump = dumpOf.ContainsKey(d.FullPath);
            var empty = tree.IsEmpty(d);
            foreach (var s in new[] { all, inDump ? dumps : without })
            {
                s.Directories++;
                if (empty) s.EmptyDirectories++;
                foreach (var f in tree.FilesIn(d))
                {
                    s.Files++;
                    s.TotalBytes += f.Size;
                    var code = TypeCode(f.FileType);
                    s.ByFileType[code] = s.ByFileType.GetValueOrDefault(code) + 1;
                }
            }
        }

        foreach (var s in new[] { all, dumps, without })
            s.ByFileType = Sorted(s.ByFileType);

        return new BundleSplitStats
        {
            All = all,
            Dumps = dumps,
            WithoutDumps = without,
            DumpCount = roots.Count(r => r.Kind == BundleKind.Dump),
            BundleCount = CountAll(roots),
            UnbundledFiles = unbundled
        };
    }

    private static int CountAll(IEnumerable<Bundle> bundles) => bundles.Sum(b => 1 + CountAll(b.Children));

    public static string TypeCode(FileType type) => JsonNamingPolicy.SnakeCaseLower.ConvertName(type.ToString());

    private static Dictionary<string, long> Count(IEnumerable<string> keys) =>
        Sorted(keys.GroupBy(k => k, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal));

    private static Dictionary<string, long> Sorted(Dictionary<string, long> counts) =>
        counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    // ---------------------------------------------------------------- tree

    private sealed class Tree
    {
        private readonly Dictionary<string, DirectoryReadRow> _byPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<DirectoryReadRow>> _children = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<FileNode>> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<long, FileNode> _fileById = new();
        private readonly Dictionary<string, long> _subtreeFiles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _subtreeMedia = new(StringComparer.Ordinal);
        private readonly string _rootPath;

        public Tree(string rootPath, IReadOnlyList<DirectoryReadRow> directories, IReadOnlyList<FileNode> files)
        {
            _rootPath = Path.TrimEndingDirectorySeparator(rootPath);
            foreach (var d in directories)
                _byPath[d.FullPath] = d;

            Root = _byPath.TryGetValue(_rootPath, out var root)
                ? root
                : directories.OrderBy(d => d.Depth).ThenBy(d => d.FullPath, StringComparer.Ordinal).FirstOrDefault()
                  ?? new DirectoryReadRow { FullPath = _rootPath, Name = Path.GetFileName(_rootPath), ParentPath = _rootPath, RelativePath = ".", DirStatus = DirectoryStatus.Scanned };
            _byPath[Root.FullPath] = Root;

            foreach (var d in _byPath.Values)
            {
                if (ReferenceEquals(d, Root) || d.ParentPath == d.FullPath || !_byPath.ContainsKey(d.ParentPath)) continue;
                if (!_children.TryGetValue(d.ParentPath, out var list))
                    _children[d.ParentPath] = list = [];
                list.Add(d);
            }
            foreach (var list in _children.Values)
                list.Sort((a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

            foreach (var f in files)
            {
                _fileById[f.Id] = f;
                if (!_files.TryGetValue(f.ParentPath, out var list))
                    _files[f.ParentPath] = list = [];
                list.Add(f);
            }

            foreach (var d in BottomUp())
            {
                var direct = FilesIn(d);
                _subtreeFiles[d.FullPath] = direct.Count + ChildrenOf(d).Sum(c => _subtreeFiles.GetValueOrDefault(c.FullPath));
                _subtreeMedia[d.FullPath] = direct.Count(IsMedia) + ChildrenOf(d).Sum(c => _subtreeMedia.GetValueOrDefault(c.FullPath));
            }
        }

        public DirectoryReadRow Root { get; }

        /// <summary>Every directory reachable from the root, parents first.</summary>
        public IEnumerable<DirectoryReadRow> All => Subtree(Root);

        public static bool IsMedia(FileNode f) => f.FileType is FileType.Image or FileType.Video;

        public IReadOnlyList<DirectoryReadRow> ChildrenOf(DirectoryReadRow d) =>
            _children.TryGetValue(d.FullPath, out var list) ? list : [];

        public IReadOnlyList<FileNode> FilesIn(DirectoryReadRow d) =>
            _files.TryGetValue(d.FullPath, out var list) ? list : [];

        public DirectoryReadRow? ParentOf(DirectoryReadRow d) =>
            ReferenceEquals(d, Root) ? null : _byPath.GetValueOrDefault(d.ParentPath);

        public DirectoryReadRow? Directory(string path) => _byPath.GetValueOrDefault(path);

        public FileNode File(long id) => _fileById[id];

        public long SubtreeFiles(DirectoryReadRow d) => _subtreeFiles.GetValueOrDefault(d.FullPath);
        public long SubtreeMedia(DirectoryReadRow d) => _subtreeMedia.GetValueOrDefault(d.FullPath);

        /// <summary>No files and no subdirectories, in a directory the scan read completely.</summary>
        public bool IsEmpty(DirectoryReadRow d) =>
            d.DirStatus == DirectoryStatus.Scanned && FilesIn(d).Count == 0 && ChildrenOf(d).Count == 0;

        public string Relative(DirectoryReadRow d) =>
            ReferenceEquals(d, Root) ? "." : Path.GetRelativePath(_rootPath, d.FullPath);

        /// <summary>The directory and everything below it, parents first; iterative, so deep trees are safe.</summary>
        public IEnumerable<DirectoryReadRow> Subtree(DirectoryReadRow start)
        {
            var stack = new Stack<DirectoryReadRow>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                yield return d;
                var children = ChildrenOf(d);
                for (var i = children.Count - 1; i >= 0; i--)
                    stack.Push(children[i]);
            }
        }

        /// <summary>Every reachable directory, children before parents.</summary>
        public IEnumerable<DirectoryReadRow> BottomUp() => All.Reverse();
    }
}
