using SmartFileOrganizer.Scanning.Engine;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Domain.Tests;

public class HeuristicsEngineTests
{
    private static HeuristicsEngine CreateEngine() =>
        new HeuristicsEngine(new HeuristicsOptions());

    [Theory]
    [InlineData("C:\\Users\\Alice\\Documents\\Reports", 2, ScanDecision.Descend)]
    [InlineData("C:\\node_modules\\react", 3, ScanDecision.Skip)]
    [InlineData("C:\\Windows\\System32", 2, ScanDecision.Skip)]
    [InlineData("C:\\Users\\Alice\\Code\\very\\deep\\path\\here\\more\\levels\\deeply\\nested\\stuff", 11, ScanDecision.Shallow)]
    [InlineData("C:\\deep\\path\\really\\really\\really\\that\\is\\very\\very\\deeply\\nested\\here\\stuff", 12, ScanDecision.Skip)]
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
            var engine = new HeuristicsEngine(new HeuristicsOptions { SkipPatterns = [pattern, "Cache"] });
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
}
