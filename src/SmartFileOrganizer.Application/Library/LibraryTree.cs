using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Library;

/// <summary>
/// One directory of a job in memory, with its direct files and the sums over its subtree. Built from scan
/// data only (<see cref="LibraryTree.Build"/>); nothing here touches the disk.
/// </summary>
public sealed class LibraryNode
{
    public required string FullPath { get; init; }
    public required string Name { get; init; }

    /// <summary>Path below the job root with <c>/</c> as separator; <c>.</c> for the root.</summary>
    public required string RelativePath { get; init; }

    public required int Depth { get; init; }
    public required DirectoryStatus Status { get; init; }

    /// <summary>Subdirectories the scan counted. For a shallow folder they exist but have no node.</summary>
    public required int StoredSubdirCount { get; init; }

    /// <summary>Stored verdict code and purpose, when the job was analysed.</summary>
    public string? Verdict { get; init; }
    public string? Purpose { get; init; }

    public LibraryNode? Parent { get; internal set; }
    public List<LibraryNode> Children { get; } = [];

    /// <summary>Direct files that count as content (system files such as Thumbs.db left out).</summary>
    public List<DirectoryFileInfo> Files { get; } = [];

    /// <summary>Direct system files (Thumbs.db, .DS_Store, <c>._*</c>).</summary>
    public int SystemFiles { get; internal set; }

    public long DirectBytes { get; internal set; }

    /// <summary>Content files in the subtree, this directory included.</summary>
    public long TreeFiles { get; internal set; }
    public long TreeBytes { get; internal set; }

    /// <summary>Directories below this one.</summary>
    public int TreeDirs { get; internal set; }

    /// <summary>Depth of the deepest directory in the subtree.</summary>
    public int TreeMaxDepth { get; internal set; }

    /// <summary>False when a folder in the subtree was skipped, recorded shallow, failed or is pending: sums are then lower bounds.</summary>
    public bool TreeComplete { get; internal set; } = true;

    /// <summary>Content files in the subtree per <see cref="FileType"/> (index = enum value).</summary>
    public long[] TreeTypes { get; } = new long[Enum.GetValues<FileType>().Length];

    /// <summary>The dump this directory belongs to (itself if it is the dump's root), or null.</summary>
    public LibraryNode? Dump { get; internal set; }

    public DumpKind? DumpKind { get; internal set; }
    public string? DumpReason { get; internal set; }

    public bool InDump => Dump is not null;

    /// <summary>Like <see cref="TreeFiles"/>, <see cref="TreeBytes"/> and <see cref="TreeTypes"/>, but without dumps below. Set by <see cref="LibraryTree.ComputeLiveSums"/>.</summary>
    public long LiveFiles { get; internal set; }
    public long LiveBytes { get; internal set; }
    public long[] LiveTypes { get; } = new long[Enum.GetValues<FileType>().Length];

    /// <summary>Number of subdirectories, also for a shallow folder whose children were not entered.</summary>
    public int SubdirCount => Math.Max(Children.Count, StoredSubdirCount);

    public bool IsScanned => Status == DirectoryStatus.Scanned;

    private DirectoryProfile? _profile;

    /// <summary>Profile of the direct files and subdirectory names (<see cref="DirectoryProfileBuilder"/>).</summary>
    public DirectoryProfile Profile => _profile ??= DirectoryProfileBuilder.Build(Files, Children.Select(c => c.Name).ToList());

    public IEnumerable<LibraryNode> Ancestors()
    {
        for (var p = Parent; p is not null; p = p.Parent)
            yield return p;
    }

    public IEnumerable<LibraryNode> Descendants()
    {
        var stack = new Stack<LibraryNode>(Children);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.Children)
                stack.Push(child);
        }
    }

    public bool IsBelow(LibraryNode other) => Ancestors().Contains(other);

    public override string ToString() => RelativePath;
}

/// <summary>The directory tree of a job with sums, the sensitive files and the largest file. Built once per review.</summary>
public sealed class LibraryTree
{
    public required LibraryNode Root { get; init; }

    /// <summary>Every directory, parents before children (by depth, then path).</summary>
    public required IReadOnlyList<LibraryNode> All { get; init; }

    /// <summary>Content files with a credential, key or identity marker (scan rules or extra name words).</summary>
    public List<(LibraryNode Dir, DirectoryFileInfo File, SensitiveMarker Markers)> SensitiveFiles { get; } = [];

    public (LibraryNode Dir, DirectoryFileInfo File)? LargestFile { get; internal set; }

    /// <summary>System files over the whole job.</summary>
    public long SystemFiles { get; internal set; }

    /// <summary>
    /// Builds the tree from the stored directories and a stream of the stored files. Files whose parent is not
    /// a stored directory are ignored. Directories outside the job root are dropped.
    /// </summary>
    public static async Task<LibraryTree> BuildAsync(
        string rootPath,
        IReadOnlyList<DirectoryReadRow> directories,
        IAsyncEnumerable<StructureFileRow> files,
        LibraryWords words,
        CancellationToken ct)
    {
        var byPath = new Dictionary<string, LibraryNode>(StringComparer.Ordinal);
        foreach (var d in directories.OrderBy(d => d.Depth).ThenBy(d => d.FullPath, StringComparer.Ordinal))
        {
            var node = new LibraryNode
            {
                FullPath = d.FullPath,
                Name = d.Depth == 0 ? (string.IsNullOrEmpty(d.Name) ? d.FullPath : d.Name) : d.Name,
                RelativePath = d.Depth == 0 || d.RelativePath is "" or "." ? "." : d.RelativePath.Replace('\\', '/'),
                Depth = d.Depth,
                Status = d.DirStatus,
                StoredSubdirCount = d.SubdirCount,
                Verdict = d.Verdict,
                Purpose = d.Purpose
            };
            byPath[d.FullPath] = node;
            if (d.Depth > 0 && byPath.TryGetValue(d.ParentPath, out var parent))
            {
                node.Parent = parent;
                parent.Children.Add(node);
            }
        }

        var root = byPath.GetValueOrDefault(rootPath)
                   ?? byPath.Values.FirstOrDefault(n => n.Depth == 0)
                   ?? throw new InvalidOperationException("The job has no root directory; was it scanned?");

        // Keep only what hangs below the root (a resumed job never stores orphans, but be strict).
        var all = new List<LibraryNode> { root };
        all.AddRange(root.Descendants());
        all.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : string.CompareOrdinal(a.FullPath, b.FullPath));
        foreach (var n in all)
            n.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var tree = new LibraryTree { Root = root, All = all };
        var inTree = all.ToDictionary(n => n.FullPath, StringComparer.Ordinal);

        long largest = -1;
        await foreach (var f in files.WithCancellation(ct))
        {
            if (!inTree.TryGetValue(f.ParentPath, out var dir))
                continue;
            if (words.IsSystemFile(f.Name))
            {
                dir.SystemFiles++;
                tree.SystemFiles++;
                continue;
            }

            var info = new DirectoryFileInfo
            {
                Name = f.Name,
                Size = f.Size,
                FileType = f.FileType,
                Extension = f.Extension,
                LastWriteTime = f.LastWriteTime
            };
            dir.Files.Add(info);
            dir.DirectBytes += f.Size;

            var markers = (f.SensitiveMarkers & (SensitiveMarker.Zugangsdaten | SensitiveMarker.Schluessel | SensitiveMarker.Ausweis))
                          | ExtraSensitive(f.Name, words);
            if (markers != SensitiveMarker.None)
                tree.SensitiveFiles.Add((dir, info, markers));

            if (f.Size > largest)
            {
                largest = f.Size;
                tree.LargestFile = (dir, info);
            }
        }

        // Sums bottom-up: deepest first.
        for (var i = all.Count - 1; i >= 0; i--)
        {
            var n = all[i];
            n.TreeFiles += n.Files.Count;
            n.TreeBytes += n.DirectBytes;
            n.TreeMaxDepth = Math.Max(n.TreeMaxDepth, n.Depth);
            if (n.Status != DirectoryStatus.Scanned || n.StoredSubdirCount > n.Children.Count)
                n.TreeComplete = false;
            foreach (var f in n.Files)
                n.TreeTypes[TypeIndex(f.FileType)]++;

            if (n.Parent is { } p)
            {
                p.TreeFiles += n.TreeFiles;
                p.TreeBytes += n.TreeBytes;
                p.TreeDirs += n.TreeDirs + 1;
                p.TreeMaxDepth = Math.Max(p.TreeMaxDepth, n.TreeMaxDepth);
                if (!n.TreeComplete) p.TreeComplete = false;
                for (var t = 0; t < p.TreeTypes.Length; t++)
                    p.TreeTypes[t] += n.TreeTypes[t];
            }
        }

        return tree;
    }

    /// <summary>Sums over each subtree without the dumps in it. Call after <see cref="DumpDetector.Mark"/>.</summary>
    public void ComputeLiveSums()
    {
        for (var i = All.Count - 1; i >= 0; i--)
        {
            var n = All[i];
            n.LiveFiles = 0;
            n.LiveBytes = 0;
            Array.Clear(n.LiveTypes);
        }
        for (var i = All.Count - 1; i >= 0; i--)
        {
            var n = All[i];
            if (n.InDump) continue;
            n.LiveFiles += n.Files.Count;
            n.LiveBytes += n.DirectBytes;
            foreach (var f in n.Files)
                n.LiveTypes[TypeIndex(f.FileType)]++;
            if (n.Parent is { } p)
            {
                p.LiveFiles += n.LiveFiles;
                p.LiveBytes += n.LiveBytes;
                for (var t = 0; t < p.LiveTypes.Length; t++)
                    p.LiveTypes[t] += n.LiveTypes[t];
            }
        }
    }

    private static int TypeIndex(int fileType) =>
        Enum.IsDefined(typeof(FileType), fileType) ? fileType : (int)FileType.Unknown;

    private static SensitiveMarker ExtraSensitive(string fileName, LibraryWords words)
    {
        if (words.SensitiveNameWords.Count == 0) return SensitiveMarker.None;
        var name = LibraryWords.Normalize(Path.GetFileNameWithoutExtension(fileName));
        var tokens = LibraryText.Tokens(name);
        foreach (var word in words.SensitiveNameWords)
        {
            // Short words only as a whole word ("puk", "2fa"); longer ones anywhere ("backupcodes").
            if (word.Length <= 4 ? tokens.Contains(word) : name.Contains(word, StringComparison.Ordinal))
                return SensitiveMarker.Zugangsdaten;
        }
        return SensitiveMarker.None;
    }
}
