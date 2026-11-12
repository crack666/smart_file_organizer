using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Cli;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
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
        // Binary of unknown format: Phase 2 decides it by rule, without a model call.
        File.WriteAllBytes(Path.Combine(_root, "photos", "data.bin"), [0x00, 0x13, 0x37, 0x00, 0xFF]);
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

        // Every file takes part in Phase 2, the binary one included (decided by rule).
        var classification = phases.GetProperty("classification");
        Assert.Equal(4, classification.GetProperty("total").GetInt64());
        Assert.Equal(0, classification.GetProperty("done").GetInt64());
        Assert.Equal(4, classification.GetProperty("pending").GetInt64());

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
        // The binary file got a rule result; only the three readable files went to the model.
        Assert.Equal(3, ollama.FileInputs.Count);

        var status = (await SfoAsync(null, "status", jobId.ToString(), "--json")).Json().GetProperty("result").GetProperty("phases");
        Assert.Equal(4, status.GetProperty("classification").GetProperty("done").GetInt64());
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
    public async Task Analyze_RetryErrors_CallsOnlyFailedFiles_AndShowReportsTheAttempts()
    {
        var jobId = await ScanAsync();
        var failing = new FakeOllamaService { FailWith = i => i.File.FileType == FileType.Image ? "HTTP 500 from the fake" : null };
        var first = await SfoAsync(failing, "analyze", jobId.ToString(), "--phase", "classification", "--json");
        Assert.Equal(ExitCodes.Ok, first.ExitCode);
        var failedFiles = failing.FileInputs.Where(i => i.File.FileType == FileType.Image).Select(i => i.File.Name).Order().ToList();
        Assert.NotEmpty(failedFiles);

        var ollama = new FakeOllamaService();
        var retry = await SfoAsync(ollama, "analyze", jobId.ToString(), "--phase", "classification", "--retry-errors", "--json");

        Assert.True(retry.ExitCode == ExitCodes.Ok, retry.Stdout + retry.Stderr);
        var phase = Assert.Single(retry.Json().GetProperty("result").GetProperty("phases").EnumerateArray());
        Assert.Equal(failedFiles.Count, phase.GetProperty("retried").GetInt32());
        Assert.Equal(0, phase.GetProperty("errors").GetInt32());
        Assert.Equal(failedFiles, ollama.FileInputs.Select(i => i.File.Name).Order().ToList());

        var path = failing.FileInputs.First(i => i.File.FileType == FileType.Image).File.FullPath;
        var show = (await SfoAsync(null, "show", jobId.ToString(), path, "--json")).Json().GetProperty("result").GetProperty("file");
        Assert.Equal(2, show.GetProperty("attempts").GetInt32());
        Assert.Equal("HTTP 500 from the fake", show.GetProperty("previous_error").GetString());
        Assert.Equal("likely", show.GetProperty("certainty").GetString());
        Assert.Equal(0, show.GetProperty("checks").GetArrayLength());

        var likely = (await SfoAsync(null, "files", jobId.ToString(), "--certainty", "likely", "--json")).Json().GetProperty("result");
        Assert.Equal(failedFiles.Count + 2, likely.GetProperty("total").GetInt64()); // and the two text files
        var review = (await SfoAsync(null, "files", jobId.ToString(), "--certainty", "review", "--json")).Json().GetProperty("result");
        Assert.Equal(0, review.GetProperty("total").GetInt64());
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
        Assert.Equal(4, status.GetProperty("result").GetProperty("phases").GetProperty("classification").GetProperty("done").GetInt64());
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

    /// <summary>A library next to the source: notes.txt renamed (same content), letter.txt with other content.</summary>
    private async Task<(long Source, long Library, string LibraryRoot)> ScanSourceAndLibraryAsync()
    {
        var library = Path.Combine(_dir, "library");
        Directory.CreateDirectory(Path.Combine(library, "Listen"));
        Directory.CreateDirectory(Path.Combine(library, "docs"));
        File.WriteAllText(Path.Combine(library, "Listen", "einkauf.txt"), "Shopping list: milk, bread");
        File.WriteAllText(Path.Combine(library, "docs", "letter.txt"), "Dear landlord, again");

        var source = await ScanAsync();
        var scan = await SfoAsync(null, "scan", library, "--role", "library", "--json");
        Assert.True(scan.ExitCode == 0, scan.Stderr + scan.Stdout);
        var job = scan.Json().GetProperty("result").GetProperty("job");
        Assert.Equal("library", job.GetProperty("role").GetString());
        return (source, job.GetProperty("id").GetInt64(), library);
    }

    [Fact]
    public async Task Compare_Json_SetsStatesPerFile_AndCounts()
    {
        var (source, library, libraryRoot) = await ScanSourceAndLibraryAsync();

        var run = await SfoAsync(null, "compare", source.ToString(), "--against", library.ToString(), "--files", "all", "--json");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        var doc = run.Json();
        AssertEnvelope(doc, "compare", ok: true, ExitCodes.Ok);
        var result = doc.GetProperty("result");
        Assert.True(result.GetProperty("complete").GetBoolean());
        Assert.Equal("source", result.GetProperty("source").GetProperty("role").GetString());
        var counts = result.GetProperty("counts");
        Assert.Equal(4, counts.GetProperty("files").GetInt32());
        Assert.Equal(1, counts.GetProperty("backed_up").GetInt32());
        Assert.Equal(2, counts.GetProperty("new").GetInt32());
        Assert.Equal(1, counts.GetProperty("differs").GetInt32());

        var files = result.GetProperty("files").EnumerateArray()
            .ToDictionary(f => Path.GetFileName(f.GetProperty("path").GetString()!), f => f);
        Assert.Equal("backed_up", files["notes.txt"].GetProperty("state").GetString());
        Assert.Equal(Path.Combine(libraryRoot, "Listen", "einkauf.txt"), files["notes.txt"].GetProperty("match_path").GetString());
        Assert.Equal("differs", files["letter.txt"].GetProperty("state").GetString());
        Assert.Equal("new", files["pic.png"].GetProperty("state").GetString());

        var root = result.GetProperty("directories").EnumerateArray()
            .Single(d => d.GetProperty("path").GetString() == Path.GetFullPath(_root));
        Assert.Equal(0.25, root.GetProperty("backed_up_share").GetDouble());
        Assert.Equal(0, result.GetProperty("errors_total").GetInt32());
    }

    [Fact]
    public async Task Compare_UnreadableLibraryFile_ExitsWith1_CompareIncomplete_WithResult()
    {
        var (source, library, libraryRoot) = await ScanSourceAndLibraryAsync();
        File.Delete(Path.Combine(libraryRoot, "Listen", "einkauf.txt"));

        var run = await SfoAsync(null, "compare", source.ToString(), "--against", library.ToString(), "--files", "error", "--json");

        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        var doc = run.Json();
        AssertEnvelope(doc, "compare", ok: false, ExitCodes.Failure);
        Assert.Equal(ErrorCodes.CompareIncomplete, doc.GetProperty("error").GetProperty("code").GetString());
        var result = doc.GetProperty("result");
        Assert.False(result.GetProperty("complete").GetBoolean());
        Assert.Equal(1, result.GetProperty("counts").GetProperty("errors").GetInt32());
        Assert.Equal("notes.txt", Path.GetFileName(Assert.Single(result.GetProperty("files").EnumerateArray()).GetProperty("path").GetString()));
        Assert.Contains(result.GetProperty("errors").EnumerateArray(), e => e.GetProperty("role").GetString() == "library");
    }

    [Fact]
    public async Task Compare_WithSwappedRoles_ExitsWith1_WrongRole()
    {
        var (source, library, _) = await ScanSourceAndLibraryAsync();

        var run = await SfoAsync(null, "compare", library.ToString(), "--against", source.ToString(), "--json");

        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Equal(ErrorCodes.WrongRole, run.Json().GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Analyze_OfLibrary_IsRefused_AndNoModelIsAsked()
    {
        var (_, library, _) = await ScanSourceAndLibraryAsync();
        var ollama = new FakeOllamaService();

        var run = await SfoAsync(ollama, "analyze", library.ToString(), "--json");

        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Equal(ErrorCodes.WrongRole, run.Json().GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(ollama.FileInputs);
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("scan", "somewhere", "--resume", "1")]
    [InlineData("scan", "somewhere", "--role", "bestand")]
    [InlineData("scan", "--resume", "1", "--role", "library")]
    [InlineData("compare")]
    [InlineData("compare", "1", "--files", "everything")]
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

    // ------------------------------------------------------------------ scan rules (#21)

    private string WriteRules(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private const string LaptopRules = """
        {
          "name": "laptop",
          "rules": [
            { "match": "bin", "action": "user_data", "why": "exports live here" },
            { "match": "Games", "action": "shallow", "why": "installed games" }
          ]
        }
        """;

    [Fact]
    public async Task RulesCheck_ThreePaths_ReportsRuleAndReasonAsJson()
    {
        Directory.CreateDirectory(Path.Combine(_root, "app", "bin"));
        var bin = Path.Combine(_root, "app", "bin");
        var docs = Path.Combine(_root, "Documents");
        var hidden = Path.Combine(_root, "app", "bin", "Documents");

        var run = await SfoAsync(null, "rules", "check", bin, docs, hidden, "--root", _root, "--json");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        var doc = run.Json();
        AssertEnvelope(doc, "rules check", ok: true, ExitCodes.Ok);
        var result = doc.GetProperty("result");
        Assert.Equal("default", result.GetProperty("rules").GetProperty("name").GetString());
        var checks = result.GetProperty("checks").EnumerateArray().ToList();
        Assert.Equal(3, checks.Count);

        Assert.Equal("skip", checks[0].GetProperty("decision").GetString());
        Assert.Equal("rule", checks[0].GetProperty("basis").GetString());
        Assert.Equal("bin", checks[0].GetProperty("rule").GetProperty("match").GetString());
        Assert.Equal("skip", checks[0].GetProperty("rule").GetProperty("action").GetString());
        Assert.Contains("build output", checks[0].GetProperty("reason").GetString());
        Assert.True(checks[0].GetProperty("exists").GetBoolean());
        Assert.Equal(2, checks[0].GetProperty("depth").GetInt32());

        Assert.Equal("descend", checks[1].GetProperty("decision").GetString());
        Assert.Equal("user_data", checks[1].GetProperty("rule").GetProperty("action").GetString());
        Assert.True(checks[1].GetProperty("reached").GetBoolean());

        // Its own rule says user data, but the scan never gets there: bin above it is skipped.
        Assert.False(checks[2].GetProperty("reached").GetBoolean());
        Assert.Equal(bin, checks[2].GetProperty("blocked_by").GetProperty("path").GetString());
        Assert.Equal("skip", checks[2].GetProperty("blocked_by").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task RulesCheck_WithOwnRules_UsesThem()
    {
        var rules = WriteRules("laptop.json", LaptopRules);

        var run = await SfoAsync(null, "rules", "check", Path.Combine(_root, "bin"), "--root", _root, "--rules", rules, "--json");

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        var result = run.Json().GetProperty("result");
        Assert.Equal("laptop", result.GetProperty("rules").GetProperty("name").GetString());
        Assert.Equal(rules, result.GetProperty("rules").GetProperty("source").GetString());
        Assert.Equal("descend", result.GetProperty("checks")[0].GetProperty("decision").GetString());
    }

    [Fact]
    public async Task RulesCheck_PathOutsideRoot_IsUsageError()
    {
        var run = await SfoAsync(null, "rules", "check", Path.Combine(_dir, "elsewhere"), "--root", _root, "--json");
        Assert.Equal(ExitCodes.Usage, run.ExitCode);
    }

    [Fact]
    public async Task RulesShow_WithoutJson_PrintsAValidRuleFile()
    {
        var run = await SfoAsync(null, "rules", "show");

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        var rules = Scanning.Rules.ScanRulesFile.Parse(run.Stdout, "x", "stdout");
        Assert.Equal(Scanning.Rules.ScanRulesFile.Hash(Scanning.Rules.ScanRulesFile.Default), Scanning.Rules.ScanRulesFile.Hash(rules));
    }

    [Fact]
    public async Task Scan_WithOwnRules_ScansSkippedFolder_RecordsGamesShallow_AndStoresProfile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "export.csv"), "a;b");
        Directory.CreateDirectory(Path.Combine(_root, "Games", "Quake"));
        File.WriteAllText(Path.Combine(_root, "Games", "readme.txt"), "games");
        File.WriteAllText(Path.Combine(_root, "Games", "Quake", "save.dat"), "save");
        var rules = WriteRules("laptop.json", LaptopRules);

        var withDefault = (await SfoAsync(null, "scan", _root, "--json")).Json().GetProperty("result").GetProperty("job");
        var withOwn = await SfoAsync(null, "scan", _root, "--rules", rules, "--json");

        Assert.True(withOwn.ExitCode == ExitCodes.Ok, withOwn.Stdout + withOwn.Stderr);
        var job = withOwn.Json().GetProperty("result").GetProperty("job");
        Assert.Equal("laptop", job.GetProperty("rules").GetProperty("name").GetString());
        Assert.Equal("default", withDefault.GetProperty("rules").GetProperty("name").GetString());
        Assert.Equal("built-in", withDefault.GetProperty("rules").GetProperty("source").GetString());

        // Default: 4 base files + Games/readme.txt + Games/Quake/save.dat; bin skipped.
        Assert.Equal(6, withDefault.GetProperty("files_processed").GetInt64());
        // Own rules: 4 base files + bin/export.csv + Games/readme.txt; Quake not entered.
        Assert.Equal(6, job.GetProperty("files_processed").GetInt64());
        Assert.Equal("completed", job.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ScanResume_WithOtherRules_ReopensJob_AndReportsRulesChanged()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "export.csv"), "a;b");
        var jobId = await ScanAsync();
        var rules = WriteRules("laptop.json", LaptopRules);

        var resume = await SfoAsync(null, "scan", "--resume", jobId.ToString(), "--rules", rules, "--json");

        Assert.Equal(ExitCodes.Ok, resume.ExitCode);
        var result = resume.Json().GetProperty("result");
        Assert.True(result.GetProperty("rules_changed").GetBoolean());
        Assert.Equal("laptop", result.GetProperty("job").GetProperty("rules").GetProperty("name").GetString());
        Assert.Equal(5, result.GetProperty("job").GetProperty("files_processed").GetInt64());
    }

    [Theory]
    [InlineData("""{ "rules": [ { "match": "bin", "action": "ignore" } ] }""", "rules_invalid")]
    [InlineData(null, "path_not_found")]
    public async Task Scan_WithBadRules_ExitsWith1(string? json, string code)
    {
        var rules = json == null ? Path.Combine(_dir, "missing.json") : WriteRules("bad.json", json);

        var run = await SfoAsync(null, "scan", _root, "--rules", rules, "--json");

        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Equal(code, run.Json().GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Jobs_AfterScanWithSeveralSkippedFolders_ShowsCompleted()
    {
        foreach (var skipped in new[] { "bin", "obj", "node_modules" })
        {
            Directory.CreateDirectory(Path.Combine(_root, skipped));
            File.WriteAllText(Path.Combine(_root, skipped, "x.txt"), "x");
        }
        await ScanAsync();

        var jobs = await SfoAsync(null, "jobs", "--json");

        Assert.Equal("completed", jobs.Json().GetProperty("result").GetProperty("jobs")[0].GetProperty("status").GetString());
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
