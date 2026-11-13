using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// A model server that goes away during <c>sfo analyze</c> (https://github.com/crack666/smart_file_organizer/issues/63):
/// the real <see cref="OllamaService"/> talks to <see cref="FakeModelServer"/>, all pauses are a few milliseconds
/// through the settings, so no test waits for real.
/// </summary>
public class ModelServerOutageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _root;
    private readonly FakeModelServer _server = new();

    /// <summary>The exit code of a run whose model server stayed away; new with this change.</summary>
    private const int ModelServerLostExitCode = 4;

    /// <summary>Files of the corpus that go to the model (all in mixed folders, so each is judged on its own).</summary>
    private static readonly string[] ModelFiles =
        ["notes.txt", "todo.txt", "letter.txt", "offer.txt", "plan.txt", "recipe.txt", "diary.txt", "poem.txt"];

    public ModelServerOutageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_outage_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _root = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(Path.Combine(_root, "misc"));
        var i = 0;
        foreach (var name in ModelFiles)
        {
            var folder = (i++ % 3) switch { 0 => _root, 1 => Path.Combine(_root, "docs"), _ => Path.Combine(_root, "misc") };
            File.WriteAllText(Path.Combine(folder, name), $"Text of {name}: some words to read.");
        }
        WriteSettings();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── Fertig, wenn ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("pre_assessment")]
    [InlineData("classification")]
    [InlineData("second_check")]
    [InlineData("summary")]
    public async Task ServerDropsMidRun_AndReturns_EveryItemGetsOneResult_NoError(string phase)
    {
        var jobId = await ScanAsync();
        if (phase == "second_check") _server.UnsureFiles.Add("letter.txt");
        // After the first answer of the phase the server refuses the next 14 requests (calls, retries and probes),
        // then answers again: long enough for the run to decide the server is gone and wait for it.
        var refused = 0;
        _server.Script = r =>
        {
            if (refused == 0 && !(r.Kind == phase && _server.Answered(phase) >= 1)) return null;
            return Interlocked.Increment(ref refused) <= 14 ? FakeServerMode.Refusing : FakeServerMode.Up;
        };

        var run = await SfoAsync("analyze", jobId.ToString(), "--json", "--progress", "jsonl");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        Assert.Equal(0, run.Json().GetProperty("result").GetProperty("error_count").GetInt32());
        Assert.Contains(WaitEvents(run), e => e.State == "waiting");
        Assert.Contains(WaitEvents(run), e => e.State == "resumed");

        var phases = (await StatusAsync(jobId)).GetProperty("phases");
        foreach (var name in new[] { "pre_assessment", "classification", "summary" })
        {
            Assert.Equal(0, phases.GetProperty(name).GetProperty("errors").GetInt64());
            Assert.Equal(0, phases.GetProperty(name).GetProperty("pending").GetInt64());
        }

        // Every file carries one stored result, stored once, and every answer the server gave was used once.
        var rows = ResultRows();
        Assert.Equal(ModelFiles.Order(), rows.Select(r => r.Name).Order());
        Assert.All(rows, r => Assert.Null(r.Error));
        Assert.All(rows, r => Assert.Equal(1, r.Attempts));
        foreach (var name in ModelFiles)
            Assert.Equal(1, _server.Answered("classification", name));
        Assert.All(DirectoryErrors(), e => Assert.Null(e));
    }

    [Fact]
    public async Task ServerStaysAway_RunEndsAfterTheBound_WithItsOwnExitCode_AndASecondRunFinishes()
    {
        var jobId = await ScanAsync();
        WriteSettings(maxWaitMinutes: 0.003); // about 0.2 s
        // Gone for good once two files were judged: every request after that is refused.
        var stillAway = true;
        _server.Script = _ => stillAway && _server.Answered("classification") >= 2 ? FakeServerMode.Refusing : null;

        var lost = await SfoAsync("analyze", jobId.ToString(), "--json", "--progress", "jsonl");

        Assert.True(lost.ExitCode == ModelServerLostExitCode, lost.Stdout + lost.Stderr);
        var doc = lost.Json();
        Assert.Equal("model_server_lost", doc.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains(WaitEvents(lost), e => e.State == "given_up");

        var classification = (await StatusAsync(jobId)).GetProperty("phases").GetProperty("classification");
        Assert.Equal(0, classification.GetProperty("errors").GetInt64());
        Assert.Equal(ModelFiles.Length - _server.Answered("classification"), classification.GetProperty("pending").GetInt64());
        Assert.All(ResultRows(), r => Assert.Null(r.Error));

        stillAway = false;
        var second = await SfoAsync("analyze", jobId.ToString(), "--json");

        Assert.True(second.ExitCode == ExitCodes.Ok, second.Stdout + second.Stderr);
        var status = await StatusAsync(jobId);
        Assert.Equal(0, status.GetProperty("phases").GetProperty("classification").GetProperty("pending").GetInt64());
        Assert.Equal(0, status.GetProperty("phases").GetProperty("classification").GetProperty("errors").GetInt64());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("model_server_wait").ValueKind);
        Assert.All(ResultRows(), r => Assert.Equal(1, r.Attempts));
    }

    [Fact]
    public async Task APermanentErrorOfOneFile_IsOneErrorAtThatFile_WithoutWaiting()
    {
        var jobId = await ScanAsync();
        _server.Script = r => r.Kind == "classification" && r.Name == "offer.txt" ? FakeServerMode.BadRequest : null;

        var run = await SfoAsync("analyze", jobId.ToString(), "--json", "--progress", "jsonl");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        Assert.Equal(1, run.Json().GetProperty("result").GetProperty("error_count").GetInt32());
        Assert.Empty(WaitEvents(run));
        // Asked exactly once: a request the server rejects is not repeated, and nothing probed the server.
        Assert.Equal(1, _server.Count("classification", FakeServerMode.BadRequest));
        Assert.Equal(1, _server.Log.Count(r => r.Kind == "tags"));

        var rows = ResultRows();
        Assert.Equal("offer.txt", Assert.Single(rows, r => r.Error is not null).Name);
        Assert.Equal(1, (await StatusAsync(jobId)).GetProperty("phases").GetProperty("classification").GetProperty("errors").GetInt64());
    }

    // ── further cases of the issue ───────────────────────────────────────

    [Fact]
    public async Task Busy503WithRetryAfter_IsWaitedOut_NotStoredAsError()
    {
        var jobId = await ScanAsync();
        var busy = 0;
        _server.Script = r => r.Kind == "classification" && Interlocked.Increment(ref busy) <= 2 ? FakeServerMode.Busy : null;

        var run = await SfoAsync("analyze", jobId.ToString(), "--json");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        Assert.Equal(0, run.Json().GetProperty("result").GetProperty("error_count").GetInt32());
        Assert.Equal(2, _server.Count("classification", FakeServerMode.Busy));
        Assert.All(ResultRows(), r => Assert.Null(r.Error));
    }

    [Fact]
    public async Task HangingServer_TimesOut_IsTreatedAsGone_AndTheRunContinuesWhenItAnswers()
    {
        var jobId = await ScanAsync();
        WriteSettings(timeoutSeconds: 0.1);
        var hung = 0;
        _server.Script = r =>
        {
            if (hung == 0 && !(r.Kind == "classification" && _server.Answered("classification") >= 1)) return null;
            return Interlocked.Increment(ref hung) <= 8 ? FakeServerMode.Hanging : FakeServerMode.Up;
        };

        var run = await SfoAsync("analyze", jobId.ToString(), "--json", "--progress", "jsonl");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        Assert.Equal(0, run.Json().GetProperty("result").GetProperty("error_count").GetInt32());
        Assert.Contains(WaitEvents(run), e => e.State == "waiting" && e.Cause == "timed_out");
        Assert.All(ResultRows(), r => Assert.Null(r.Error));
    }

    [Fact]
    public async Task AFileThatBringsTheServerDownEveryTime_EndsWithAnError_InsteadOfLooping()
    {
        var jobId = await ScanAsync();
        _server.Script = r => r.Kind == "classification" && r.Name == "plan.txt" ? FakeServerMode.Refusing : null;

        var run = await SfoAsync("analyze", jobId.ToString(), "--json");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        Assert.Equal(1, run.Json().GetProperty("result").GetProperty("error_count").GetInt32());
        Assert.Equal("plan.txt", Assert.Single(ResultRows(), r => r.Error is not null).Name);
    }

    [Fact]
    public async Task Status_WhileARunWaits_ShowsTheWait_AndClearsItAfterwards()
    {
        var jobId = await ScanAsync();
        var away = true;
        _server.Script = _ => away && _server.Answered("classification") >= 1 ? FakeServerMode.Refusing : null;

        var analyze = SfoAsync("analyze", jobId.ToString(), "--json");
        JsonElement wait = default;
        for (var i = 0; i < 500 && wait.ValueKind != JsonValueKind.Object; i++)
        {
            await Task.Delay(20);
            wait = (await StatusAsync(jobId)).GetProperty("model_server_wait");
        }
        away = false;
        var run = await analyze;

        Assert.Equal(JsonValueKind.Object, wait.ValueKind);
        Assert.Equal("connection_failed", wait.GetProperty("cause").GetString());
        Assert.EndsWith("Z", wait.GetProperty("since").GetString());
        Assert.False(wait.GetProperty("stale").GetBoolean());
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        Assert.Equal(JsonValueKind.Null, (await StatusAsync(jobId)).GetProperty("model_server_wait").ValueKind);
    }

    [Fact]
    public async Task RetryErrors_RetriesTheFile_AndWritesTheSummaryOfItsFolderAgain()
    {
        var jobId = await ScanAsync();
        var broken = true;
        _server.Script = r => broken && r.Kind == "classification" && r.Name == "letter.txt" ? FakeServerMode.BadRequest : null;
        var first = await SfoAsync("analyze", jobId.ToString(), "--json");
        Assert.Equal(1, first.Json().GetProperty("result").GetProperty("error_count").GetInt32());
        Assert.Equal(1, _server.Answered("summary", "docs"));

        broken = false;
        var retry = await SfoAsync("analyze", jobId.ToString(), "--json", "--retry-errors");

        Assert.True(retry.ExitCode == ExitCodes.Ok, retry.Stdout + retry.Stderr);
        Assert.All(ResultRows(), r => Assert.Null(r.Error));
        // The summary of docs was written while letter.txt had no result; it is written again, the others are not.
        Assert.Equal(2, _server.Answered("summary", "docs"));
        Assert.Equal(1, _server.Answered("summary", "misc"));
    }

    [Fact]
    public async Task IsAvailable_PassesCancellationThrough_InsteadOfReportingTheServerAway()
    {
        _server.Mode = FakeServerMode.Hanging;
        var ollama = new OllamaService(new HttpClient(_server, disposeHandler: false),
            new OllamaOptions { BaseUrl = "http://model-server.test:11999" },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaService>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ollama.IsAvailableAsync(cts.Token));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private sealed record Run(int ExitCode, string Stdout, string Stderr)
    {
        public JsonElement Json()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.Clone();
        }
    }

    private sealed record WaitEvent(string State, string? Cause);

    private static List<WaitEvent> WaitEvents(Run run) =>
        run.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.TrimStart().StartsWith('{'))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("type").GetString() == "wait")
            .Select(e => new WaitEvent(e.GetProperty("state").GetString()!,
                e.TryGetProperty("cause", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null))
            .ToList();

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

    private async Task<long> ScanAsync()
    {
        var scan = await SfoAsync("scan", _root, "--json");
        Assert.True(scan.ExitCode == 0, scan.Stderr + scan.Stdout);
        return scan.Json().GetProperty("result").GetProperty("job").GetProperty("id").GetInt64();
    }

    private async Task<JsonElement> StatusAsync(long jobId)
    {
        var status = await SfoAsync("status", jobId.ToString(), "--json");
        Assert.True(status.ExitCode == 0, status.Stderr + status.Stdout);
        return status.Json().GetProperty("result");
    }

    private sealed record ResultRow(string Name, string? Error, long Attempts);

    private List<ResultRow> ResultRows()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT f.name, a.error, a.attempts FROM ai_results a JOIN file_nodes f ON f.id = a.file_node_id ORDER BY f.name";
        using var reader = cmd.ExecuteReader();
        var rows = new List<ResultRow>();
        while (reader.Read())
            rows.Add(new ResultRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2)));
        return rows;
    }

    /// <summary>Errors stored at directories: verdicts and both directory phases.</summary>
    private List<string?> DirectoryErrors()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT error FROM directory_verdicts UNION ALL SELECT error FROM ai_directory_results";
        using var reader = cmd.ExecuteReader();
        var errors = new List<string?>();
        while (reader.Read()) errors.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return errors;
    }

    /// <summary>Settings next to the redirected database: the fake model and pauses of milliseconds.</summary>
    private void WriteSettings(double timeoutSeconds = 5, double maxWaitMinutes = 0.5)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dbPath)!, "settings.json"), $$"""
            {
              "Ollama": { "BaseUrl": "http://model-server.test:11999", "Model": "{{FakeModelServer.ModelName}}",
                          "TimeoutSeconds": {{timeoutSeconds.ToString(inv)}}, "MaxRetries": 2, "MaxParallelRequests": 2 },
              "ModelServer": { "RetryDelaySeconds": 0.002, "MaxRetryAfterSeconds": 0.01, "OutageAfterFailures": 2,
                               "ProbeIntervalSeconds": 0.005, "ProbeTimeoutSeconds": 0.2,
                               "MaxWaitMinutes": {{maxWaitMinutes.ToString(inv)}}, "WarmupTimeoutSeconds": 1 }
            }
            """);
    }
}
