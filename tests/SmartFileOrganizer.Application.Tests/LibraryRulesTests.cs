using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// The structure rules of <c>sfo library review</c> (https://github.com/crack666/smart_file_organizer/issues/29)
/// on a generated tree with invented names.
/// </summary>
public class LibraryRulesTests
{
    /// <summary>
    /// A library with one finding of each kind the issue asks for, a phone dump, and a clean photo branch
    /// ("Fotos") named consistently by occasion and year.
    /// </summary>
    internal static LibraryTreeFixture SampleLibrary()
    {
        var t = new LibraryTreeFixture();

        // Clean: six occasions, all "<occasion> <year>", photos only, distinct sizes.
        string[] occasions = ["Skifahrt 2015", "Grillen bei Chris 2016", "Hochzeit Anna 2017", "Ostern 2018", "Zelten 2020", "Kindergeburtstag 2021"];
        for (var i = 0; i < occasions.Length; i++)
            t.Numbered($"Fotos/{occasions[i]}", "IMG_", ".jpg", 12, size: 2000 + i);

        // Naming: year in front, at the end, a full date, none; plus a near-equal pair.
        foreach (var name in new[] { "Urlaub Italien", "Italien Urlaub 2", "Paris 2016", "2017 London", "2018-07-01 Prag", "Wien 2019" })
            t.Numbered($"Reisen/{name}", "DSC", ".jpg", 3, size: 3000 + name.Length);

        // Same purpose at two places.
        t.Numbered("Dokumente/Rechnungen", "rechnung_", ".pdf", 5, size: 500);
        t.Numbered("Dokumente/Versicherung", "police_", ".pdf", 3, size: 600);
        t.Numbered("Dokumente/Steuer", "bescheid_", ".pdf", 2, size: 700);
        t.Numbered("Archiv/Rechnungen", "beleg_", ".pdf", 2, size: 800);

        // A catch-all that has grown, a placeholder, empty and very deep branches.
        t.Files("Archiv/Sonstiges", Enumerable.Range(1, 60).Select(i => $"datei{i}.pdf").Concat(Enumerable.Range(1, 60).Select(i => $"bild{i}.png")), size: 900);
        t.Dir("Archiv/Neuer Ordner");
        t.Dir("Archiv/Alte Sachen/leer/noch leerer");
        t.Files("Archiv/Alte Sachen", ["Thumbs.db"]);
        t.Files("Archiv/Projekt/a/b/c/d/e/f/g/h/i", ["ende.txt"]);

        // A name that promises pictures, holding documents.
        t.Numbered("Bilder", "scan_", ".pdf", 15, size: 400);
        t.Numbered("Bilder", "foto_", ".jpg", 2, size: 400);

        // Series: four divided into seasons (two spellings), one flat with many episodes.
        t.Numbered("Serien/Show A/Staffel 1", "ep", ".mkv", 3);
        t.Numbered("Serien/Show B/Staffel 1", "ep", ".mkv", 3);
        t.Numbered("Serien/Show C/S01", "ep", ".mkv", 3);
        t.Numbered("Serien/Show D/Staffel 1", "ep", ".mkv", 3);
        t.Numbered("Serien/Show E", "episode", ".mkv", 60);

        // A phone dump with many empty folders and its own depth.
        t.Numbered("Handy/DCIM/Camera", "IMG_", ".jpg", 3, size: 5000);
        t.Dir("Handy/LOST.DIR");
        foreach (var app in new[] { "com.example.chat", "com.example.maps", "org.example.notes" })
            t.Dir($"Handy/Android/data/{app}/cache/x/y/z");

        return t;
    }

    private static async Task<LibraryRuleResult> RunAsync(LibraryTreeFixture t, LibraryWords? words = null, LibraryReviewOptions? options = null)
    {
        var tree = await t.BuildAsync(words);
        return LibraryRules.Run(tree, words ?? LibraryWords.Default, options ?? new LibraryReviewOptions());
    }

    private static bool Touches(LibraryFinding f, string branch) =>
        f.RelativePath == branch || f.RelativePath.StartsWith(branch + "/", StringComparison.Ordinal)
        || f.Items.Any(i => i == branch || i.StartsWith(branch + "/", StringComparison.Ordinal));

    [Fact]
    public async Task SampleLibrary_FindsEachKindTheIssueNames_AndLeavesTheCleanBranchAlone()
    {
        var result = await RunAsync(SampleLibrary());
        var f = result.Findings;
        LibraryFinding One(LibraryFindingKind kind, string path) =>
            Assert.Single(f, x => x.Kind == kind && x.RelativePath == path);

        // 1. Inconsistent naming among siblings.
        var naming = One(LibraryFindingKind.NamingInconsistent, "Reisen");
        Assert.Contains("2017 London", naming.Items);
        Assert.Equal("year_suffix", naming.Details["majority_dated"]);

        // 2. The same purpose at several places.
        var same = Assert.Single(f, x => x.Kind == LibraryFindingKind.SameNameElsewhere);
        Assert.Equal(["Archiv/Rechnungen", "Dokumente/Rechnungen"], same.Items.Order());

        // 3. Nearly equal names.
        var similar = Assert.Single(f, x => x.Kind == LibraryFindingKind.SimilarNames);
        Assert.Equal(["Reisen/Italien Urlaub 2", "Reisen/Urlaub Italien"], similar.Items.Order());

        // 4. A catch-all that has grown, with its size; a placeholder.
        var grown = One(LibraryFindingKind.PlaceholderName, "Archiv/Sonstiges");
        Assert.Equal(FindingImpact.High, grown.Impact);
        Assert.Equal(120, grown.Files);
        Assert.Equal(120 * 900, grown.Bytes);
        One(LibraryFindingKind.PlaceholderName, "Archiv/Neuer Ordner");

        // 5. Very deep, flat and empty branches.
        var deep = One(LibraryFindingKind.VeryDeep, "Archiv/Projekt/a/b/c/d/e/f");
        Assert.Equal(11, deep.Details["max_depth"]);
        var flat = One(LibraryFindingKind.FlatAmongStructured, "Serien");
        Assert.Equal(["Show E (60 files)"], flat.Items);
        var empty = One(LibraryFindingKind.Empty, "Archiv");
        Assert.Equal(["Alte Sachen", "Neuer Ordner"], empty.Items.Order());
        Assert.Equal(1, empty.Details["only_system_files"]);

        // 6. File types that do not fit the name.
        var mismatch = One(LibraryFindingKind.TypeMismatch, "Bilder");
        Assert.Equal("image", mismatch.Details["expected"]);
        Assert.Equal("document", mismatch.Details["found"]);

        // Spellings of seasons across the series.
        var variants = One(LibraryFindingKind.VariantSpellings, "Serien");
        Assert.Equal(["Show C: s_nn"], variants.Items);

        // The clean branch has no finding at all.
        Assert.DoesNotContain(f, x => Touches(x, "Fotos"));
    }

    [Fact]
    public async Task PhoneDump_IsOneFinding_AndLeftOutOfEveryOtherRule()
    {
        var result = await RunAsync(SampleLibrary());

        var dump = Assert.Single(result.Findings, x => x.Kind == LibraryFindingKind.Dump);
        Assert.Equal("Handy", dump.RelativePath);
        Assert.Equal("device", dump.Details["dump_kind"]);
        Assert.Equal(FindingImpact.High, dump.Impact);
        Assert.True((int)dump.Details["empty_dirs"]! >= 4);   // LOST.DIR and the three app caches
        Assert.Equal(dump, result.Findings[0]);                // high impact, most folders: first

        Assert.DoesNotContain(result.Findings, x => x.Kind != LibraryFindingKind.Dump && Touches(x, "Handy"));
    }

    /// <summary>
    /// Dumps are recognised by the signatures shared with the bundles
    /// (https://github.com/crack666/smart_file_organizer/issues/39): a USB stick and a profile copy, which the
    /// library word list never knew, are dumps here too, and the reason names the signature.
    /// </summary>
    [Fact]
    public async Task Dumps_AreRecognisedByTheSharedBundleSignatures()
    {
        var t = new LibraryTreeFixture();
        t.Numbered("Fotos/Zelten 2020", "IMG_", ".jpg", 12, size: 2000);
        t.Numbered("Stick alt/System Volume Information", "x", ".dat", 1, size: 10);
        t.Numbered("Stick alt/Lieder", "song", ".mp3", 4, size: 900);
        t.Dir("Stick alt/.Trashes");
        t.Dir("Profil Laptop/AppData/Local");
        t.Numbered("Profil Laptop/Desktop", "notiz", ".txt", 2, size: 20);
        t.Numbered("Profil Laptop/Documents", "brief", ".pdf", 2, size: 700);

        var result = await RunAsync(t);

        var dumps = result.Findings.Where(x => x.Kind == LibraryFindingKind.Dump).ToDictionary(x => x.RelativePath);
        Assert.Equal("device", dumps["Stick alt"].Details["dump_kind"]);
        Assert.Equal("computer", dumps["Profil Laptop"].Details["dump_kind"]);
        Assert.Contains("signature 'user-profile'", dumps["Profil Laptop"].Details["evidence"]?.ToString());
        Assert.DoesNotContain(result.Findings, x => Touches(x, "Fotos"));
    }

    [Fact]
    public async Task SiblingRules_NeedFiveSiblings()
    {
        var t = new LibraryTreeFixture();
        foreach (var name in new[] { "2015 Skifahrt", "Grillen 2016", "ostern" })
            t.Numbered($"Fotos/{name}", "IMG_", ".jpg", 2);

        var result = await RunAsync(t);

        Assert.DoesNotContain(result.Findings, x => x.Kind is LibraryFindingKind.NamingInconsistent or LibraryFindingKind.CaseInconsistent);
    }

    [Fact]
    public async Task LikelyCopies_SameCountAndBytes_IgnoringPreviewFiles()
    {
        var t = new LibraryTreeFixture();
        t.Numbered("Fotos/Hochzeit Anna 2017", "IMG_", ".jpg", 4, size: 400_000);
        t.Numbered("Sicherung/Hochzeit", "IMG_", ".jpg", 4, size: 400_000);
        t.Files("Sicherung/Hochzeit", ["Thumbs.db"], size: 9999);   // the +1 file a viewer leaves
        t.Numbered("Fotos/Ostern 2018", "IMG_", ".jpg", 4, size: 300_000);

        var result = await RunAsync(t);

        var copy = Assert.Single(result.Findings, x => x.Kind == LibraryFindingKind.LikelyCopy);
        Assert.Equal(["Fotos/Hochzeit Anna 2017", "Sicherung/Hochzeit"], copy.Items.Order());
        Assert.Equal(1_600_000, copy.Bytes);
    }

    [Fact]
    public async Task CameraNames_WebPages_LooseFiles_AndCase_AreBundledPerParent()
    {
        var t = new LibraryTreeFixture();
        t.Numbered("Fotos/100CANON", "IMG_", ".jpg", 2);
        t.Numbered("Fotos/101CANON", "IMG_", ".jpg", 2);
        foreach (var name in new[] { "Ostern 2018", "Zelten 2020", "Taufe 2019", "Hochzeit 2017", "zoo 2021" })
            t.Numbered($"Fotos/{name}", "IMG_", ".jpg", 2);
        t.Files(".", ["notiz.txt", "Rezept.html"]);
        t.Numbered("Rezept_files", "img", ".png", 3);
        t.Dir("Musik/A");
        t.Dir("Musik/B");
        t.Numbered("Musik/A", "song", ".mp3", 2);
        t.Numbered("Musik/B", "song", ".mp3", 2);

        var f = (await RunAsync(t)).Findings;

        var camera = Assert.Single(f, x => x.Kind == LibraryFindingKind.CameraFolderName);
        Assert.Equal(["100CANON", "101CANON"], camera.Items);
        var web = Assert.Single(f, x => x.Kind == LibraryFindingKind.SavedWebPage);
        Assert.Equal(".", web.RelativePath);
        var loose = Assert.Single(f, x => x.Kind == LibraryFindingKind.LooseFiles);
        Assert.Equal(2, loose.Files);
        var lettercase = Assert.Single(f, x => x.Kind == LibraryFindingKind.CaseInconsistent);
        Assert.Equal(["zoo 2021"], lettercase.Items);
    }

    [Fact]
    public async Task DocumentsInPhotoArea_AreOneFindingPerArea()
    {
        var t = new LibraryTreeFixture();
        for (var i = 0; i < 6; i++)
            t.Numbered($"Fotos/Anlass {i} 2015", "IMG_", ".jpg", 25, size: 1000 + i);
        t.Numbered("Fotos/Anlass 9 2016/Belege", "beleg", ".pdf", 25);
        t.Numbered("Fotos/Kram", "brief", ".docx", 22);
        // Phone clips of an occasion are fine next to its photos.
        t.Numbered("Fotos/Anlass 0 2015", "VID_", ".mp4", 30);

        var f = (await RunAsync(t)).Findings;

        var area = Assert.Single(f, x => x.Kind == LibraryFindingKind.TypeMismatch);
        Assert.Equal("Fotos", area.RelativePath);
        Assert.Equal("area", area.Details["basis"]);
        Assert.Equal(2, area.Items.Count);
        Assert.Equal(47, area.Files);
    }

    [Fact]
    public async Task SensitiveFiles_AreReportedWithPathAndMarkerOnly()
    {
        var t = new LibraryTreeFixture();
        t.Files("Dokumente", ["tresor.kdbx"], markers: SensitiveMarker.Credentials);
        t.Files("Dokumente", ["2fa recovery codes.txt", "einkauf.txt"]);

        var f = (await RunAsync(t)).Findings;

        var s = Assert.Single(f, x => x.Kind == LibraryFindingKind.SensitiveLocation);
        Assert.Equal(["Dokumente/2fa recovery codes.txt", "Dokumente/tresor.kdbx"], s.Items.Order());
        Assert.Equal(["credentials"], (IEnumerable<string>)s.Details["markers"]!);
    }

    [Fact]
    public async Task WordsFile_AddsNames_AndExceptionsSilenceFindings()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sfo_words_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "words.json");
            File.WriteAllText(file, """
                {
                  // my own catch-all, and one folder kept as it is on purpose
                  "catch_all_names": ["Kiste"],
                  "exceptions": [ { "path": "Bilder", "kinds": ["type_mismatch"], "why": "scanned letters of the trip" } ]
                }
                """);
            var words = LibraryWords.LoadWithDefaults(file);
            var t = new LibraryTreeFixture();
            t.Numbered("Kiste", "x", ".txt", 3);
            t.Numbered("Bilder", "scan_", ".pdf", 15);

            var result = await RunAsync(t, words);

            Assert.Contains(result.Findings, x => x.Kind == LibraryFindingKind.PlaceholderName && x.RelativePath == "Kiste");
            Assert.DoesNotContain(result.Findings, x => x.Kind == LibraryFindingKind.TypeMismatch);
            Assert.Equal(1, result.ExcludedByExceptions);
            Assert.Contains("words.json", words.Source);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WordsFile_WithUnknownFieldOrKind_IsRejectedWithEveryProblem()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sfo_words_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "words.json");
            File.WriteAllText(file, """{ "exceptions": [ { "path": "x", "kinds": ["nonsense"] } ], "type_words": { "pictures": ["x"] } }""");
            var ex = Assert.Throws<LibraryWordsException>(() => LibraryWords.LoadWithDefaults(file));
            Assert.Equal(2, ex.Problems.Count);

            File.WriteAllText(file, """{ "catch_all": ["x"] }""");
            Assert.Throws<LibraryWordsException>(() => LibraryWords.LoadWithDefaults(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Skifahrt 2015", NamePattern.YearSuffix)]
    [InlineData("Skifahrt (2015)", NamePattern.YearSuffix)]
    [InlineData("2015 Skifahrt", NamePattern.YearPrefix)]
    [InlineData("2015-02-14 Skifahrt", NamePattern.DateYmdPrefix)]
    [InlineData("20150214 Skifahrt", NamePattern.DateCompactPrefix)]
    [InlineData("14.02.2015 Skifahrt", NamePattern.DateDmyPrefix)]
    [InlineData("2015-02 Skifahrt", NamePattern.YearMonthPrefix)]
    [InlineData("Skifahrt 14.02.2015", NamePattern.DateSuffix)]
    [InlineData("Skifahrt 2015 Teil 2", NamePattern.YearInside)]
    [InlineData("2015", NamePattern.YearOnly)]
    [InlineData("Skifahrt", NamePattern.NoDate)]
    [InlineData("Max 30", NamePattern.NoDate)]
    [InlineData("100CANON", NamePattern.Camera)]
    [InlineData("0042", NamePattern.Other)]
    public void Classify_NamesTheDatePattern(string name, string pattern) =>
        Assert.Equal(pattern, LibraryText.Classify(name));

    [Theory]
    [InlineData("asdf", true)]
    [InlineData("jjj", true)]
    [InlineData("qwe", true)]
    [InlineData("neu", false)]
    [InlineData("Opa", false)]
    [InlineData("asdfg", false)]
    public void KeyboardMash(string name, bool expected) => Assert.Equal(expected, LibraryText.IsKeyboardMash(name));
}
