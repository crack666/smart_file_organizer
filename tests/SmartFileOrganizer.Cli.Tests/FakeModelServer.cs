using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>How the fake model server answers one request.</summary>
public enum FakeServerMode
{
    /// <summary>Answers like Ollama.</summary>
    Up,
    /// <summary>The connection is refused, as when the server process is gone.</summary>
    Refusing,
    /// <summary>Accepts the request and never answers, as when the server hangs.</summary>
    Hanging,
    /// <summary>HTTP 503 with <c>Retry-After</c>, as when a server or a broker in front of it is busy.</summary>
    Busy,
    /// <summary>HTTP 400 with an Ollama error body: a request the server will never accept.</summary>
    BadRequest
}

/// <summary>One request the fake server saw.</summary>
/// <param name="Kind"><c>tags</c>, <c>warm</c>, <c>pre_assessment</c>, <c>classification</c>, <c>second_check</c>, <c>summary</c>.</param>
/// <param name="Name">File name (classification), relative path (pre-assessment, summary), else null.</param>
/// <param name="Fields">The fields of the answer schema the request asked for (empty without a schema).</param>
public sealed record FakeRequest(string Kind, string? Name, FakeServerMode Answer, IReadOnlySet<string>? Fields = null);

/// <summary>
/// Stand-in for an Ollama server at the HTTP level, plugged in as the primary handler of the real
/// <c>OllamaService</c>: answers <c>/api/tags</c> and <c>/api/chat</c> for all three phases, and can refuse,
/// hang, answer 503 or reject, per request through <see cref="Script"/>. Never opens a socket.
/// </summary>
public sealed class FakeModelServer : HttpMessageHandler
{
    private readonly object _sync = new();
    private readonly List<FakeRequest> _log = [];
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public const string ModelName = "fake-model";

    /// <summary>The answer to every request <see cref="Script"/> does not decide.</summary>
    public FakeServerMode Mode { get; set; } = FakeServerMode.Up;

    /// <summary>Decides per request: return null to answer with <see cref="Mode"/>. Called under no lock.</summary>
    public Func<FakeRequest, FakeServerMode?>? Script { get; set; }

    /// <summary>Seconds sent in <c>Retry-After</c> with <see cref="FakeServerMode.Busy"/>.</summary>
    public int RetryAfterSeconds { get; set; } = 1;

    /// <summary>1: each request waits for the previous one. Anything else: requests overlap.</summary>
    public int MaxConcurrent { get; set; } = int.MaxValue;

    /// <summary>Pause before each answer, so a parallelism measurement can tell a queue from overlap.</summary>
    public TimeSpan AnswerDelay { get; set; }

    /// <summary>Paths of the requests, in arrival order.</summary>
    public List<string> Paths { get; } = [];

    /// <summary>Bodies of the requests that had one, in arrival order.</summary>
    public List<string> Bodies { get; } = [];

    /// <summary>Authorization header of the latest request, or null.</summary>
    public string? LastAuthorization { get; private set; }

    /// <summary>Confidence of first looks at these files: below the second-check limit, so a second call follows.</summary>
    public HashSet<string> UnsureFiles { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<FakeRequest> Log { get { lock (_sync) return _log.ToList(); } }

    /// <summary>Description fields added to a classification answer when its schema asks for them (stage 1 of Issue 37),
    /// by file name; files not listed get <see cref="DefaultPicture"/> or <see cref="DefaultDocument"/>.</summary>
    public Dictionary<string, string> Descriptions { get; } = new(StringComparer.Ordinal);

    public string DefaultPicture { get; set; } =
        "\"scene\":\"garden\",\"activity\":\"\",\"setting\":\"outdoor\",\"season\":\"unknown\",\"occasion\":\"everyday\",\"people\":false";

    public string DefaultDocument { get; set; } = "\"document_kind\":\"notes\",\"issuer\":\"\",\"year\":0";

    /// <summary>Requests of a kind that the server answered like Ollama.</summary>
    public int Answered(string kind, string? name = null) =>
        Log.Count(r => r.Kind == kind && r.Answer == FakeServerMode.Up && (name is null || r.Name == name));

    public int Count(string kind, FakeServerMode answer) => Log.Count(r => r.Kind == kind && r.Answer == answer);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var (kind, name, fields, openAi) = await ReadAsync(request, ct);
        var probe = new FakeRequest(kind, name, Mode, fields);
        var answer = Script?.Invoke(probe) ?? Mode;
        lock (_sync) _log.Add(probe with { Answer = answer });

        var limited = MaxConcurrent == 1;
        if (limited)
            await _oneAtATime.WaitAsync(ct);
        try
        {
            if (AnswerDelay > TimeSpan.Zero)
                await Task.Delay(AnswerDelay, ct);

        switch (answer)
        {
            case FakeServerMode.Refusing:
                throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made (fake).",
                    new SocketException((int)SocketError.ConnectionRefused));
            case FakeServerMode.Hanging:
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            case FakeServerMode.Busy:
                var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("""{"error":"server busy"}""", Encoding.UTF8, "application/json")
                };
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(RetryAfterSeconds));
                return busy;
            case FakeServerMode.BadRequest:
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"invalid image data"}""", Encoding.UTF8, "application/json")
                };
        }

            return Json(kind switch
            {
                "tags" => openAi
                    ? $$$"""{"data":[{"id":"{{{ModelName}}}"}]}"""
                    : $$$"""{"models":[{"name":"{{{ModelName}}}","details":{"family":"fake"}}]}""",
                "warm" or "probe" => Envelope("{}", openAi),
                "pre_assessment" => Envelope("""{"verdict":"mixed","purpose":"test files","retention":"keep","reason":"fake","homogeneity":"low","dominant_type":"text","theme":"test","anomalous_files":[],"sampling_strategy":"analyze_all","suggested_sample_size":1}""", openAi),
                "summary" => Envelope("""{"summary":"fake summary","theme":"test","confirmed_anomalies":[]}""", openAi),
                _ => Envelope($$"""{"category":"Document","importance":"Medium","confidence":{{(kind == "classification" && name is not null && UnsureFiles.Contains(name) ? "0.3" : "0.95")}},"summary":"a text","suggested_target":"Docs","sensitive":[],"keep_hint":"worth_keeping","keep_reason":"fake"{{Describe(name, fields)}}}""", openAi)
            });
        }
        finally
        {
            if (limited)
                _oneAtATime.Release();
        }
    }

    /// <summary>The kind of video named when the schema asks for <c>video_kind</c> (Issue 103).</summary>
    public string VideoKind { get; set; } = "own_recording";

    private string Describe(string? name, IReadOnlySet<string> fields)
    {
        var kind = fields.Contains("video_kind") ? $",\"video_kind\":\"{VideoKind}\"" : string.Empty;
        if (!fields.Contains("scene") && !fields.Contains("document_kind")) return kind;
        if (name is not null && Descriptions.TryGetValue(name, out var own)) return "," + own + kind;
        return "," + (fields.Contains("scene") ? DefaultPicture : DefaultDocument) + kind;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>An <c>/api/chat</c> answer whose message content is <paramref name="content"/>.</summary>
    private static string Chat(string content) =>
        JsonSerializer.Serialize(new
        {
            model = ModelName, done = true, done_reason = "stop", message = new { role = "assistant", content },
            // The counts a real server adds to every answer: 100 prompt tokens, 60 of them cached, 20 answer tokens, 0.3 s.
            prompt_eval_count = 100, prompt_eval_cached_count = 60, eval_count = 20,
            prompt_eval_duration = 100_000_000L, eval_duration = 200_000_000L
        });

    /// <summary>The same answer in the shape of the request: Ollama, or an OpenAI chat completion.</summary>
    private static string Envelope(string content, bool openAi) => openAi
        ? JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 100, completion_tokens = 20, prompt_tokens_details = new { cached_tokens = 60 } }
        })
        : Chat(content);

    private async Task<(string Kind, string? Name, IReadOnlySet<string> Fields, bool OpenAi)> ReadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var openAi = path.Contains("chat/completions", StringComparison.Ordinal) || path.EndsWith("/models", StringComparison.Ordinal);
        LastAuthorization = request.Headers.Authorization?.ToString();
        lock (_sync) Paths.Add(path);
        if (path.EndsWith("/api/tags", StringComparison.Ordinal) || path.EndsWith("/models", StringComparison.Ordinal))
            return ("tags", null, new HashSet<string>(), openAi);

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        lock (_sync) Bodies.Add(body);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        var fields = FieldsOf(doc.RootElement);
        if (messages.GetArrayLength() == 0) return ("warm", null, fields, openAi);

        // The fixed task of a phase is the system message, the file or directory the last message.
        var task = TextOf(messages[0]);
        var prompt = TextOf(messages[messages.GetArrayLength() - 1]);
        if (prompt.StartsWith(SmartFileOrganizer.Infrastructure.ModelServer.ModelWire.ParallelProbePrompt, StringComparison.Ordinal)
            || task.StartsWith(SmartFileOrganizer.Infrastructure.ModelServer.ModelWire.ParallelProbePrompt, StringComparison.Ordinal))
            return ("probe", null, fields, openAi);
        if (prompt == ".") return ("warm", null, fields, openAi);
        if (task.StartsWith("You are judging a directory", StringComparison.Ordinal))
            return ("pre_assessment", Line(prompt, "Relative path: "), fields, openAi);
        if (task.StartsWith("You are writing a final summary", StringComparison.Ordinal))
            return ("summary", Line(prompt, "Relative path: "), fields, openAi);
        var kind = prompt.Contains("Context: the directory this file is in", StringComparison.Ordinal) ? "second_check" : "classification";
        return (kind, Line(prompt, "File name: "), fields, openAi);
    }

    private static string TextOf(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
            return string.Empty;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var piece) && piece.ValueKind == JsonValueKind.String)
                text.Append(piece.GetString());
        }
        return text.ToString();
    }

    private static HashSet<string> FieldsOf(JsonElement root)
    {
        if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object
            && format.TryGetProperty("properties", out var props))
            return props.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (root.TryGetProperty("response_format", out var responseFormat)
            && responseFormat.TryGetProperty("json_schema", out var schemaDoc)
            && schemaDoc.TryGetProperty("schema", out var schema)
            && schema.TryGetProperty("properties", out var schemaProps))
            return schemaProps.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return new HashSet<string>(StringComparer.Ordinal);
    }

    private static string? Line(string prompt, string prefix)
    {
        var m = Regex.Match(prompt, "^" + Regex.Escape(prefix) + "(.*)$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.TrimEnd('\r') : null;
    }
}
