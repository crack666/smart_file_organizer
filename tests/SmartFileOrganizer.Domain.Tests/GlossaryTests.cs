using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Tests;

/// <summary>
/// Every coded enum value has a code and a line in <c>docs/glossary.md</c>, and the values people see
/// have words in <see cref="CodeLabels"/> (https://github.com/crack666/smart_file_organizer/issues/35).
/// A new enum value without them fails here instead of reaching the data format unexplained.
/// </summary>
public class GlossaryTests
{
    private static readonly Lazy<string[]> GlossaryLines = new(() =>
        File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "glossary.md")));

    /// <summary>Enums whose codes come from their own value classes rather than the member attribute.</summary>
    private static readonly Type[] OtherCodedEnums = [typeof(SensitiveMarker), typeof(ScanRole), typeof(ContentState), typeof(FileCategory)];

    public static TheoryData<string> CodedEnums()
    {
        var data = new TheoryData<string>();
        foreach (var type in AllCodedEnums())
            data.Add(type.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CodedEnums))]
    public void EveryValue_HasACode_AndAGlossaryEntry(string enumName)
    {
        var type = AllCodedEnums().Single(t => t.Name == enumName);
        var codes = CodesOf(type);
        var section = Section(type);
        Assert.True(section is not null, $"docs/glossary.md has no section with (`{enumName}`) in its heading.");

        var listed = section!
            .Select(line => Regex.Match(line, @"^\| `(?<code>[a-z0-9_]+)` \| (?<meaning>.*\S) \|$"))
            .Where(m => m.Success)
            .ToDictionary(m => m.Groups["code"].Value, m => m.Groups["meaning"].Value);

        foreach (var code in codes)
            Assert.True(listed.ContainsKey(code), $"{enumName}: code '{code}' has no line in docs/glossary.md.");
        foreach (var code in listed.Keys)
            Assert.True(codes.Contains(code), $"{enumName}: docs/glossary.md lists '{code}', which is not a code of the enum.");
    }

    [Fact]
    public void CodedEnums_IncludeTheGroupsOfTheDecisionTable()
    {
        var names = AllCodedEnums().Select(t => t.Name).ToList();
        foreach (var expected in new[] { nameof(DirectoryVerdictKind), nameof(RetentionValue), nameof(VerdictSource),
                     nameof(ResultOrigin), nameof(SensitiveMarker), nameof(KeepHint), nameof(ScanRole), nameof(ContentState) })
        {
            Assert.Contains(expected, names);
            Assert.Contains(CodeLabels.Enums, t => t.Name == expected);
        }
    }

    [Fact]
    public void ShownValues_HaveWordsInEnglishAndGerman()
    {
        var label = typeof(CodeLabels).GetMethod(nameof(CodeLabels.Label))!;
        foreach (var type in CodeLabels.Enums)
        {
            foreach (var value in Enum.GetValues(type).Cast<object>().Where(v => !IsNone(type, v)))
            {
                foreach (var language in Enum.GetValues<DisplayLanguage>())
                {
                    var word = (string)label.MakeGenericMethod(type).Invoke(null, [value, language])!;
                    Assert.False(string.IsNullOrWhiteSpace(word), $"{type.Name}.{value} has no {language} word.");
                }
            }
        }
    }

    [Theory]
    [InlineData("Deutsch", DisplayLanguage.German)]
    [InlineData("german", DisplayLanguage.German)]
    [InlineData("English", DisplayLanguage.English)]
    [InlineData("Français", DisplayLanguage.English)]
    [InlineData(null, DisplayLanguage.English)]
    public void DisplayLanguage_FollowsTheSummaryLanguage(string? setting, DisplayLanguage expected) =>
        Assert.Equal(expected, CodeLabels.LanguageFor(setting));

    [Fact]
    public void Words_AreForPeople_CodesStayCodes()
    {
        Assert.Equal("mixed", CodeLabels.Label(DirectoryVerdictKind.Mixed, DisplayLanguage.English));
        Assert.Equal("gemischt", CodeLabels.Label(DirectoryVerdictKind.Mixed, DisplayLanguage.German));
        Assert.Equal("private Nachricht", CodeLabels.LabelOfCode<SensitiveMarker>("private_message", DisplayLanguage.German));
        Assert.Equal("none", CodeLabels.LabelOfCode<RetentionValue>("none", DisplayLanguage.German));
        Assert.Null(CodeLabels.LabelOfCode<RetentionValue>(null, DisplayLanguage.German));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static IReadOnlyList<Type> AllCodedEnums() =>
        typeof(VerdictCodes).Assembly.GetTypes()
            .Where(t => t.IsEnum && t.Namespace == typeof(VerdictCodes).Namespace)
            .Where(t => OtherCodedEnums.Contains(t)
                        || t.GetFields(BindingFlags.Public | BindingFlags.Static)
                            .Any(f => f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>() is not null))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>The code of every value; throws (through the code classes) when a value has none.</summary>
    private static HashSet<string> CodesOf(Type type)
    {
        if (type == typeof(SensitiveMarker))
        {
            return Enum.GetValues<SensitiveMarker>().Where(m => m != SensitiveMarker.None)
                .Select(m => SensitiveMarkerNames.Format(m) ?? throw new InvalidOperationException($"{m} has no code."))
                .ToHashSet();
        }
        if (type == typeof(ScanRole))
            return Enum.GetValues<ScanRole>().Select(r => r.ToValue()).ToHashSet();
        if (type == typeof(ContentState))
            return Enum.GetValues<ContentState>().Select(s => s.ToValue()).ToHashSet();
        // Categories are written as their names in snake_case, in all CLI JSON and in the truth of the corpus.
        if (type == typeof(FileCategory))
            return Enum.GetNames<FileCategory>().Select(JsonNamingPolicy.SnakeCaseLower.ConvertName).ToHashSet();

        // VerdictCodes refuses an enum with a member that has no code attribute.
        var all = typeof(VerdictCodes).GetMethod(nameof(VerdictCodes.All))!.MakeGenericMethod(type);
        try
        {
            return ((IReadOnlyList<string>)all.Invoke(null, null)!).ToHashSet();
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>The lines below the heading that names <paramref name="type"/>, up to the next heading.</summary>
    private static List<string>? Section(Type type)
    {
        var lines = GlossaryLines.Value;
        var start = Array.FindIndex(lines, l => l.StartsWith('#') && l.Contains($"(`{type.Name}`)", StringComparison.Ordinal));
        if (start < 0) return null;
        return lines.Skip(start + 1).TakeWhile(l => !l.StartsWith('#')).ToList();
    }

    private static bool IsNone(Type type, object value) => type == typeof(SensitiveMarker) && (SensitiveMarker)value == SensitiveMarker.None;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SmartFileOrganizer.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Repository root (SmartFileOrganizer.slnx) not found above the test output.");
    }
}
