using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Infrastructure.ModelServer;

/// <summary>The two model connections (https://github.com/crack666/smart_file_organizer/issues/53).</summary>
public static class ModelProviders
{
    public const string Ollama = "ollama";
    public const string OpenAiCompatible = "openai_compatible";

    public static bool IsOpenAi(string? provider) =>
        string.Equals(provider, OpenAiCompatible, StringComparison.OrdinalIgnoreCase);

    /// <summary><see cref="Ollama"/> or <see cref="OpenAiCompatible"/>; anything else is a settings error.</summary>
    public static string Canonical(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.Equals(provider, Ollama, StringComparison.OrdinalIgnoreCase))
            return Ollama;
        if (IsOpenAi(provider))
            return OpenAiCompatible;
        throw new ModelSettingsException(
            $"Unknown model provider '{provider}'. Use {Ollama} or {OpenAiCompatible}.");
    }
}

/// <summary>A model setting the program cannot use (unknown provider or profile).</summary>
public sealed class ModelSettingsException(string message) : Exception(message);

/// <summary>One chat message. Images are raw base64; the wire format decides how they are sent.</summary>
public sealed record ModelChatMessage(string Role, string Content, IReadOnlyList<string>? Images = null);

/// <summary>One chat call, independent of Ollama or an OpenAI-compatible broker.</summary>
public sealed record ModelChatRequest
{
    public required string Model { get; init; }
    public IReadOnlyList<ModelChatMessage> Messages { get; init; } = [];
    public object? Schema { get; init; }
    public int MaxTokens { get; init; } = 256;

    /// <summary>Load the model. Ollama gets an empty message list; an OpenAI broker gets one short user message.</summary>
    public bool Warmup { get; init; }
}

/// <summary>
/// Builds and reads the HTTP of one model call. Ollama keeps <c>/api/chat</c> and <c>/api/tags</c>.
/// An OpenAI-compatible broker uses <c>/v1/chat/completions</c> and <c>/v1/models</c>. Answers are rewritten
/// into the Ollama envelope so the parsers stay on one shape. Knows no prompts.
/// </summary>
public static class ModelWire
{
    /// <summary>The short prompt <c>sfo doctor</c> uses to see whether several calls run at once.</summary>
    public const string ParallelProbePrompt = "Reply with the word ok.";

    private const int ImageTokens = 4000;

    public static bool OpenAi(OllamaOptions options) => ModelProviders.IsOpenAi(options.Provider);

    public static string ProbePath(OllamaOptions options) =>
        OpenAi(options) ? (BaseHasV1(options) ? "models" : "v1/models") : "api/tags";

    public static string ChatPath(OllamaOptions options) =>
        OpenAi(options) ? (BaseHasV1(options) ? "chat/completions" : "v1/chat/completions") : "api/chat";

    /// <summary>
    /// Shortens the last message until the estimate fits <see cref="OllamaOptions.NumCtx"/>, and says so in the text.
    /// Without a context size the server's own default applies and nothing is cut here.
    /// </summary>
    public static ModelChatRequest Fit(OllamaOptions options, ModelChatRequest request)
    {
        if (options.NumCtx is not int ctx || ctx <= 0 || request.Messages.Count == 0)
            return request;

        var budget = ctx - request.MaxTokens - 512;
        if (budget < 256)
            budget = 256;
        if (request.Messages.Sum(Tokens) <= budget)
            return request;

        var last = request.Messages[^1];
        var others = request.Messages.Take(request.Messages.Count - 1).Sum(Tokens);
        var imageCost = (last.Images?.Count ?? 0) * ImageTokens;
        var allowChars = Math.Max(200, (budget - others - imageCost) * 4);
        if (last.Content.Length <= allowChars)
            return request;

        var note = $"\n[truncated to fit a context of {ctx} tokens; the middle of this text was omitted]";
        var keep = Math.Max(80, allowChars - note.Length);
        var head = keep / 2;
        var tail = keep - head;
        var shortened = last.Content[..head] + note + last.Content[^tail..];
        var messages = request.Messages.ToArray();
        messages[^1] = last with { Content = shortened };
        return request with { Messages = messages };
    }

    public static HttpRequestMessage Create(OllamaOptions options, ModelChatRequest request, Uri uri)
    {
        var root = OpenAi(options) ? OpenAiBody(options, request) : OllamaBody(options, request);
        var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(root.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        var key = options.ResolveApiKey();
        if (!string.IsNullOrEmpty(key))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return message;
    }

    /// <summary>
    /// Rewrites an OpenAI chat answer into the Ollama envelope (<c>message.content</c>, <c>done_reason</c>, token counts).
    /// An Ollama answer is returned unchanged. A body that is not JSON is returned unchanged.
    /// </summary>
    public static string NormalizeAnswer(string? provider, string body)
    {
        if (!ModelProviders.IsOpenAi(provider) || string.IsNullOrWhiteSpace(body))
            return body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out _))
                return body;

            string? content = null;
            var done = "stop";
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
                    content = text.GetString();
                if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String
                    && reason.GetString() == "length")
                    done = "length";
            }

            long prompt = 0, cached = 0, completion = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                prompt = Number(usage, "prompt_tokens");
                completion = Number(usage, "completion_tokens");
                if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                    cached = Number(details, "cached_tokens");
            }

            return JsonSerializer.Serialize(new
            {
                done = true,
                done_reason = done,
                message = new { role = "assistant", content },
                prompt_eval_count = prompt,
                prompt_eval_cached_count = cached,
                eval_count = completion,
                prompt_eval_duration = 0L,
                eval_duration = 0L
            });
        }
        catch (JsonException)
        {
            return body;
        }
    }

    public static List<OllamaLocalModel> ReadModels(string? provider, JsonElement root)
    {
        var models = new List<OllamaLocalModel>();
        if (ModelProviders.IsOpenAi(provider))
        {
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return models;
            foreach (var item in data.EnumerateArray())
            {
                models.Add(new OllamaLocalModel
                {
                    Name = item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty
                });
            }
            return models;
        }

        if (!root.TryGetProperty("models", out var list) || list.ValueKind != JsonValueKind.Array)
            return models;
        foreach (var item in list.EnumerateArray())
        {
            var details = item.TryGetProperty("details", out var d) ? d : default;
            models.Add(new OllamaLocalModel
            {
                Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                Family = Text(details, "family"),
                ParameterSize = Text(details, "parameter_size"),
                QuantizationLevel = Text(details, "quantization_level")
            });
        }
        return models;
    }

    /// <summary>
    /// How many calls ran at once, from the time of one call and of <paramref name="sent"/> calls together.
    /// A batch that is not clearly slower than one call counts as fully parallel: a fast server must not be warned about.
    /// </summary>
    public static int EstimateSlots(int sent, TimeSpan one, TimeSpan wall)
    {
        if (sent <= 1)
            return 1;
        if (one < TimeSpan.FromMilliseconds(30) || wall < one * 1.3)
            return sent;
        var slots = (int)Math.Round(sent * one.TotalSeconds / Math.Max(wall.TotalSeconds, 0.001));
        return Math.Clamp(slots, 1, sent);
    }

    /// <summary>The warning when the process asks for more parallel calls than the server answered at once; null otherwise.</summary>
    public static string? ParallelWarning(int requested, int? measured)
    {
        if (measured is not int got || got >= requested)
            return null;
        return $"The server handled about {got} request(s) at once; --parallel {requested} is higher. " +
               "On Ollama the server's own limit is OLLAMA_NUM_PARALLEL (sfo cannot raise it).";
    }

    /// <summary>The address without user or password, for a stored measurement. A key never belongs in it.</summary>
    public static string AddressWithoutSecrets(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            return url;
        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return builder.Uri.ToString().TrimEnd('/');
    }

    private static int Tokens(ModelChatMessage message) =>
        (message.Content.Length + 3) / 4 + (message.Images?.Count ?? 0) * ImageTokens;

    private static bool BaseHasV1(OllamaOptions options) =>
        options.BaseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase);

    private static JsonObject OllamaBody(OllamaOptions options, ModelChatRequest request)
    {
        var root = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = false,
            ["think"] = false,
            ["keep_alive"] = options.KeepAlive,
            ["messages"] = Messages(request, dataUrls: false)
        };
        if (request.Schema is not null)
            root["format"] = JsonSerializer.SerializeToNode(request.Schema);
        var modelOptions = new JsonObject { ["num_predict"] = request.MaxTokens };
        if (options.NumCtx is int ctx and > 0)
            modelOptions["num_ctx"] = ctx;
        if (options.Temperature is double temperature)
            modelOptions["temperature"] = temperature;
        root["options"] = modelOptions;
        AddCallerFields(root, options);
        return root;
    }

    private static JsonObject OpenAiBody(OllamaOptions options, ModelChatRequest request)
    {
        var root = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = false,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = Messages(request, dataUrls: true)
        };
        if (options.Temperature is double temperature)
            root["temperature"] = temperature;
        if (options.NumCtx is int ctx and > 0)
            root["num_ctx"] = ctx;
        if (request.Schema is not null)
        {
            root["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "answer",
                    ["strict"] = true,
                    ["schema"] = JsonSerializer.SerializeToNode(request.Schema)
                }
            };
        }
        AddCallerFields(root, options);
        return root;
    }

    private static void AddCallerFields(JsonObject root, OllamaOptions options)
    {
        if (options.Metadata is { Count: > 0 } metadata)
        {
            var node = new JsonObject();
            foreach (var (key, value) in metadata)
                node[key] = value;
            root["metadata"] = node;
        }
        if (options.ExtraBody is not { Count: > 0 } extra)
            return;
        foreach (var (key, value) in extra)
            root[key] = value is null ? null : JsonNode.Parse(value.ToJsonString());
    }

    private static JsonArray Messages(ModelChatRequest request, bool dataUrls)
    {
        var messages = request.Messages;
        if (request.Warmup && messages.Count == 0)
            messages = dataUrls ? [new ModelChatMessage("user", ".")] : messages;

        var array = new JsonArray();
        foreach (var message in messages)
        {
            var node = new JsonObject { ["role"] = message.Role };
            if (dataUrls && message.Images is { Count: > 0 } images)
            {
                var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = message.Content } };
                foreach (var image in images)
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = DataUrl(image) }
                    });
                }
                node["content"] = parts;
            }
            else
            {
                node["content"] = message.Content;
                if (!dataUrls && message.Images is { Count: > 0 } raw)
                {
                    var list = new JsonArray();
                    foreach (var image in raw)
                        list.Add(image);
                    node["images"] = list;
                }
            }
            array.Add(node);
        }
        return array;
    }

    /// <summary>A data URL. The type comes from the base64 prefix; JPEG when it is not one of the common image headers.</summary>
    public static string DataUrl(string base64)
    {
        var mime = base64.StartsWith("/9j/", StringComparison.Ordinal) ? "image/jpeg"
            : base64.StartsWith("iVBOR", StringComparison.Ordinal) ? "image/png"
            : base64.StartsWith("R0lGOD", StringComparison.Ordinal) ? "image/gif"
            : base64.StartsWith("UklGR", StringComparison.Ordinal) ? "image/webp"
            : "image/jpeg";
        return $"data:{mime};base64,{base64}";
    }

    private static long Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;

    private static string Text(JsonElement details, string name) =>
        details.ValueKind == JsonValueKind.Object && details.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
