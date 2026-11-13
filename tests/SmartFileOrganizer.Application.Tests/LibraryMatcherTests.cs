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
    /// Period and file type choose the folder but no longer decide (https://github.com/crack666/smart_file_organizer/issues/56):
    /// without the content they give <c>uncertain</c> (until then <c>likely</c>); the same files at the target still give
    /// <c>certain</c>.
    /// </summary>
    [Fact]
    public async Task SameContentAndPeriod_IsCertain_PeriodAndTypeAlone_IsUncertain()
    {
        var map = await MapAsync(Occasions());
        var unit = Loose(new DateTime(2015, 2, 7, 18, 0, 0), new DateTime(2015, 2, 8, 9, 0, 0));

        var byTime = LibraryMatcher.Match(map, unit, Options, 3, LibraryWords.Default);
        Assert.Equal("Fotos/Skifahrt 2015", byTime.Candidates[0].Target.RelativePath);
        Assert.Equal(ResultCertainty.Uncertain, byTime.Candidates[0].Certainty);
        Assert.Contains("the content was not checked (no model)", byTime.Candidates[0].Reason);

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

        var result = LibraryMatcher.Match(map, Loose(new DateTime(2020, 3, 1, 9, 0, 0), new DateTime(2020, 3, 1, 12, 0, 0)), Options, 3, LibraryWords.Default);

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

    /// <summary>Without fitting content (unclear, not checked, not described) nothing is better than uncertain.</summary>
    [Theory]
    [InlineData(ContentCheckStatus.Checked)]
    [InlineData(ContentCheckStatus.NoModel)]
    [InlineData(ContentCheckStatus.Unavailable)]
    [InlineData(ContentCheckStatus.NoDescription)]
    public async Task WithoutFittingContent_NothingIsBetterThanUncertain(ContentCheckStatus status)
    {
        var map = await MapAsync(Occasions());
        // Period and name agree as strongly as they can: the unit is named like the folder and taken on its days.
        var unit = Loose(new DateTime(2015, 2, 7, 10, 0, 0), new DateTime(2015, 2, 7, 12, 0, 0)) with { Words = ["skifahrt"] };
        var check = status == ContentCheckStatus.Checked
            ? Check(SkiTrip, "Fotos/Skifahrt 2015", ContentFit.Unclear, "too few pictures described")
            : ContentCheck.NotChecked(status, "test");

        var result = LibraryMatcher.Match(map, unit, Options, 3, LibraryWords.Default, check);

        Assert.NotEmpty(result.Candidates);
        Assert.All(result.Candidates, c => Assert.True(c.Certainty >= ResultCertainty.Uncertain, $"{c.Target.RelativePath}: {c.Certainty}"));
        Assert.Contains("the content", result.Candidates[0].Reason);
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
}
