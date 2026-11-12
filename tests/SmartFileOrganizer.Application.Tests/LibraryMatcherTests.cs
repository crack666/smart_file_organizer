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

    [Fact]
    public async Task SameContentAndPeriod_IsCertain_PeriodAndTypeAlone_IsLikely()
    {
        var map = await MapAsync(Occasions());
        var unit = Loose(new DateTime(2015, 2, 7, 18, 0, 0), new DateTime(2015, 2, 8, 9, 0, 0));

        var likely = LibraryMatcher.Match(map, unit, Options, 3, LibraryWords.Default);
        Assert.Equal("Fotos/Skifahrt 2015", likely.Candidates[0].Target.RelativePath);
        Assert.Equal(ResultCertainty.Likely, likely.Candidates[0].Certainty);

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
}
