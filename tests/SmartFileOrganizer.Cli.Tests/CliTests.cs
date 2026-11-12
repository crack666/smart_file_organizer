using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Cli;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Hosting.Tests;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// Runs <c>sfo</c> in-process against a temp folder and a temp database. Ollama is either the fake
/// or the real client pointed at a closed local port; no test talks to a real Ollama.
/// </summary>
public class CliTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _root;

    public CliTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_cli_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _root = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(Path.Combine(_root, "photos"));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "Shopping list: milk, bread");
        File.WriteAllText(Path.Combine(_root, "docs", "letter.txt"), "Dear landlord");
        File.WriteAllBytes(Path.Combine(_root, "photos", "pic.png"), [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        File.WriteAllText(Path.Combine(_root, "photos", "data.bin"), "not eligible for classification");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr)
    {
        public JsonElement Json()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.Clone();
        }
    }

    private async Task<Run> SfoAsync(FakeOllamaService? ollama, CancellationToken ct, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost
        {
            Out = stdout,
            Error = stderr,
            ConfigureServices = services =>
            {
                if (ollama != null)
                    services.AddSingleton<IOllamaService>(ollama);
            }
        };

        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], host, ct);
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private Task<Run> SfoAsync(FakeOllamaService? ollama, params string[] args) => SfoAsync(ollama, CancellationToken.None, args);

    private static void AssertEnvelope(JsonElement doc, string command, bool ok, int exitCode)
    {
        Assert.Equal(CliJson.SchemaVersion, doc.GetProperty("schema_version").GetInt32());
        Assert.Equal(command, doc.GetProperty("command").GetString());
        Assert.Equal(ok, doc.GetProperty("ok").GetBoolean());
        Assert.Equal(exitCode, doc.GetProperty("exit_code").GetInt32());
    }

    private async Task<long> ScanAsync()
    {
        var scan = await SfoAsync(null, "scan", _root, "--json");
        Assert.True(scan.ExitCode == 0, scan.Stderr + scan.Stdout);
        return scan.Json().GetProperty("result").GetProperty("job").GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task ScanThenStatus_Json_ReportsExpectedCounters()
    {
        var scan = await SfoAsync(null, "scan", _root, "--json");

        Assert.Equal(ExitCodes.Ok, scan.ExitCode);
        var scanDoc = scan.Json();
        AssertEnvelope(scanDoc, "scan", ok: true, ExitCodes.Ok);
        Assert.Equal(JsonValueKind.Null, scanDoc.GetProperty("error").ValueKind);
        var job = scanDoc.GetProperty("result").GetProperty("job");
        Assert.Equal("completed", job.GetProperty("status").GetString());
        Assert.Equal(Path.GetFullPath(_root), job.GetProperty("root_path").GetString());
        Assert.Equal(4, job.GetProperty("files_processed").GetInt64());
        Assert.EndsWith("Z", job.GetProperty("created_at").GetString());
        var jobId = job.GetProperty("id").GetInt64();

        var status = await SfoAsync(null, "status", jobId.ToString(), "--json");

        Assert.Equal(ExitCodes.Ok, status.ExitCode);
        var doc = status.Json();
        AssertEnvelope(doc, "status", ok: true, ExitCodes.Ok);
        var result = doc.GetProperty("result");
        Assert.Equal(jobId, result.GetProperty("job").GetProperty("id").GetInt64());

        var phases = result.GetProperty("phases");
        var scanPhase = phases.GetProperty("scan");
        Assert.Equal("completed", scanPhase.GetProperty("status").GetString());
        Assert.Equal(4, scanPhase.GetProperty("files_processed").GetInt64());
        Assert.Equal(3, scanPhase.GetProperty("directories_processed").GetInt64());
        Assert.Equal(0, scanPhase.GetProperty("errors").GetInt64());

        // Three text/image files are eligible; the .bin file is not.
        var classification = phases.GetProperty("classification");
        Assert.Equal(3, classification.GetProperty("total").GetInt64());
        Assert.Equal(0, classification.GetProperty("done").GetInt64());
        Assert.Equal(3, classification.GetProperty("pending").GetInt64());

        foreach (var dirPhase in new[] { "pre_assessment", "summary" })
        {
            Assert.Equal(3, phases.GetProperty(dirPhase).GetProperty("total").GetInt64());
            Assert.Equal(3, phases.GetProperty(dirPhase).GetProperty("pending").GetInt64());
        }

        Assert.Equal(0, result.GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public async Task Analyze_WithFakeOllama_RunsAllPhases_AndStatusShowsThemDone()
    {
        var jobId = await ScanAsync();
        var ollama = new FakeOllamaService();

        var analyze = await SfoAsync(ollama, "analyze", jobId.ToString(), "--json", "--parallel", "2", "--model", "fake-model");

        Assert.True(analyze.ExitCode == ExitCodes.Ok, analyze.Stdout + analyze.Stderr);
        var result = analyze.Json().GetProperty("result");
        Assert.Equal("fake-model", result.GetProperty("model").GetString());
        Assert.Equal(2, result.GetProperty("parallel").GetInt32());
        var phases = result.GetProperty("phases").EnumerateArray().Select(p => p.GetProperty("phase").GetString()).ToList();
        Assert.Equal(["pre_assessment", "classification", "summary"], phases);
        Assert.Equal(3, ollama.FileInputs.Count);

        var status = (await SfoAsync(null, "status", jobId.ToString(), "--json")).Json().GetProperty("result").GetProperty("phases");
        Assert.Equal(3, status.GetProperty("classification").GetProperty("done").GetInt64());
        Assert.Equal(0, status.GetProperty("classification").GetProperty("pending").GetInt64());
        Assert.Equal(3, status.GetProperty("pre_assessment").GetProperty("done").GetInt64());
        Assert.Equal(3, status.GetProperty("summary").GetProperty("done").GetInt64());
    }

    [Fact]
    public async Task Analyze_SinglePhase_RunsOnlyThatPhase()
    {
        var jobId = await ScanAsync();
        var ollama = new FakeOllamaService();

        var analyze = await SfoAsync(ollama, "analyze", jobId.ToString(), "--phase", "classification", "--json");

        Assert.Equal(ExitCodes.Ok, analyze.ExitCode);
        Assert.Equal("classification", Assert.Single(analyze.Json().GetProperty("result").GetProperty("phases").EnumerateArray())
            .GetProperty("phase").GetString());
        Assert.Empty(ollama.PreAssessedDirectories);
        Assert.Empty(ollama.SummarizedDirectories);
    }

    [Fact]
    public async Task Analyze_OllamaUnreachable_ExitsWith3()
    {
        var jobId = await ScanAsync();
        WriteSettingsWithOllamaAt(ClosedLocalPort());

        // No fake: the real Ollama client against a closed port.
        var analyze = await SfoAsync(null, "analyze", jobId.ToString(), "--json");

        Assert.Equal(ExitCodes.OllamaUnavailable, analyze.ExitCode);
        var doc = analyze.Json();
        AssertEnvelope(doc, "analyze", ok: false, ExitCodes.OllamaUnavailable);
        Assert.Equal(ErrorCodes.OllamaUnavailable, doc.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Doctor_OllamaUnreachable_ExitsWith3_AndReportsDatabase()
    {
        WriteSettingsWithOllamaAt(ClosedLocalPort());

        var doctor = await SfoAsync(null, "doctor", "--json");

        Assert.Equal(ExitCodes.OllamaUnavailable, doctor.ExitCode);
        var result = doctor.Json().GetProperty("result");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.True(result.GetProperty("database").GetProperty("ok").GetBoolean());
        Assert.Equal(Path.GetFullPath(_dbPath), result.GetProperty("database").GetProperty("path").GetString());
        Assert.False(result.GetProperty("ollama").GetProperty("reachable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("model").GetProperty("available").ValueKind);
    }

    [Fact]
    public async Task Analyze_Cancelled_ExitsWith130_AndSecondRunFinishes()
    {
        var jobId = await ScanAsync();
        using var cts = new CancellationTokenSource();
        var ollama = new FakeOllamaService { BeforeClassify = _ => cts.Cancel() };

        var first = await SfoAsync(ollama, cts.Token, "analyze", jobId.ToString(), "--phase", "classification", "--json");

        Assert.Equal(ExitCodes.Cancelled, first.ExitCode);
        Assert.Equal(ErrorCodes.Cancelled, first.Json().GetProperty("error").GetProperty("code").GetString());

        var resumed = await SfoAsync(new FakeOllamaService(), "analyze", jobId.ToString(), "--phase", "classification", "--json");
        Assert.Equal(ExitCodes.Ok, resumed.ExitCode);
        var status = (await SfoAsync(null, "status", jobId.ToString(), "--json")).Json();
        Assert.Equal(3, status.GetProperty("result").GetProperty("phases").GetProperty("classification").GetProperty("done").GetInt64());
    }

    [Fact]
    public async Task Jobs_ListsCreatedJobs()
    {
        var first = await ScanAsync();
        var second = await ScanAsync();

        var jobs = await SfoAsync(null, "jobs", "--json");

        Assert.Equal(ExitCodes.Ok, jobs.ExitCode);
        var ids = jobs.Json().GetProperty("result").GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Contains(first, ids);
        Assert.Contains(second, ids);
    }

    [Fact]
    public async Task ScanResume_OfCompletedJob_ReturnsItUnchanged()
    {
        var jobId = await ScanAsync();

        var resume = await SfoAsync(null, "scan", "--resume", jobId.ToString(), "--json");

        Assert.Equal(ExitCodes.Ok, resume.ExitCode);
        var result = resume.Json().GetProperty("result");
        Assert.True(result.GetProperty("resumed").GetBoolean());
        Assert.Equal(4, result.GetProperty("job").GetProperty("files_processed").GetInt64());
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("scan", "somewhere", "--resume", "1")]
    [InlineData("analyze", "1", "--parallel", "9")]
    [InlineData("analyze", "1", "--phase", "everything")]
    [InlineData("status", "not-a-number")]
    [InlineData("frobnicate")]
    public async Task WrongInvocation_ExitsWith2_WithUsageEnvelope(params string[] args)
    {
        var run = await SfoAsync(null, [.. args, "--json"]);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        var doc = run.Json();
        Assert.False(doc.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.Usage, doc.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task WrongInvocation_WithoutJson_WritesNothingToStdout()
    {
        var run = await SfoAsync(null, "status");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Equal(string.Empty, run.Stdout);
        Assert.Contains("usage", run.Stderr);
    }

    [Fact]
    public async Task UnknownJob_ExitsWith1()
    {
        await ScanAsync();

        var run = await SfoAsync(null, "status", "999", "--json");

        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Equal(ErrorCodes.JobNotFound, run.Json().GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ScanMissingFolder_ExitsWith1()
    {
        var run = await SfoAsync(null, "scan", Path.Combine(_dir, "nope"), "--json");

        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Equal(ErrorCodes.PathNotFound, run.Json().GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ProgressJsonl_EveryStderrLineIsJson_AndStdoutIsOneDocument()
    {
        var run = await SfoAsync(null, "scan", _root, "--json", "--progress", "jsonl", "--verbose");

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        run.Json(); // stdout parses as exactly one document

        var lines = run.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(lines);
        var types = new HashSet<string>();
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal(CliJson.SchemaVersion, doc.RootElement.GetProperty("schema_version").GetInt32());
            types.Add(doc.RootElement.GetProperty("type").GetString()!);
        }
        Assert.Contains("progress", types);
        Assert.Contains("log", types);
    }

    [Fact]
    public async Task WithoutJson_StdoutIsText()
    {
        var run = await SfoAsync(null, "scan", _root, "--progress", "none");

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        Assert.StartsWith("#", run.Stdout.TrimStart());
        Assert.Contains("completed", run.Stdout);
    }

    /// <summary>Settings follow a redirected database, so this file is what the CLI reads.</summary>
    private void WriteSettingsWithOllamaAt(int port)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dbPath)!, "settings.json"),
            $$"""{ "Ollama": { "BaseUrl": "http://127.0.0.1:{{port}}" } }""");
    }

    /// <summary>A local port nothing listens on: bind to an ephemeral port, then release it.</summary>
    private static int ClosedLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
