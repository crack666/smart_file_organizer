using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Hosting;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// Assessments by content (https://github.com/crack666/smart_file_organizer/issues/38): two old disks with the same
/// letter under other names, analysed one after the other through the real <see cref="OllamaService"/> against
/// <see cref="FakeModelServer"/>. The second disk is a new job; its copy of the letter costs no model call.
/// </summary>
public class ContentStoreTests : IDisposable
{
    private const string LetterText = "Sehr geehrte Frau Berger, anbei die Unterlagen zur Hausratversicherung vom 2. Mai 2017.";

    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _diskA;
    private readonly string _diskB;
    private readonly FakeModelServer _server = new();

    public ContentStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_store_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _diskA = Path.Combine(_dir, "disk-a");
        _diskB = Path.Combine(_dir, "disk-b");

        Write(_diskA, "post/brief_berger.txt", LetterText);
        Write(_diskA, "post/einkauf.txt", "Milch, Brot, Kaffee.");
        // The same letter on the second disk, renamed and in another folder; beside it something new.
        Write(_diskB, "alt/kopie/scan_0001.txt", LetterText);
        Write(_diskB, "alt/notiz.txt", "Reifenwechsel im Oktober.");

        _server.Descriptions["brief_berger.txt"] = "\"document_kind\":\"insurance\",\"issuer\":\"Versicherung Berger\",\"year\":2017";
        // Were the copy sent to the model, it would be described differently: the facts below come from the first disk.
        _server.Descriptions["scan_0001.txt"] = "\"document_kind\":\"notes\",\"issuer\":\"\",\"year\":0";
        WriteSettings(systemPrompt: null);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    // ── Fertig, wenn ─────────────────────────────────────────────────────

    [Fact]
    public async Task TheSameContent_UnderAnotherName_InASecondJob_CallsNoModel_AndCarriesAssessmentAndCorrection()
    {
        var jobA = await ScanAndAnalyzeAsync(_diskA);
        Assert.Equal(1, _server.Answered("classification", "brief_berger.txt"));

        // A person corrects the letter on the first disk; the category belongs to the content, the target to the place.
        await CorrectAsync("brief_berger.txt", FileCategory.Letter, "Insurance/Berger", "insurance letter");

        var jobB = await ScanAndAnalyzeAsync(_diskB);
        Assert.Equal(0, _server.Answered("classification", "scan_0001.txt"));
        Assert.Equal(0, _server.Answered("second_check", "scan_0001.txt"));
        Assert.Equal(1, _server.Answered("classification", "notiz.txt"));

        using var conn = Open();
        var copy = conn.QuerySingle<(string Origin, string? Model, string? Target, long Category, string? Raw)>(
            """
            SELECT a.origin, a.model_used, a.suggested_target, a.category, a.raw_response
            FROM ai_results a JOIN file_nodes f ON f.id = a.file_node_id WHERE f.job_id = @jobB AND f.name = 'scan_0001.txt'
            """, new { jobB });
        Assert.Equal("reused", copy.Origin);
        Assert.Equal(FakeModelServer.ModelName, copy.Model);
        Assert.Null(copy.Target);
        Assert.Null(copy.Raw);
        Assert.Equal((long)FileCategory.Document, copy.Category);

        // The description of stage 1 came along with the content.
        var facts = conn.Query<(string Fact, string Value)>(
            """
            SELECT ff.fact, ff.value FROM file_facts ff JOIN file_nodes f ON f.id = ff.file_node_id
            WHERE f.job_id = @jobB AND f.name = 'scan_0001.txt' AND ff.source = 'model' ORDER BY ff.fact
            """, new { jobB }).ToList();
        Assert.Contains(("document_kind", "insurance"), facts);
        Assert.Contains(("issuer", "Versicherung Berger"), facts);
        Assert.Contains(("year", "2017"), facts);

        // The correction followed the content, without the target of the other place.
        var carried = conn.QuerySingle<(long Category, string? Target, string? Note, string Origin)>(
            """
            SELECT o.overridden_category, o.overridden_target, o.note, o.origin
            FROM user_overrides o JOIN file_nodes f ON f.id = o.file_node_id WHERE f.job_id = @jobB AND f.name = 'scan_0001.txt'
            """, new { jobB });
        Assert.Equal((long)FileCategory.Letter, carried.Category);
        Assert.Null(carried.Target);
        Assert.Equal("insurance letter", carried.Note);
        Assert.Equal("reused", carried.Origin);
        Assert.Equal("human", conn.ExecuteScalar<string>(
            "SELECT o.origin FROM user_overrides o JOIN file_nodes f ON f.id = o.file_node_id WHERE f.job_id = @jobA",
            new { jobA }));

        // sfo status counts the taken-over result apart from the model calls.
        var status = await SfoAsync("status", jobB.ToString(), "--json");
        Assert.True(status.ExitCode == 0, status.Stderr + status.Stdout);
        var classification = status.Json().GetProperty("result").GetProperty("phases").GetProperty("classification");
        Assert.Equal(1, classification.GetProperty("reused").GetInt64());
        Assert.Equal(1, classification.GetProperty("by_model").GetInt64());
        Assert.Equal(2, classification.GetProperty("done").GetInt64());

        var stats = await CacheStatsAsync();
        Assert.Equal(1, stats.GetProperty("reuses").GetInt64());
        Assert.Equal(1, stats.GetProperty("corrections").GetInt64());
        Assert.Equal(0, stats.GetProperty("stale").GetInt64());
        Assert.Equal(stats.GetProperty("entries").GetInt64(), stats.GetProperty("current").GetInt64());
    }

    [Fact]
    public async Task AChangedPromptVersion_LeadsToANewAnalysis_AndKeepsTheOldEntries()
    {
        await ScanAndAnalyzeAsync(_diskA);
        var before = await CacheStatsAsync();
        Assert.True(before.GetProperty("entries").GetInt64() >= 2);

        // Another system prompt is another prompt version: nothing is taken over, the copy is judged anew.
        WriteSettings(systemPrompt: OllamaOptions.DefaultSystemPrompt + "\nBe brief.");
        await ScanAndAnalyzeAsync(_diskB);
        Assert.Equal(1, _server.Answered("classification", "scan_0001.txt"));

        var after = await CacheStatsAsync();
        Assert.Equal(0, after.GetProperty("reuses").GetInt64());
        Assert.Equal(before.GetProperty("entries").GetInt64(), after.GetProperty("stale").GetInt64());
        Assert.NotEqual(before.GetProperty("prompt_version").GetString(), after.GetProperty("prompt_version").GetString());

        // Old entries are invalid, not gone, until they are cleared.
        var clear = await SfoAsync("cache", "clear", "--stale", "--json");
        Assert.True(clear.ExitCode == 0, clear.Stderr + clear.Stdout);
        Assert.Equal(before.GetProperty("entries").GetInt64(), clear.Json().GetProperty("result").GetProperty("entries_removed").GetInt64());
        var cleared = await CacheStatsAsync();
        Assert.Equal(0, cleared.GetProperty("stale").GetInt64());
        Assert.Equal(after.GetProperty("current").GetInt64(), cleared.GetProperty("entries").GetInt64());
    }

    // ── further rules ─────────────────────────────────────────────────────

    [Fact]
    public void ThePromptVersion_FollowsThePromptText()
    {
        var plain = new OllamaOptions { Model = "m" };
        Assert.Equal(OllamaService.ClassificationPromptVersion(plain), OllamaService.ClassificationPromptVersion(new OllamaOptions { Model = "other" }));
        Assert.NotEqual(OllamaService.ClassificationPromptVersion(plain),
            OllamaService.ClassificationPromptVersion(new OllamaOptions { SystemPrompt = plain.SystemPrompt + " " }));
        Assert.NotEqual(OllamaService.ClassificationPromptVersion(plain),
            OllamaService.ClassificationPromptVersion(new OllamaOptions { SummaryLanguage = "German" }));
    }

    [Fact]
    public async Task AnotherModel_DoesNotTakeOver_AndAClearedStore_AsksAgain()
    {
        await ScanAndAnalyzeAsync(_diskA);
        var clear = await SfoAsync("cache", "clear", "--json");
        Assert.True(clear.ExitCode == 0, clear.Stderr + clear.Stdout);
        Assert.Equal(0, clear.Json().GetProperty("result").GetProperty("entries_left").GetInt64());

        await ScanAndAnalyzeAsync(_diskB);
        Assert.Equal(1, _server.Answered("classification", "scan_0001.txt"));
        using var conn = Open();
        Assert.Equal(0, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM ai_results WHERE origin = 'reused'"));
    }

    [Fact]
    public async Task AFileMarkedByItsName_GivesTheStoreNothing()
    {
        // The name marks the file (bank statement); a marked file is never described, so it gives the store nothing.
        Write(_diskA, "bank/kontoauszug_2020.txt", "Saldo alt, Saldo neu, Buchungen im Maerz.");
        await ScanAndAnalyzeAsync(_diskA);
        using var conn = Open();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(Path.Combine(_diskA, "bank", "kontoauszug_2020.txt"))));
        Assert.Equal(0, conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM content_assessments WHERE lower(content_hash) = lower(@hash)", new { hash }));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private async Task<long> ScanAndAnalyzeAsync(string root)
    {
        var scan = await SfoAsync("scan", root, "--json");
        Assert.True(scan.ExitCode == 0, scan.Stderr + scan.Stdout);
        var job = scan.Json().GetProperty("result").GetProperty("job").GetProperty("id").GetInt64();
        var analyze = await SfoAsync("analyze", job.ToString(), "--json");
        Assert.True(analyze.ExitCode == 0, analyze.Stderr + analyze.Stdout);
        return job;
    }

    private async Task<JsonElement> CacheStatsAsync()
    {
        var run = await SfoAsync("cache", "stats", "--json");
        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        return run.Json().GetProperty("result");
    }

    /// <summary>A correction as the desktop makes it, through <see cref="ReviewService"/>.</summary>
    private async Task CorrectAsync(string name, FileCategory category, string target, string note)
    {
        long id;
        using (var conn = Open())
            id = conn.ExecuteScalar<long>("SELECT id FROM file_nodes WHERE name = @name", new { name });

        var services = new ServiceCollection();
        services.AddSmartFileOrganizerCore(o => { o.DatabasePath = _dbPath; o.ReloadSettingsOnChange = false; });
        await using var provider = services.BuildServiceProvider();
        await provider.InitializeSmartFileOrganizerDatabaseAsync();
        await provider.GetRequiredService<ReviewService>().ApplyOverrideAsync(id, category, target, note);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        return conn;
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr)
    {
        public JsonElement Json()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.Clone();
        }
    }

    private async Task<Run> SfoAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost
        {
            Out = stdout,
            Error = stderr,
            ConfigureServices = services =>
                services.AddHttpClient<IOllamaService, OllamaService>().ConfigurePrimaryHttpMessageHandler(() => _server)
        };
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], host, CancellationToken.None);
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Settings next to the redirected database: the fake model, short pauses, and optionally another system prompt.</summary>
    private void WriteSettings(string? systemPrompt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        var ollama = new Dictionary<string, object>
        {
            ["BaseUrl"] = "http://model-server.test:11999", ["Model"] = FakeModelServer.ModelName,
            ["TimeoutSeconds"] = 5, ["MaxRetries"] = 2, ["MaxParallelRequests"] = 2
        };
        if (systemPrompt is not null) ollama["SystemPrompt"] = systemPrompt;
        var settings = new Dictionary<string, object>
        {
            ["Ollama"] = ollama,
            ["ModelServer"] = new Dictionary<string, object>
            {
                ["RetryDelaySeconds"] = 0.002, ["MaxRetryAfterSeconds"] = 0.01, ["OutageAfterFailures"] = 2,
                ["ProbeIntervalSeconds"] = 0.005, ["ProbeTimeoutSeconds"] = 0.2, ["MaxWaitMinutes"] = 0.5, ["WarmupTimeoutSeconds"] = 1
            }
        };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dbPath)!, "settings.json"), JsonSerializer.Serialize(settings));
    }
}
