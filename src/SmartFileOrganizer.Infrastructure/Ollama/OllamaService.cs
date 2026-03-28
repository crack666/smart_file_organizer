using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Ollama;

/// <summary>
/// Calls the Ollama HTTP API to classify files semantically.
/// For image/video/document files it sends a structured prompt;
/// for other types it returns a heuristic result without calling Ollama.
/// </summary>
public class OllamaService : IOllamaService
{
    private readonly HttpClient _http;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaService> _logger;

    public OllamaService(HttpClient http, OllamaOptions options, ILogger<OllamaService> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync(BuildUri("api/tags"), ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(BuildUri("api/tags"), ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var models = new List<OllamaLocalModel>();
        if (!doc.RootElement.TryGetProperty("models", out var modelsProp) || modelsProp.ValueKind != JsonValueKind.Array)
            return models;

        foreach (var modelProp in modelsProp.EnumerateArray())
        {
            var details = modelProp.TryGetProperty("details", out var d) ? d : default;
            models.Add(new OllamaLocalModel
            {
                Name = modelProp.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                Family = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("family", out var family) ? family.GetString() ?? string.Empty : string.Empty,
                ParameterSize = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("parameter_size", out var parameterSize) ? parameterSize.GetString() ?? string.Empty : string.Empty,
                QuantizationLevel = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("quantization_level", out var q) ? q.GetString() ?? string.Empty : string.Empty
            });
        }

        return models.OrderByDescending(m => string.Equals(m.Name, _options.Model, StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.Name)
            .ToList();
    }

    public async Task WarmModelAsync(string? model = null, CancellationToken ct = default)
    {
        var requestBody = new
        {
            model = string.IsNullOrWhiteSpace(model) ? _options.Model : model,
            messages = Array.Empty<object>(),
            stream = false,
            keep_alive = _options.KeepAlive
        };

        using var response = await _http.PostAsJsonAsync(BuildUri("api/chat"), requestBody, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<FileClassification> ClassifyAsync(OllamaClassificationInput input, CancellationToken ct = default)
    {
        var file = input.File;
        var result = new FileClassification
        {
            FileNodeId = file.Id,
            AnalyzedAt = DateTime.UtcNow,
            ModelUsed = _options.Model,
            IsAiResult = true
        };

        try
        {
            var prompt = BuildPrompt(input);
            var requestBody = new
            {
                model = _options.Model,
                stream = false,
                keep_alive = _options.KeepAlive,
                format = BuildClassificationSchema(),
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "You classify user files for cleanup and organization. Prefer preserving valuable user data, personal records, family media, and important documents. Return only JSON that matches the schema."
                    },
                    new
                    {
                        role = "user",
                        content = prompt,
                        images = input.Base64Images.Count > 0 ? input.Base64Images : null
                    }
                }
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var response = await _http.PostAsJsonAsync(BuildUri("api/chat"), requestBody, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
                var errorMessage = TryExtractOllamaError(errorBody)
                    ?? $"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}";

                result.Error = errorMessage;
                result.RawResponse = errorBody;
                _logger.LogWarning(
                    "Ollama returned a non-success status for file {Path}: {StatusCode} {Message}",
                    file.FullPath,
                    (int)response.StatusCode,
                    errorMessage);
                return result;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            result.RawResponse = json;

            ParseResponse(json, result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Error = "Ollama request timed out.";
            _logger.LogWarning("Ollama timeout for file {Path}", file.FullPath);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger.LogError(ex, "Ollama error for file {Path}", file.FullPath);
        }

        return result;
    }

    private static string BuildPrompt(OllamaClassificationInput input)
    {
        var file = input.File;
        var sb = new StringBuilder();
        sb.AppendLine("Analyze this file to decide whether it is valuable user data, an important document/media file, or likely trash/system clutter.");
        sb.AppendLine();
        sb.AppendLine($"File name: {file.Name}");
        sb.AppendLine($"Extension: {file.Extension}");
        sb.AppendLine($"File type: {file.FileType}");
        sb.AppendLine($"Size (bytes): {file.Size}");
        sb.AppendLine($"Last modified: {file.LastWriteTime:yyyy-MM-dd}");
        sb.AppendLine($"Directory: {file.RelativeDir}");
        sb.AppendLine($"Has visual attachment: {(input.Base64Images.Count > 0 ? "yes" : "no")}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(input.ExtractedText))
        {
            sb.AppendLine("Extracted text / preview content:");
            sb.AppendLine(input.ExtractedText.Length > 8000
                ? input.ExtractedText[..8000]
                : input.ExtractedText);
            sb.AppendLine();
        }

        sb.AppendLine("Consider whether this is likely:");
        sb.AppendLine("- private photos or memories");
        sb.AppendLine("- personal, financial, legal, medical, or identity documents");
        sb.AppendLine("- meaningful user-created work");
        sb.AppendLine("- duplicates, software artifacts, scans of low value, system files, or cleanup candidates");
        sb.AppendLine();
        sb.AppendLine("Return only JSON matching the provided schema.");
        return sb.ToString();
    }

    private static object BuildClassificationSchema() => new
    {
        type = "object",
        properties = new
        {
            category = new
            {
                type = "string",
                @enum = new[]
                {
                    "Photo", "DocumentScan", "Screenshot", "Video", "Audio", "Document",
                    "Invoice", "PersonalDocument", "Letter", "Archive", "Code",
                    "SoftwareInstaller", "SystemFile", "TrashCandidate", "ReviewNeeded", "Unknown"
                }
            },
            importance = new
            {
                type = "string",
                @enum = new[] { "Low", "Medium", "High", "Critical", "Unknown" }
            },
            confidence = new { type = "number" },
            summary = new { type = "string" },
            suggested_target = new { type = "string" }
        },
        required = new[] { "category", "importance", "confidence", "summary", "suggested_target" }
    };

    private static void ParseResponse(string json, FileClassification result)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // /api/chat returns the actual content in message.content
            string? responseText = null;
            if (root.TryGetProperty("message", out var messageProp) &&
                messageProp.ValueKind == JsonValueKind.Object &&
                messageProp.TryGetProperty("content", out var contentProp))
            {
                responseText = contentProp.GetString();
            }
            else if (root.TryGetProperty("response", out var responseProp))
            {
                responseText = responseProp.GetString();
            }

            if (string.IsNullOrWhiteSpace(responseText))
                return;

            // Extract JSON block from the response text
            var startIdx = responseText.IndexOf('{');
            var endIdx = responseText.LastIndexOf('}');
            if (startIdx < 0 || endIdx < 0 || endIdx <= startIdx)
                return;

            var jsonBlock = responseText[startIdx..(endIdx + 1)];
            using var innerDoc = JsonDocument.Parse(jsonBlock);
            var inner = innerDoc.RootElement;

            if (inner.TryGetProperty("category", out var cat) &&
                Enum.TryParse<FileCategory>(cat.GetString(), true, out var parsedCat))
                result.Category = parsedCat;

            if (inner.TryGetProperty("importance", out var imp) &&
                Enum.TryParse<Importance>(imp.GetString(), true, out var parsedImp))
                result.Importance = parsedImp;

            if (inner.TryGetProperty("confidence", out var conf))
                result.Confidence = conf.GetDouble();

            if (inner.TryGetProperty("summary", out var summary))
                result.Summary = summary.GetString();

            if (inner.TryGetProperty("suggested_target", out var target))
                result.SuggestedTarget = target.GetString();
        }
        catch
        {
            // Malformed response — leave defaults, caller will see empty fields
        }
    }

    private static string? TryExtractOllamaError(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var errorProp))
                return errorProp.GetString();
        }
        catch
        {
            // Ignore parsing errors and fall back to the raw response body.
        }

        return responseBody;
    }

    private Uri BuildUri(string relativePath)
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(baseUrl), relativePath);
    }
}
