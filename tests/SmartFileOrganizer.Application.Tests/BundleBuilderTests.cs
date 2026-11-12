using SmartFileOrganizer.Application.Bundles;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// The bundle builder on generated trees held in memory (https://github.com/crack666/smart_file_organizer/issues/39):
/// dumps and their parts, named folders, events from loose photos, and the statistics with and without dumps.
/// No file is touched; every name is invented.
/// </summary>
public class BundleBuilderTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "sfo-bundles-root");

    /// <summary>A scanned tree: directories and files with modification times (UTC), as the scan stores them.</summary>
    private sealed class FakeTree
    {
        private readonly Dictionary<string, DirectoryReadRow> _dirs = new(StringComparer.Ordinal);
        public List<FileNode> Files { get; } = [];
        public Dictionary<long, CaptureInfo> Captures { get; } = new();
        private long _nextId = 1;

        public FakeTree() => Dir(".");

        public IReadOnlyList<DirectoryReadRow> Directories => _dirs.Values.ToList();

        public DirectoryReadRow Dir(string relative, DirectoryStatus status = DirectoryStatus.Scanned)
        {
            var full = relative == "." ? Root : Path.Combine(Root, Normalise(relative));
            if (_dirs.TryGetValue(full, out var existing)) return existing;
            var parent = full == Root ? Root : Path.GetDirectoryName(full)!;
            if (full != Root) Dir(Path.GetRelativePath(Root, parent));
            var row = new DirectoryReadRow
            {
                Id = _nextId++,
                FullPath = full,
                Name = full == Root ? "sfo-bundles-root" : Path.GetFileName(full),
                ParentPath = parent,
                RelativePath = relative,
                Depth = full == Root ? 0 : Normalise(relative).Split(Path.DirectorySeparatorChar).Length,
                DirStatus = status
            };
            _dirs[full] = row;
            return row;
        }

        public FileNode File(string relative, DateTime? modifiedUtc = null, long size = 1000)
        {
            var full = Path.Combine(Root, Normalise(relative));
            var parent = Path.GetDirectoryName(full)!;
            Dir(parent == Root ? "." : Path.GetRelativePath(Root, parent));
            var node = new FileNode
            {
                Id = _nextId++,
                FullPath = full,
                Name = Path.GetFileName(full),
                ParentPath = parent,
                RootPath = Root,
                Size = size,
                Extension = Path.GetExtension(full).ToLowerInvariant(),
                FileType = Path.GetExtension(full).ToLowerInvariant() switch
                {
                    ".jpg" or ".png" or ".heic" => FileType.Image,
                    ".mp4" => FileType.Video,
                    ".pdf" or ".txt" or ".docx" => FileType.Document,
                    ".exe" => FileType.Executable,
                    ".dll" => FileType.Executable,
                    _ => FileType.Other
                },
                LastWriteTime = modifiedUtc ?? new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc)
            };
            Files.Add(node);
            return node;
        }

        private static string Normalise(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);
    }

    private static BundleBuildResult Build(FakeTree tree, BundleOptions? options = null) =>
        new BundleBuilder(BundlePatterns.Default, options ?? new BundleOptions())
            .Build(Root, tree.Directories, tree.Files, tree.Captures);

    private static IEnumerable<Bundle> Flatten(IEnumerable<Bundle> bundles) =>
        bundles.SelectMany(b => new[] { b }.Concat(Flatten(b.Children)));

    private static HashSet<long> FilesOf(Bundle b) => Flatten([b]).SelectMany(x => x.FileIds).ToHashSet();

    /// <summary>A phone storage copy: camera roll, screenshots, chat media, app data, an empty LOST.DIR.</summary>
    private static (FakeTree Tree, List<FileNode> Camera) PhoneDump(string at)
    {
        var tree = new FakeTree();
        var camera = new List<FileNode>();
        var day = new DateTime(2014, 5, 3, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 12; i++)
            camera.Add(tree.File($"{at}/DCIM/Camera/IMG_201405{(i < 6 ? "03" : "20")}_1{i % 6}0000.jpg", day));
        tree.File($"{at}/DCIM/.thumbnails/123456.jpg");
        tree.File($"{at}/DCIM/Screenshots/Screenshot_2014-05-04-10-00-00.png");
        tree.File($"{at}/WhatsApp/Media/WhatsApp Images/IMG-20140510-WA0001.jpg");
        tree.File($"{at}/WhatsApp/Databases/msgstore.db.crypt12");
        tree.File($"{at}/Android/data/com.example.app/files/Pictures/cached.jpg");
        tree.File($"{at}/Android/data/com.example.app/cache/blob.bin");
        tree.Dir($"{at}/LOST.DIR");
        tree.Dir($"{at}/Alarms");
        tree.File($"{at}/Download/manual.pdf");
        return (tree, camera);
    }

    [Fact]
    public void PhoneDump_IsOneBundle_AndItsCameraFolderIsKept()
    {
        var (tree, camera) = PhoneDump("Altes Handy");
        tree.File("Unsortiert/IMG_20190101_100000.jpg");

        var result = Build(tree);

        var dump = Assert.Single(result.Roots, b => b.Kind == BundleKind.Dump);
        Assert.Equal(DumpOrigin.Phone, dump.Origin);
        Assert.Equal(Path.Combine(Root, "Altes Handy"), dump.RootPath);
        Assert.Contains("dir:DCIM", dump.Facts.Markers);
        Assert.Contains("dir:LOST.DIR", dump.Facts.Markers);
        Assert.Equal(tree.Files.Count(f => f.FullPath.Contains("Altes Handy")), dump.FileCount);

        var keep = dump.Children.Where(c => c.Kind == BundleKind.DumpKeep).ToDictionary(c => c.Part!.Value);
        var cameraPart = keep[DumpPart.Camera];
        Assert.Equal(camera.Select(f => f.Id).ToHashSet(), FilesOf(cameraPart));
        // The camera roll is shown by occasion below it: two days, two events.
        Assert.Equal(2, cameraPart.Children.Count(c => c.Kind == BundleKind.Event));

        Assert.Single(FilesOf(keep[DumpPart.Screenshots]));
        Assert.Single(FilesOf(keep[DumpPart.ChatMedia]));
        Assert.Single(FilesOf(keep[DumpPart.Downloads]));

        // App data, thumbnails and the chat database are noise, although "Pictures" appears below Android/data.
        var noise = Assert.Single(dump.Children, c => c.Kind == BundleKind.DumpNoise);
        var noiseNames = FilesOf(noise).Select(id => tree.Files.Single(f => f.Id == id).Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "123456.jpg", "msgstore.db.crypt12", "cached.jpg", "blob.bin" }, noiseNames);
        Assert.True(noise.EmptyDirectoryCount >= 2, "LOST.DIR and Alarms are empty noise folders");
        Assert.False(keep.ContainsKey(DumpPart.Pictures));

        // Nothing of the dump is loose: the only event outside it is the one loose photo's scattered group.
        Assert.DoesNotContain(result.Roots, b => b.Kind == BundleKind.Event);
    }

    [Fact]
    public void PhoneDump_NestedInFoldersWithNothingElse_StartsAtTheOutermost()
    {
        var (tree, _) = PhoneDump("Handy 2014/Interner Speicher");

        var dump = Assert.Single(Build(tree).Roots, b => b.Kind == BundleKind.Dump);

        Assert.Equal(Path.Combine(Root, "Handy 2014"), dump.RootPath);
        Assert.StartsWith("Handy 2014 (phone 2014", dump.SuggestedName);
    }

    [Fact]
    public void ThreeHundredLoosePhotos_FromThreePeriods_FormThreeBundles()
    {
        var tree = new FakeTree();
        DateTime[] starts =
        [
            new(2015, 2, 14, 9, 0, 0, DateTimeKind.Utc),
            new(2016, 7, 30, 15, 0, 0, DateTimeKind.Utc),
            new(2018, 12, 24, 17, 0, 0, DateTimeKind.Utc)
        ];
        for (var period = 0; period < 3; period++)
        {
            for (var i = 0; i < 100; i++)
                tree.File($"Neuer Ordner/P{period}_{i:D3}.jpg", starts[period].AddMinutes(i * 3));
        }

        var result = Build(tree);

        var events = result.Roots.Where(b => b.Kind == BundleKind.Event).ToList();
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(100, e.FileCount));
        Assert.All(events, e => Assert.Equal(TimeSource.ModifiedDate, e.TimeSource));
        Assert.Equal(starts.Select(s => s.ToLocalTime().Year), events.Select(e => e.FirstTime!.Value.Year));
        Assert.DoesNotContain(result.Roots, b => b.Kind == BundleKind.Scattered);
        Assert.Equal(300, events.SelectMany(e => e.FileIds).Distinct().Count());
    }

    [Fact]
    public void LoosePhotos_PreferCaptureDateAndNameOverModificationTime()
    {
        var tree = new FakeTree();
        var copied = new DateTime(2023, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        // Copied the same second, but taken on two different days: the capture date decides.
        for (var i = 0; i < 4; i++)
        {
            var f = tree.File($"Import/a{i}.jpg", copied);
            tree.Captures[f.Id] = new CaptureInfo(new DateTime(2012, 6, 1, 10, i, 0), "Cam A");
        }
        for (var i = 0; i < 4; i++)
        {
            var f = tree.File($"Import/b{i}.jpg", copied);
            tree.Captures[f.Id] = new CaptureInfo(new DateTime(2012, 9, 9, 10, i, 0), "Cam A");
        }
        // Names with dates in two different folders still meet when the times agree.
        tree.File("Neu/IMG_20130505_120000.jpg", copied);
        tree.File("Neu/IMG_20130505_121000.jpg", copied);
        tree.File("Neu (2)/IMG_20130505_123000.jpg", copied);

        var events = Build(tree).Roots.Where(b => b.Kind == BundleKind.Event).OrderBy(e => e.FirstTime).ToList();

        Assert.Equal(3, events.Count);
        Assert.Equal(TimeSource.CaptureDate, events[0].TimeSource);
        Assert.Equal("Cam A", events[0].Facts.Device);
        Assert.Equal("2012-06-01", events[0].SuggestedName);
        Assert.Equal(TimeSource.FileName, events[2].TimeSource);
        Assert.Equal(3, events[2].FileCount);
        Assert.Equal(2, events[2].Facts.DirectoriesTotal);
    }

    [Fact]
    public void DifferentDevicesAndScreenshots_AreNeverOneEvent()
    {
        var tree = new FakeTree();
        var at = new DateTime(2017, 4, 1, 12, 0, 0);
        for (var i = 0; i < 3; i++)
        {
            tree.Captures[tree.File($"Bilder/x{i}.jpg").Id] = new CaptureInfo(at.AddMinutes(i), "Phone X");
            tree.Captures[tree.File($"Bilder/y{i}.jpg").Id] = new CaptureInfo(at.AddMinutes(i), "Camera Y");
            tree.Captures[tree.File($"Bilder/s{i}.png").Id] = new CaptureInfo(at.AddMinutes(i), null, ImageOrigin.Screenshot);
        }

        var events = Build(tree).Roots.Where(b => b.Kind == BundleKind.Event).ToList();

        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(3, e.FileCount));
    }

    [Fact]
    public void ModificationTimes_AreNotGroupedAcrossFolders_AndACopyIsFlagged()
    {
        var tree = new FakeTree();
        var copy = new DateTime(2021, 3, 3, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 25; i++)
        {
            tree.File($"Neuer Ordner/a{i}.jpg", copy);
            tree.File($"Neuer Ordner (2)/b{i}.jpg", copy);
        }

        var events = Build(tree).Roots.Where(b => b.Kind == BundleKind.Event).ToList();

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.True(e.Facts.CopyTimeSuspected));
        Assert.All(events, e => Assert.Contains("time of a copy", e.Reason));
    }

    [Fact]
    public void FewPhotos_GoToTheYearsScatteredBundle()
    {
        var tree = new FakeTree();
        tree.File("IMG_20150101_100000.jpg");
        tree.File("IMG_20150601_100000.jpg");
        tree.File("IMG_20160601_100000.jpg");

        var roots = Build(tree).Roots;

        Assert.DoesNotContain(roots, b => b.Kind == BundleKind.Event);
        var scattered = roots.Where(b => b.Kind == BundleKind.Scattered).ToList();
        Assert.Equal(["2015 (scattered)", "2016 (scattered)"], scattered.Select(s => s.SuggestedName));
    }

    [Fact]
    public void ADailyCameraRoll_IsCutIntoPartsOfAtMostMaxEventDays()
    {
        var tree = new FakeTree();
        var start = new DateTime(2019, 1, 1);
        for (var day = 0; day < 60; day++)
        {
            // Photos every few hours around the clock: never a pause of 8 hours.
            for (var h = 0; h < 24; h += 6)
                tree.Captures[tree.File($"Neu/d{day}_{h}.jpg").Id] = new CaptureInfo(start.AddDays(day).AddHours(h + (day % 3)), null);
        }

        var events = Build(tree).Roots.Where(b => b.Kind == BundleKind.Event).ToList();

        Assert.True(events.Count >= 5, $"{events.Count} events");
        Assert.All(events, e => Assert.True(e.LastTime - e.FirstTime <= TimeSpan.FromDays(14)));
        Assert.Equal(240, events.Sum(e => e.FileCount));
    }

    [Fact]
    public void NamedFolder_IsOneBundle_NeverSplitOrRenamed()
    {
        var tree = new FakeTree();
        tree.File("Fotos/Skifahrt 2015/Tag 1/IMG_20150201_100000.jpg");
        tree.File("Fotos/Skifahrt 2015/Tag 2/IMG_20150202_100000.jpg");
        tree.File("Fotos/Skifahrt 2015/IMG_20150210_100000.jpg");
        tree.File("Fotos/Skifahrt 2015/Notizen.txt");

        var roots = Build(tree).Roots;

        var folder = Assert.Single(roots);
        Assert.Equal(BundleKind.Folder, folder.Kind);
        Assert.Equal("Skifahrt 2015", folder.SuggestedName);
        Assert.Equal(4, folder.FileCount);
        Assert.Equal(3, folder.DirectoryCount);
    }

    [Fact]
    public void FolderOfOccasions_GivesOneBundlePerNamedSubfolder()
    {
        var tree = new FakeTree();
        tree.File("Familie/Weihnachten 2015/a.jpg");
        tree.File("Familie/Ostern 2016/b.jpg");
        tree.File("Familie/Stammbaum.pdf");
        tree.File("Familie/2016-08-01/IMG_20160801_100000.jpg");

        var roots = Build(tree).Roots;

        var folders = roots.Where(b => b.Kind == BundleKind.Folder).OrderBy(b => b.SuggestedName).ToList();
        Assert.Equal(["Familie", "Ostern 2016", "Weihnachten 2015"], folders.Select(f => f.SuggestedName));
        Assert.True(folders[0].Facts.DirectFilesOnly);
        Assert.Equal(1, folders[0].FileCount);
        // A date folder is no name: its photo is loose.
        Assert.Contains(roots, b => b.Kind == BundleKind.Scattered && b.FileCount == 1);
    }

    [Fact]
    public void SoftwareAndStickCopies_AreWholeDumps_ButTheScanRootIsNeverAStick()
    {
        var tree = new FakeTree();
        tree.Dir("System Volume Information");
        tree.File("autorun.inf");
        tree.File("Projekt/.git/HEAD");
        tree.File("Projekt/package.json");
        tree.File("Projekt/src/index.js");
        tree.File("Stick 2009/autorun.inf");
        tree.Dir("Stick 2009/System Volume Information");
        tree.File("Stick 2009/Bewerbung.docx");
        tree.File("Spiele/Game/game.exe");
        tree.File("Spiele/Game/engine.dll");

        var dumps = Build(tree).Roots.Where(b => b.Kind == BundleKind.Dump).ToDictionary(b => Path.GetFileName(b.RootPath!)!);

        Assert.Equal(DumpOrigin.Software, dumps["Projekt"].Origin);
        Assert.Equal(3, dumps["Projekt"].FileCount);
        Assert.Empty(dumps["Projekt"].Children);
        Assert.Equal(DumpOrigin.RemovableDrive, dumps["Stick 2009"].Origin);
        // "Spiele" holds nothing but the game, so the dump starts there.
        Assert.Equal(DumpOrigin.Software, dumps["Spiele"].Origin);
        Assert.DoesNotContain(Path.GetFileName(Root), dumps.Keys);
    }

    [Fact]
    public void ProgramsNextToManyPhotos_AreNoSoftwareDump()
    {
        var tree = new FakeTree();
        tree.File("Urlaub/setup.exe");
        tree.File("Urlaub/helper.dll");
        for (var i = 0; i < 10; i++)
            tree.File($"Urlaub/IMG_20140801_10{i}000.jpg");

        var roots = Build(tree).Roots;

        Assert.DoesNotContain(roots, b => b.Kind == BundleKind.Dump);
        Assert.Equal(12, Assert.Single(roots, b => b.Kind == BundleKind.Folder).FileCount);
    }

    [Fact]
    public void UserProfile_IsAComputerDump_ButALibraryWithDocumentsAndPicturesIsNot()
    {
        var profile = new FakeTree();
        profile.Dir("Backup PC/Max/AppData/Roaming", DirectoryStatus.Skip);
        profile.File("Backup PC/Max/NTUSER.DAT");
        profile.File("Backup PC/Max/Documents/Steuer 2010.pdf");
        profile.File("Backup PC/Max/Desktop/link.lnk");
        profile.File("Backup PC/Max/Pictures/Urlaub/IMG_20100707_100000.jpg");

        var dump = Assert.Single(Build(profile).Roots, b => b.Kind == BundleKind.Dump);
        Assert.Equal(DumpOrigin.Computer, dump.Origin);
        var parts = dump.Children.Where(c => c.Kind == BundleKind.DumpKeep).Select(c => c.Part!.Value).ToHashSet();
        Assert.Equal(new HashSet<DumpPart> { DumpPart.Documents, DumpPart.Desktop, DumpPart.Pictures }, parts);

        var library = new FakeTree();
        library.File("Documents/Steuer 2010.pdf");
        library.File("Pictures/Urlaub/IMG_20100707_100000.jpg");
        library.File("Music/a.mp3");
        library.File("Videos/b.mp4");
        library.File("Downloads/c.pdf");
        Assert.DoesNotContain(Build(library).Roots, b => b.Kind == BundleKind.Dump);
    }

    [Fact]
    public void Statistics_ShowTheJobWithAndWithoutDumps()
    {
        var (tree, _) = PhoneDump("Handy");
        tree.File("Briefe/Kuendigung.pdf", size: 500);
        tree.Dir("Leer");

        var stats = Build(tree).Stats;

        Assert.Equal(1, stats.DumpCount);
        Assert.Equal(stats.All.Files, stats.Dumps.Files + stats.WithoutDumps.Files);
        Assert.Equal(stats.All.Directories, stats.Dumps.Directories + stats.WithoutDumps.Directories);
        Assert.Equal(1, stats.WithoutDumps.Files);
        Assert.Equal(500, stats.WithoutDumps.TotalBytes);
        Assert.Equal(1, stats.WithoutDumps.EmptyDirectories);
        Assert.True(stats.Dumps.EmptyDirectories >= 2);
        Assert.Equal(new Dictionary<string, long> { ["document"] = 1 }, stats.WithoutDumps.ByFileType);
    }

    [Theory]
    [InlineData("IMG_20150712_143000.jpg", "2015-07-12 14:30:00")]
    [InlineData("20150712_143005.mp4", "2015-07-12 14:30:05")]
    [InlineData("PXL_20210304_101500123.jpg", "2021-03-04 10:15:00")]
    [InlineData("IMG-20150712-WA0001.jpg", "2015-07-12 12:00:00")]
    [InlineData("Screenshot_2015-07-12-14-30-00.png", "2015-07-12 14:30:00")]
    [InlineData("WhatsApp Image 2015-07-12 at 14.30.00.jpeg", "2015-07-12 14:30:00")]
    [InlineData("DSC_0042.JPG", null)]
    [InlineData("IMG_1234.jpg", null)]
    [InlineData("Rechnung 2015.pdf", null)]
    [InlineData("IMG_20151345_100000.jpg", null)]
    public void FileNameDates_ReadWhatCamerasAndMessengersWrite(string name, string? expected)
    {
        var parsed = FileNameDates.TryParse(name, new DateTime(2026, 1, 1));
        Assert.Equal(expected, parsed?.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void PatternFile_WithMistakes_ReportsEveryProblem()
    {
        const string json = """
            {
              "schema_version": 1,
              "signatures": [ { "origin": "toaster", "min_score": 0, "markers": [ { "dir": "x", "file": "y" } ] } ],
              "keep_parts": [ { "part": "selfies", "paths": [] } ]
            }
            """;

        var ex = Assert.Throws<BundlePatternsException>(() => BundlePatterns.Parse(json, "test"));

        Assert.Contains("origin 'toaster'", ex.Message);
        Assert.Contains("min_score", ex.Message);
        Assert.Contains("exactly one of dir", ex.Message);
        Assert.Contains("\"key\": true", ex.Message);
        Assert.Contains("part 'selfies'", ex.Message);
    }

    [Fact]
    public void ShippedPatterns_Load()
    {
        var patterns = BundlePatterns.Default;
        Assert.Equal("default", patterns.Name);
        Assert.True(patterns.IsGenericName("Neuer Ordner (3)"));
        Assert.True(patterns.IsGenericName("Bilder - Kopie (2)"));
        Assert.True(patterns.IsGenericName("100CANON"));
        Assert.True(patterns.IsGenericName("2015-07"));
        Assert.False(patterns.IsGenericName("Skifahrt 2015"));
        Assert.False(patterns.IsGenericName("Oma"));
    }
}
