using SmartFileOrganizer.Application.Bundles;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// The rules of the target map and the matcher (https://github.com/crack666/smart_file_organizer/issues/25), on
/// libraries built in memory with invented names.
/// </summary>
public class LibraryMatcherTests
{
    private static readonly LibraryMatchOptions Options = new();

    private static async Task<LibraryMap> MapAsync(LibraryTreeFixture fixture)
    {
        var tree = await fixture.BuildAsync();
        var rules = LibraryRules.Run(tree, LibraryWords.Default, new LibraryReviewOptions());
        return LibraryMap.Build(tree, rules, LibraryWords.Default, BundlePatterns.Default, Options);
    }

    private static LibraryTreeFixture Occasions()
    {
        var f = new LibraryTreeFixture();
        f.Files("Fotos/Skifahrt 2015", Enumerable.Range(0, 5).Select(i => $"IMG_20150207_1{i}0000.jpg"));
        f.Files("Fotos/Grillen 2016", Enumerable.Range(0, 5).Select(i => $"IMG_20160716_1{i}0000.jpg"));
        f.Files("Fotos/Ostern 2018", Enumerable.Range(0, 5).Select(i => $"IMG_20180401_1{i}0000.jpg"));
        return f;
    }

    private static long[] Types(FileType type, long count)
    {
        var t = new long[Enum.GetValues<FileType>().Length];
        t[(int)type] = count;
        return t;
    }

    private static MatchUnit Loose(DateTime from, DateTime to, int files = 4) => new()
    {
        Kind = MatchUnitKind.Folder,
        Name = "DCIM",
        Role = FolderRole.Occasion,
        Files = files,
        Types = Types(FileType.Image, files),
        From = from,
        To = to,
        TimeSource = TimeSource.FileName,
        States = new Dictionary<string, long>()
    };

    [Fact]
    public async Task Map_FindsTheHabit_OccasionsNamedWithTheirYear()
    {
        var map = await MapAsync(Occasions());

        var fotos = map.ByRelativePath["Fotos"];
        Assert.Equal(FolderRole.Collection, fotos.Role);
        Assert.Equal(FolderRole.Occasion, fotos.ChildrenRole);
        Assert.Equal("{occasion} {year}", fotos.Pattern);
        Assert.Equal(["Grillen 2016", "Ostern 2018", "Skifahrt 2015"], fotos.ChildrenPattern!.Examples);
        var ski = map.ByRelativePath["Fotos/Skifahrt 2015"];
        Assert.Equal(new DateTime(2015, 2, 7, 10, 0, 0), ski.From);
        Assert.Equal(TimeSource.FileName, ski.TimeSource);
        Assert.Equal(new DateTime(2015, 1, 1), ski.NamePeriod!.From);
    }

    /// <summary>
    /// Period and file type choose the folder but do not decide (https://github.com/crack666/smart_file_organizer/issues/56):
    /// without content that confirms it the folder is no proposal at all (Issue 85; until then it was <c>uncertain</c>), and
    /// the new folder names it as unconfirmed. The same files at the target still give <c>certain</c>.
    /// </summary>
    [Fact]
    public async Task SameContentAndPeriod_IsCertain_PeriodAndTypeAlone_IsNoExistingFolder()
    {
        var map = await MapAsync(Occasions());
        var unit = Loose(new DateTime(2015, 2, 7, 18, 0, 0), new DateTime(2015, 2, 8, 9, 0, 0));

        var byTime = LibraryMatcher.Match(map, unit, Options, 3, LibraryWords.Default);
        Assert.Empty(byTime.Candidates);
        Assert.Equal("Fotos/Skifahrt 2015", byTime.Unconfirmed![0].Target.RelativePath);
        Assert.Contains("the content was not checked (no model)", byTime.Unconfirmed[0].Reason);
        Assert.Equal("Fotos", byTime.NewFolder!.Parent.RelativePath);
        Assert.Contains("Fotos/Skifahrt 2015 fits by time, name or kind of files only, and no content confirms it", byTime.NewFolder.Reason);

        var withContent = unit with { MatchDirectories = [LibraryTreeFixture.Full("Fotos/Skifahrt 2015"), LibraryTreeFixture.Full("Fotos/Skifahrt 2015")] };
        var certain = LibraryMatcher.Match(map, withContent, Options, 3, LibraryWords.Default);
        Assert.Equal(ResultCertainty.Certain, certain.Candidates[0].Certainty);
        Assert.Contains("2 of 4 files already lie here with the same content", certain.Candidates[0].Reason);
    }

    [Fact]
    public async Task SameContentInAnOccasionOfAnotherYear_IsForAPersonToLookAt()
    {
        var map = await MapAsync(Occasions());
        var unit = Loose(new DateTime(2019, 6, 1), new DateTime(2019, 6, 2)) with
        {
            MatchDirectories = [LibraryTreeFixture.Full("Fotos/Grillen 2016"), LibraryTreeFixture.Full("Fotos/Grillen 2016")]
        };

        var result = LibraryMatcher.Match(map, unit, Options, 3, LibraryWords.Default);

        var first = result.Candidates[0];
        Assert.Equal("Fotos/Grillen 2016", first.Target.RelativePath);
        Assert.Equal(ResultCertainty.Review, first.Certainty);
        Assert.Contains("but taken", first.Reason);
    }

    [Fact]
    public async Task NothingFits_ANewFolderNamedLikeItsNeighbours_WithTheOccasionLeftOpen()
    {
        var map = await MapAsync(Occasions());

        var result = LibraryMatcher.Match(map, Loose(new DateTime(2021, 8, 14), new DateTime(2021, 8, 14, 18, 0, 0)), Options, 3, LibraryWords.Default);

        Assert.Empty(result.Candidates);
        var proposal = result.NewFolder!;
        Assert.Equal("Fotos", proposal.Parent.RelativePath);
        Assert.Equal("{occasion} 2021", proposal.Name);
        Assert.False(proposal.NameComplete);
        Assert.Equal("Fotos/{occasion} 2021", proposal.Destination);
        Assert.Equal(ResultCertainty.Uncertain, proposal.Certainty);
    }

    [Fact]
    public async Task ANamedFolder_KeepsItsName_AndTheSameFolderIsFoundByName()
    {
        var map = await MapAsync(Occasions());
        var zelten = Loose(new DateTime(2019, 8, 10), new DateTime(2019, 8, 11)) with
        {
            Name = "Zelten 2019", KeepWhole = true, Words = ["zelten"], OccasionWords = "Zelten"
        };

        var result = LibraryMatcher.Match(map, zelten, Options, 5, LibraryWords.Default);
        Assert.All(result.Candidates, c =>
        {
            Assert.Equal(MatchPlacement.AsSubfolder, c.Placement);
            Assert.EndsWith("/Zelten 2019", c.Destination);
        });
        Assert.Equal("Fotos", result.Candidates[0].Target.RelativePath);

        var ski = zelten with { Name = "skifahrt (2015)", Words = ["skifahrt"], From = new DateTime(2015, 2, 7, 12, 0, 0), To = new DateTime(2015, 2, 7, 13, 0, 0) };
        var same = LibraryMatcher.Match(map, ski, Options, 5, LibraryWords.Default);
        Assert.Equal(MatchPlacement.IntoExisting, same.Candidates[0].Placement);
        Assert.Equal("Fotos/Skifahrt 2015", same.Candidates[0].Destination);
    }

    [Fact]
    public async Task AWeakPlace_IsSaid_AndCapsTheCertainty()
    {
        var f = Occasions();
        f.Files("Fotos/Sonstiges", Enumerable.Range(0, 5).Select(i => $"IMG_20200301_1{i}0000.jpg"));
        var map = await MapAsync(f);

        // Two of the four files already lie there: the content confirms the place, the weak order still caps it.
        var unit = Loose(new DateTime(2020, 3, 1, 9, 0, 0), new DateTime(2020, 3, 1, 12, 0, 0)) with
        {
            MatchDirectories = [LibraryTreeFixture.Full("Fotos/Sonstiges"), LibraryTreeFixture.Full("Fotos/Sonstiges")]
        };
        var result = LibraryMatcher.Match(map, unit, Options, 3, LibraryWords.Default);

        var first = result.Candidates[0];
        Assert.Equal("Fotos/Sonstiges", first.Target.RelativePath);
        Assert.NotNull(first.Weak);
        Assert.Contains(first.Weak!.Findings, x => x.Kind == LibraryFindingKind.PlaceholderName);
        Assert.NotEqual(ResultCertainty.Likely, first.Certainty);
        Assert.NotEqual(ResultCertainty.Certain, first.Certainty);
    }

    [Theory]
    [InlineData("{occasion} {year}", "Ski", 2015, 2015, "Ski 2015", true)]
    [InlineData("{Anlass} {Jahr}", "Ski", 2015, 2016, "Ski 2015-2016", true)]
    [InlineData("{date} {occasion}", null, 2015, 2015, "2015-02-07 {occasion}", false)]
    [InlineData("{year}", null, 2015, 2015, "2015", true)]
    public void Fill_UsesWhatIsKnown_AndLeavesTheRestOpen(string pattern, string? occasion, int fromYear, int toYear, string expected, bool complete)
    {
        var unit = Loose(new DateTime(fromYear, 2, 7), new DateTime(toYear, 2, 8)) with { OccasionWords = occasion };

        var (name, done) = LibraryMatcher.Fill(pattern, unit);

        Assert.Equal(expected, name);
        Assert.Equal(complete, done);
    }

    [Theory]
    [InlineData("Skifahrt 2015", "skifahrt (2015)", true)]
    [InlineData("Skifahrt 2015", "Skifahrt 2016", false)]
    [InlineData("Grillen bei Chris", "Grillen_bei_Chris", true)]
    [InlineData("2015", "2015", false)]
    public void SameFolderName_IgnoresCaseAndSeparators_ButNotTheYear(string a, string b, bool same) =>
        Assert.Equal(same, LibraryMatcher.SameFolderName(a, b));

    // ------------------------------------------------------------------ content (issue 56)

    private static ContentDescription Describe(params (FileFactKey Key, string Value)[] facts) =>
        ContentDescription.From([facts.Select(f => new SmartFileOrganizer.Domain.Models.FileFact(f.Key, f.Value, FactSource.Model)).ToList()]);

    private static readonly ContentDescription SkiTrip = Describe(
        (FileFactKey.Occasion, "holiday"), (FileFactKey.Scene, "snowy slope"), (FileFactKey.Activity, "skiing"), (FileFactKey.Season, "winter"));

    private static readonly ContentDescription BirthdayParty = Describe(
        (FileFactKey.Occasion, "celebration"), (FileFactKey.Scene, "table with cake"), (FileFactKey.Activity, "singing"));

    private static ContentCheck Check(ContentDescription unit, string folder, ContentFit fit, string reason = "scripted") =>
        new(ContentCheckStatus.Checked, unit,
            new Dictionary<string, CandidateContent> { [ContentCheck.KeyOf(MatchPlacement.IntoExisting, folder)] = new(fit, reason, SkiTrip) }, "scripted");

    [Fact]
    public async Task FittingContentAndPeriod_IsCertain_AndTheReasonSaysWhatWasSeen()
    {
        var map = await MapAsync(Occasions());
        var unit = Loose(new DateTime(2015, 2, 7, 18, 0, 0), new DateTime(2015, 2, 8, 9, 0, 0));

        var result = LibraryMatcher.Match(map, unit, Options, 3, LibraryWords.Default, Check(SkiTrip, "Fotos/Skifahrt 2015", ContentFit.Fits));

        var first = result.Candidates[0];
        Assert.Equal(ResultCertainty.Certain, first.Certainty);
        Assert.StartsWith("Into Fotos/Skifahrt 2015: the content fits: snowy slope, skiing", first.Reason);
        Assert.Equal(MatchSignal.ContentFit, first.Signals[0].Signal);
    }

    /// <summary>Contradicting content drops the folder, however well the period fits; then a new folder is proposed.</summary>
    [Fact]
    public async Task ContradictingContent_DropsTheFolder_EvenWhenThePeriodFits()
    {
        var map = await MapAsync(Occasions());
        var unit = Loose(new DateTime(2015, 2, 7, 18, 0, 0), new DateTime(2015, 2, 8, 9, 0, 0));

        var result = LibraryMatcher.Match(map, unit, Options, 3, LibraryWords.Default,
            Check(BirthdayParty, "Fotos/Skifahrt 2015", ContentFit.Contradicts, "a birthday, not a ski trip"));

        Assert.Empty(result.Candidates);
        Assert.Equal("Fotos/Skifahrt 2015", Assert.Single(result.Rejected!).Target.RelativePath);
        Assert.NotNull(result.NewFolder);
        Assert.Contains("a birthday, not a ski trip", result.NewFolder!.Reason);

        // The same files already at the target win over a description: kept, but for a person to look at.
        var withFiles = unit with { MatchDirectories = [LibraryTreeFixture.Full("Fotos/Skifahrt 2015"), LibraryTreeFixture.Full("Fotos/Skifahrt 2015")] };
        var kept = LibraryMatcher.Match(map, withFiles, Options, 3, LibraryWords.Default,
            Check(BirthdayParty, "Fotos/Skifahrt 2015", ContentFit.Contradicts));
        Assert.Equal(ResultCertainty.Review, kept.Candidates[0].Certainty);
    }

    /// <summary>
    /// Done when of Issue 85: without content that fits (unclear, not checked, not described) and without the same files at
    /// the target, no existing folder is the target, however well period and names agree; a new folder is proposed and
    /// says which folder fit by time and name only. Until then such a folder was proposed as <c>uncertain</c>.
    /// </summary>
    [Theory]
    [InlineData(ContentCheckStatus.Checked)]
    [InlineData(ContentCheckStatus.NoModel)]
    [InlineData(ContentCheckStatus.Unavailable)]
    [InlineData(ContentCheckStatus.NoDescription)]
    public async Task WithoutFitsAndWithoutTheSameFiles_NoExistingFolderIsTheTarget(ContentCheckStatus status)
    {
        var map = await MapAsync(Occasions());
        // Period and name agree as strongly as they can: the unit is named like the folder and taken on its days.
        var unit = Loose(new DateTime(2015, 2, 7, 10, 0, 0), new DateTime(2015, 2, 7, 12, 0, 0)) with { Words = ["skifahrt", "berghuette"] };
        var check = status == ContentCheckStatus.Checked
            ? Check(SkiTrip, "Fotos/Skifahrt 2015", ContentFit.Unclear, "too few pictures described")
            : ContentCheck.NotChecked(status, "test");

        var result = LibraryMatcher.Match(map, unit, Options, 3, LibraryWords.Default, check);

        Assert.Empty(result.Candidates);
        Assert.Equal("Fotos/Skifahrt 2015", result.Unconfirmed![0].Target.RelativePath);
        Assert.Contains("the content", result.Unconfirmed[0].Reason);
        Assert.NotNull(result.NewFolder);
        Assert.Contains("no content confirms it", result.NewFolder!.Reason);

        // With a fitting content check the same folder is the target.
        var fits = LibraryMatcher.Match(map, unit, Options, 3, LibraryWords.Default, Check(SkiTrip, "Fotos/Skifahrt 2015", ContentFit.Fits));
        Assert.Equal("Fotos/Skifahrt 2015", fits.Candidates[0].Target.RelativePath);
    }

    // ------------------------------------------------------------------ closed units and names (issue 85)

    /// <summary>
    /// A library as the first real run met it: photos per occasion, films with posters, a series with one folder per
    /// episode, a game with its cache, a program project with pictures inside. Invented names.
    /// </summary>
    private static LibraryTreeFixture WithClosedUnits()
    {
        var f = Occasions();
        var t = new DateTime(2019, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        f.Files("Filme/Ein Sommer in Rom (2009)", ["Ein Sommer in Rom.mkv", "poster.jpg", "fanart.jpg", "folder.jpg", "Ein Sommer in Rom.srt"], time: t);
        f.Files("Filme/Die lange Reise (2011)", ["Die lange Reise.mkv", "poster.jpg", "fanart.jpg"], time: t);
        f.Files("Serien/Beispielserie/Staffel 1/Folge 01", ["Folge 01.mkv", "thumb.jpg"], time: t);
        f.Files("Serien/Beispielserie/Staffel 1/Folge 02", ["Folge 02.mkv", "thumb.jpg"], time: t);
        f.Numbered("Spiele/Beispielspiel/cache", "tex", ".jpg", 6, time: t);
        f.Files("Spiele/Beispielspiel/saves", ["slot1.sav"], time: t);
        f.Files("Projekte/Webseite", ["index.html", "site.js"], time: t);
        f.Numbered("Projekte/Webseite/src", "page", ".js", 8, time: t);
        f.Numbered("Projekte/Webseite/bilder", "header", ".png", 3, time: t);
        return f;
    }

    /// <summary>Loose holiday photos of that summer, named with one everyday word the film shares.</summary>
    private static MatchUnit SummerPhotos() =>
        Loose(new DateTime(2019, 7, 20, 10, 0, 0), new DateTime(2019, 7, 20, 18, 0, 0)) with
        {
            Words = ["sommer", "garten"], TimeSource = TimeSource.ModifiedDate
        };

    [Fact]
    public async Task Map_MarksClosedUnits_AndFilmsAreTitlesNotOccasions()
    {
        var map = await MapAsync(WithClosedUnits());

        var film = map.ByRelativePath["Filme/Ein Sommer in Rom (2009)"];
        Assert.Equal(FolderRole.MediaTitle, film.Role);
        Assert.Equal(ClosedUnitKind.SingleTitle, film.Closed);
        Assert.Equal(ClosedUnitKind.SingleTitle, map.ByRelativePath["Serien/Beispielserie/Staffel 1/Folge 01"].Closed);
        Assert.Equal(ClosedUnitKind.Technical, map.ByRelativePath["Spiele/Beispielspiel/cache"].Closed);
        var inside = map.ByRelativePath["Projekte/Webseite/bilder"];
        Assert.Equal(ClosedUnitKind.Program, inside.Closed);
        Assert.Equal("Projekte/Webseite", inside.ClosedAt);
        Assert.Null(map.ByRelativePath["Fotos/Skifahrt 2015"].Closed);
        Assert.Null(map.ByRelativePath["Filme"].Closed);
        Assert.Equal(FolderRole.MediaTitle, map.ByRelativePath["Filme"].ChildrenRole);
    }

    /// <summary>
    /// Done when of Issue 85: loose photos never get a film folder because a word of their names is in the film's name,
    /// and nothing is proposed into a folder of a closed unit (episode, cache, project), not even as a candidate the
    /// content check could confirm, nor as the parent of a new folder.
    /// </summary>
    [Fact]
    public async Task LoosePhotos_GetNoFilmFolderForACommonWord_AndNothingGoesIntoAClosedUnit()
    {
        var map = await MapAsync(WithClosedUnits());
        var closed = map.Entries.Where(e => e.Closed is not null).Select(e => e.RelativePath).ToHashSet();
        Assert.Contains("Spiele/Beispielspiel/cache", closed);

        // Even a content check that says "fits" for every closed folder changes nothing: they are never asked about.
        var fitsEverywhere = new ContentCheck(ContentCheckStatus.Checked, SkiTrip,
            closed.ToDictionary(p => ContentCheck.KeyOf(MatchPlacement.IntoExisting, p), _ => new CandidateContent(ContentFit.Fits, "scripted", SkiTrip)),
            "scripted");
        var result = LibraryMatcher.Match(map, SummerPhotos(), Options, 10, LibraryWords.Default, fitsEverywhere);

        var named = result.Candidates.Concat(result.Unconfirmed ?? []).Concat(result.Rejected ?? []).Select(c => c.Target.RelativePath).ToList();
        Assert.DoesNotContain(named, closed.Contains);
        Assert.DoesNotContain(named, p => p.StartsWith("Filme/", StringComparison.Ordinal));
        Assert.Empty(result.Candidates);
        Assert.NotNull(result.NewFolder);
        Assert.DoesNotContain(result.NewFolder!.Parent.RelativePath, closed);
        Assert.Equal("Fotos", result.NewFolder.Parent.RelativePath);
        Assert.DoesNotContain(LibraryMatcher.Preselect(map, SummerPhotos(), Options, LibraryWords.Default), c => closed.Contains(c.Target.RelativePath));
    }

    /// <summary>A kept-whole film from elsewhere may go into the film folder of the same name: the same unit, not loose files.</summary>
    [Fact]
    public async Task TheSameFilmUnderItsOwnName_IsStillACandidate()
    {
        var map = await MapAsync(WithClosedUnits());
        var types = Types(FileType.Video, 1);
        types[(int)FileType.Image] = 3;
        var film = new MatchUnit
        {
            Kind = MatchUnitKind.Folder, Name = "Ein Sommer in Rom (2009)", KeepWhole = true, Role = FolderRole.MediaTitle,
            Files = 4, Types = types, Words = ["sommer", "rom"], States = new Dictionary<string, long>()
        };

        var result = LibraryMatcher.Match(map, film, Options, 10, LibraryWords.Default);

        Assert.Contains(result.Candidates, c => c.Placement == MatchPlacement.IntoExisting && c.Destination == "Filme/Ein Sommer in Rom (2009)");
    }

    [Theory]
    [InlineData(new[] { "sommer" }, false)]                   // one everyday word
    [InlineData(new[] { "sommer", "garten" }, false)]         // an everyday word, and a word not in the name
    [InlineData(new[] { "rechnungen", "stadtwerke" }, true)]  // two words that say something
    public async Task Names_OneWordOrEverydayWordsNeverAgree(string[] unitWords, bool agrees)
    {
        var f = new LibraryTreeFixture();
        f.Files("Ablage/Sommergarten Rechnungen Stadtwerke", ["a.pdf", "b.pdf", "c.pdf"]);
        f.Files("Ablage/Anderes", ["d.pdf"]);
        var map = await MapAsync(f);
        var unit = new MatchUnit
        {
            Kind = MatchUnitKind.File, Name = "x.pdf", Role = FolderRole.DocumentType, Files = 1, Types = Types(FileType.Document, 1),
            Words = unitWords, From = new DateTime(2016, 5, 1), To = new DateTime(2016, 5, 1), TimeSource = TimeSource.ModifiedDate,
            States = new Dictionary<string, long>()
        };

        var result = LibraryMatcher.Match(map, unit, Options, 10, LibraryWords.Default);

        var candidate = result.Unconfirmed!.Concat(result.Candidates).Single(c => c.Target.RelativePath == "Ablage/Sommergarten Rechnungen Stadtwerke");
        Assert.Equal(agrees, candidate.Signals.Single(s => s.Signal == MatchSignal.Name).Agrees);
    }

    /// <summary>Photos and a library that holds only papers: no folder fits, and a new folder there would be a guess too.</summary>
    [Fact]
    public async Task ALibraryWithoutThisKindOfContent_GivesNoTarget()
    {
        var f = new LibraryTreeFixture();
        f.Files("Unterlagen/Rechnungen", ["r1.pdf", "r2.pdf", "r3.pdf"]);
        f.Files("Unterlagen/Vertraege", ["v1.pdf", "v2.pdf"]);
        var map = await MapAsync(f);

        var result = LibraryMatcher.Match(map, SummerPhotos(), Options, 3, LibraryWords.Default);

        Assert.Empty(result.Candidates);
        Assert.Null(result.NewFolder);
        Assert.Contains("holds no own photos and videos", result.NoTarget);
    }

    [Fact]
    public async Task KindOfContent_OwnPicturesAndFilmsAreToldApart()
    {
        var map = await MapAsync(WithClosedUnits());
        var photos = SummerPhotos();
        var films = photos with { Types = Types(FileType.Video, 2), TimeSource = TimeSource.ModifiedDate };

        Assert.Equal(ContentKind.OwnRecordings, LibraryMatcher.KindOf(photos));
        Assert.Equal(ContentKind.ForeignMedia, LibraryMatcher.KindOf(films));
        Assert.Equal(ContentKind.OwnRecordings, LibraryMatcher.KindOf(films with { TimeSource = TimeSource.CaptureDate }));
        // The four film and episode videos do not count as own recordings.
        var own = LibraryMatcher.KindShare(map, photos);
        var foreign = LibraryMatcher.KindShare(map, films);
        Assert.True(own > foreign, $"{own} vs {foreign}");
    }

    [Fact]
    public void Description_CountsWhatMostFilesShow()
    {
        var facts = new List<IReadOnlyList<SmartFileOrganizer.Domain.Models.FileFact>>();
        for (var i = 0; i < 3; i++)
            facts.Add([new(FileFactKey.Occasion, "holiday", FactSource.Model), new(FileFactKey.Scene, "Snowy slope", FactSource.Model)]);
        facts.Add([new(FileFactKey.Occasion, "celebration", FactSource.Model), new(FileFactKey.Scene, "snowy slope", FactSource.Model)]);
        facts.Add([new(FileFactKey.Width, "100", FactSource.Metadata)]);

        var d = ContentDescription.From(facts);

        Assert.Equal(5, d.Sampled);
        Assert.Equal(4, d.Described);
        Assert.Equal("holiday", d.Top(FileFactKey.Occasion));
        Assert.Equal(new DescriptionValue("Snowy slope", 4), d.Of(FileFactKey.Scene).Single());
        Assert.StartsWith("occasion: holiday 3, celebration 1; scene: Snowy slope 4", d.Text());
        Assert.Equal(d.Text(), ContentDescription.FromJson(d.ToJson()).Text());
    }

    [Fact]
    public void RulesJudge_SeparatesOccasionsAndIssuers()
    {
        Assert.Equal(ContentFit.Fits, DescriptionRulesFitJudge.Judge(SkiTrip, SkiTrip).Fit);
        Assert.Equal(ContentFit.Contradicts, DescriptionRulesFitJudge.Judge(BirthdayParty, SkiTrip).Fit);
        var stadtwerke = Describe((FileFactKey.DocumentKind, "invoice"), (FileFactKey.Issuer, "Stadtwerke Musterstadt"));
        var phone = Describe((FileFactKey.DocumentKind, "invoice"), (FileFactKey.Issuer, "Fonnet GmbH"));
        Assert.Equal(ContentFit.Fits, DescriptionRulesFitJudge.Judge(stadtwerke, stadtwerke).Fit);
        Assert.Equal(ContentFit.Contradicts, DescriptionRulesFitJudge.Judge(stadtwerke, phone).Fit);
        Assert.Equal(ContentFit.Contradicts, DescriptionRulesFitJudge.Judge(stadtwerke, SkiTrip).Fit);
        Assert.Equal(ContentFit.Unclear, DescriptionRulesFitJudge.Judge(ContentDescription.Empty, SkiTrip).Fit);
    }

    [Fact]
    public void ModelJudge_SeesDescriptionsOnly_AndReadsOnlyKnownIdsAndCodes()
    {
        var candidates = new[]
        {
            new FitCandidate("c1", "k1", MatchPlacement.IntoExisting, "Fotos/Skifahrt 2015", "2015-02-07..2015-02-07", SkiTrip),
            new FitCandidate("c2", "k2", MatchPlacement.IntoExisting, "Fotos/Grillen 2016", null, BirthdayParty)
        };
        var facts = ModelContentFitJudge.Facts(BirthdayParty, candidates);
        Assert.StartsWith("Files to place: occasion: celebration 1", facts);
        Assert.Contains("c1 | into_existing | Fotos/Skifahrt 2015 | 2015-02-07..2015-02-07 | occasion: holiday 1", facts);

        var (verdicts, error) = ModelContentFitJudge.Parse("""
            { "verdicts": [ { "id": "c1", "fit": "contradicts", "reason": "a party, not skiing" },
                            { "id": "c9", "fit": "fits", "reason": "unknown id" },
                            { "id": "c2", "fit": "maybe", "reason": "unknown code" } ] }
            """, new HashSet<string> { "c1", "c2" });
        Assert.Null(error);
        Assert.Equal(new FitVerdict(ContentFit.Contradicts, "a party, not skiing"), Assert.Single(verdicts).Value);
        Assert.NotNull(ModelContentFitJudge.Parse("no json", new HashSet<string>()).Error);
    }

    /// <summary>
    /// Issue 85, finding 1 of the second real run: the model called loose videos fitting to a course folder because both
    /// were "indoors, everyday life". The answer names the concrete shared feature and its kind; a fit without one, or
    /// with general features only, is unclear.
    /// </summary>
    [Fact]
    public void AFitNeedsAConcreteSharedFeature_GeneralFeaturesAreUnclear()
    {
        var (verdicts, _) = ModelContentFitJudge.Parse("""
            { "verdicts": [ { "id": "c1", "fit": "fits", "shared": "indoors, everyday life", "shared_kind": "topic", "reason": "both inside" },
                            { "id": "c2", "fit": "fits", "shared": "", "shared_kind": "none", "reason": "looks alike" },
                            { "id": "c3", "fit": "fits", "shared": "skiing on a snowy slope", "shared_kind": "topic", "reason": "both skiing" },
                            { "id": "c4", "fit": "fits", "reason": "an old answer without the new fields" } ] }
            """, new HashSet<string> { "c1", "c2", "c3", "c4" });
        Assert.Equal("skiing on a snowy slope", verdicts["c3"].Shared);
        Assert.Equal(SharedFeatureKind.Topic, verdicts["c3"].SharedKind);

        var words = LibraryWords.Default;
        var general = FitRules.Enforce(verdicts["c1"], words);
        Assert.Equal(ContentFit.Unclear, general.Fit);
        Assert.Contains("only general features", general.Reason);
        Assert.Equal(ContentFit.Unclear, FitRules.Enforce(verdicts["c2"], words).Fit);
        Assert.Equal(ContentFit.Unclear, FitRules.Enforce(verdicts["c4"], words).Fit);
        Assert.Equal(ContentFit.Fits, FitRules.Enforce(verdicts["c3"], words).Fit);
        // contradicts and unclear stay as they are.
        Assert.Equal(ContentFit.Contradicts, FitRules.Enforce(new FitVerdict(ContentFit.Contradicts, "x"), words).Fit);
    }

    /// <summary>The rules judge of sfo eval --stub follows the same rule: the same everyday occasion in the same season is no fit.</summary>
    [Fact]
    public void RulesJudge_SameEverydayOccasionAndSeason_IsNoFit()
    {
        var kitchen = Describe((FileFactKey.Occasion, "everyday"), (FileFactKey.Scene, "kitchen table"), (FileFactKey.Setting, "indoor"),
            (FileFactKey.Season, "winter"));
        var sofa = Describe((FileFactKey.Occasion, "everyday"), (FileFactKey.Scene, "sofa by the window"), (FileFactKey.Setting, "indoor"),
            (FileFactKey.Season, "winter"));
        var judged = FitRules.Enforce(DescriptionRulesFitJudge.Judge(kitchen, sofa), LibraryWords.Default);
        Assert.Equal(ContentFit.Unclear, judged.Fit);

        var ski = FitRules.Enforce(DescriptionRulesFitJudge.Judge(SkiTrip, SkiTrip), LibraryWords.Default);
        Assert.Equal(ContentFit.Fits, ski.Fit);
        Assert.NotNull(ski.Shared);
    }
}
