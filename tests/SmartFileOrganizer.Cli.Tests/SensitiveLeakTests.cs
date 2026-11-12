using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// Paths by which the content of a marked file could reach a caller outside the machine
/// (https://github.com/crack666/smart_file_organizer/issues/33). Every test plants a made-up token in
/// a marked file and searches stdout, stderr and exports for it.
/// </summary>
public class SensitiveLeakTests : IDisposable
{
    /// <summary>Made up; stands for a password in a password list.</summary>
    private static readonly string Secret = "FAKEPW-" + Guid.NewGuid().ToString("N")[..12];

    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _root;

    public SensitiveLeakTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_leak_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _root = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "docs", "letter.txt"), "Dear landlord, the heating is broken.");
        // Marked by the name rule (keyword "passw"); a text file, so its text goes to the local model.
        File.WriteAllText(Path.Combine(_root, "docs", "passwords.txt"), $"mail: {Secret}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr)
    {
        public JsonElement Result()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.GetProperty("result").Clone();
        }
    }

    /// <summary>Runs <c>sfo</c> with the real Ollama client against <see cref="EchoingOllamaHandler"/>.</summary>
    private async Task<Run> SfoAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost
        {
            Out = stdout,
            Error = stderr,
            ConfigureServices = services => services
                .AddHttpClient<IOllamaService, OllamaService>()
                .ConfigurePrimaryHttpMessageHandler(() => new EchoingOllamaHandler())
        };
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], host);
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<long> ScanAsync()
    {
        var scan = await SfoAsync("scan", _root, "--json");
        Assert.True(scan.ExitCode == ExitCodes.Ok, scan.Stdout + scan.Stderr);
        return scan.Result().GetProperty("job").GetProperty("id").GetInt64();
    }

    private string DebugLogPath => Path.Combine(Path.GetDirectoryName(_dbPath)!, CliLogFiles.DebugLogName);

    [Fact]
    public async Task AnalyzeVerbose_WritesNoExcerptOfAMarkedFileToStderr()
    {
        var job = await ScanAsync();

        var run = await SfoAsync("analyze", job.ToString(), "--verbose", "--json");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        Assert.DoesNotContain(Secret, run.Stderr);
        Assert.DoesNotContain(Secret, run.Stdout);
        // No prompt and no model answer on stderr, not even for an unmarked file.
        Assert.DoesNotContain("Dear landlord", run.Stderr);
        Assert.DoesNotContain("PROMPT", run.Stderr);
    }

    [Fact]
    public async Task AnalyzeVerbose_LogsPromptsToALocalFile_WithoutExcerptOfAMarkedFile()
    {
        var job = await ScanAsync();

        var run = await SfoAsync("analyze", job.ToString(), "--verbose", "--json");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        var log = await File.ReadAllTextAsync(DebugLogPath);
        Assert.Contains("Dear landlord", log);
        Assert.Contains("passwords.txt", log);
        Assert.DoesNotContain(Secret, log);
    }

    [Fact]
    public async Task Analyze_WithoutVerbose_WritesNoDebugLog()
    {
        var job = await ScanAsync();

        var run = await SfoAsync("analyze", job.ToString(), "--json");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        Assert.False(File.Exists(DebugLogPath));
        Assert.DoesNotContain(Secret, run.Stderr);
    }
}

/// <summary>
/// Stands in for the Ollama HTTP API. Its answers repeat the whole prompt in every free-text field:
/// the worst case of a model that quotes what it was shown.
/// </summary>
internal sealed class EchoingOllamaHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/api/tags", StringComparison.Ordinal))
            return Json("""{"models":[{"name":"echo","details":{}}]}""");

        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var root = body.RootElement;
        var prompt = string.Join("\n", root.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("content").GetString()));
        var properties = root.GetProperty("format").GetProperty("properties");

        object answer;
        if (properties.TryGetProperty("verdict", out _))
        {
            answer = new
            {
                verdict = "gemischt", purpose = prompt, retention = "pruefen", reason = prompt,
                homogeneity = "low", dominant_type = "document", theme = prompt,
                anomalous_files = Array.Empty<object>(), sampling_strategy = "analyze_all", suggested_sample_size = 2
            };
        }
        else if (properties.TryGetProperty("category", out _))
        {
            answer = new
            {
                category = "Document", importance = "Medium", confidence = 0.9,
                summary = prompt, suggested_target = prompt, sensitive = Array.Empty<string>()
            };
        }
        else
        {
            answer = new { summary = prompt, theme = prompt, confirmed_anomalies = Array.Empty<object>() };
        }

        var content = JsonSerializer.Serialize(answer);
        return Json(JsonSerializer.Serialize(new { message = new { role = "assistant", content }, done = true }));
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
