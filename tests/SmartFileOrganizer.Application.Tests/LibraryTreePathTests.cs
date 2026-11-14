using SmartFileOrganizer.Application.Bundles;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// The folder tree of a library is built from stored paths whatever form the root was stored in
/// (https://github.com/crack666/smart_file_organizer/issues/97): with or without a trailing separator, in another case,
/// with the other separator, or stored twice. Before, a root scanned with a trailing separator had no subfolders: its
/// subfolders name it without one as their parent. Invented paths; nothing touches the disk.
/// </summary>
public class LibraryTreePathTests
{
    private static readonly char Sep = Path.DirectorySeparatorChar;
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\Ablage" : "/srv/ablage";

    private static DirectoryReadRow Row(string full, string parent, string relative, int depth, int subdirs = 0) => new()
    {
        FullPath = full,
        Name = depth == 0 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(full)) : Path.GetFileName(full),
        ParentPath = parent,
        RelativePath = relative,
        Depth = depth,
        DirStatus = DirectoryStatus.Scanned,
        SubdirCount = subdirs
    };

    /// <summary>The rows a scan of <paramref name="root"/> stores: the root as given, the folders below as the file system names them.</summary>
    private static List<DirectoryReadRow> Scanned(string root)
    {
        var plain = Path.TrimEndingDirectorySeparator(root);
        var photos = Path.Combine(plain, "Fotos");
        return
        [
            Row(root, Path.GetDirectoryName(plain) ?? plain, ".", 0, subdirs: 2),
            Row(photos, plain, "Fotos", 1, subdirs: 1),
            Row(Path.Combine(photos, "Skifahrt 2015"), photos, Path.Combine("Fotos", "Skifahrt 2015"), 2),
            Row(Path.Combine(plain, "Dokumente"), plain, "Dokumente", 1)
        ];
    }

    private static async IAsyncEnumerable<StructureFileRow> Files(params (string Dir, string Name)[] files)
    {
        foreach (var (dir, name) in files)
        {
            await Task.Yield();
            yield return new StructureFileRow
            {
                ParentPath = dir, Name = name, Size = 100, Extension = Path.GetExtension(name), FileType = (int)FileType.Image,
                LastWriteTime = new DateTime(2015, 2, 7, 10, 0, 0, DateTimeKind.Utc)
            };
        }
    }

    private static Task<LibraryTree> BuildAsync(string rootPath, IReadOnlyList<DirectoryReadRow> rows, params (string Dir, string Name)[] files) =>
        LibraryTree.BuildAsync(rootPath, rows, Files(files), LibraryWords.Default, CancellationToken.None);

    private static void HasTopLevel(LibraryTree tree)
    {
        Assert.Equal(["Dokumente", "Fotos"], tree.Root.Children.Select(c => c.Name).ToList());
        Assert.Equal(["Skifahrt 2015"], tree.Root.Children.Single(c => c.Name == "Fotos").Children.Select(c => c.Name).ToList());
        Assert.Equal(4, tree.All.Count);
    }

    [Fact]
    public async Task ARootStoredWithoutTrailingSeparator_HasItsTopLevel()
    {
        HasTopLevel(await BuildAsync(Root, Scanned(Root)));
    }

    /// <summary>The cause of Issue 97: the root as typed, <c>\\server\share\</c> or <c>D:\Photos\</c>.</summary>
    [Fact]
    public async Task ARootStoredWithTrailingSeparator_HasItsTopLevel_AndItsFilesBelow()
    {
        var root = Root + Sep;
        var photo = Path.Combine(Root, "Fotos", "Skifahrt 2015");
        var tree = await BuildAsync(root, Scanned(root), (photo, "IMG_20150207_100000.jpg"), (root, "liesmich.txt"));

        HasTopLevel(tree);
        Assert.Equal(2, tree.Root.TreeFiles);
        Assert.Equal(1, tree.Root.Children.Single(c => c.Name == "Fotos").TreeFiles);
    }

    [Fact]
    public async Task AJobRootWithTrailingSeparator_FindsTheRootRowWithout_AndTheOtherWayRound()
    {
        HasTopLevel(await BuildAsync(Root + Sep, Scanned(Root)));
        HasTopLevel(await BuildAsync(Root, Scanned(Root + Sep)));
    }

    [Fact]
    public async Task TheRootStoredTwice_IsOneFolder()
    {
        var rows = Scanned(Root + Sep);
        rows.Add(Row(Root, Path.GetDirectoryName(Root) ?? Root, ".", 0, subdirs: 2));
        // And once more as a row below itself, as a row whose parent is itself.
        rows.Add(Row(Root, Root, ".", 1));

        var tree = await BuildAsync(Root + Sep, rows);

        HasTopLevel(tree);
        Assert.DoesNotContain(tree.All.Skip(1), n => PathKey.Same(n.FullPath, Root));
    }

    [Fact]
    public async Task ParentsInAnotherCaseOrWithTheOtherSeparator_AreFound_WhereTheFileSystemIgnoresThem()
    {
        if (!OperatingSystem.IsWindows()) return;   // case and '/' are part of a name elsewhere

        var rows = Scanned(Root);
        rows[0] = Row(Root.ToUpperInvariant(), @"C:\", ".", 0, subdirs: 2);
        rows[3] = Row(Root + @"\Dokumente", Root.Replace('\\', '/'), "Dokumente", 1);

        HasTopLevel(await BuildAsync(Root, rows));
    }

    [Fact]
    public async Task TheMap_FindsTheRootByFullPath_WithOrWithoutTrailingSeparator()
    {
        var tree = await BuildAsync(Root + Sep, Scanned(Root + Sep));
        var rules = LibraryRules.Run(tree, LibraryWords.Default, new LibraryReviewOptions());
        var map = LibraryMap.Build(tree, rules, LibraryWords.Default, BundlePatterns.Default, new LibraryMatchOptions());

        Assert.Same(map.Root, map.ByFullPath[Root]);
        Assert.Same(map.Root, map.ByFullPath[Root + Sep]);
        Assert.Equal(["Dokumente", "Fotos"], map.Root.Children.Select(c => c.Name).ToList());
    }

    [Fact]
    public void PathKey_KeepsADriveOrFileSystemRoot()
    {
        var driveRoot = OperatingSystem.IsWindows() ? @"C:\" : "/";
        Assert.Equal(driveRoot, PathKey.Of(driveRoot));
        Assert.True(PathKey.Same(Root + Sep, Root));
        Assert.False(PathKey.Same(Root, Root + "2"));
    }
}
