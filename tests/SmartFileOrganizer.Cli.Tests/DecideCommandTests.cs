using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Infrastructure.Ollama;
using SmartFileOrganizer.Infrastructure.Persistence.Migrations;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>sfo decide</c>, stage 2 of https://github.com/crack666/smart_file_organizer/issues/37: a generated source analysed
/// once through the real <see cref="OllamaService"/> against <see cref="FakeModelServer"/>, then decided against two
/// target structures. The model is asked once per file, in stage 1, and never again.
/// </summary>
public class DecideCommandTests : IDisposable
{
    private const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _source;
    private readonly FakeModelServer _server = new();

    public DecideCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_decide_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _source = Path.Combine(_dir, "stick");

        Directory.CreateDirectory(Path.Combine(_source, "urlaub"));
        File.WriteAllBytes(Path.Combine(_source, "urlaub", "IMG_0001.png"), Convert.FromBase64String(Png1x1));
        Write("rechnungen/strom.txt", "Stadtwerke, Rechnung Nr. 2019-0042 vom 3. April 2019, Strom, 81,20 EUR.");
        Write("vertraege/miete.txt", "Mietvertrag zwischen Hausverwaltung Meier und dem Mieter, ab 1. Mai 2018.");
        Write("notizen/einkauf.txt", "Milch, Brot, Kaffee, Batterien.");
        Write("bank/kontoauszug_2020.txt", "Kontoauszug Nr. 3, Saldo alt, Saldo neu.");

        _server.Descriptions["IMG_0001.png"] =
            "\"scene\":\"beach with sea\",\"activity\":\"swimming\",\"setting\":\"outdoor\",\"season\":\"summer\",\"occasion\":\"holiday\",\"people\":true";
        _server.Descriptions["strom.txt"] = "\"document_kind\":\"invoice\",\"issuer\":\"Stadtwerke\",\"year\":2019";
        _server.Descriptions["miete.txt"] = "\"document_kind\":\"contract\",\"issuer\":\"Hausverwaltung Meier\",\"year\":2018";
        WriteSettings();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    // ── Fertig, wenn ─────────────────────────────────────────────────────

    [Fact]
    public async Task AChangedTargetStructure_GivesNewTargets_WithoutASingleCallOfStage1_AndEveryTargetNamesItsFacts()
    {
        var job = await ScanAndAnalyzeAsync();
        var calls = _server.Log.Count;
        Assert.True(_server.Answered("classification") >= 4);

        var first = await DecideAsync(job, WriteTargets("a.json", """
            [ { "id": "photos", "path": "Photos", "pattern": "{occasion}", "holds": ["image", "video"] },
              { "id": "documents", "path": "Documents", "pattern": "{area}/{year}", "holds": ["document"] } ]
            """));
        Assert.Equal("Photos/holiday", Target(first, "urlaub/IMG_0001.png"));
        Assert.Equal("Documents/invoice/2019", Target(first, "rechnungen/strom.txt"));
        Assert.Equal("Documents/contract/2018", Target(first, "vertraege/miete.txt"));
        // No year in the notes: the folder level of the pattern stays open, and says so.
        Assert.Equal("Documents", Target(first, "notizen/einkauf.txt"));
        Assert.Equal(["year"], Item(first, "notizen/einkauf.txt").GetProperty("target").GetProperty("unresolved").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(
            [("file_type", "document", "scan"), ("document_kind", "invoice", "model"), ("year", "2019", "model")],
            TargetBasis(first, "rechnungen/strom.txt"));
        Assert.Equal([("file_type", "image", "scan"), ("occasion", "holiday", "model")], TargetBasis(first, "urlaub/IMG_0001.png"));

        // The person rewrites the structure: other folders, other patterns, German placeholder names.
        var second = await DecideAsync(job, WriteTargets("b.json", """
            [ { "id": "bilder", "path": "Bilder", "pattern": "{Anlass}/{szene}", "holds": ["image"] },
              { "id": "papiere", "path": "Archiv/Papiere", "pattern": "{Aussteller}/{Jahr}", "holds": ["document"] } ]
            """));
        Assert.Equal("Bilder/holiday/beach with sea", Target(second, "urlaub/IMG_0001.png"));
        Assert.Equal("Archiv/Papiere/Stadtwerke/2019", Target(second, "rechnungen/strom.txt"));
        Assert.Equal("Archiv/Papiere/Hausverwaltung Meier/2018", Target(second, "vertraege/miete.txt"));
        Assert.Equal("Documents/invoice/2019", Item(second, "rechnungen/strom.txt").GetProperty("previous_target").GetString());
        Assert.True(Item(second, "rechnungen/strom.txt").GetProperty("changed").GetBoolean());
        Assert.Equal(4, second.GetProperty("changed").GetInt64());
        Assert.Equal(
            [("file_type", "document", "scan"), ("issuer", "Stadtwerke", "model"), ("year", "2019", "model")],
            TargetBasis(second, "rechnungen/strom.txt"));

        // Not one model call since the analysis: no description, no classification, not even a probe.
        Assert.Equal(calls, _server.Log.Count);

        // Every target names the facts it rests on: the file type, and one input per filled placeholder.
        foreach (var item in second.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("target").ValueKind != JsonValueKind.Object) continue;
            var basis = item.GetProperty("target").GetProperty("basis").EnumerateArray().ToList();
            Assert.Contains(basis, b => b.GetProperty("key").GetString() == "file_type");
            var unresolved = item.GetProperty("target").GetProperty("unresolved").GetArrayLength();
            Assert.Equal(1 + 2 - unresolved, basis.Count);
            Assert.All(basis, b => Assert.False(string.IsNullOrEmpty(b.GetProperty("source").GetString())));
        }

        // Stored: the last run replaced the first.
        using var conn = Open();
        Assert.Equal("Archiv/Papiere/Stadtwerke/2019", conn.ExecuteScalar<string>(
            "SELECT d.target_path FROM file_decisions d JOIN file_nodes f ON f.id = d.file_node_id WHERE f.name = 'strom.txt'"));
        Assert.Equal(5, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM file_decisions"));
    }

    [Fact]
    public async Task AMarkedFile_RestsOnItsMarkerAlone_AndIsNeitherDescribedNorPlaced()
    {
        var job = await ScanAndAnalyzeAsync();
        var run = await DecideAsync(job, WriteTargets("a.json", """
            [ { "id": "documents", "path": "Documents", "pattern": "{issuer}", "holds": ["document"] } ]
            """));

        var marked = Item(run, "bank/kontoauszug_2020.txt");
        Assert.Equal("keep", marked.GetProperty("worth").GetString());
        Assert.Equal("sensitive", marked.GetProperty("rule").GetString());
        var basis = Assert.Single(marked.GetProperty("basis").EnumerateArray());
        Assert.Equal("sensitive", basis.GetProperty("key").GetString());
        Assert.Equal("finance", basis.GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, marked.GetProperty("target").ValueKind);

        using var conn = Open();
        Assert.Equal(0, conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM file_facts ff JOIN file_nodes f ON f.id = ff.file_node_id WHERE f.name = 'kontoauszug_2020.txt' AND ff.source = 'model'"));
        Assert.True(conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM file_facts ff JOIN file_nodes f ON f.id = ff.file_node_id WHERE f.name = 'strom.txt' AND ff.source = 'model'") >= 3);
    }

    [Fact]
    public async Task WithoutTargets_WorthOnly_AndChangedListsOnlyWhatMoved()
    {
        var job = await ScanAndAnalyzeAsync();
        var plain = await DecideAsync(job, null);
        Assert.Equal(5, plain.GetProperty("files").GetInt64());
        Assert.Equal(5, plain.GetProperty("by_worth").GetProperty("keep").GetInt64());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("targets").GetProperty("structure").ValueKind);
        Assert.All(plain.GetProperty("items").EnumerateArray(), i => Assert.Equal(JsonValueKind.Null, i.GetProperty("target").ValueKind));
        Assert.True(plain.GetProperty("findings").TryGetProperty("not_backed_up", out _));

        var again = await SfoAsync("decide", job.ToString(), "--changed", "--json");
        Assert.True(again.ExitCode == 0, again.Stderr + again.Stdout);
        var result = again.Json().GetProperty("result");
        Assert.Equal(0, result.GetProperty("changed").GetInt64());
        Assert.Equal(0, result.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task ResultsStoredBeforeStage1_StayReadable_AndAreDecidedWithoutAModel()
    {
        // A database of the step before decisions, with a job analysed then: results and metadata facts, no description.
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        using (var conn = Open())
        {
            await new SchemaMigrator(SchemaMigrator.BuiltIn.Where(m => m.Version < 20), NullLogger.Instance).MigrateAsync(conn);
            await conn.ExecuteAsync($"""
                INSERT INTO scan_jobs (root_path, status, created_at, role) VALUES ('{_source}', 3, '2026-11-01T00:00:00Z', 'source');
                INSERT INTO file_nodes (job_id, full_path, name, parent_path, root_path, relative_path, relative_dir, size, last_write_time, extension, file_type, status, scanned_at)
                VALUES (1, '{Path.Combine(_source, "urlaub", "IMG_0001.png")}', 'IMG_0001.png', '{Path.Combine(_source, "urlaub")}', '{_source}', 'urlaub/IMG_0001.png', 'urlaub', 70, '2019-07-14T10:00:00Z', '.png', {(int)FileType.Image}, {(int)FileNodeStatus.AiAnalyzed}, '2026-11-01T00:00:00Z');
                INSERT INTO ai_results (file_node_id, category, importance, confidence, summary, model_used, analyzed_at, is_ai_result, origin, certainty)
                VALUES (1, {(int)FileCategory.Photo}, {(int)Importance.High}, 0.9, 'A beach.', 'old-model', '2026-11-01T00:00:00Z', 1, 'model', 'likely');
                INSERT INTO file_facts (file_node_id, fact, value, source, recorded_at)
                VALUES (1, 'taken_at', '2019-07-14T10:00:00', 'metadata', '2026-11-01T00:00:00Z');
                """);
        }

        var run = await DecideAsync(1, WriteTargets("a.json", """
            [ { "id": "photos", "path": "Photos", "pattern": "{occasion} {year}", "holds": ["image"] } ]
            """));

        var photo = Item(run, "urlaub/IMG_0001.png");
        Assert.Equal("keep", photo.GetProperty("worth").GetString());
        Assert.Equal("model", photo.GetProperty("rule").GetString());
        Assert.Contains(photo.GetProperty("basis").EnumerateArray(), b => b.GetProperty("key").GetString() == "category" && b.GetProperty("value").GetString() == "photo");
        // No occasion was described then: the target stays at its folder, names the open placeholder and the year it has.
        Assert.Equal("Photos", Target(run, "urlaub/IMG_0001.png"));
        Assert.Equal(["occasion"], photo.GetProperty("target").GetProperty("unresolved").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(_server.Log);
    }

    [Fact]
    public async Task ABrokenTargetFile_IsRefusedBeforeAnythingIsDecided()
    {
        var job = await ScanAndAnalyzeAsync();
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, """{ "schema_version": 1, "kind": "sfo_target_structure", "targets": [ { "id": "A B", "path": "../x", "holds": ["photo"] } ] }""");
        var run = await SfoAsync("decide", job.ToString(), "--targets", path, "--json");
        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Equal(ErrorCodes.TargetsInvalid, run.Json().GetProperty("error").GetProperty("code").GetString());
        using var conn = Open();
        Assert.Equal(0, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM file_decisions"));

        var missing = await SfoAsync("decide", "999", "--json");
        Assert.Equal(ErrorCodes.JobNotFound, missing.Json().GetProperty("error").GetProperty("code").GetString());
    }

    // ── the library root of the targets (Issue 71) ───────────────────────

    private const string PhotosAndDocuments = """
        [ { "id": "photos", "path": "Photos", "pattern": null, "holds": ["image"] },
          { "id": "documents", "path": "Documents", "pattern": null, "holds": ["document"] } ]
        """;

    /// <summary>
    /// The targets lie in a library: that of the structure's root, the named one, or the only one known. With several and
    /// none named they stay relative; a root outside every library gives no target at all.
    /// </summary>
    [Fact]
    public async Task Targets_LieInALibraryRoot_OrSayWhyNot()
    {
        var job = await ScanAndAnalyzeAsync();
        var free = WriteTargets("free.json", PhotosAndDocuments);

        var none = await DecideAsync(job, free);
        Assert.Equal("no_library", Storage(none).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, Storage(none).GetProperty("space").ValueKind);
        Assert.Equal("Documents", Target(none, "rechnungen/strom.txt"));
        Assert.Equal(JsonValueKind.Null, Full(none, "rechnungen/strom.txt").ValueKind);

        var firstRoot = Directory.CreateDirectory(Path.Combine(_dir, "ablage")).FullName;
        File.WriteAllText(Path.Combine(firstRoot, "readme.txt"), "first");
        var first = await ScanLibraryAsync(firstRoot);
        var only = await DecideAsync(job, free);
        Assert.Equal("bound", Storage(only).GetProperty("status").GetString());
        Assert.Equal(first, Storage(only).GetProperty("library_job_id").GetInt64());
        Assert.Equal(Path.Combine(firstRoot, "Documents"), Full(only, "rechnungen/strom.txt").GetString());
        // Nobody chose that library, it is only the one there is: it is not stored with the decision.
        Assert.False(Item(only, "rechnungen/strom.txt").GetProperty("changed").GetBoolean());
        Assert.Equal(0, StoredLibrary());

        var secondRoot = Directory.CreateDirectory(Path.Combine(_dir, "ablage-zwei")).FullName;
        File.WriteAllText(Path.Combine(secondRoot, "readme.txt"), "second");
        var second = await ScanLibraryAsync(secondRoot);
        var several = await DecideAsync(job, free);
        Assert.Equal("several_libraries", Storage(several).GetProperty("status").GetString());
        Assert.Contains("--library", Storage(several).GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, Full(several, "rechnungen/strom.txt").ValueKind);

        var named = await DecideAsync(job, free, "--library", second.ToString());
        Assert.Equal(Path.Combine(secondRoot, "Documents"), Full(named, "rechnungen/strom.txt").GetString());
        Assert.True(Item(named, "rechnungen/strom.txt").GetProperty("changed").GetBoolean());

        // The root of the structure below a library: the path is relative to the library, the plan needs nothing else.
        var below = await DecideAsync(job, WriteTargets("below.json", PhotosAndDocuments, Path.Combine(secondRoot, "Neu")));
        Assert.Equal(second, Storage(below).GetProperty("library_job_id").GetInt64());
        Assert.Equal("Neu", Storage(below).GetProperty("under").GetString());
        Assert.Equal("Neu/Documents", Target(below, "rechnungen/strom.txt"));
        Assert.Equal(Path.Combine(secondRoot, "Neu", "Documents"), Full(below, "rechnungen/strom.txt").GetString());

        // Chosen: stored with its library.
        Assert.Equal(second, StoredLibrary());

        var outside = await DecideAsync(job, WriteTargets("outside.json", PhotosAndDocuments, Path.Combine(_dir, "elsewhere")));
        Assert.Equal("outside_library", Storage(outside).GetProperty("status").GetString());
        Assert.Equal(0, outside.GetProperty("targets").GetProperty("proposed").GetInt64());
        Assert.Equal(5, outside.GetProperty("by_worth").GetProperty("keep").GetInt64());
        Assert.All(outside.GetProperty("items").EnumerateArray(), i => Assert.Equal(JsonValueKind.Null, i.GetProperty("target").ValueKind));

        // A root in one library and another one named, a job that is no library, and --library without a structure.
        foreach (var (args, exit, code) in new[]
                 {
                     (new[] { "--targets", Path.Combine(_dir, "below.json"), "--library", first.ToString() }, ExitCodes.Failure, ErrorCodes.LibraryInvalid),
                     (new[] { "--targets", free, "--library", job.ToString() }, ExitCodes.Failure, ErrorCodes.LibraryInvalid),
                     (new[] { "--library", first.ToString() }, ExitCodes.Usage, ErrorCodes.Usage)
                 })
        {
            var refused = await SfoAsync(["decide", job.ToString(), .. args, "--json"]);
            Assert.Equal(exit, refused.ExitCode);
            Assert.Equal(code, refused.Json().GetProperty("error").GetProperty("code").GetString());
        }
    }

    /// <summary>Room at the library root: what the targets need there, without what a library holds already; a warning when it is too little.</summary>
    [Fact]
    public async Task Targets_WeighTheRoomAtTheLibraryRoot()
    {
        var job = await ScanAndAnalyzeAsync();
        var root = Directory.CreateDirectory(Path.Combine(_dir, "ablage", "alt")).Parent!.FullName;
        File.Copy(Path.Combine(_source, "rechnungen", "strom.txt"), Path.Combine(root, "alt", "strom.txt"));
        var library = await ScanLibraryAsync(root);
        var targets = WriteTargets("a.json", PhotosAndDocuments);
        long Size(string rel) => new FileInfo(Path.Combine(_source, rel.Replace('/', Path.DirectorySeparatorChar))).Length;
        // The bank statement is marked and has no target.
        string[] placed = ["urlaub/IMG_0001.png", "vertraege/miete.txt", "notizen/einkauf.txt"];
        var withoutInvoice = placed.Sum(Size);

        // Not compared yet: every placed file counts.
        _probe = new FixedProbe(withoutInvoice);
        var before = Storage(await DecideAsync(job, targets));
        Assert.Equal(4, before.GetProperty("needed_files").GetInt64());
        Assert.Equal(withoutInvoice + Size("rechnungen/strom.txt"), before.GetProperty("needed_bytes").GetInt64());
        Assert.Equal("not_enough", before.GetProperty("space").GetString());
        Assert.Contains($"Library job {library}", before.GetProperty("warning").GetString());

        // The library holds the invoice: it needs no room there.
        Assert.Equal(0, (await SfoAsync("compare", job.ToString(), "--against", library.ToString(), "--json")).ExitCode);
        var after = Storage(await DecideAsync(job, targets));
        Assert.Equal(3, after.GetProperty("needed_files").GetInt64());
        Assert.Equal(withoutInvoice, after.GetProperty("needed_bytes").GetInt64());
        Assert.Equal(withoutInvoice, after.GetProperty("free_bytes").GetInt64());
        Assert.Equal("enough", after.GetProperty("space").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("warning").ValueKind);

        _probe = new FixedProbe(null);
        var unknown = Storage(await DecideAsync(job, targets));
        Assert.Equal("unknown", unknown.GetProperty("space").GetString());
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("free_bytes").ValueKind);

        var text = await SfoAsync("decide", job.ToString(), "--targets", targets);
        Assert.Contains($"in library job {library}", text.Stdout);
        Assert.Contains("free space unknown", text.Stdout);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private FixedProbe? _probe;

    private sealed class FixedProbe(long? free) : IFreeSpaceProbe
    {
        public long? AvailableBytes(string path) => free;
    }

    private async Task<long> ScanLibraryAsync(string path)
    {
        var scan = await SfoAsync("scan", path, "--role", "library", "--json");
        Assert.True(scan.ExitCode == 0, scan.Stderr + scan.Stdout);
        return scan.Json().GetProperty("result").GetProperty("job").GetProperty("id").GetInt64();
    }

    /// <summary>The library stored with the target of the invoice; 0 for none.</summary>
    private long StoredLibrary()
    {
        using var conn = Open();
        return conn.ExecuteScalar<long>(
            "SELECT COALESCE(d.target_library_job_id, 0) FROM file_decisions d JOIN file_nodes f ON f.id = d.file_node_id WHERE f.name = 'strom.txt'");
    }

    private static JsonElement Storage(JsonElement result) => result.GetProperty("targets").GetProperty("storage");

    private static JsonElement Full(JsonElement result, string relativePath) =>
        Item(result, relativePath).GetProperty("target").GetProperty("full_path");

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string WriteTargets(string name, string targets, string? root = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, $$"""
            { "schema_version": 1, "kind": "sfo_target_structure", "root": {{JsonSerializer.Serialize(root)}}, "source_job": null,
              "created_at": "2026-11-13T10:00:00Z", "origin": "rule", "model": null,
              "targets": {{targets.Replace("] }", "], \"reason\": null, \"examples\": [] }")}} }
            """);
        return path;
    }

    private async Task<long> ScanAndAnalyzeAsync()
    {
        var scan = await SfoAsync("scan", _source, "--json");
        Assert.True(scan.ExitCode == 0, scan.Stderr + scan.Stdout);
        var job = scan.Json().GetProperty("result").GetProperty("job").GetProperty("id").GetInt64();
        var analyze = await SfoAsync("analyze", job.ToString(), "--json");
        Assert.True(analyze.ExitCode == 0, analyze.Stderr + analyze.Stdout);
        return job;
    }

    private async Task<JsonElement> DecideAsync(long job, string? targets, params string[] options)
    {
        string[] args = targets is null ? ["decide", job.ToString(), "--json"] : ["decide", job.ToString(), "--targets", targets, .. options, "--json"];
        var run = await SfoAsync(args);
        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        return run.Json().GetProperty("result");
    }

    private static JsonElement Item(JsonElement result, string relativePath) =>
        result.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("relative_path").GetString() == relativePath);

    private static string? Target(JsonElement result, string relativePath) =>
        Item(result, relativePath).GetProperty("target") is { ValueKind: JsonValueKind.Object } t ? t.GetProperty("path").GetString() : null;

    private static List<(string, string, string)> TargetBasis(JsonElement result, string relativePath) =>
        Item(result, relativePath).GetProperty("target").GetProperty("basis").EnumerateArray()
            .Select(b => (b.GetProperty("key").GetString()!, b.GetProperty("value").GetString()!, b.GetProperty("source").GetString()!))
            .ToList();

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
            {
                services.AddHttpClient<IOllamaService, OllamaService>().ConfigurePrimaryHttpMessageHandler(() => _server);
                if (_probe is { } probe) services.AddSingleton<IFreeSpaceProbe>(probe);
            }
        };
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], host, CancellationToken.None);
        return new Run(exit, stdout.ToString(), stderr.ToString());
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
