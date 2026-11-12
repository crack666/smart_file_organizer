using System.Text.Json;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Ollama;

/// <summary>
/// Reads the Phase 1 answer of Ollama (<c>/api/chat</c> or <c>/api/generate</c> envelope) into the
/// pre-assessment and the directory verdict. Defensive: anything unusable becomes an error on the
/// verdict instead of an exception.
/// </summary>
public static class PreAssessmentResponseParser
{
    /// <summary>English words small models tend to answer with instead of the German codes.</summary>
    private static readonly Dictionary<string, DirectoryVerdictKind> VerdictAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unit"] = DirectoryVerdictKind.Einheit,
        ["uniform"] = DirectoryVerdictKind.Einheitlich,
        ["homogeneous"] = DirectoryVerdictKind.Einheitlich,
        ["mixed"] = DirectoryVerdictKind.Gemischt,
        ["empty"] = DirectoryVerdictKind.Leer,
    };

    private static readonly Dictionary<string, RetentionValue> RetentionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["prüfen"] = RetentionValue.Pruefen,
        ["keep"] = RetentionValue.Behalten,
        ["review"] = RetentionValue.Pruefen,
        ["check"] = RetentionValue.Pruefen,
        ["expendable"] = RetentionValue.Entbehrlich,
        ["disposable"] = RetentionValue.Entbehrlich,
    };

    public static DirectoryPreAssessmentOutcome Parse(string ollamaJson, string? modelUsed)
    {
        var assessment = new DirectoryClassificationResult
        {
            Phase = "pre_assessment",
            AnalyzedAt = DateTime.UtcNow,
            ModelUsed = modelUsed
        };
        var verdict = new DirectoryVerdict
        {
            Source = VerdictSource.Modell,
            ModelUsed = modelUsed,
            RawResponse = ollamaJson,
            JudgedAt = assessment.AnalyzedAt
        };

        try
        {
            var inner = ExtractAnswerObject(ollamaJson, out var problem);
            if (inner is null)
            {
                verdict.Error = problem;
                return new DirectoryPreAssessmentOutcome(assessment, verdict);
            }

            using var innerDoc = inner;
            var root = innerDoc.RootElement;

            assessment.Homogeneity = GetString(root, "homogeneity");
            assessment.DominantType = GetString(root, "dominant_type");
            assessment.Theme = GetString(root, "theme");
            assessment.SamplingStrategy = GetString(root, "sampling_strategy");
            if (root.TryGetProperty("suggested_sample_size", out var sss) && sss.ValueKind == JsonValueKind.Number
                && sss.TryGetInt32(out var size))
                assessment.SampleSize = size;
            if (root.TryGetProperty("anomalous_files", out var anomaly) && anomaly.ValueKind == JsonValueKind.Array)
                assessment.AnomalousFileIds = anomaly.GetRawText();

            var verdictText = GetString(root, "verdict");
            if (TryParseVerdict(verdictText, out var kind))
                verdict.Verdict = kind;
            else
                verdict.Error = verdictText is null
                    ? "The model answer contained no verdict."
                    : $"The model answered an unknown verdict '{verdictText}'.";

            verdict.Purpose = GetString(root, "purpose");
            verdict.Reason = GetString(root, "reason");
            if (TryParseRetention(GetString(root, "retention"), out var retention))
                verdict.Retention = retention;
        }
        catch (JsonException ex)
        {
            verdict.Verdict = null;
            verdict.Error = $"The model answer is not valid JSON: {ex.Message}";
        }

        return new DirectoryPreAssessmentOutcome(assessment, verdict);
    }

    public static bool TryParseVerdict(string? text, out DirectoryVerdictKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return VerdictCodes.TryParse(t, out kind) || VerdictAliases.TryGetValue(t, out kind);
    }

    public static bool TryParseRetention(string? text, out RetentionValue value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return VerdictCodes.TryParse(t, out value) || RetentionAliases.TryGetValue(t, out value);
    }

    /// <summary>Unwraps the Ollama envelope and the JSON object inside the answer text.</summary>
    private static JsonDocument? ExtractAnswerObject(string ollamaJson, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(ollamaJson))
        {
            problem = "The model returned an empty response.";
            return null;
        }

        using var doc = JsonDocument.Parse(ollamaJson);
        var root = doc.RootElement;

        string? responseText = null;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("message", out var messageProp)
            && messageProp.ValueKind == JsonValueKind.Object
            && messageProp.TryGetProperty("content", out var contentProp))
        {
            responseText = contentProp.GetString();
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out var responseProp))
        {
            responseText = responseProp.GetString();
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            problem = "The model response contained no answer text.";
            return null;
        }

        var startIdx = responseText.IndexOf('{');
        var endIdx = responseText.LastIndexOf('}');
        if (startIdx < 0 || endIdx <= startIdx)
        {
            problem = "The model answer contained no JSON object.";
            return null;
        }

        return JsonDocument.Parse(responseText[startIdx..(endIdx + 1)]);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
