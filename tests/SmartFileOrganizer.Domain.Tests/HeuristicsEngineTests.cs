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
        var result = engine.EvaluateDirectory(path, depth);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("C:\\Users\\Alice\\Pictures", true)]
    [InlineData("C:\\Users\\Alice\\Documents", true)]
    [InlineData("C:\\Program Files\\SomeApp\\bin", false)]
    [InlineData("C:\\Windows\\System32", false)]
    public void IsLikelyUserData_ReturnsExpectedResult(string path, bool expected)
    {
        var engine = CreateEngine();
        Assert.Equal(expected, engine.IsLikelyUserData(path));
    }
}
