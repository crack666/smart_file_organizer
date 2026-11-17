using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Cli;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Infrastructure.ModelServer;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// Ollama and an OpenAI-compatible broker (https://github.com/crack666/smart_file_organizer/issues/53):
/// the same scan and analysis, a busy broker, doctor, profiles and a key that stays out of the output.
/// </summary>
public class ModelProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sfo_provider_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ScanAndAnalyze_SameCategories_OnBothProviders()
    {
        var ollama = await Analyse("ollama");
        var openai = await Analyse("openai_compatible");

        Assert.Equal(ollama.Categories, openai.Categories);
        Assert.NotEmpty(openai.Categories);
        Assert.All(openai.Categories, row => Assert.Equal("document", row.Category));
        Assert.Contains(openai.Paths, path => path.Contains("chat/completions", StringComparison.Ordinal));
        Assert.Contains(openai.Bodies, body => body.Contains("\"response_format\"", StringComparison.Ordinal));
        Assert.Contains(ollama.Paths, path => path.EndsWith("/api/chat", StringComparison.Ordinal));
        Assert.DoesNotContain(ollama.Bodies, body => body.Contains("response_format", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenAi_Busy503WithRetryAfter_IsWaitedOut_NotStoredAsError()
    {
        var server = new FakeModelServer { RetryAfterSeconds = 30 };
        var chats = 0;
        server.Script = request =>
        {
            if (request.Kind is "tags" or "warm" or "probe")
                return FakeServerMode.Up;
            return Interlocked.Increment(ref chats) == 1 ? FakeServerMode.Busy : FakeServerMode.Up;
        };

        var run = await Analyse("openai_compatible", server);
        Assert.NotEmpty(run.Categories);
        Assert.All(run.Categories, row => Assert.Equal("document", row.Category));
        Assert.Contains(server.Log, request => request.Answer == FakeServerMode.Busy);
    }

    [Fact]
    public async Task Doctor_ReportsProvider_AndMeasuredParallelism()
    {
        var server = new FakeModelServer();
        var db = Prepare(server, "ollama", parallel: 2);
        var run = await Sfo(server, db, "doctor", "--json");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        var result = run.Json().GetProperty("result");
        Assert.Equal("ollama", result.GetProperty("provider").GetString());
        Assert.False(result.GetProperty("busy").GetBoolean());
        Assert.Equal(2, result.GetProperty("parallel_requested").GetInt32());
        Assert.Equal(2, result.GetProperty("parallel_measured").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("parallel_warning").ValueKind);
    }

    [Fact]
    public async Task Doctor_Warns_WhenTheServerAnswersFewerCallsAtOnce()
    {
        var server = new FakeModelServer { MaxConcurrent = 1, AnswerDelay = TimeSpan.FromMilliseconds(50) };
        var db = Prepare(server, "ollama", parallel: 4);
        var run = await Sfo(server, db, "doctor", "--json");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        var result = run.Json().GetProperty("result");
        Assert.True(result.GetProperty("parallel_measured").GetInt32() < 4);
        Assert.False(string.IsNullOrEmpty(result.GetProperty("parallel_warning").GetString()));
    }

    [Fact]
    public async Task Profile_SelectsTheModel_UnknownProfileIsUsage()
    {
        var server = new FakeModelServer();
        var db = Prepare(server, "ollama", parallel: 1, profile: true);
        var chosen = await Sfo(server, db, "--profile", "tight", "doctor", "--json");
        Assert.Equal(ExitCodes.Failure, chosen.ExitCode);
        Assert.Equal("other-model", chosen.Json().GetProperty("result").GetProperty("model").GetProperty("name").GetString());

        var unknown = await Sfo(server, db, "--profile", "missing", "doctor", "--json");
        Assert.Equal(ExitCodes.Usage, unknown.ExitCode);
        Assert.Equal(ErrorCodes.SettingsInvalid, unknown.Json().GetProperty("error").GetProperty("code").GetString());

        var bad = await Sfo(server, db, "--provider", "nope", "doctor", "--json");
        Assert.Equal(ExitCodes.Usage, bad.ExitCode);
        Assert.Equal(ErrorCodes.SettingsInvalid, bad.Json().GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ApiKey_IsSent_AndNeverPrinted()
    {
        const string secret = "s3cret-model-key";
        var server = new FakeModelServer();
        var db = Prepare(server, "openai_compatible", parallel: 1, apiKey: secret);
        var run = await Analyse("openai_compatible", server, db);
        var doctor = await Sfo(server, db, "doctor", "--json");

        Assert.Equal("Bearer " + secret, server.LastAuthorization);
        Assert.True(doctor.Json().GetProperty("result").GetProperty("api_key_configured").GetBoolean());
        Assert.DoesNotContain(secret, run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, doctor.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, doctor.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(server.Bodies, body => body.Contains(secret, StringComparison.Ordinal));
    }

    private async Task<AnalyseRun> Analyse(string provider, FakeModelServer? server = null, string? db = null)
    {
        server ??= new FakeModelServer();
        db ??= Prepare(server, provider, parallel: 2);
        var root = Path.Combine(_dir, "tree-" + provider);
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            File.WriteAllText(Path.Combine(root, name), "A short note about " + name);

        var scan = await Sfo(server, db, "scan", root, "--json");
        Assert.True(scan.ExitCode == 0, scan.Stdout + scan.Stderr);
        var job = scan.Json().GetProperty("result").GetProperty("job").GetProperty("id").GetInt64();
        var analyse = await Sfo(server, db, "analyze", job.ToString(), "--json");
        Assert.True(analyse.ExitCode == 0, analyse.Stdout + analyse.Stderr);

        var files = await Sfo(server, db, "files", job.ToString(), "--json");
        Assert.True(files.ExitCode == 0, files.Stdout + files.Stderr);
        var categories = files.Json().GetProperty("result").GetProperty("items").EnumerateArray()
            .Select(item => new FileCategoryRow(item.GetProperty("name").GetString()!, item.GetProperty("category").GetString()))
            .OrderBy(row => row.Name, StringComparer.Ordinal)
            .ToList();
        return new AnalyseRun(categories, server.Paths.ToList(), server.Bodies.ToList(), analyse.Stdout, analyse.Stderr);
    }

    private string Prepare(FakeModelServer server, string provider, int parallel, bool profile = false, string? apiKey = null)
    {
        _ = server;
        var dbDir = Path.Combine(_dir, "db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbDir);
        var profileJson = profile
            ? """, "Profiles": { "tight": { "Model": "other-model", "NumCtx": 4096, "Parallel": 1 } }"""
            : string.Empty;
        var keyJson = apiKey is null ? string.Empty : $", \"ApiKey\": \"{apiKey}\"";
        File.WriteAllText(Path.Combine(dbDir, "settings.json"), $$"""
            {
              "Ollama": { "BaseUrl": "http://model-server.test:11999", "Model": "{{FakeModelServer.ModelName}}",
                          "TimeoutSeconds": 5, "MaxRetries": 2, "MaxParallelRequests": {{parallel}} },
              "Model": { "Provider": "{{provider}}"{{keyJson}}{{profileJson}} },
              "ModelServer": { "RetryDelaySeconds": 0.002, "MaxRetryAfterSeconds": 0.01, "OutageAfterFailures": 2,
                               "ProbeIntervalSeconds": 0.005, "ProbeTimeoutSeconds": 0.2,
                               "MaxWaitMinutes": 0.5, "WarmupTimeoutSeconds": 1 }
            }
            """);
        return Path.Combine(dbDir, "data.db");
    }

    private static async Task<Run> Sfo(FakeModelServer server, string db, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost
        {
            Out = stdout,
            Error = stderr,
            ConfigureServices = services =>
                services.AddHttpClient<IOllamaService, OllamaService>().ConfigurePrimaryHttpMessageHandler(() => server)
        };
        var exit = await SfoApp.RunAsync([.. args, "--db", db], host, CancellationToken.None);
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private sealed record FileCategoryRow(string Name, string? Category);

    private sealed record AnalyseRun(
        List<FileCategoryRow> Categories, List<string> Paths, List<string> Bodies, string Stdout, string Stderr);

    private sealed record Run(int ExitCode, string Stdout, string Stderr)
    {
        public JsonElement Json()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.Clone();
        }
    }
}

public class ModelWireTests
{
    [Fact]
    public void EstimateSlots_TreatsAFastServerAsParallel_AndAQueueAsOne()
    {
        Assert.Equal(4, ModelWire.EstimateSlots(4, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(12)));
        Assert.Equal(4, ModelWire.EstimateSlots(4, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(45)));
        Assert.Equal(1, ModelWire.EstimateSlots(4, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(160)));
        Assert.Null(ModelWire.ParallelWarning(4, 4));
        Assert.Contains("OLLAMA_NUM_PARALLEL", ModelWire.ParallelWarning(4, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void Excerpt_KeepsBeginningMiddleAndEnd()
    {
        var text = new string('a', 50) + new string('b', 50) + new string('c', 50);
        var excerpt = OllamaService.Excerpt(text, 30);
        Assert.StartsWith("aaa", excerpt, StringComparison.Ordinal);
        Assert.Contains("omitted", excerpt, StringComparison.Ordinal);
        Assert.Contains("bbb", excerpt, StringComparison.Ordinal);
        Assert.EndsWith("ccc", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Fit_NotesWhenTheContextCannotHoldTheText()
    {
        var options = new OllamaOptions { NumCtx = 800 };
        var request = new ModelChatRequest
        {
            Model = "m",
            MaxTokens = 64,
            Messages = [new ModelChatMessage("user", new string('x', 20_000))]
        };
        var fitted = ModelWire.Fit(options, request);
        Assert.Contains("truncated to fit a context of 800 tokens", fitted.Messages[0].Content, StringComparison.Ordinal);
        Assert.True(fitted.Messages[0].Content.Length < 20_000);
    }

    [Fact]
    public async Task Create_OpenAiUsesDataUrlsAndMetadata_OllamaKeepsBase64Images()
    {
        var jpeg = "/9j/" + new string('A', 8);
        var openAi = new OllamaOptions
        {
            Provider = ModelProviders.OpenAiCompatible,
            Metadata = new Dictionary<string, string> { ["caller"] = "sfo" },
            ExtraBody = new JsonObject { ["think"] = false },
            NumCtx = 4096,
            Temperature = 0,
            ApiKey = "s3cret-model-key"
        };
        using var openaiRequest = ModelWire.Create(openAi, new ModelChatRequest
        {
            Model = "m",
            MaxTokens = 32,
            Schema = new { type = "object" },
            Messages = [new ModelChatMessage("user", "look", [jpeg])]
        }, new Uri("http://broker.test/v1/chat/completions"));
        var openaiBody = await openaiRequest.Content!.ReadAsStringAsync();
        Assert.Contains("image_url", openaiBody, StringComparison.Ordinal);
        Assert.Contains(ModelWire.DataUrl(jpeg), openaiBody, StringComparison.Ordinal);
        Assert.Contains("\"metadata\"", openaiBody, StringComparison.Ordinal);
        Assert.Contains("\"think\":false", openaiBody, StringComparison.Ordinal);
        Assert.Contains("response_format", openaiBody, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-model-key", openaiBody, StringComparison.Ordinal);
        Assert.Equal("Bearer", openaiRequest.Headers.Authorization?.Scheme);
        Assert.Equal("s3cret-model-key", openaiRequest.Headers.Authorization?.Parameter);

        var ollama = new OllamaOptions { NumCtx = 4096 };
        using var ollamaRequest = ModelWire.Create(ollama, new ModelChatRequest
        {
            Model = "m",
            MaxTokens = 32,
            Messages = [new ModelChatMessage("user", "look", [jpeg])]
        }, new Uri("http://127.0.0.1:11434/api/chat"));
        var ollamaBody = await ollamaRequest.Content!.ReadAsStringAsync();
        Assert.Contains("\"images\"", ollamaBody, StringComparison.Ordinal);
        Assert.Contains("\"think\":false", ollamaBody, StringComparison.Ordinal);
        Assert.Contains("\"num_ctx\":4096", ollamaBody, StringComparison.Ordinal);
        Assert.DoesNotContain("image_url", ollamaBody, StringComparison.Ordinal);
        Assert.Null(ollamaRequest.Headers.Authorization);
    }

    [Fact]
    public void NormalizeAnswer_MapsOpenAiUsageAndCutOff()
    {
        var body = """
            {"choices":[{"message":{"content":"{\"category\":\"Document\"}"},"finish_reason":"length"}],
             "usage":{"prompt_tokens":10,"completion_tokens":4,"prompt_tokens_details":{"cached_tokens":3}}}
            """;
        using var doc = JsonDocument.Parse(ModelWire.NormalizeAnswer(ModelProviders.OpenAiCompatible, body));
        Assert.Equal("length", doc.RootElement.GetProperty("done_reason").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("prompt_eval_count").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("prompt_eval_cached_count").GetInt32());
        Assert.Contains("Document", doc.RootElement.GetProperty("message").GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AddressWithoutSecrets_DropsUserInfo()
    {
        Assert.Equal("http://broker.test/v1", ModelWire.AddressWithoutSecrets("http://user:s3cret@broker.test/v1"));
    }
}
