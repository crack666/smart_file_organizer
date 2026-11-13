using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.ModelServer;

namespace SmartFileOrganizer.Infrastructure.Ollama;

/// <summary>
/// Calls the Ollama HTTP API to classify files semantically from the evidence built by
/// <c>IClassificationInputPreparer</c>. Files decided by a rule never reach this class.
/// </summary>
/// <remarks>
/// Two loggers: the regular one gets progress and errors without any text from or about file contents
/// (it may end up on stderr, which an external caller reads); prompts, answers and error bodies go to
/// <see cref="ModelTraffic"/> on level Debug, and for a marked file not even there
/// (https://github.com/crack666/smart_file_organizer/issues/33).
/// </remarks>
public class OllamaService : IOllamaService
{
    private readonly HttpClient _http;
    private readonly OllamaOptions _options;
    private readonly ModelServerOptions _server;
    private readonly TimeProvider _time;
    private readonly ModelHttpSender _sender;
    private readonly ILogger<OllamaService> _logger;
    private readonly ILogger _traffic;

    /// <param name="http">Its own <c>Timeout</c> should be infinite: every call here sets its own limit.</param>
    /// <param name="server">Retry pauses and probe limits; null for the defaults.</param>
    public OllamaService(
        HttpClient http,
        OllamaOptions options,
        ILogger<OllamaService> logger,
        ILogger<ModelTraffic>? traffic = null,
        ModelServerOptions? server = null,
        TimeProvider? time = null)
    {
        _http = http;
        _options = options;
        _logger = logger;
        _traffic = (ILogger?)traffic ?? NullLogger.Instance;
        _server = server ?? new ModelServerOptions();
        _time = time ?? TimeProvider.System;
        _sender = new ModelHttpSender(http, _time, logger);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var limit = new CancellationTokenSource(_server.ProbeTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            using var response = await _http.GetAsync(BuildUri("api/tags"), linked.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The server answers and lists the configured model (a name without tag also matches <c>:latest</c>).</summary>
    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        try
        {
            var models = await GetLocalModelsAsync(ct);
            return models.Any(m => string.Equals(m.Name, _options.Model, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(m.Name, _options.Model + ":latest", StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default)
    {
        using var limit = new CancellationTokenSource(_server.ProbeTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        using var response = await _http.GetAsync(BuildUri("api/tags"), linked.Token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: linked.Token);

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

        // Loading a large model takes minutes; its own limit, far above the one of a call.
        using var limit = new CancellationTokenSource(_server.WarmupTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        using var response = await _http.PostAsJsonAsync(BuildUri("api/chat"), requestBody, linked.Token);
        response.EnsureSuccessStatusCode();
    }

    private ModelHttpRetryPolicy RetryPolicy => new(_options.MaxRetries, _server.RetryDelay, _server.MaxRetryAfter);

    private TimeSpan CallTimeout => TimeSpan.FromSeconds(Math.Max(0.001, _options.TimeoutSeconds));

    /// <summary>
    /// One <c>/api/chat</c> call with retries (<see cref="ModelHttpSender"/>). Returns the answer, also a refusal;
    /// throws <see cref="ModelServerUnavailableException"/> when the server is away and
    /// <see cref="ModelCallTimedOutException"/> when only this request runs out of time.
    /// </summary>
    private Task<HttpResponseMessage> PostChatAsync(object requestBody, string what, CancellationToken ct) =>
        _sender.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, BuildUri("api/chat")) { Content = JsonContent.Create(requestBody) },
            CallTimeout, RetryPolicy, IsAvailableAsync, what, ct);

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

        // Marked by a rule before the call; the model may add markers in its answer (checked below).
        var marked = SensitiveOutputGuard.IsSensitive(file);

        try
        {
            var prompt = BuildClassificationMessages(input, _options);
            _traffic.LogDebug("[Phase2] PROMPT for '{File}' (depth={Depth}, images={ImgCount}, textLen={TextLen}):\n{Prompt}",
                file.Name, input.Depth, input.Base64Images.Count, input.ExtractedText?.Length ?? 0,
                marked ? ModelTraffic.Withheld : Trunc(prompt.User, 8000));

            var requestBody = new
            {
                model = _options.Model,
                stream = false,
                think = false,
                keep_alive = _options.KeepAlive,
                format = BuildClassificationSchema(input.AskKeepHint),
                messages = new object[]
                {
                    new { role = "system", content = prompt.System },
                    new { role = "user", content = prompt.User, images = input.Base64Images.Count > 0 ? input.Base64Images : null }
                }
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var response = await PostChatAsync(requestBody, "Phase 2 call", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                var errorMessage = TryExtractOllamaError(errorBody)
                    ?? $"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}";

                result.Error = errorMessage;
                result.RawResponse = errorBody;
                // The body can echo the prompt: only the status on the regular logger.
                _logger.LogWarning("[Phase2] HTTP {Status} for '{File}' after {Elapsed:F1}s; the error is stored with the file",
                    (int)response.StatusCode, file.Name, sw.Elapsed.TotalSeconds);
                _traffic.LogDebug("[Phase2] HTTP {Status} for '{File}', body: {Body}",
                    (int)response.StatusCode, file.Name, marked ? ModelTraffic.Withheld : Trunc(errorBody, 2000));
                return result;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            result.RawResponse = json;

            ParseResponse(json, result);
            marked |= result.SensitiveMarkers != SensitiveMarker.None;
            _logger.Log(input.Depth == EvidenceDepth.Extended ? LogLevel.Debug : LogLevel.Information,
                "[Phase2] '{File}': {Category} ({Confidence:P0}) in {Elapsed:F1}s",
                file.Name, result.Category, result.Confidence, sw.Elapsed.TotalSeconds);
            _traffic.LogDebug("[Phase2] ANSWER for '{File}': {Raw}",
                file.Name, marked ? ModelTraffic.Withheld : Trunc(json, 4000));
        }
        catch (ModelCallTimedOutException ex)
        {
            result.Error = $"Ollama request timed out after {_options.TimeoutSeconds}s.";
            _logger.LogWarning("[Phase2] TIMEOUT ({Seconds}s, {Attempts} attempts) for '{File}'; the server answers, so the error is stored with the file",
                _options.TimeoutSeconds, ex.Attempts, file.Name);
        }
        catch (Exception ex) when (ex is not ModelServerUnavailableException and not OperationCanceledException)
        {
            result.Error = ex.Message;
            _logger.LogError(ex, "[Phase2] Unexpected error for '{File}'", file.Name);
        }

        return result;
    }

    /// <summary>
    /// Short hash over everything that shapes what the model is asked: system prompt, summary
    /// language, the prompt templates (built for a fixed sample input) and their answer
    /// schemas. Stored with every evaluation run, so two runs show whether the prompts differed.
    /// </summary>
    public static string PromptFingerprint(OllamaOptions options)
    {
        var file = new FileNode
        {
            Name = "sample.pdf", Extension = ".pdf", FileType = FileType.Document, Size = 1,
            LastWriteTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), RelativeDir = "sample"
        };
        var dir = new DirectoryNode { Name = "sample", RelativePath = "sample" };
        var messages = new[]
        {
            BuildClassificationMessages(new OllamaClassificationInput { File = file, ExtractedText = "sample" }, options),
            BuildClassificationMessages(new OllamaClassificationInput { File = file, ExtractedText = "sample", AskKeepHint = true }, options),
            BuildPreAssessmentMessages(new DirectoryPreAssessmentInput
            {
                Directory = dir,
                DirectFiles = [new DirectoryFileInfo { Name = "sample.pdf", Size = 1 }],
                SubdirectoryNames = ["sub"]
            }, options.SummaryLanguage),
            BuildSummaryMessages(new DirectorySummaryInput
            {
                Directory = dir,
                PreAssessment = new DirectoryClassificationResult(),
                FileSummaryLines = ["sample.pdf"],
                AnomalyDescriptions = ["sample.pdf: sample"]
            })
        };
        var parts = new List<string> { options.SystemPrompt, options.SummaryLanguage };
        foreach (var m in messages) parts.AddRange([m.System, m.User]);
        parts.AddRange(
        [
            JsonSerializer.Serialize(BuildClassificationSchema(askKeepHint: false)),
            JsonSerializer.Serialize(BuildClassificationSchema(askKeepHint: true)),
            JsonSerializer.Serialize(BuildPreAssessmentSchema()),
            JsonSerializer.Serialize(BuildSummarySchema())
        ]);
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u0000", parts)));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// The two messages of a Phase 2 call (also the second check): the system message holds everything that is the
    /// same for every file (system prompt, guidance, markers, language, then the keep hint block when asked), the user
    /// message only what belongs to this file. The server can then reuse its prompt cache for the fixed part, and
    /// attached images, which the server puts at the start of the user message, do not break it.
    /// </summary>
    public static ChatPrompt BuildClassificationMessages(OllamaClassificationInput input, OllamaOptions options) =>
        new(options.SystemPrompt.TrimEnd() + "\n\n" + BuildClassificationInstructions(input.AskKeepHint, options.SummaryLanguage),
            BuildClassificationFilePart(input));

    /// <summary>The whole Phase 2 text after the system prompt: the fixed instructions, then the part about the file.</summary>
    public static string BuildClassificationPrompt(OllamaClassificationInput input, string summaryLanguage = "English") =>
        BuildClassificationInstructions(input.AskKeepHint, summaryLanguage) + "\n" + BuildClassificationFilePart(input);

    /// <summary>
    /// The Phase 2 part about one file: identity, facts read without the model (marked as reliable),
    /// what the evidence contains and what is missing, the extracted text and, for a second check, the directory.
    /// </summary>
    public static string BuildClassificationFilePart(OllamaClassificationInput input)
    {
        var file = input.File;
        var sb = new StringBuilder();
        sb.AppendLine("The file to classify:");
        sb.AppendLine($"File name: {file.Name}");
        sb.AppendLine($"Extension: {file.Extension}");
        sb.AppendLine($"File type: {file.FileType}");
        sb.AppendLine($"Size (bytes): {file.Size}");
        sb.AppendLine($"Last modified: {file.LastWriteTime:yyyy-MM-dd}");
        sb.AppendLine($"Directory: {file.RelativeDir}");
        sb.AppendLine($"Attached images: {input.Base64Images.Count}");
        sb.AppendLine();

        if (input.Facts.Count > 0)
        {
            sb.AppendLine("Facts read from the file without a model (reliable; prefer them over your own impression):");
            foreach (var fact in input.Facts)
                sb.AppendLine($"  {fact.Key.ToCode()}: {fact.Value} ({fact.Source.ToCode()})");
            sb.AppendLine();
        }

        if (input.Contains.Count > 0)
        {
            sb.AppendLine("The evidence contains:");
            foreach (var item in input.Contains)
                sb.AppendLine($"  - {item}");
            sb.AppendLine();
        }

        if (input.Missing.Count > 0)
        {
            sb.AppendLine("Not available (do not guess these; say so in the summary if they matter):");
            foreach (var item in input.Missing)
                sb.AppendLine($"  - {item}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(input.ExtractedText))
        {
            var maxText = input.Depth == EvidenceDepth.Extended ? 24_000 : 8_000;
            sb.AppendLine("Extracted text / preview content:");
            sb.AppendLine(input.ExtractedText.Length > maxText
                ? input.ExtractedText[..maxText]
                : input.ExtractedText);
            sb.AppendLine();
        }

        if (input.Context is { } context)
            AppendContext(sb, context);

        return sb.ToString();
    }

    /// <summary>
    /// The fixed Phase 2 instructions, the same text for every file of a run: task, categories, markers, summary
    /// language, then the keep hint block (only when asked, so it comes last).
    /// </summary>
    public static string BuildClassificationInstructions(bool askKeepHint, string summaryLanguage = "English")
    {
        var sb = new StringBuilder();
        sb.AppendLine("Classify the file described in the user message. Choose the most accurate category and importance.");
        sb.AppendLine();

        // The kinds of documents, one category each (https://github.com/crack666/smart_file_organizer/issues/50;
        // the same wording for people is in docs/glossary.md). The category says what the paper is, not how
        // important or how private it is: that is the importance and the sensitive markers below.
        sb.AppendLine("Classification guidance:");
        sb.AppendLine("- Family or personal photos and videos: Photo or Video, importance High");
        sb.AppendLine("- DocumentScan: a picture of a paper page (a scan, or a PDF of page images without text), whatever the paper says; importance Medium or High");
        sb.AppendLine("- Invoice: a bill or receipt for goods or services (electricity, internet, a craftsman, a shop, a garage); importance Medium");
        sb.AppendLine("- Letter: correspondence written to or from someone: a cancellation, an objection, an application, a complaint, a personal letter; importance Medium, a personal letter High");
        sb.AppendLine("- PersonalDocument: a record about the person that is not correspondence: CV, certificate, contract (rental, employment, loan), tax papers, bank and pay statements, medical findings and doctor letters; importance High or Critical");
        sb.AppendLine("- Document: any other text: a manual, an article, notes, a list, a table, a form; importance by content");
        sb.AppendLine("- Software installers, update packages, build artifacts: SoftwareInstaller or SystemFile, importance Low");
        sb.AppendLine("- Temporary files, logs, crash dumps, cache: TrashCandidate, importance Low");
        sb.AppendLine("- Images: image_origin camera = Photo, screenshot = Screenshot, scan = DocumentScan");
        sb.AppendLine("- Own speech recordings (audio_kind speech: voice memos, interviews): Audio, importance High; music: Audio, importance Medium");
        sb.AppendLine("- Archives: judge by the listing; photos or personal documents inside: Archive, importance High; programs or installers: SoftwareInstaller, importance Low");
        sb.AppendLine("- Anything personal or user-created but hard to categorize: ReviewNeeded");
        sb.AppendLine();
        sb.AppendLine("Do NOT use TrashCandidate for personal documents, even if they appear old or superseded.");
        sb.AppendLine("Use ReviewNeeded if you are uncertain — do not guess TrashCandidate.");
        sb.AppendLine();
        // Where each marker ends, with examples on both sides (issue 50: three models marked the same ordinary
        // invoices as finance and the same letters to companies as private messages). A false alarm costs rework
        // and breaks folder verdicts; a missed marker can let private content out. docs/glossary.md says the same.
        sb.AppendLine("Sensitive content: list every marker that applies in the sensitive field (empty list if none).");
        sb.AppendLine("A marker means the content would expose the person if it got out; mark by what the file shows, not by its topic:");
        sb.AppendLine($"- {SensitiveMarkerNames.Credentials}: passwords, password lists, login data, recovery codes, API keys or tokens");
        sb.AppendLine($"- {SensitiveMarkerNames.Keys}: private keys, certificates with private key, crypto wallets or seed phrases");
        sb.AppendLine($"- {SensitiveMarkerNames.PrivateMessage}: personal messages between people who know each other: personal letters, chats and chat exports, private e-mails, mail archives. " +
                      "Not: a formal letter to or from a company, an authority or a landlord (a cancellation, an objection, an application, a complaint), a newsletter, an order confirmation");
        sb.AppendLine($"- {SensitiveMarkerNames.Identity}: identity card, passport, driving licence or a scan of one");
        sb.AppendLine($"- {SensitiveMarkerNames.Finance}: papers that show the person's money, income or debts: bank and credit card statements, tax returns and tax certificates, payslips, depot and loan papers, card numbers. " +
                      "Not: an ordinary invoice or receipt for something bought, a rental or other contract, a price list or a table of amounts without account, income or debt");
        sb.AppendLine($"- {SensitiveMarkerNames.Health}: medical findings, doctor letters, health insurance documents");
        sb.AppendLine("If any marker applies: never use TrashCandidate, and keep the summary neutral (what kind of document it is),");
        sb.AppendLine("without quoting passwords, keys, numbers, names or message text.");
        sb.AppendLine();
        sb.AppendLine("summary: one short sentence saying what the file is.");
        if (!string.IsNullOrWhiteSpace(summaryLanguage) &&
            !string.Equals(summaryLanguage, "English", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"Write the summary field in: {summaryLanguage}.");
        }

        sb.AppendLine("Return only JSON matching the provided schema.");
        if (askKeepHint)
        {
            sb.AppendLine();
            sb.AppendLine("Keep hint (field keep_hint; only for temporary also a short keep_reason, for worth_keeping leave keep_reason out):");
            sb.AppendLine("- temporary: installer, setup or update package, download remainder, partial download, duplicate or copy of another file, cache or log");
            sb.AppendLine("- worth_keeping: personal, created by the user, or hard to get again");
            sb.AppendLine("If unsure, use worth_keeping. Never temporary for a file with a sensitive marker.");
        }
        return sb.ToString();
    }

    /// <summary>The two messages of one call: the fixed part of the phase, then the part about one file or directory.</summary>
    public sealed record ChatPrompt(string System, string User);

    /// <summary>
    /// The directory around the file, for the second call of a check: verdict, purpose, retention and other
    /// files with their category. Background only; the prompt says the file's own content decides.
    /// </summary>
    private static void AppendContext(StringBuilder sb, ClassificationContext context)
    {
        sb.AppendLine("Context: the directory this file is in (background only; the file's own content decides):");
        if (context.Verdict is not null) sb.AppendLine($"  Directory verdict: {context.Verdict}");
        if (!string.IsNullOrWhiteSpace(context.Purpose)) sb.AppendLine($"  Purpose: {context.Purpose}");
        if (context.Retention is not null) sb.AppendLine($"  Retention: {context.Retention}");
        sb.AppendLine($"  Files in the directory: {context.FilesInDirectory}");
        if (context.Neighbours.Count > 0)
        {
            sb.AppendLine("  Other files (name | category given so far):");
            foreach (var n in context.Neighbours)
                sb.AppendLine($"    {n.Name} | {n.Category ?? "not judged yet"}");
        }
        sb.AppendLine();
    }

    /// <summary>Upper bound of the <c>summary</c> field in characters; the instructions ask for one short sentence.</summary>
    public const int SummaryMaxLength = 200;

    /// <summary>Upper bound of the <c>keep_reason</c> field in characters.</summary>
    public const int KeepReasonMaxLength = 120;

    /// <summary>
    /// JSON schema of the classification answer; with <paramref name="askKeepHint"/> also the keep hint and,
    /// optional, the reason (asked for <c>temporary</c> only). No target: targets come from the library match.
    /// </summary>
    public static object BuildClassificationSchema(bool askKeepHint)
    {
        var properties = new Dictionary<string, object>
        {
            ["category"] = new
            {
                type = "string",
                @enum = new[]
                {
                    "Photo", "DocumentScan", "Screenshot", "Video", "Audio", "Document",
                    "Invoice", "PersonalDocument", "Letter", "Archive", "Code",
                    "SoftwareInstaller", "SystemFile", "TrashCandidate", "ReviewNeeded", "Unknown"
                }
            },
            ["importance"] = new
            {
                type = "string",
                @enum = new[] { "Low", "Medium", "High", "Critical", "Unknown" }
            },
            ["confidence"] = new { type = "number" },
            ["summary"] = new { type = "string", maxLength = SummaryMaxLength },
            ["sensitive"] = new
            {
                type = "array",
                items = new { type = "string", @enum = SensitiveMarkerNames.AllTokens }
            }
        };
        var required = new List<string> { "category", "importance", "confidence", "summary", "sensitive" };

        if (askKeepHint)
        {
            properties["keep_hint"] = new { type = "string", @enum = VerdictCodes.All<KeepHint>().ToArray() };
            // Not required: the instructions ask for it only with temporary.
            properties["keep_reason"] = new { type = "string", maxLength = KeepReasonMaxLength };
            required.Add("keep_hint");
        }

        return new { type = "object", properties, required };
    }

    /// <summary>Reads an <c>/api/chat</c> answer of the classification call into <paramref name="result"/>; malformed parts are left at their defaults.</summary>
    public static void ParseResponse(string json, FileClassification result)
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

            // Until a confidence is read, there is none.
            result.ConfidenceMissing = true;

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

            // A missing confidence is not assumed (until 12.11.2026 it became 0.85): the result counts as
            // uncertain and gets a second check (https://github.com/crack666/smart_file_organizer/issues/23).
            if (inner.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number)
            {
                result.Confidence = Math.Clamp(conf.GetDouble(), 0, 1);
                result.ConfidenceMissing = false;
            }
            else
            {
                result.Confidence = 0;
                result.ConfidenceMissing = true;
            }

            if (inner.TryGetProperty("summary", out var summary))
                result.Summary = summary.GetString();

            // No longer asked for since 13.11.2026; still read so that stored older answers keep their target.
            if (inner.TryGetProperty("suggested_target", out var target))
                result.SuggestedTarget = target.GetString();

            result.SensitiveMarkers = ParseSensitiveMarkers(inner);

            // Keep hint: only the fixed codes count; anything else stays unknown rather than guessed.
            if (inner.TryGetProperty("keep_hint", out var keep) && keep.ValueKind == JsonValueKind.String
                && VerdictCodes.TryParse<KeepHint>(keep.GetString(), out var hint))
            {
                result.KeepHint = hint;
                if (inner.TryGetProperty("keep_reason", out var keepReason) && keepReason.ValueKind == JsonValueKind.String)
                    result.KeepReason = keepReason.GetString();
            }
        }
        catch
        {
            // Malformed response — leave defaults, caller will see empty fields
        }
    }

    /// <summary>
    /// Reads the <c>sensitive</c> field: an array of tokens as the schema asks, or a single
    /// comma-separated string, which some models return instead. Unknown tokens are ignored.
    /// </summary>
    private static SensitiveMarker ParseSensitiveMarkers(JsonElement inner)
    {
        JsonElement prop = default;
        var found = false;
        foreach (var name in new[] { "sensitive", "sensitive_markers", "sensitivity" })
        {
            if (inner.TryGetProperty(name, out prop)) { found = true; break; }
        }
        if (!found) return SensitiveMarker.None;

        var markers = SensitiveMarker.None;
        switch (prop.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in prop.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        markers |= SensitiveMarkerNames.ParseAnswer(item.GetString());
                }
                break;
            case JsonValueKind.String:
                markers = SensitiveMarkerNames.ParseAnswer(prop.GetString());
                break;
        }
        return markers;
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

    public async Task<DirectoryPreAssessmentOutcome> PreAssessDirectoryAsync(
        DirectoryPreAssessmentInput input, CancellationToken ct = default)
    {
        var result = new DirectoryClassificationResult
        {
            DirectoryNodeId = input.Directory.Id,
            Phase = "pre_assessment",
            AnalyzedAt = DateTime.UtcNow,
            ModelUsed = _options.Model
        };

        DirectoryPreAssessmentOutcome Failed(string error, string? raw = null)
        {
            result.Error = error;
            return new DirectoryPreAssessmentOutcome(result, new DirectoryVerdict
            {
                DirectoryNodeId = input.Directory.Id,
                Source = VerdictSource.Model,
                ModelUsed = _options.Model,
                RawResponse = raw,
                JudgedAt = result.AnalyzedAt,
                Error = error
            });
        }

        try
        {
            var prompt = BuildPreAssessmentMessages(input, _options.SummaryLanguage);
            // Phase 1 sees names and the profile only, no file content; still a prompt, so not on the regular logger.
            _traffic.LogDebug("[Phase1] PROMPT for '{Dir}' ({Files} files, {Subdirs} subdirs, {Len} chars):\n{Prompt}",
                input.Directory.RelativePath, input.DirectFiles.Count, input.SubdirectoryNames.Count,
                prompt.User.Length, Trunc(prompt.User, 8000));

            var requestBody = new
            {
                model = _options.Model,
                stream = false,
                think = false,
                keep_alive = _options.KeepAlive,
                format = BuildPreAssessmentSchema(),
                messages = new object[]
                {
                    new { role = "system", content = prompt.System },
                    new { role = "user", content = prompt.User }
                }
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var response = await PostChatAsync(requestBody, "Phase 1 call", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                var error = TryExtractOllamaError(errorBody) ?? $"HTTP {(int)response.StatusCode}";
                _logger.LogWarning(
                    "[Phase1] HTTP {Status} for '{Dir}' after {Elapsed:F1}s; the error is stored with the directory",
                    (int)response.StatusCode, input.Directory.RelativePath, sw.Elapsed.TotalSeconds);
                _traffic.LogDebug("[Phase1] HTTP {Status} for '{Dir}', body: {Body}",
                    (int)response.StatusCode, input.Directory.RelativePath, Trunc(errorBody, 2000));
                return Failed(error, errorBody);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var outcome = PreAssessmentResponseParser.Parse(json, _options.Model);
            outcome.Assessment.DirectoryNodeId = input.Directory.Id;
            outcome.Verdict.DirectoryNodeId = input.Directory.Id;
            _logger.LogInformation(
                "[Phase1] '{Dir}' in {Elapsed:F1}s — verdict={Verdict}, retention={Retention}, strategy={Strat}, sample={Sample}, anomalies={Anom}{Err}",
                input.Directory.RelativePath, sw.Elapsed.TotalSeconds,
                outcome.Verdict.Verdict.ToCode(), outcome.Verdict.Retention.ToCode(),
                outcome.Assessment.SamplingStrategy, outcome.Assessment.SampleSize,
                CountJsonArray(outcome.Assessment.AnomalousFileIds),
                outcome.Verdict.Error is null ? string.Empty : ", error stored");
            _traffic.LogDebug("[Phase1] ANSWER for '{Dir}': {Raw}", input.Directory.RelativePath, Trunc(json, 4000));
            return outcome;
        }
        catch (ModelCallTimedOutException ex)
        {
            _logger.LogWarning("[Phase1] TIMEOUT ({Seconds}s, {Attempts} attempts) for '{Dir}'",
                _options.TimeoutSeconds, ex.Attempts, input.Directory.RelativePath);
            return Failed($"Ollama request timed out after {_options.TimeoutSeconds}s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ModelServerUnavailableException)
        {
            _logger.LogError(ex, "[Phase1] Unexpected error for '{Dir}'", input.Directory.RelativePath);
            return Failed(ex.Message);
        }
    }

    /// <summary>
    /// The two messages of a Phase 1 call: the fixed task as system message (the same for every directory, so the
    /// server can reuse its prompt cache), the directory with its profile and listing as user message.
    /// </summary>
    public static ChatPrompt BuildPreAssessmentMessages(DirectoryPreAssessmentInput input, string summaryLanguage = "English") =>
        new(BuildPreAssessmentInstructions(summaryLanguage), BuildPreAssessmentDirectoryPart(input));

    /// <summary>The whole Phase 1 text: the fixed task, then the directory.</summary>
    public static string BuildPreAssessmentPrompt(DirectoryPreAssessmentInput input, string summaryLanguage = "English")
    {
        var m = BuildPreAssessmentMessages(input, summaryLanguage);
        return m.System + "\n" + m.User;
    }

    /// <summary>The fixed Phase 1 task, the same text for every directory of a run.</summary>
    public static string BuildPreAssessmentInstructions(string summaryLanguage = "English")
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are judging a directory before files are moved: what lies here, and can it be handled as a whole?");
        sb.AppendLine("You also plan targeted file inspection. The directory is described in the user message.");
        sb.AppendLine();
        sb.AppendLine("Tasks:");
        sb.AppendLine("1. Judge the directory as a whole (field verdict), using its profile:");
        sb.AppendLine($"   {DirectoryVerdictKind.Unit.ToCode()} = project, program, game or backup that only makes sense as a whole; never split into single files");
        sb.AppendLine($"   {DirectoryVerdictKind.Uniform.ToCode()} = collection of similar content with one purpose (photos of one trip, invoices of one year)");
        sb.AppendLine($"   {DirectoryVerdictKind.Mixed.ToCode()} = storage without a common purpose (desktop, downloads, misc); files are handled one by one");
        sb.AppendLine($"   {DirectoryVerdictKind.Empty.ToCode()} = nothing worth looking at");
        sb.AppendLine($"   {DirectoryVerdictKind.System.ToCode()} = operating system, application or cache data, not user content");
        sb.AppendLine("2. purpose: what the directory is for, in one sentence.");
        sb.AppendLine($"3. retention: {RetentionValue.Keep.ToCode()} = personal or hard to replace; {RetentionValue.Review.ToCode()} = unclear, a human should look;");
        sb.AppendLine($"   {RetentionValue.Disposable.ToCode()} = can be recreated or downloaded again (installers, caches, temporary files).");
        sb.AppendLine("4. reason: one or two sentences why, naming the facts from the profile you relied on.");
        sb.AppendLine("5. Assess homogeneity: high=all same type/theme, medium=mostly similar, low=very mixed.");
        sb.AppendLine("6. Identify anomalous files: different type from the majority, or dramatically larger/smaller than peers.");
        sb.AppendLine("7. Choose a sampling strategy for deep vision/text analysis:");
        sb.AppendLine("   analyze_all = ≤10 files or content is too mixed to sample");
        sb.AppendLine("   random_sample = pick a representative subset (use this for large homogeneous dirs)");
        sb.AppendLine("   skip = only dir name/path are enough to classify (e.g. a clearly-named system folder)");
        sb.AppendLine("8. Suggest how many files to analyze (0 for skip, all count for analyze_all).");
        sb.AppendLine();
        sb.AppendLine("Use the codes exactly as written for verdict and retention.");
        if (!string.IsNullOrWhiteSpace(summaryLanguage) &&
            !string.Equals(summaryLanguage, "English", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"Write the purpose and reason fields in: {summaryLanguage}.");
        }
        sb.AppendLine("Return only JSON matching the provided schema.");
        return sb.ToString();
    }

    /// <summary>The Phase 1 part about one directory: identity, profile, subdirectories and files.</summary>
    public static string BuildPreAssessmentDirectoryPart(DirectoryPreAssessmentInput input)
    {
        var dir = input.Directory;
        var profile = input.Profile;
        var sb = new StringBuilder();
        sb.AppendLine($"Directory: {dir.Name}");
        sb.AppendLine($"Relative path: {dir.RelativePath}");
        sb.AppendLine($"Total direct files: {input.DirectFiles.Count}");
        sb.AppendLine($"Subdirectories: {input.SubdirectoryNames.Count}");

        AppendProfile(sb, profile);

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
        return sb.ToString();
    }

    private static void AppendProfile(StringBuilder sb, DirectoryProfile profile)
    {
        sb.AppendLine();
        sb.AppendLine("Profile (computed from scan data, reliable):");
        sb.AppendLine($"  Direct files: {profile.FileCount}, {profile.TotalBytes} bytes");
        if (profile.FileTypes.Count > 0)
            sb.AppendLine($"  By file type: {string.Join(", ", profile.FileTypes.Select(b => $"{b.Key} {b.Count} ({b.Bytes} bytes)"))}");
        if (profile.Extensions.Count > 0)
        {
            var shown = profile.Extensions.Take(12)
                .Select(b => $"{(b.Key.Length == 0 ? "(none)" : b.Key)} {b.Count}");
            var more = profile.Extensions.Count > 12 ? $", +{profile.Extensions.Count - 12} more" : string.Empty;
            sb.AppendLine($"  By extension: {string.Join(", ", shown)}{more}");
        }
        if (profile.OldestWrite is { } oldest && profile.NewestWrite is { } newest)
            sb.AppendLine($"  Last modified: {oldest:yyyy-MM-dd} to {newest:yyyy-MM-dd} ({profile.SpanDays} days)");
        if (profile.NamePatterns.Count > 0)
            sb.AppendLine($"  Name patterns: {string.Join(", ", profile.NamePatterns.Select(p => $"{p.Pattern} {p.Count}"))}");
        if (profile.UnitMarkers.Count > 0)
            sb.AppendLine($"  Unit markers: {string.Join(", ", profile.UnitMarkers)}");
        if (profile.Suggests is { } suggests)
            sb.AppendLine($"  Hint: the profile points to '{suggests.ToCode()}'. {profile.SuggestionReason}");
    }

    private static object BuildPreAssessmentSchema() => new
    {
        type = "object",
        properties = new
        {
            // "dump" is decided by a rule only (DirectoryVerdictKinds.IsRuleOnly); the model is not offered it.
            verdict = new { type = "string", @enum = DirectoryVerdictKinds.ModelCodes.ToArray() },
            purpose = new { type = "string" },
            retention = new { type = "string", @enum = VerdictCodes.All<RetentionValue>().ToArray() },
            reason = new { type = "string" },
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
        required = new[]
        {
            "verdict", "purpose", "retention", "reason",
            "homogeneity", "dominant_type", "theme", "anomalous_files", "sampling_strategy", "suggested_sample_size"
        }
    };

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
            var prompt = BuildSummaryMessages(input);
            // Marked files enter this prompt only as their neutral line (DirectorySummaryService), so the
            // local log may hold it.
            _traffic.LogDebug("[Phase3] PROMPT for '{Dir}' ({Len} chars):\n{Prompt}",
                input.Directory.RelativePath, prompt.User.Length, Trunc(prompt.User, 8000));
            var requestBody = new
            {
                model = _options.Model,
                stream = false,
                think = false,
                keep_alive = _options.KeepAlive,
                format = BuildSummarySchema(),
                messages = new object[]
                {
                    new { role = "system", content = prompt.System },
                    new { role = "user", content = prompt.User }
                }
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var response = await PostChatAsync(requestBody, "Phase 3 call", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                result.Error = TryExtractOllamaError(errorBody) ?? $"HTTP {(int)response.StatusCode}";
                _logger.LogWarning(
                    "[Phase3] Ollama returned {Status} for '{Dir}' after {Elapsed:F1}s; the error is stored with the directory",
                    (int)response.StatusCode, input.Directory.RelativePath, sw.Elapsed.TotalSeconds);
                _traffic.LogDebug("[Phase3] HTTP {Status} for '{Dir}', body: {Body}",
                    (int)response.StatusCode, input.Directory.RelativePath, Trunc(errorBody, 2000));
                return result;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            ParseSummaryResponse(json, result);

            _logger.LogInformation("[Phase3] '{Dir}' summarized in {Elapsed:F1}s",
                input.Directory.RelativePath, sw.Elapsed.TotalSeconds);
            _traffic.LogDebug("[Phase3] ANSWER for '{Dir}': {Raw}", input.Directory.RelativePath, Trunc(json, 4000));
        }
        catch (ModelCallTimedOutException ex)
        {
            result.Error = $"Ollama request timed out after {_options.TimeoutSeconds}s.";
            _logger.LogWarning("[Phase3] TIMEOUT ({Seconds}s, {Attempts} attempts) for '{Dir}'",
                _options.TimeoutSeconds, ex.Attempts, input.Directory.RelativePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ModelServerUnavailableException)
        {
            result.Error = ex.Message;
            _logger.LogError(ex, "[Phase3] Unexpected error for '{Dir}'", input.Directory.RelativePath);
        }

        return result;
    }

    /// <summary>The fixed Phase 3 task, the same text for every directory of a run.</summary>
    private const string SummaryInstructions =
        "You are writing a final summary for a directory based on AI analysis of a sample of its files.\n" +
        "The directory is described in the user message.\n" +
        "Write a short summary (1-2 sentences) describing what this directory contains.\n" +
        "Confirm which anomalies are real outliers, if any.\n" +
        "Return only JSON matching the provided schema.\n";

    /// <summary>The two messages of a Phase 3 call: the fixed task as system message, the directory as user message.</summary>
    public static ChatPrompt BuildSummaryMessages(DirectorySummaryInput input) => new(SummaryInstructions, BuildSummaryDirectoryPart(input));

    private static string BuildSummaryDirectoryPart(DirectorySummaryInput input)
    {
        var dir = input.Directory;
        var pre = input.PreAssessment;
        var sb = new StringBuilder();
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
