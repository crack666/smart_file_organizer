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
            var response = await _http.GetAsync($"{_options.BaseUrl}/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<FileClassification> ClassifyAsync(FileNode file, CancellationToken ct = default)
    {
        var result = new FileClassification
        {
            FileNodeId = file.Id,
            AnalyzedAt = DateTime.UtcNow,
            ModelUsed = _options.Model,
            IsAiResult = true
        };

        try
        {
            var prompt = BuildPrompt(file);
            var requestBody = new
            {
                model = _options.Model,
                prompt,
                stream = false
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var response = await _http.PostAsJsonAsync(
                $"{_options.BaseUrl}/api/generate", requestBody, cts.Token);

            response.EnsureSuccessStatusCode();

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

    private static string BuildPrompt(FileNode file)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a file classification assistant. Analyze the following file metadata and classify it.");
        sb.AppendLine();
        sb.AppendLine($"File name: {file.Name}");
        sb.AppendLine($"Extension: {file.Extension}");
        sb.AppendLine($"File type: {file.FileType}");
        sb.AppendLine($"Size (bytes): {file.Size}");
        sb.AppendLine($"Last modified: {file.LastWriteTime:yyyy-MM-dd}");
        sb.AppendLine($"Directory: {file.RelativeDir}");
        sb.AppendLine();
        sb.AppendLine("Respond ONLY in this exact JSON format, nothing else:");
        sb.AppendLine("""
            {
              "category": "<one of: Photo, DocumentScan, Screenshot, Video, Audio, Document, Invoice, PersonalDocument, Letter, Archive, Code, SoftwareInstaller, SystemFile, TrashCandidate, ReviewNeeded, Unknown>",
              "importance": "<one of: Low, Medium, High, Critical, Unknown>",
              "confidence": <0.0 to 1.0>,
              "summary": "<one sentence description>",
              "suggested_target": "<folder name suggestion like photos, documents, videos, software, trash>"
            }
            """);
        return sb.ToString();
    }

    private static void ParseResponse(string json, FileClassification result)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Ollama wraps the actual response in a "response" field
            string? responseText = null;
            if (root.TryGetProperty("response", out var responseProp))
                responseText = responseProp.GetString();

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
}
