using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Scanning.Rules;

namespace SmartFileOrganizer.Domain.Tests;

public class ScanRulesFileTests
{
    // The patterns that were hard-coded in HeuristicsOptions until #21; the shipped file must keep them.
    private static readonly string[] FormerSkipPatterns =
    [
        "node_modules", ".git", ".svn", ".hg", "bin", "obj", "__pycache__", ".mypy_cache",
        "Windows", "System32", "SysWOW64", "$Recycle.Bin", "System Volume Information", "AppData",
        "ProgramData/Microsoft", "ProgramData/Package Cache", "Program Files", "Program Files (x86)",
        ".nuget", "packages",
    ];

    private static readonly string[] FormerUserDataPatterns =
    [
        "Documents", "Pictures", "Videos", "Music", "Desktop", "Downloads", "Photos", "Fotos", "Bilder",
        "Dokumente", "Eigene Dateien", "OneDrive", "Dropbox", "Google Drive",
    ];

    [Fact]
    public void Default_ContainsTheFormerPatterns_AndSettings()
    {
        var rules = ScanRulesFile.Default;

        Assert.Equal("default", rules.Name);
        Assert.Equal(FormerSkipPatterns, rules.Rules.Where(r => r.Action == ScanRuleAction.Skip).Select(r => r.Match));
        Assert.Equal(FormerUserDataPatterns, rules.Rules.Where(r => r.Action == ScanRuleAction.UserData).Select(r => r.Match));
        Assert.Equal(6, rules.ShallowFrom);
        Assert.True(rules.SkipHidden);
        Assert.Equal([FileType.Image, FileType.Video, FileType.Audio, FileType.Document], rules.UserTypes);
        Assert.Equal([FileType.Executable], rules.ProgramTypes);
    }

    [Fact]
    public void Parse_MinimalFile_UsesDefaultsAndFallbackName()
    {
        var rules = ScanRulesFile.Parse("""{ "rules": [ { "match": "Games", "action": "shallow", "max_depth": 1 } ] }""", "usb-stick", "test");

        Assert.Equal("usb-stick", rules.Name);
        Assert.Equal(6, rules.ShallowFrom);
        Assert.Equal(32, rules.SafetyLimit);
        var rule = Assert.Single(rules.Rules);
        Assert.Equal(new ScanRule("Games", ScanRuleAction.Shallow, 1), rule);
    }

    [Fact]
    public void Parse_AcceptsCommentsAndTrailingCommas_AndNormalisesSeparators()
    {
        var rules = ScanRulesFile.Parse("""
            // profile for the old laptop disk
            {
              "name": "laptop",
              "rules": [
                { "match": "Steam\\steamapps", "action": "skip", "why": "games" },
              ],
            }
            """, "x", "test");

        Assert.Equal("laptop", rules.Name);
        Assert.Equal("Steam/steamapps", rules.Rules[0].Match);
        Assert.Equal("games", rules.Rules[0].Why);
    }

    [Fact]
    public void Parse_ReportsEveryProblemAtOnce()
    {
        var json = """
            {
              "shallow_from": 0,
              "user_types": ["image", "pictures"],
              "rules": [
                { "match": "bin", "action": "ignore" },
                { "match": "Games", "action": "skip", "max_depth": 2 },
                { "match": "  ", "action": "user_data" }
              ]
            }
            """;

        var ex = Assert.Throws<ScanRulesException>(() => ScanRulesFile.Parse(json, "x", "my.json"));

        Assert.Equal(5, ex.Problems.Count);
        Assert.Contains(ex.Problems, p => p.Contains("rules[2].match"));
        Assert.Contains(ex.Problems, p => p.Contains("shallow_from"));
        Assert.Contains(ex.Problems, p => p.Contains("'pictures'"));
        Assert.Contains(ex.Problems, p => p.Contains("rules[0].action") && p.Contains("'ignore'"));
        Assert.Contains(ex.Problems, p => p.Contains("rules[1].max_depth"));
        Assert.Contains("my.json", ex.Message);
    }

    [Fact]
    public void Parse_UnknownProperty_IsRejected_SoTyposDoNotPassSilently()
    {
        var ex = Assert.Throws<ScanRulesException>(() =>
            ScanRulesFile.Parse("""{ "rules": [ { "match": "bin", "acton": "skip" } ] }""", "x", "test"));
        Assert.Contains("acton", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{ "schema_version": 2 }""")]
    public void Parse_Unusable_Throws(string json)
    {
        Assert.Throws<ScanRulesException>(() => ScanRulesFile.Parse(json, "x", "test"));
    }

    [Fact]
    public void Serialize_RoundTrips_AndHashIgnoresLayout()
    {
        var a = ScanRulesFile.Parse("""{"name":"p","rules":[{"match":"bin","action":"skip"}]}""", "x", "a");
        var b = ScanRulesFile.Parse("""
            {
              "name": "p",
              "rules": [ { "match": "bin",   "action": "skip" } ]
            }
            """, "x", "b");
        var c = ScanRulesFile.Parse("""{"name":"p","rules":[{"match":"obj","action":"skip"}]}""", "x", "c");

        Assert.Equal(ScanRulesFile.Hash(a), ScanRulesFile.Hash(b));
        Assert.NotEqual(ScanRulesFile.Hash(a), ScanRulesFile.Hash(c));

        var again = ScanRulesFile.Parse(ScanRulesFile.Serialize(ScanRulesFile.Default), "x", "roundtrip");
        Assert.Equal(ScanRulesFile.Hash(ScanRulesFile.Default), ScanRulesFile.Hash(again));
    }

    [Fact]
    public void Load_NamesTheProfileAfterTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sfo_rules_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "rules": [] }""");
        try
        {
            var profile = ScanRulesFile.ToProfile(ScanRulesFile.Load(path), path);
            Assert.Equal(Path.GetFileNameWithoutExtension(path), profile.Name);
            Assert.Equal(path, profile.Source);
            Assert.Equal(64, profile.Hash.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
