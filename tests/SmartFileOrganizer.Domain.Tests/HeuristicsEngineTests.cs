using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Scanning.Engine;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Scanning.Rules;

namespace SmartFileOrganizer.Domain.Tests;

public class HeuristicsEngineTests
{
    private static HeuristicsEngine CreateEngine() =>
        new HeuristicsEngine(ScanRulesFile.Default);

    private static HeuristicsEngine Engine(params ScanRule[] rules) =>
        new HeuristicsEngine(new ScanRuleSet { Name = "test", Rules = rules });

    private static Func<DirectoryContent?> Content(params FileType[] types) => () => new DirectoryContent(types);

    [Theory]
    [InlineData("C:\\Users\\Alice\\Documents\\Reports", 2, ScanDecision.Descend)]
    [InlineData("C:\\node_modules\\react", 3, ScanDecision.Skip)]
    [InlineData("C:\\Windows\\System32", 2, ScanDecision.Skip)]
    public void EvaluateDirectory_ReturnsExpectedDecision(string path, int depth, ScanDecision expected)
    {
        var engine = CreateEngine();
        var result = engine.EvaluateDirectory("C:\\", path, depth);
        Assert.Equal(expected, result.Decision);
    }

    // Folder names that merely contain a skip pattern (bin, obj, ...) are user data, not build output.
    [Theory]
    [InlineData("C:\\Data", "C:\\Data\\Combinations")]
    [InlineData("C:\\Data", "C:\\Data\\cabinet")]
    [InlineData("C:\\Data", "C:\\Data\\Robin")]
    [InlineData("C:\\Data", "C:\\Data\\Objects")]
    [InlineData("C:\\Data", "C:\\Data\\Windows Photos")]
    [InlineData("/home/alice/data", "/home/alice/data/Combinations")]
    [InlineData("/home/alice/data", "/home/alice/data/cabinet/Robin")]
    public void EvaluateDirectory_DoesNotSkip_PartialWordMatches(string root, string path)
    {
        var result = CreateEngine().EvaluateDirectory(root, path, 1);
        Assert.Equal(ScanDecision.Descend, result.Decision);
    }

    [Theory]
    [InlineData("C:\\Code", "C:\\Code\\App\\bin", "bin")]
    [InlineData("C:\\Code", "C:\\Code\\App\\obj", "obj")]
    [InlineData("C:\\Code", "C:\\Code\\web\\node_modules", "node_modules")]
    [InlineData("/home/alice/code", "/home/alice/code/app/bin", "bin")]
    [InlineData("/home/alice/code", "/home/alice/code/app/obj", "obj")]
    [InlineData("/home/alice/code", "/home/alice/code/web/node_modules", "node_modules")]
    public void EvaluateDirectory_Skips_WholeSegmentMatches_WithReason(string root, string path, string pattern)
    {
        var result = CreateEngine().EvaluateDirectory(root, path, 2);
        Assert.Equal(ScanDecision.Skip, result.Decision);
        Assert.Contains($"'{pattern}'", result.Reason);
        Assert.Contains("rules 'default'", result.Reason);
    }

    [Theory]
    [InlineData("C:\\Code", "C:\\Code\\App\\BIN")]
    [InlineData("C:\\Code", "C:\\Code\\App\\Obj")]
    [InlineData("C:\\Code", "C:\\Code\\web\\Node_Modules")]
    [InlineData("C:\\", "C:\\users\\alice\\appdata")]
    [InlineData("/home/alice/code", "/home/alice/code/app/Bin")]
    public void EvaluateDirectory_IgnoresCase(string root, string path)
    {
        Assert.Equal(ScanDecision.Skip, CreateEngine().EvaluateDirectory(root, path, 2).Decision);
    }

    // The user chose the root; an AppData (or bin, Windows, ...) folder above it must not hide the scan.
    [Theory]
    [InlineData("C:\\Users\\Alice\\AppData\\Local\\Temp\\scan", "C:\\Users\\Alice\\AppData\\Local\\Temp\\scan\\docs")]
    [InlineData("C:\\Users\\Alice\\AppData\\Local\\Temp\\scan\\", "C:\\Users\\Alice\\AppData\\Local\\Temp\\scan\\docs\\photos")]
    [InlineData("C:\\Projects\\bin\\export", "C:\\Projects\\bin\\export\\reports")]
    [InlineData("/home/alice/AppData/scan", "/home/alice/AppData/scan/docs")]
    [InlineData("/srv/node_modules/backup", "/srv/node_modules/backup/photos")]
    public void EvaluateDirectory_IgnoresPathAboveRoot(string root, string path)
    {
        Assert.Equal(ScanDecision.Descend, CreateEngine().EvaluateDirectory(root, path, 1).Decision);
    }

    [Theory]
    [InlineData("C:\\Users", "C:\\Users\\Alice\\AppData")]
    [InlineData("C:\\Users", "C:\\Users\\Alice\\AppData\\Roaming")]
    [InlineData("/home", "/home/alice/AppData")]
    public void EvaluateDirectory_StillSkips_PatternBelowRoot(string root, string path)
    {
        var result = CreateEngine().EvaluateDirectory(root, path, 2);
        Assert.Equal(ScanDecision.Skip, result.Decision);
        Assert.Contains("'AppData'", result.Reason);
    }

    // Multi-segment patterns match as a run of whole segments, written with either separator.
    [Theory]
    [InlineData("C:\\", "C:\\ProgramData\\Microsoft", ScanDecision.Skip)]
    [InlineData("C:\\", "C:\\ProgramData\\Package Cache", ScanDecision.Skip)]
    [InlineData("/", "/ProgramData/Microsoft", ScanDecision.Skip)]
    [InlineData("C:\\", "C:\\ProgramData\\MicrosoftEdge", ScanDecision.Descend)]
    [InlineData("C:\\", "C:\\Microsoft\\ProgramData", ScanDecision.Descend)]
    [InlineData("C:\\", "C:\\ProgramData\\Other\\Microsoft", ScanDecision.Descend)]
    public void EvaluateDirectory_MultiSegmentPatterns_MatchAsSegmentRun(string root, string path, ScanDecision expected)
    {
        Assert.Equal(expected, CreateEngine().EvaluateDirectory(root, path, 2).Decision);
    }

    [Theory]
    [InlineData("C:\\Data", "C:\\Data\\Cache\\Temp", true)]
    [InlineData("C:\\Data", "C:\\Data\\Local\\Temp", true)]
    [InlineData("/data", "/data/Local/Temp", true)]
    [InlineData("C:\\Data", "C:\\Data\\Local\\Temporary", false)]
    public void EvaluateDirectory_CustomPatternWithEitherSeparator(string root, string path, bool skipped)
    {
        foreach (var pattern in new[] { "Local\\Temp", "Local/Temp" })
        {
            var engine = Engine(new ScanRule(pattern, ScanRuleAction.Skip), new ScanRule("Cache", ScanRuleAction.Skip));
            var decision = engine.EvaluateDirectory(root, path, 2).Decision;
            Assert.Equal(skipped ? ScanDecision.Skip : ScanDecision.Descend, decision);
        }
    }

    [Fact]
    public void EvaluateDirectory_HiddenDirectory_IsSkippedWithReason_OnBothSeparators()
    {
        var engine = CreateEngine();
        var windows = engine.EvaluateDirectory("C:\\Data", "C:\\Data\\.cache", 1);
        var unix = engine.EvaluateDirectory("/data", "/data/.cache", 1);

        Assert.Equal(ScanDecision.Skip, windows.Decision);
        Assert.Equal(ScanDecision.Skip, unix.Decision);
        Assert.Contains("hidden", windows.Reason);
    }

    [Fact]
    public void EvaluateDirectory_HiddenDirectoryInUserData_IsScanned()
    {
        var result = CreateEngine().EvaluateDirectory("/home/alice", "/home/alice/Documents/.thunderbird", 2);
        Assert.Equal(ScanDecision.Descend, result.Decision);
    }

    [Theory]
    [InlineData("C:\\Users\\Alice\\Pictures", true)]
    [InlineData("C:\\Users\\Alice\\Documents", true)]
    [InlineData("/home/alice/Documents/2024", true)]
    [InlineData("C:\\Users\\Alice\\documents\\taxes", true)]
    [InlineData("C:\\Program Files\\SomeApp\\bin", false)]
    [InlineData("C:\\Windows\\System32", false)]
    [InlineData("C:\\Code\\DocumentsParser", false)]
    public void IsLikelyUserData_ReturnsExpectedResult(string path, bool expected)
    {
        var engine = CreateEngine();
        Assert.Equal(expected, engine.IsLikelyUserData(path));
    }

    // ---------------------------------------------------------------- depth: decided by content

    private const string DeepRoot = "/disk";
    private const string Deep7 = "/disk/a/b/c/d/e/f/Urlaubsfotos";    // depth 7, beyond shallow_from 6
    private const string Deep7Win = "D:\\a\\b\\c\\d\\e\\f\\Urlaubsfotos";

    // Since #19 "Urlaubsfotos" no longer counts as user data through the partial word "Fotos".
    // Its content decides instead: photos inside, so the scan enters it.
    [Theory]
    [InlineData(DeepRoot, Deep7)]
    [InlineData("D:\\", Deep7Win)]
    public void BeyondShallowDepth_FolderWithPhotos_IsDescended(string root, string path)
    {
        var result = CreateEngine().Explain(root, path, 7, Content(FileType.Image, FileType.Image, FileType.Other));

        Assert.Equal(ScanDecision.Descend, result.Decision);
        Assert.Equal(DecisionBasis.UserContent, result.Basis);
        Assert.Equal(2, result.UserFiles);
        Assert.Contains("2 image", result.Reason);
    }

    [Theory]
    [InlineData(FileType.Video)]
    [InlineData(FileType.Document)]
    [InlineData(FileType.Audio)]
    public void BeyondShallowDepth_UserFilesNextToProgramFiles_StillDescends(FileType userType)
    {
        var result = CreateEngine().Explain(DeepRoot, Deep7, 7, Content(FileType.Executable, userType));
        Assert.Equal(ScanDecision.Descend, result.Decision);
    }

    [Fact]
    public void BeyondShallowDepth_ProgramFolder_IsShallow_WithReason()
    {
        var result = CreateEngine().Explain(DeepRoot, "/disk/a/b/c/d/e/f/Tool", 7, Content(FileType.Executable, FileType.Executable, FileType.Code));

        Assert.Equal(ScanDecision.Shallow, result.Decision);
        Assert.Equal(DecisionBasis.ProgramContent, result.Basis);
        Assert.Contains("program folder", result.Reason);
        Assert.Contains("2 executable", result.Reason);
    }

    // In doubt the scan enters: a folder of subfolders only, or one it could not look into.
    [Fact]
    public void BeyondShallowDepth_NoProgramFiles_OrUnknownContent_Descends()
    {
        var engine = CreateEngine();
        Assert.Equal(DecisionBasis.NoProgramContent, engine.Explain(DeepRoot, Deep7, 7, Content()).Basis);
        Assert.Equal(ScanDecision.Descend, engine.Explain(DeepRoot, Deep7, 7, Content()).Decision);
        Assert.Equal(ScanDecision.Descend, engine.Explain(DeepRoot, Deep7, 7, () => null).Decision);
        Assert.Equal(ScanDecision.Descend, engine.Explain(DeepRoot, Deep7, 7).Decision);
    }

    [Fact]
    public void AboveShallowDepth_ContentIsNotLookedAt()
    {
        var probed = false;
        var result = CreateEngine().Explain(DeepRoot, "/disk/a/Tool", 2, () => { probed = true; return new DirectoryContent([FileType.Executable]); });

        Assert.Equal(ScanDecision.Descend, result.Decision);
        Assert.False(probed);
    }

    // The old fixed limit skipped everything from depth 12, user data included.
    [Fact]
    public void UserData_BeyondOldMaxDepth_IsDescended_UntilSafetyLimit()
    {
        var engine = CreateEngine();
        var deep = "/home/alice/Documents/" + string.Join('/', Enumerable.Range(1, 14));

        Assert.Equal(ScanDecision.Descend, engine.EvaluateDirectory("/home/alice", deep, 15).Decision);

        var atLimit = engine.Explain("/home/alice", deep, ScanRulesFile.Default.SafetyLimit);
        Assert.Equal(ScanDecision.Shallow, atLimit.Decision);
        Assert.Equal(DecisionBasis.SafetyLimit, atLimit.Basis);
    }

    // ---------------------------------------------------------------- rule precedence

    [Fact]
    public void SkipRuleInsideUserData_WinsBecauseItMatchesCloserToTheFolder()
    {
        var result = CreateEngine().EvaluateDirectory("/home/alice", "/home/alice/Documents/project/bin", 3);
        Assert.Equal(ScanDecision.Skip, result.Decision);
    }

    [Fact]
    public void ShallowRule_WithMaxDepth_ScansThatManyLevelsBelowTheMatch()
    {
        var engine = Engine(new ScanRule("Games", ScanRuleAction.Shallow, MaxDepth: 2, Why: "installed games"));

        Assert.Equal(ScanDecision.Descend, engine.EvaluateDirectory("/d", "/d/Games", 1).Decision);
        Assert.Equal(ScanDecision.Descend, engine.EvaluateDirectory("/d", "/d/Games/Quake", 2).Decision);
        var limit = engine.Explain("/d", "/d/Games/Quake/id1", 3);
        Assert.Equal(ScanDecision.Shallow, limit.Decision);
        Assert.Equal(0, limit.RuleIndex);
        Assert.Contains("installed games", limit.Reason);
        Assert.Contains("max_depth 2", limit.Reason);
    }

    [Fact]
    public void UserDataRule_InsideShallowRule_WinsBecauseItMatchesCloserToTheFolder()
    {
        var engine = Engine(
            new ScanRule("Games", ScanRuleAction.Shallow, MaxDepth: 1),
            new ScanRule("Saves", ScanRuleAction.UserData));

        Assert.Equal(ScanDecision.Shallow, engine.EvaluateDirectory("/d", "/d/Games/Quake/data", 3).Decision);
        Assert.Equal(ScanDecision.Descend, engine.EvaluateDirectory("/d", "/d/Games/Quake/Saves", 3).Decision);
    }

    [Fact]
    public void ShallowRule_AppliesWhateverTheContent()
    {
        var engine = Engine(new ScanRule("Archive", ScanRuleAction.Shallow));
        var result = engine.EvaluateDirectory("/d", "/d/Archive", 1, Content(FileType.Image));
        Assert.Equal(ScanDecision.Shallow, result.Decision);
    }

    [Fact]
    public void SkipHiddenOff_ScansDotFolders()
    {
        var engine = new HeuristicsEngine(new ScanRuleSet { Name = "t", SkipHidden = false });
        Assert.Equal(ScanDecision.Descend, engine.EvaluateDirectory("/d", "/d/.config", 1).Decision);
    }

    // ---------------------------------------------------------------- check: the way from the root

    [Fact]
    public void Check_FolderBelowSkippedFolder_IsReportedAsNotReached()
    {
        var root = Path.Combine(Path.GetTempPath(), "sfo-check-root");
        var below = Path.Combine(root, "app", "bin", "Documents");

        var check = CreateEngine().Check(root, below, _ => null);

        Assert.Equal(ScanDecision.Descend, check.Evaluation.Decision);   // own rule: user data
        Assert.False(check.Reached);
        Assert.Equal(Path.Combine(root, "app", "bin"), check.BlockedAt);
        Assert.Equal(ScanDecision.Skip, check.BlockedBy!.Decision);
        Assert.Equal(3, check.Evaluation.Depth);
    }

    [Fact]
    public void Check_RootItself_IsTheScanRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sfo-check-root");
        var check = CreateEngine().Check(root, root + Path.DirectorySeparatorChar, _ => null);
        Assert.Equal(DecisionBasis.ScanRoot, check.Evaluation.Basis);
        Assert.True(check.Reached);
    }

    [Fact]
    public void Check_PathOutsideRoot_Throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "sfo-check-root");
        var other = Path.Combine(Path.GetTempPath(), "elsewhere", "x");
        Assert.Throws<ArgumentException>(() => CreateEngine().Check(root, other, _ => null));
    }
}
