using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>sfo analyze &lt;job&gt; --against &lt;library&gt;</c> leaves out what the library already holds
/// (https://github.com/crack666/smart_file_organizer/issues/42): a generated source and library, the real
/// <see cref="OllamaService"/> against <see cref="FakeModelServer"/>, and the calls counted.
/// </summary>
public class BackedUpSkipTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _source;
    private readonly string _library;
    private readonly FakeModelServer _server = new();

    public BackedUpSkipTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_backedup_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _source = Path.Combine(_dir, "stick");
        _library = Path.Combine(_dir, "archive");

        // saved: every file is in the library by content (under other names and folders).
        Write(_source, "saved/letter.txt", "Letter to the landlord, March: the heating is broken again.");
        Write(_source, "saved/recipe.txt", "Recipe: apple cake with cinnamon and a lot of butter.");
        Write(_library, "letters/landlord.txt", "Letter to the landlord, March: the heating is broken again.");
        Write(_library, "kitchen/cake.txt", "Recipe: apple cake with cinnamon and a lot of butter.");

        // fresh: one file is in the library, one is not.
        Write(_source, "fresh/poem.txt", "A poem about the sea, written on a train in summer.");
        Write(_source, "fresh/diary.txt", "Diary, day one: nothing of this is anywhere else.");
        Write(_library, "texts/sea.txt", "A poem about the sea, written on a train in summer.");

        // other: same name in a folder of the same name, other content: differs, judged as usual.
        Write(_source, "other/plan.txt", "Plan for the garden, version two with the pond.");
        Write(_library, "other/plan.txt", "Plan for the garden, version one without a pond.");

        WriteSettings();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── Fertig, wenn ─────────────────────────────────────────────────────

    [Fact]
    public async Task FullyBackedUpFolder_CostsNoCall_AFolderWithOneNewFile_ExactlyOneClassification()
    {
        var (source, library) = await ScanAndCompareAsync();

        var run = await SfoAsync("analyze", source.ToString(), "--against", library.ToString(), "--json");
        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);

        // Nothing about the fully backed-up folder or its files reached the model.
        Assert.DoesNotContain(_server.Log, r => r.Name is { } n && (n.StartsWith("saved", StringComparison.Ordinal)
                                                                  || n is "letter.txt" or "recipe.txt"));
        // fresh: only the new file; other: the file that differs.
        Assert.Equal(1, _server.Answered("classification", "diary.txt"));
        Assert.Equal(0, _server.Answered("classification", "poem.txt"));
        Assert.Equal(1, _server.Answered("classification", "plan.txt"));
        Assert.Equal(2, _server.Answered("classification"));

        var skip = run.Json().GetProperty("result").GetProperty("backed_up");
        Assert.Equal(library, skip.GetProperty("against").GetInt64());
        Assert.Equal(3, skip.GetProperty("files").GetInt64());
        Assert.Equal(1, skip.GetProperty("directories").GetInt64());

        // The reason stays at the file and the folder.
        Assert.Equal(["letter.txt", "poem.txt", "recipe.txt"], Names("SELECT name FROM file_nodes WHERE analysis_skip = 'backed_up' ORDER BY name"));
        Assert.Equal(["saved"], Names("SELECT name FROM directory_nodes WHERE analysis_skip = 'backed_up' ORDER BY name"));

        var phases = (await StatusAsync(source)).GetProperty("phases");
        var classification = phases.GetProperty("classification");
        Assert.Equal(3, classification.GetProperty("backed_up").GetInt64());
        Assert.Equal(0, classification.GetProperty("pending").GetInt64());
        Assert.True(classification.GetProperty("skipped").GetInt64() >= 3);
        Assert.Equal(1, phases.GetProperty("pre_assessment").GetProperty("backed_up").GetInt64());
        Assert.Equal(0, phases.GetProperty("pre_assessment").GetProperty("pending").GetInt64());
        Assert.Equal(1, phases.GetProperty("summary").GetProperty("backed_up").GetInt64());
        Assert.Equal(0, phases.GetProperty("summary").GetProperty("pending").GetInt64());
    }

    [Fact]
    public async Task WithoutAgainst_EverythingIsJudged_AndAnEarlierSkipIsReleased()
    {
        var (source, library) = await ScanAndCompareAsync();

        var skipped = await SfoAsync("analyze", source.ToString(), "--against", library.ToString(), "--phase", "pre_assessment", "--json");
        Assert.True(skipped.ExitCode == 0, skipped.Stderr + skipped.Stdout);
        Assert.Equal(3, Names("SELECT name FROM file_nodes WHERE analysis_skip IS NOT NULL").Count);

        var plain = await SfoAsync("analyze", source.ToString(), "--json");
        Assert.True(plain.ExitCode == 0, plain.Stderr + plain.Stdout);
        Assert.Equal(JsonValueKind.Null, plain.Json().GetProperty("result").GetProperty("backed_up").ValueKind);
        Assert.Empty(Names("SELECT name FROM file_nodes WHERE analysis_skip IS NOT NULL"));
        Assert.Empty(Names("SELECT name FROM directory_nodes WHERE analysis_skip IS NOT NULL"));
        foreach (var name in new[] { "letter.txt", "recipe.txt", "poem.txt", "diary.txt", "plan.txt" })
            Assert.Equal(1, _server.Answered("classification", name));
    }

    [Fact]
    public async Task AgainstALibraryWithoutStoredComparison_FailsBeforeAnyCall()
    {
        var source = await ScanAsync(_source);
        var library = await ScanAsync(_library, "--role", "library");

        var run = await SfoAsync("analyze", source.ToString(), "--against", library.ToString(), "--json");
        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Equal(ErrorCodes.ComparisonNotFound, run.Json().GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(_server.Log, r => r.Kind is "pre_assessment" or "classification" or "summary");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private async Task<(long Source, long Library)> ScanAndCompareAsync()
    {
        var source = await ScanAsync(_source);
        var library = await ScanAsync(_library, "--role", "library");
        var compare = await SfoAsync("compare", source.ToString(), "--against", library.ToString(), "--json");
        Assert.True(compare.ExitCode == 0, compare.Stderr + compare.Stdout);
        var counts = compare.Json().GetProperty("result").GetProperty("counts");
        Assert.Equal(3, counts.GetProperty("backed_up").GetInt64());
        Assert.Equal(1, counts.GetProperty("differs").GetInt64());
        return (source, library);
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

    private async Task<long> ScanAsync(string root, params string[] extra)
    {
        var scan = await SfoAsync(["scan", root, .. extra, "--json"]);
        Assert.True(scan.ExitCode == 0, scan.Stderr + scan.Stdout);
        return scan.Json().GetProperty("result").GetProperty("job").GetProperty("id").GetInt64();
    }

    private async Task<JsonElement> StatusAsync(long jobId)
    {
        var status = await SfoAsync("status", jobId.ToString(), "--json");
        Assert.True(status.ExitCode == 0, status.Stderr + status.Stdout);
        return status.Json().GetProperty("result");
    }

    private List<string> Names(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>Settings next to the redirected database: the fake model and short pauses.</summary>
    private void WriteSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dbPath)!, "settings.json"), $$"""
            {
              "Ollama": { "BaseUrl": "http://model-server.test:11999", "Model": "{{FakeModelServer.ModelName}}",
                          "TimeoutSeconds": 5, "MaxRetries": 2, "MaxParallelRequests": 2 },
              "ModelServer": { "RetryDelaySeconds": 0.002, "MaxRetryAfterSeconds": 0.01, "OutageAfterFailures": 2,
                               "ProbeIntervalSeconds": 0.005, "ProbeTimeoutSeconds": 0.2,
                               "MaxWaitMinutes": 0.5, "WarmupTimeoutSeconds": 1 }
            }
            """);
    }
}
