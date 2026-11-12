using System.Text.Json;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>Phase 1 model answers as Ollama returns them, parsed into pre-assessment and verdict.</summary>
public class PreAssessmentResponseParserTests
{
    private static string Chat(string content) =>
        JsonSerializer.Serialize(new { model = "m", message = new { role = "assistant", content }, done = true });

    [Fact]
    public void FullAnswer_FillsVerdictAndAssessment()
    {
        var json = Chat("""
            {"verdict":"uniform","purpose":"Photos of a trip in May 2024.","retention":"keep",
             "reason":"200 camera-numbered JPGs within 8 days.","homogeneity":"high","dominant_type":"Image",
             "theme":"holiday","anomalous_files":[{"name":"IMG_0007.JPG","reason":"much larger"}],
             "sampling_strategy":"random_sample","suggested_sample_size":12}
            """);

        var (assessment, verdict) = PreAssessmentResponseParser.Parse(json, "m");

        Assert.Null(verdict.Error);
        Assert.Equal(DirectoryVerdictKind.Uniform, verdict.Verdict);
        Assert.Equal(RetentionValue.Keep, verdict.Retention);
        Assert.Equal("Photos of a trip in May 2024.", verdict.Purpose);
        Assert.StartsWith("200 camera-numbered", verdict.Reason);
        Assert.Equal(VerdictSource.Model, verdict.Source);
        Assert.Equal(json, verdict.RawResponse);
        Assert.Equal("m", verdict.ModelUsed);

        Assert.Equal("high", assessment.Homogeneity);
        Assert.Equal("random_sample", assessment.SamplingStrategy);
        Assert.Equal(12, assessment.SampleSize);
        Assert.Contains("IMG_0007.JPG", assessment.AnomalousFileIds);
    }

    [Fact]
    public void GenerateEnvelope_WithProseAroundTheJson_IsParsed()
    {
        var json = JsonSerializer.Serialize(new
        {
            response = "Here is my answer:\n```json\n{\"verdict\": \"Gemischt\", \"retention\": \"pruefen\", \"purpose\": \"Downloads\", \"reason\": \"mixed\"}\n```"
        });

        var (_, verdict) = PreAssessmentResponseParser.Parse(json, "m");

        Assert.Null(verdict.Error);
        Assert.Equal(DirectoryVerdictKind.Mixed, verdict.Verdict);
        Assert.Equal(RetentionValue.Review, verdict.Retention);
    }

    [Theory]
    [InlineData("mixed", "keep", DirectoryVerdictKind.Mixed, RetentionValue.Keep)]
    [InlineData("unit", "prüfen", DirectoryVerdictKind.Unit, RetentionValue.Review)]
    [InlineData("empty", "disposable", DirectoryVerdictKind.Empty, RetentionValue.Disposable)]
    public void EnglishAnswers_AreMappedToTheCodes(string verdictText, string retentionText,
        DirectoryVerdictKind expectedVerdict, RetentionValue expectedRetention)
    {
        var json = Chat($$"""{"verdict":"{{verdictText}}","retention":"{{retentionText}}","purpose":"p","reason":"r"}""");

        var (_, verdict) = PreAssessmentResponseParser.Parse(json, "m");

        Assert.Equal(expectedVerdict, verdict.Verdict);
        Assert.Equal(expectedRetention, verdict.Retention);
    }

    /// <summary>The German words were the codes before; a model may still answer with them. Only the code is kept.</summary>
    [Theory]
    [InlineData("einheit", "behalten", DirectoryVerdictKind.Unit, RetentionValue.Keep)]
    [InlineData("Einheitlich", "pruefen", DirectoryVerdictKind.Uniform, RetentionValue.Review)]
    [InlineData("gemischt", "entbehrlich", DirectoryVerdictKind.Mixed, RetentionValue.Disposable)]
    [InlineData("leer", "prüfen", DirectoryVerdictKind.Empty, RetentionValue.Review)]
    public void OldGermanCodes_AreReadAsAliases_AndStoredAsCodes(string verdictText, string retentionText,
        DirectoryVerdictKind expectedVerdict, RetentionValue expectedRetention)
    {
        var json = Chat($$"""{"verdict":"{{verdictText}}","retention":"{{retentionText}}","purpose":"p","reason":"r"}""");

        var (_, verdict) = PreAssessmentResponseParser.Parse(json, "m");

        Assert.Null(verdict.Error);
        Assert.Equal(expectedVerdict, verdict.Verdict);
        Assert.Equal(expectedRetention, verdict.Retention);
        Assert.Equal(expectedVerdict.ToCode(), verdict.Verdict.ToCode());
        Assert.DoesNotContain(verdictText, VerdictCodes.All<DirectoryVerdictKind>(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void OldGermanMarkerWords_AreAcceptedInModelAnswers_ButNotInStoredValues()
    {
        const string answer = "zugangsdaten, Schlüssel; private_nachricht ausweis,finanzen,gesundheit,made_up";
        var all = SensitiveMarker.Credentials | SensitiveMarker.Keys | SensitiveMarker.PrivateMessage
                  | SensitiveMarker.Identity | SensitiveMarker.Finance | SensitiveMarker.Health;

        Assert.Equal(all, SensitiveMarkerNames.ParseAnswer(answer));
        Assert.Equal("credentials,keys,private_message,identity,finance,health", SensitiveMarkerNames.Format(all));
        Assert.Equal(SensitiveMarker.None, SensitiveMarkerNames.Parse(answer));
    }

    [Fact]
    public void UnknownVerdict_IsAnError_AndNamesTheValue()
    {
        var (_, verdict) = PreAssessmentResponseParser.Parse(Chat("""{"verdict":"chaotisch","retention":"keep"}"""), "m");

        Assert.Null(verdict.Verdict);
        Assert.Contains("chaotisch", verdict.Error);
    }

    [Fact]
    public void MissingVerdict_IsAnError_ButSamplingIsKept()
    {
        var (assessment, verdict) = PreAssessmentResponseParser.Parse(
            Chat("""{"homogeneity":"low","sampling_strategy":"analyze_all","suggested_sample_size":3}"""), "m");

        Assert.Null(verdict.Verdict);
        Assert.Equal("The model answer contained no verdict.", verdict.Error);
        Assert.Equal("analyze_all", assessment.SamplingStrategy);
    }

    [Fact]
    public void UnknownRetention_LeavesRetentionEmpty_WithoutFailingTheVerdict()
    {
        var (_, verdict) = PreAssessmentResponseParser.Parse(Chat("""{"verdict":"system","retention":"maybe"}"""), "m");

        Assert.Equal(DirectoryVerdictKind.System, verdict.Verdict);
        Assert.Null(verdict.Retention);
        Assert.Null(verdict.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"message\":{\"content\":\"\"}}")]
    [InlineData("{\"message\":{\"content\":\"no braces here\"}}")]
    [InlineData("{\"message\":{\"content\":\"{\\\"verdict\\\": unit}\"}}")]
    public void UnusableAnswers_BecomeStoredErrors_NotExceptions(string json)
    {
        var (_, verdict) = PreAssessmentResponseParser.Parse(json, "m");

        Assert.Null(verdict.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Error));
    }
}
