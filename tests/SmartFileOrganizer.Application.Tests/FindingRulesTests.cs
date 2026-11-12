using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>The pure rules of the findings (https://github.com/crack666/smart_file_organizer/issues/26).</summary>
public class FindingRulesTests
{
    private static FileNode File(string name, FileType type, FileClassification? c = null) => new()
    {
        Id = 1, Name = name, Extension = Path.GetExtension(name), FileType = type, Classification = c
    };

    private static readonly FilePlace Plain = new(null, null, null, null, false, false);
    private static readonly FilePlace Noise = new(null, null, null, null, true, false);

    [Fact]
    public void Key_IsStable_AndDependsOnKindAndSubject()
    {
        Assert.Equal(FindingRules.Key(FindingKind.InNoise, "dir|Downloads|noise"), FindingRules.Key(FindingKind.InNoise, "dir|Downloads|noise"));
        Assert.NotEqual(FindingRules.Key(FindingKind.InNoise, "a"), FindingRules.Key(FindingKind.NotBackedUp, "a"));
        Assert.Matches("^in_noise-[0-9a-f]{10}$", FindingRules.Key(FindingKind.InNoise, "a"));
    }

    [Theory]
    [InlineData(ResultCertainty.Certain, TargetContentCheck.NotChecked, ResultCertainty.Uncertain)]
    [InlineData(ResultCertainty.Certain, TargetContentCheck.Fits, ResultCertainty.Likely)]
    [InlineData(ResultCertainty.Review, TargetContentCheck.Fits, ResultCertainty.Review)]
    public void Misplaced_StaysUncertain_UntilTheTargetContentFits(ResultCertainty files, TargetContentCheck check, ResultCertainty expected)
    {
        var target = new FindingTarget("/lib/x", "x", ResultCertainty.Likely, "period", 0, check);
        Assert.Equal(expected, FindingRules.MisplacedCertainty(files, target));
        Assert.Equal(ResultCertainty.Uncertain, FindingRules.MisplacedCertainty(ResultCertainty.Certain, null));
    }

    [Fact]
    public void Value_HumanOverride_IsCertain()
    {
        var f = File("a.pdf", FileType.Document);
        f.Override = new UserOverride { OverriddenCategory = FileCategory.Invoice };
        Assert.Equal(new FileValue(FileWorth.Keep, ResultCertainty.Certain, "human"), FindingRules.ValueOf(f, Plain));
    }

    [Fact]
    public void Value_ReviewResult_CountsNeitherAmongNoiseNorForOtherTypes()
    {
        var review = new FileClassification { Category = FileCategory.ReviewNeeded, Certainty = ResultCertainty.Review };
        Assert.Equal(FileWorth.Keep, FindingRules.ValueOf(File("scan.pdf", FileType.Document, review), Plain).Worth);
        Assert.Equal(FileWorth.None, FindingRules.ValueOf(File("scan.pdf", FileType.Document, review), Noise).Worth);
        Assert.Equal(FileWorth.None, FindingRules.ValueOf(File("cache.db", FileType.Database, review), Plain).Worth);
    }

    [Fact]
    public void Value_AmongNoise_WithoutResult_OnlyCameraPhotosAndPersonalDocuments()
    {
        Assert.Equal(new FileValue(FileWorth.Keep, ResultCertainty.Uncertain, "name"),
            FindingRules.ValueOf(File("IMG_20190704_120000.jpg", FileType.Image), Noise));
        Assert.Equal(FileWorth.Keep, FindingRules.ValueOf(File("Bewerbung.docx", FileType.Document), Noise).Worth);
        Assert.Equal(FileWorth.None, FindingRules.ValueOf(File("thumb_0001.jpg", FileType.Image), Noise).Worth);
        Assert.Equal(FileWorth.None, FindingRules.ValueOf(File("readme.pdf", FileType.Document), Noise).Worth);
        Assert.Equal(FileWorth.None, FindingRules.ValueOf(File("Screenshot_20190704_120000.png", FileType.Image), Noise).Worth);
    }

    [Theory]
    [InlineData("header:private-key", null, ResultCertainty.Certain)]
    [InlineData("name:id_rsa;model", null, ResultCertainty.Certain)]
    [InlineData("word:passw", null, ResultCertainty.Likely)]
    [InlineData("model", ResultCertainty.Uncertain, ResultCertainty.Uncertain)]
    public void SensitiveCertainty_FollowsWhatMarkedTheFile(string rules, ResultCertainty? model, ResultCertainty expected) =>
        Assert.Equal(expected, FindingRules.SensitiveCertainty(rules, model));

    [Fact]
    public void Rank_PutsSureBeforeUnsure_ThenByKind()
    {
        Finding F(string key, FindingKind kind, ResultCertainty c, long files) => new()
        {
            Key = key, Kind = kind, Certainty = c, Title = key, Reason = key, Files = files,
            Action = new FindingAction(PlanActionKind.Leave, "-")
        };
        var ranked = FindingRules.Rank(
        [
            F("d", FindingKind.Duplicate, ResultCertainty.Certain, 100),
            F("u", FindingKind.NotBackedUp, ResultCertainty.Uncertain, 500),
            F("n", FindingKind.NotBackedUp, ResultCertainty.Likely, 5),
            F("s", FindingKind.Sensitive, ResultCertainty.Likely, 1)
        ]);
        Assert.Equal(["n", "s", "d", "u"], ranked.Select(f => f.Key));
        Assert.Equal([1, 2, 3, 4], ranked.Select(f => f.Rank));
    }
}
