using System.Collections.Concurrent;
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
public sealed record FakeRequest(string Kind, string? Name, FakeServerMode Answer);

/// <summary>
/// Stand-in for an Ollama server at the HTTP level, plugged in as the primary handler of the real
/// <c>OllamaService</c>: answers <c>/api/tags</c> and <c>/api/chat</c> for all three phases, and can refuse,
/// hang, answer 503 or reject, per request through <see cref="Script"/>. Never opens a socket.
/// </summary>
public sealed class FakeModelServer : HttpMessageHandler
{
    private readonly object _sync = new();
    private readonly List<FakeRequest> _log = [];

    public const string ModelName = "fake-model";

    /// <summary>The answer to every request <see cref="Script"/> does not decide.</summary>
    public FakeServerMode Mode { get; set; } = FakeServerMode.Up;

    /// <summary>Decides per request: return null to answer with <see cref="Mode"/>. Called under no lock.</summary>
    public Func<FakeRequest, FakeServerMode?>? Script { get; set; }

    /// <summary>Seconds sent in <c>Retry-After</c> with <see cref="FakeServerMode.Busy"/>.</summary>
    public int RetryAfterSeconds { get; set; } = 1;

    /// <summary>Confidence of first looks at these files: below the second-check limit, so a second call follows.</summary>
    public HashSet<string> UnsureFiles { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<FakeRequest> Log { get { lock (_sync) return _log.ToList(); } }

    /// <summary>Requests of a kind that the server answered like Ollama.</summary>
    public int Answered(string kind, string? name = null) =>
        Log.Count(r => r.Kind == kind && r.Answer == FakeServerMode.Up && (name is null || r.Name == name));

    public int Count(string kind, FakeServerMode answer) => Log.Count(r => r.Kind == kind && r.Answer == answer);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var (kind, name) = await ReadAsync(request, ct);
        var probe = new FakeRequest(kind, name, Mode);
        var answer = Script?.Invoke(probe) ?? Mode;
        lock (_sync) _log.Add(probe with { Answer = answer });

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
            "tags" => $$$"""{"models":[{"name":"{{{ModelName}}}","details":{"family":"fake"}}]}""",
            "warm" => """{"done":true}""",
            "pre_assessment" => Chat("""{"verdict":"mixed","purpose":"test files","retention":"keep","reason":"fake","homogeneity":"low","dominant_type":"text","theme":"test","anomalous_files":[],"sampling_strategy":"analyze_all","suggested_sample_size":1}"""),
            "summary" => Chat("""{"summary":"fake summary","theme":"test","confirmed_anomalies":[]}"""),
            _ => Chat($$"""{"category":"Document","importance":"Medium","confidence":{{(kind == "classification" && name is not null && UnsureFiles.Contains(name) ? "0.3" : "0.95")}},"summary":"a text","suggested_target":"Docs","sensitive":[],"keep_hint":"worth_keeping","keep_reason":"fake"}""")
        });
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>An <c>/api/chat</c> answer whose message content is <paramref name="content"/>.</summary>
    private static string Chat(string content) =>
        JsonSerializer.Serialize(new { model = ModelName, done = true, message = new { role = "assistant", content } });

    private static async Task<(string Kind, string? Name)> ReadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (path.EndsWith("/api/tags", StringComparison.Ordinal)) return ("tags", null);

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        if (messages.GetArrayLength() == 0) return ("warm", null);

        // The fixed task of a phase is the system message, the file or directory the last message.
        var task = messages[0].GetProperty("content").GetString() ?? string.Empty;
        var prompt = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString() ?? string.Empty;
        if (task.StartsWith("You are judging a directory", StringComparison.Ordinal))
            return ("pre_assessment", Line(prompt, "Relative path: "));
        if (task.StartsWith("You are writing a final summary", StringComparison.Ordinal))
            return ("summary", Line(prompt, "Relative path: "));
        var kind = prompt.Contains("Context: the directory this file is in", StringComparison.Ordinal) ? "second_check" : "classification";
        return (kind, Line(prompt, "File name: "));
    }

    private static string? Line(string prompt, string prefix)
    {
        var m = Regex.Match(prompt, "^" + Regex.Escape(prefix) + "(.*)$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.TrimEnd('\r') : null;
    }
}
