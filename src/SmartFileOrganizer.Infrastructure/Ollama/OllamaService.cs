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
            var prompt = BuildPrompt(input, _options.SummaryLanguage);
            _logger.LogInformation("[Phase2] PROMPT for '{File}' (images={ImgCount}, textLen={TextLen}):\n{Prompt}",
                file.Name, input.Base64Images.Count, input.ExtractedText?.Length ?? 0,
                Trunc(prompt, 800));

            var requestBody = new
            {
                model = _options.Model,
                stream = false,
                think = false,
                keep_alive = _options.KeepAlive,
                format = BuildClassificationSchema(),
                messages = new object[]
                {
                    new { role = "system", content = _options.SystemPrompt },
                    new { role = "user", content = prompt, images = input.Base64Images.Count > 0 ? input.Base64Images : null }
                }
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await _http.PostAsJsonAsync(BuildUri("api/chat"), requestBody, cts.Token);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
                var errorMessage = TryExtractOllamaError(errorBody)
                    ?? $"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}";

                result.Error = errorMessage;
                result.RawResponse = errorBody;
                _logger.LogWarning("[Phase2] HTTP {Status} for '{File}' after {Elapsed:F1}s — {Message}\nBody: {Body}",
                    (int)response.StatusCode, file.Name, sw.Elapsed.TotalSeconds, errorMessage, Trunc(errorBody, 500));
                return result;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            result.RawResponse = json;

            ParseResponse(json, result);
            _logger.LogInformation("[Phase2] '{File}': {Category} ({Confidence:P0}) in {Elapsed:F1}s — RAW: {Raw}",
                file.Name, result.Category, result.Confidence, sw.Elapsed.TotalSeconds, Trunc(json, 400));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Error = $"Ollama request timed out after {_options.TimeoutSeconds}s.";
            _logger.LogWarning("[Phase2] TIMEOUT ({Seconds}s) for '{File}'", _options.TimeoutSeconds, file.Name);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger.LogError(ex, "[Phase2] Unexpected error for '{File}'", file.Name);
        }

        return result;
    }

    private static string BuildPrompt(OllamaClassificationInput input, string summaryLanguage = "English")
    {
        var file = input.File;
        var sb = new StringBuilder();
        sb.AppendLine("Classify the following file. Choose the most accurate category and importance.");
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

        sb.AppendLine("Classification guidance:");
        sb.AppendLine("- Personal documents (CVs, tax returns, contracts, letters, medical records): PersonalDocument or Letter, importance High or Critical");
        sb.AppendLine("- Family or personal photos and videos: Photo or Video, importance High");
        sb.AppendLine("- Scanned documents (receipts, official letters, forms): DocumentScan, importance Medium or High");
        sb.AppendLine("- Financial records (invoices, bank statements): Invoice or Document, importance High");
        sb.AppendLine("- Software installers, update packages, build artifacts: SoftwareInstaller or SystemFile, importance Low");
        sb.AppendLine("- Temporary files, logs, crash dumps, cache: TrashCandidate, importance Low");
        sb.AppendLine("- Anything personal or user-created but hard to categorize: ReviewNeeded");
        sb.AppendLine();
        sb.AppendLine("Do NOT use TrashCandidate for personal documents, even if they appear old or superseded.");
        sb.AppendLine("Use ReviewNeeded if you are uncertain — do not guess TrashCandidate.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(summaryLanguage) &&
            !string.Equals(summaryLanguage, "English", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"Write the summary field in: {summaryLanguage}.");
        }

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

            // Accept both "category" (our schema) and "classification" (gemma4 alias)
            var catProp = inner.TryGetProperty("category", out var cp) ? cp
                        : inner.TryGetProperty("classification", out var cl) ? cl
                        : default;
            if (catProp.ValueKind == JsonValueKind.String &&
                Enum.TryParse<FileCategory>(catProp.GetString(), true, out var parsedCat))
                result.Category = parsedCat;

            if (inner.TryGetProperty("importance", out var imp) &&
                Enum.TryParse<Importance>(imp.GetString(), true, out var parsedImp))
                result.Importance = parsedImp;

            // confidence may be missing — derive a default from whether category was parsed
            if (inner.TryGetProperty("confidence", out var conf))
                result.Confidence = conf.GetDouble();
            else if (result.Category != FileCategory.Unknown)
                result.Confidence = 0.85; // model gave a valid category but omitted confidence

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

    private static int CountJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch { return 0; }
    }

    private static string Trunc(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty
        : s.Length <= max ? s
        : s[..max] + $"… [{s.Length - max} more chars]";

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

    // ─── Phase 1: Directory Pre-Assessment ───────────────────────────────────

    public async Task<DirectoryClassificationResult> PreAssessDirectoryAsync(
        DirectoryPreAssessmentInput input, CancellationToken ct = default)
    {
        var result = new DirectoryClassificationResult
        {
            DirectoryNodeId = input.Directory.Id,
            Phase = "pre_assessment",
            AnalyzedAt = DateTime.UtcNow,
            ModelUsed = _options.Model
        };

        try
        {
            var prompt = BuildPreAssessmentPrompt(input);
            _logger.LogInformation("[Phase1] PROMPT for '{Dir}' ({Files} files, {Subdirs} subdirs, {Len} chars):\n{Prompt}",
                input.Directory.RelativePath, input.DirectFiles.Count, input.SubdirectoryNames.Count,
                prompt.Length, Trunc(prompt, 800));

            var requestBody = new
            {
                model = _options.Model,
                stream = false,
                think = false,
                keep_alive = _options.KeepAlive,
                format = BuildPreAssessmentSchema(),
                messages = new object[] { new { role = "user", content = prompt } }
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await _http.PostAsJsonAsync(BuildUri("api/chat"), requestBody, cts.Token);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
                result.Error = TryExtractOllamaError(errorBody) ?? $"HTTP {(int)response.StatusCode}";
                _logger.LogWarning(
                    "[Phase1] HTTP {Status} for '{Dir}' after {Elapsed:F1}s — Error: {Err}\nBody: {Body}",
                    (int)response.StatusCode, input.Directory.RelativePath, sw.Elapsed.TotalSeconds,
                    result.Error, Trunc(errorBody, 500));
                return result;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            ParsePreAssessmentResponse(json, result);
            _logger.LogInformation(
                "[Phase1] '{Dir}' in {Elapsed:F1}s — homogeneity={Hom}, strategy={Strat}, sample={Sample}, anomalies={Anom} — RAW: {Raw}",
                input.Directory.RelativePath, sw.Elapsed.TotalSeconds,
                result.Homogeneity, result.SamplingStrategy,
                result.SampleSize, CountJsonArray(result.AnomalousFileIds), Trunc(json, 400));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Error = $"Ollama request timed out after {_options.TimeoutSeconds}s.";
            _logger.LogWarning("[Phase1] TIMEOUT ({Seconds}s) for '{Dir}'", _options.TimeoutSeconds, input.Directory.RelativePath);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger.LogError(ex, "[Phase1] Unexpected error for '{Dir}'", input.Directory.RelativePath);
        }

        return result;
    }

    private static string BuildPreAssessmentPrompt(DirectoryPreAssessmentInput input)
    {
        var dir = input.Directory;
        var sb = new StringBuilder();
        sb.AppendLine("You are analyzing a directory to plan targeted file inspection.");
        sb.AppendLine();
        sb.AppendLine($"Directory: {dir.Name}");
        sb.AppendLine($"Relative path: {dir.RelativePath}");
        sb.AppendLine($"Total direct files: {input.DirectFiles.Count}");
        sb.AppendLine($"Subdirectories: {input.SubdirectoryNames.Count}");

        if (input.SubdirectoryNames.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Subdirectory names:");
            foreach (var sub in input.SubdirectoryNames.Take(20))
                sb.AppendLine($"  {sub}");
            if (input.SubdirectoryNames.Count > 20)
                sb.AppendLine($"  ... (+{input.SubdirectoryNames.Count - 20} more)");
        }

        // Sort by size descending to put outliers first; cap at 200 for prompt length
        var listed = input.DirectFiles.OrderByDescending(f => f.Size).Take(200).ToList();
        sb.AppendLine();
        sb.AppendLine("Files (name | size in bytes):");
        foreach (var f in listed)
            sb.AppendLine($"  {f.Name} | {f.Size}");
        if (input.DirectFiles.Count > 200)
            sb.AppendLine($"  ... (+{input.DirectFiles.Count - 200} more, total {input.DirectFiles.Count})");

        sb.AppendLine();
        sb.AppendLine("Tasks:");
        sb.AppendLine("1. Assess homogeneity: high=all same type/theme, medium=mostly similar, low=very mixed.");
        sb.AppendLine("2. Identify anomalous files: different type from the majority, or dramatically larger/smaller than peers.");
        sb.AppendLine("3. Choose a sampling strategy for deep vision/text analysis:");
        sb.AppendLine("   analyze_all = ≤10 files or content is too mixed to sample");
        sb.AppendLine("   random_sample = pick a representative subset (use this for large homogeneous dirs)");
        sb.AppendLine("   skip = only dir name/path are enough to classify (e.g. a clearly-named system folder)");
        sb.AppendLine("4. Suggest how many files to analyze (0 for skip, all count for analyze_all).");
        sb.AppendLine();
        sb.AppendLine("Return only JSON matching the provided schema.");
        return sb.ToString();
    }

    private static object BuildPreAssessmentSchema() => new
    {
        type = "object",
        properties = new
        {
            homogeneity = new { type = "string", @enum = new[] { "high", "medium", "low" } },
            dominant_type = new { type = "string" },
            theme = new { type = "string" },
            anomalous_files = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string" },
                        reason = new { type = "string" }
                    },
                    required = new[] { "name", "reason" }
                }
            },
            sampling_strategy = new { type = "string", @enum = new[] { "analyze_all", "random_sample", "skip" } },
            suggested_sample_size = new { type = "integer" }
        },
        required = new[] { "homogeneity", "dominant_type", "theme", "anomalous_files", "sampling_strategy", "suggested_sample_size" }
    };

    private static void ParsePreAssessmentResponse(string json, DirectoryClassificationResult result)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

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

            if (string.IsNullOrWhiteSpace(responseText)) return;

            var startIdx = responseText.IndexOf('{');
            var endIdx = responseText.LastIndexOf('}');
            if (startIdx < 0 || endIdx < 0 || endIdx <= startIdx) return;

            var jsonBlock = responseText[startIdx..(endIdx + 1)];
            using var innerDoc = JsonDocument.Parse(jsonBlock);
            var inner = innerDoc.RootElement;

            if (inner.TryGetProperty("homogeneity", out var hom)) result.Homogeneity = hom.GetString();
            if (inner.TryGetProperty("dominant_type", out var dt)) result.DominantType = dt.GetString();
            if (inner.TryGetProperty("theme", out var th)) result.Theme = th.GetString();
            if (inner.TryGetProperty("sampling_strategy", out var ss)) result.SamplingStrategy = ss.GetString();
            if (inner.TryGetProperty("suggested_sample_size", out var sss))
                result.SampleSize = sss.ValueKind == JsonValueKind.Number ? sss.GetInt32() : 0;
            if (inner.TryGetProperty("anomalous_files", out var anomaly) && anomaly.ValueKind == JsonValueKind.Array)
                result.AnomalousFileIds = anomaly.GetRawText();
        }
        catch
        {
            // Malformed response — leave defaults
        }
    }

    // ─── Phase 3: Directory Summary ───────────────────────────────────────────

    public async Task<DirectoryClassificationResult> SummarizeDirectoryAsync(
        DirectorySummaryInput input, CancellationToken ct = default)
    {
        var result = new DirectoryClassificationResult
        {
            DirectoryNodeId = input.Directory.Id,
            Phase = "summary",
            AnalyzedAt = DateTime.UtcNow,
            ModelUsed = _options.Model
        };

        try
        {
            var prompt = BuildSummaryPrompt(input);
            var requestBody = new
            {
                model = _options.Model,
                stream = false,
                think = false,
                keep_alive = _options.KeepAlive,
                format = BuildSummarySchema(),
                messages = new object[] { new { role = "user", content = prompt } }
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await _http.PostAsJsonAsync(BuildUri("api/chat"), requestBody, cts.Token);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
                result.Error = TryExtractOllamaError(errorBody) ?? $"HTTP {(int)response.StatusCode}";
                _logger.LogWarning(
                    "[Phase3] Ollama returned {Status} for '{Dir}' after {Elapsed:F1}s — Error: {Err}",
                    (int)response.StatusCode, input.Directory.RelativePath, sw.Elapsed.TotalSeconds, result.Error);
                return result;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            _logger.LogDebug("[Phase3] Summary for '{Dir}' in {Elapsed:F1}s", input.Directory.RelativePath, sw.Elapsed.TotalSeconds);
            ParseSummaryResponse(json, result);

            _logger.LogInformation("[Phase3] '{Dir}': theme={Theme} — {Summary}",
                input.Directory.RelativePath, result.Theme, result.Summary?.Length > 120 ? result.Summary[..120] + "…" : result.Summary);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Error = $"Ollama request timed out after {_options.TimeoutSeconds}s.";
            _logger.LogWarning("[Phase3] TIMEOUT ({Seconds}s) for '{Dir}'", _options.TimeoutSeconds, input.Directory.RelativePath);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger.LogError(ex, "[Phase3] Unexpected error for '{Dir}'", input.Directory.RelativePath);
        }

        return result;
    }

    private static string BuildSummaryPrompt(DirectorySummaryInput input)
    {
        var dir = input.Directory;
        var pre = input.PreAssessment;
        var sb = new StringBuilder();
        sb.AppendLine("You are writing a final summary for a directory based on AI analysis of a sample of its files.");
        sb.AppendLine();
        sb.AppendLine($"Directory: {dir.Name}");
        sb.AppendLine($"Relative path: {dir.RelativePath}");
        sb.AppendLine($"Pre-assessment: theme=\"{pre.Theme}\", homogeneity={pre.Homogeneity}, dominant_type={pre.DominantType}");
        sb.AppendLine();

        if (input.FileSummaryLines.Count > 0)
        {
            sb.AppendLine($"Analyzed files ({input.FileSummaryLines.Count}):");
            foreach (var line in input.FileSummaryLines)
                sb.AppendLine($"  {line}");
            sb.AppendLine();
        }

        if (input.AnomalyDescriptions.Count > 0)
        {
            sb.AppendLine($"Previously flagged anomalies ({input.AnomalyDescriptions.Count}):");
            foreach (var line in input.AnomalyDescriptions)
                sb.AppendLine($"  {line}");
            sb.AppendLine();
        }

        sb.AppendLine("Write a short summary (1-2 sentences) describing what this directory contains.");
        sb.AppendLine("Confirm which anomalies are real outliers, if any.");
        sb.AppendLine("Return only JSON matching the provided schema.");
        return sb.ToString();
    }

    private static object BuildSummarySchema() => new
    {
        type = "object",
        properties = new
        {
            summary = new { type = "string" },
            theme = new { type = "string" },
            confirmed_anomalies = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string" },
                        reason = new { type = "string" }
                    },
                    required = new[] { "name", "reason" }
                }
            }
        },
        required = new[] { "summary", "theme", "confirmed_anomalies" }
    };

    private static void ParseSummaryResponse(string json, DirectoryClassificationResult result)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

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

            if (string.IsNullOrWhiteSpace(responseText)) return;

            var startIdx = responseText.IndexOf('{');
            var endIdx = responseText.LastIndexOf('}');
            if (startIdx < 0 || endIdx < 0 || endIdx <= startIdx) return;

            var jsonBlock = responseText[startIdx..(endIdx + 1)];
            using var innerDoc = JsonDocument.Parse(jsonBlock);
            var inner = innerDoc.RootElement;

            if (inner.TryGetProperty("summary", out var sum)) result.Summary = sum.GetString();
            if (inner.TryGetProperty("theme", out var th)) result.Theme = th.GetString();
            if (inner.TryGetProperty("confirmed_anomalies", out var ca) && ca.ValueKind == JsonValueKind.Array)
                result.AnomalousFileIds = ca.GetRawText();
        }
        catch
        {
            // Malformed response — leave defaults
        }
    }

    private Uri BuildUri(string relativePath)
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(baseUrl), relativePath);
    }
}
