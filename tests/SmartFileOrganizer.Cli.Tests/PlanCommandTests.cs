using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>sfo plan</c> (https://github.com/crack666/smart_file_organizer/issues/11), in-process on a generated source and a
/// generated library. The source is analysed with scripted answers (<c>sfo eval --stub</c>); no model runs and no file
/// is moved. Every name and every file is invented; the "profile" is a folder of the test, named as protected place.
/// </summary>
public class PlanCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _corpus;
    private readonly string _tree;
    private readonly string _library;
    private readonly string _profile;
    private readonly List<TruthDirectoryEntry> _dirs = [new(".", "mixed")];
    private readonly List<TruthFileEntry> _files = [];

    public PlanCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_plan_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _corpus = Path.Combine(_dir, "corpus");
        _tree = Path.Combine(_corpus, "tree");
        _library = Path.Combine(_dir, "library");
        _profile = Path.Combine(_dir, "profile");
        Directory.CreateDirectory(_profile);

        // A uniform folder of a trip the library does not hold: one action for the folder, its name stays.
        Folder("Urlaub Kroatien 2021", "uniform");
        for (var i = 0; i < 4; i++)
            Write($"Urlaub Kroatien 2021/IMG_20210703_1{i}0000.jpg", "photo", $"croatia {i}");

        // A trip the library holds already: no action at all.
        Folder("Urlaub Italien 2019", "uniform");
        for (var i = 0; i < 3; i++)
            Write($"Urlaub Italien 2019/IMG_20190612_1{i}0000.jpg", "photo", $"italy {i}");

        // A mixed download folder: two photos and an invoice worth keeping, an installer, a photo the library holds.
        Folder("Downloads", "mixed");
        Write("Downloads/IMG_20230812_143012.jpg", "photo", "download photo 1");
        Write("Downloads/IMG_20230812_143512.jpg", "photo", "download photo 2");
        Write("Downloads/Rechnung_Strom.pdf", "invoice", "%PDF-1.4 power");
        Write("Downloads/setup_tool_1.2.exe", "software_installer", "MZ installer");
        Write("Downloads/IMG_20220101_120000.jpg", "photo", "already in the library");

        // A password list: sensitive, decided one by one.
        Folder("Privat", "mixed");
        Write("Privat/passwoerter.txt", "document", "mail: dummy-not-real", ["credentials"]);

        var truth = new EvalTruthDocument(EvalTruth.SchemaVersion, new EvalCorpusInfo("plan-test", 1, 11), "tree", _dirs, _files);
        File.WriteAllText(Path.Combine(_corpus, EvalTruth.FileName), JsonSerializer.Serialize(truth, EvalJson.Options));

        Directory.CreateDirectory(Path.Combine(_library, "Fotos", "2019-06 Italien"));
        foreach (var file in Directory.GetFiles(Path.Combine(_tree, "Urlaub Italien 2019")))
            File.Copy(file, Path.Combine(_library, "Fotos", "2019-06 Italien", Path.GetFileName(file)));
        Directory.CreateDirectory(Path.Combine(_library, "Fotos", "alt"));
        File.Copy(Path.Combine(_tree, "Downloads", "IMG_20220101_120000.jpg"), Path.Combine(_library, "Fotos", "alt", "IMG_20220101_120000.jpg"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    // ── Fertig, wenn ─────────────────────────────────────────────────────

    [Fact]
    public async Task Create_UniformFolderIsOneActionWithItsName_MixedFolderOnlyWorthKeeping_BackedUpNothing()
    {
        var (job, library) = await PrepareAsync();
        var plan = await OkAsync("plan", "create", job);
        Assert.Equal(0, plan.GetProperty("with_problems").GetInt64());
        var actions = await ActionsAsync(plan.GetProperty("plan_id").GetInt64().ToString());

        // The uniform folder: exactly one action, for the folder, its name unchanged below a library root.
        var croatia = actions.Where(a => Under(a, "Urlaub Kroatien 2021")).ToList();
        var folder = Assert.Single(croatia);
        Assert.Equal("folder", Str(folder, "source_kind"));
        Assert.Equal("move", Str(folder, "kind"));
        Assert.Equal(4, folder.GetProperty("files").GetInt64());
        Assert.Equal(Path.Combine(_library, "Fotos", "Urlaub Kroatien 2021"), Str(folder, "target"));
        Assert.Equal(library, folder.GetProperty("library_job_id").GetInt64());

        // The mixed folder: one action per worth-keeping file; the installer stays, the photo in the library too.
        var downloads = actions.Where(a => Under(a, "Downloads")).ToList();
        Assert.Equal(
            ["Downloads/IMG_20230812_143012.jpg", "Downloads/IMG_20230812_143512.jpg", "Downloads/Rechnung_Strom.pdf"],
            downloads.Select(a => Str(a, "relative_path")).Order(StringComparer.Ordinal));
        Assert.All(downloads, a => Assert.Equal("file", Str(a, "source_kind")));
        Assert.All(downloads, a => Assert.Equal("move", Str(a, "kind")));
        Assert.Equal(Path.Combine(_library, "Dokumente", "Rechnung_Strom.pdf"),
            Str(downloads.Single(a => Str(a, "relative_path")!.EndsWith(".pdf")), "target"));

        // Backed up: no action, neither for the trip nor for the single photo.
        Assert.DoesNotContain(actions, a => Under(a, "Urlaub Italien 2019"));
        Assert.DoesNotContain(actions, a => Str(a, "relative_path")!.Contains("IMG_20220101_120000"));

        // Sensitive: left, one by one.
        var secret = Assert.Single(actions, a => a.GetProperty("sensitive").GetBoolean());
        Assert.Equal("leave", Str(secret, "kind"));
        Assert.True(secret.GetProperty("single_decision").GetBoolean());

        // Every target lies below the library root; the plan changed nothing on disk.
        Assert.All(actions.Where(a => Str(a, "target") is not null), a => Assert.StartsWith(_library, Str(a, "target")));
        Assert.False(Directory.Exists(Path.Combine(_library, "Fotos", "Urlaub Kroatien 2021")));
        Assert.True(File.Exists(Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf")));
    }

    [Fact]
    public async Task Import_ChangedTargets_ThenApproveAll_ConfirmsOnlyValidActions()
    {
        var (job, _) = await PrepareAsync();
        var planId = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        var actions = await ActionsAsync(planId);
        var folder = actions.Single(a => Str(a, "source_kind") == "folder");
        var invoice = actions.Single(a => Str(a, "relative_path") == "Downloads/Rechnung_Strom.pdf");
        var photo = actions.Single(a => Str(a, "relative_path") == "Downloads/IMG_20230812_143012.jpg");
        var secret = actions.Single(a => a.GetProperty("sensitive").GetBoolean());

        // One invalid entry (a target in the profile) refuses the whole file: nothing changes.
        var bad = WriteImport("bad.json", new[]
        {
            new { id = Id(invoice), target = Path.Combine(_library, "Papiere", "Strom", "Rechnung_Strom.pdf") },
            new { id = Id(photo), target = Path.Combine(_profile, "Bilder", "IMG_20230812_143012.jpg") }
        });
        var refused = await SfoAsync("plan", "import", planId, bad);
        Assert.Equal(ExitCodes.Failure, refused.ExitCode);
        Assert.Equal("plan_change_refused", refused.ErrorCode());
        var why = Assert.Single(refused.Result().GetProperty("refused").EnumerateArray());
        Assert.Equal(Id(photo), why.GetProperty("action_id").GetInt64());
        Assert.Contains("target_protected", Strings(why, "problems"));
        Assert.Contains("target_outside_library", Strings(why, "problems"));
        Assert.Equal(Str(invoice, "target"), Str((await ActionsAsync(planId)).Single(a => Id(a) == Id(invoice)), "target"));

        // Valid changes: new targets in the library, by a steering agent.
        var good = WriteImport("good.json", new[]
        {
            new { id = Id(invoice), target = Path.Combine(_library, "Papiere", "Strom", "Rechnung_Strom.pdf") },
            new { id = Id(folder), target = Path.Combine(_library, "Fotos", "2021", "Urlaub Kroatien 2021") },
            new { id = Id(photo), target = Path.Combine(_library, "Fotos", "Downloads", "IMG_20230812_143012.jpg") }
        });
        var changed = await OkAsync("plan", "import", planId, good, "--by", "agent");
        Assert.Equal(3, changed.GetProperty("items").GetArrayLength());
        Assert.All(changed.GetProperty("items").EnumerateArray(), a =>
        {
            Assert.Equal("agent", Str(a, "origin"));
            Assert.Equal("proposed", Str(a, "state"));
        });

        // Something appears at one target after the change: that action is no longer valid.
        Directory.CreateDirectory(Path.Combine(_library, "Fotos", "Downloads"));
        File.WriteAllText(Path.Combine(_library, "Fotos", "Downloads", "IMG_20230812_143012.jpg"), "other");

        var approved = await OkAsync("plan", "approve", planId, "--all");
        var changedIds = approved.GetProperty("changed").EnumerateArray().Select(e => e.GetInt64()).ToHashSet();
        var skipped = approved.GetProperty("skipped").EnumerateArray()
            .ToDictionary(s => s.GetProperty("action_id").GetInt64(), s => Strings(s, "reasons"));
        Assert.Contains(Id(invoice), changedIds);
        Assert.Contains(Id(folder), changedIds);
        Assert.Equal(["name_collision"], skipped[Id(photo)]);
        Assert.Equal(["single_decision"], skipped[Id(secret)]);

        var after = await ActionsAsync(planId);
        foreach (var a in after)
        {
            var valid = a.GetProperty("problems").GetArrayLength() == 0 && !a.GetProperty("single_decision").GetBoolean();
            Assert.Equal(valid ? "approved" : "proposed", Str(a, "state"));
        }

        // The sensitive action is confirmed only by name.
        var single = await OkAsync("plan", "approve", planId, "--action", Id(secret).ToString());
        Assert.Equal([Id(secret)], single.GetProperty("changed").EnumerateArray().Select(e => e.GetInt64()));
    }

    // ── harte Vorgaben ───────────────────────────────────────────────────

    [Fact]
    public async Task ATargetOfDecideWithoutALibraryToBindTo_IsLeave_WithAHint_AndBoundWhenNamed()
    {
        var (job, library) = await PrepareAsync();
        // A second library: the relative targets of sfo decide no longer name one root.
        var other = Path.Combine(_dir, "library2");
        Directory.CreateDirectory(Path.Combine(other, "Musik"));
        File.WriteAllText(Path.Combine(other, "Musik", "x.txt"), "x");
        var second = (await OkAsync("scan", other, "--role", "library")).GetProperty("job").GetProperty("id").GetInt64();
        await OkAsync("compare", job, "--against", library.ToString(), "--against", second.ToString());

        var unbound = await ActionsAsync((await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString());
        var folder = unbound.Single(a => Under(a, "Urlaub Kroatien 2021"));
        Assert.Equal("leave", Str(folder, "kind"));
        Assert.Equal("move", Str(folder, "recommended"));
        Assert.Null(Str(folder, "target"));
        Assert.Contains("--library", Str(folder, "note"));

        var bound = await ActionsAsync((await OkAsync("plan", "create", job, "--library", second.ToString())).GetProperty("plan_id").GetInt64().ToString());
        var moved = bound.Single(a => Under(a, "Urlaub Kroatien 2021"));
        Assert.Equal("move", Str(moved, "kind"));
        Assert.Equal(Path.Combine(other, "Fotos", "Urlaub Kroatien 2021"), Str(moved, "target"));
    }

    [Fact]
    public async Task Set_RefusesTargetsInTheSourceOutsideLibrariesAndQuarantineOfSensitiveFiles()
    {
        var (job, _) = await PrepareAsync();
        var planId = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        var actions = await ActionsAsync(planId);
        var invoice = Id(actions.Single(a => Str(a, "relative_path") == "Downloads/Rechnung_Strom.pdf")).ToString();
        var secret = Id(actions.Single(a => a.GetProperty("sensitive").GetBoolean())).ToString();

        async Task<List<string>> RefusedAsync(params string[] args)
        {
            var run = await SfoAsync(["plan", "set", planId, .. args]);
            Assert.True(run.ExitCode == ExitCodes.Failure, run.Stdout);
            Assert.Equal("plan_change_refused", run.ErrorCode());
            return Strings(run.Result().GetProperty("refused")[0], "problems");
        }

        Assert.Contains("target_in_source", await RefusedAsync(invoice, "--target", Path.Combine(_tree, "Archiv", "Rechnung_Strom.pdf")));
        Assert.Contains("target_outside_library", await RefusedAsync(invoice, "--target", Path.Combine(_dir, "elsewhere", "Rechnung_Strom.pdf")));
        Assert.Contains("target_protected", await RefusedAsync(invoice, "--target", Path.Combine(_profile, "Dokumente", "Rechnung_Strom.pdf")));
        Assert.Equal(["sensitive_kind"], await RefusedAsync(secret, "--kind", "quarantine", "--target", Path.Combine(_library, "Quarantaene", "passwoerter.txt")));

        // Moving it into the library is a person's call and allowed; a relative target lies below the only library.
        var moved = await OkAsync("plan", "set", planId, secret, "--kind", "move", "--target", "Privat/passwoerter.txt", "--reason", "my own list");
        var item = moved.GetProperty("items")[0];
        Assert.Equal(Path.Combine(_library, "Privat", "passwoerter.txt"), Str(item, "target"));
        Assert.Equal("human", Str(item, "origin"));
        Assert.Equal("my own list", Str(item, "reason"));
        // Still only by name.
        var all = await OkAsync("plan", "approve", planId, "--all");
        Assert.Contains(all.GetProperty("skipped").EnumerateArray(), s => s.GetProperty("action_id").GetInt64().ToString() == secret);
    }

    [Fact]
    public async Task ALibraryIsNeverASource()
    {
        var (_, library) = await PrepareAsync();
        var run = await SfoAsync("plan", "create", library.ToString());
        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Equal("wrong_role", run.ErrorCode());
    }

    [Fact]
    public async Task ManagedAreas_MarkTargetsInThem_AndNothingInThemIsMoved()
    {
        var (job, _) = await PrepareAsync();
        WriteSettings($$"""{ "Plan": { "ManagedAreas": [ {{JsonSerializer.Serialize(Path.Combine(_library, "Fotos"))}} ] } }""");
        var plan = await OkAsync("plan", "create", job);
        Assert.True(plan.GetProperty("into_managed_area").GetInt64() >= 2);
        var actions = await ActionsAsync(plan.GetProperty("plan_id").GetInt64().ToString());
        Assert.True(actions.Single(a => Str(a, "source_kind") == "folder").GetProperty("into_managed_area").GetBoolean());
        Assert.False(actions.Single(a => Str(a, "relative_path") == "Downloads/Rechnung_Strom.pdf").GetProperty("into_managed_area").GetBoolean());

        // A managed area on the source: its files stay, with the reason.
        WriteSettings($$"""{ "Plan": { "ManagedAreas": [ {{JsonSerializer.Serialize(Path.Combine(_tree, "Downloads"))}} ] } }""");
        var planId = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        var downloads = (await ActionsAsync(planId)).Where(a => Under(a, "Downloads")).ToList();
        Assert.NotEmpty(downloads);
        Assert.All(downloads, a =>
        {
            Assert.Equal("leave", Str(a, "kind"));
            Assert.Contains("source_in_managed_area", Str(a, "note"));
        });
        var set = await SfoAsync("plan", "set", planId, Id(downloads[0]).ToString(),
            "--kind", "move", "--target", Path.Combine(_library, "Dokumente", "x.pdf"));
        Assert.Equal("plan_change_refused", set.ErrorCode());
        Assert.Contains("source_in_managed_area", Strings(set.Result().GetProperty("refused")[0], "problems"));
        // Copying out of a managed area takes nothing away there: allowed.
        await OkAsync("plan", "set", planId, Id(downloads[0]).ToString(), "--kind", "copy", "--target", Path.Combine(_library, "Dokumente", "x.pdf"));
    }

    [Fact]
    public async Task Summary_And_Show_ReadThePlan()
    {
        var (job, library) = await PrepareAsync();
        var planId = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        var summary = await OkAsync("plan", "summary", planId);
        Assert.Equal(4, summary.GetProperty("by_kind").GetProperty("move").GetProperty("actions").GetInt64());
        Assert.Equal(1, summary.GetProperty("by_kind").GetProperty("leave").GetProperty("actions").GetInt64());
        Assert.Equal(5, summary.GetProperty("by_state").GetProperty("proposed").GetInt64());
        var lib = Assert.Single(summary.GetProperty("libraries").EnumerateArray());
        Assert.Equal(library, lib.GetProperty("job_id").GetInt64());
        Assert.Equal(4, lib.GetProperty("actions").GetInt64());
        Assert.Equal(0, summary.GetProperty("with_problems").GetInt64());

        var moves = await OkAsync("plan", "show", planId, "--kind", "move", "--under", "Downloads", "--limit", "2");
        Assert.Equal(3, moves.GetProperty("total").GetInt64());
        Assert.Equal(2, moves.GetProperty("items").GetArrayLength());
        Assert.Equal(2, moves.GetProperty("next_offset").GetInt32());

        var text = await SfoTextAsync("plan", "summary", planId);
        Assert.Contains("move", text);
        var missing = await SfoAsync("plan", "show", "999");
        Assert.Equal("plan_not_found", missing.ErrorCode());
        var noSelection = await SfoAsync("plan", "approve", planId);
        Assert.Equal(ExitCodes.Usage, noSelection.ExitCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private sealed class FixedPlaces(params string[] places) : IProtectedPlaces
    {
        public IReadOnlyList<string> Get() => places;
    }

    private void Folder(string path, string verdict) => _dirs.Add(new TruthDirectoryEntry(path, verdict));

    private void Write(string relative, string category, string content, IReadOnlyList<string>? sensitive = null)
    {
        var path = Path.Combine(_tree, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _files.Add(new TruthFileEntry(relative, category, sensitive ?? []));
    }

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dbPath)!, "settings.json"), json);
    }

    private string WriteImport(string name, object entries)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(new { actions = entries }));
        return path;
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr)
    {
        public JsonElement Result()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.GetProperty("result").Clone();
        }

        public string? ErrorCode()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.GetProperty("error").GetProperty("code").GetString();
        }
    }

    private CliHost Host(StringWriter stdout, StringWriter stderr) => new()
    {
        Out = stdout,
        Error = stderr,
        ConfigureServices = s => s.AddSingleton<IProtectedPlaces>(new FixedPlaces(_profile))
    };

    private async Task<Run> SfoAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--json", "--db", _dbPath], Host(stdout, stderr));
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<string> SfoTextAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], Host(stdout, stderr));
        Assert.True(exit == ExitCodes.Ok, stdout.ToString() + stderr);
        return stdout.ToString();
    }

    private async Task<JsonElement> OkAsync(params string[] args)
    {
        var run = await SfoAsync(args);
        Assert.True(run.ExitCode == ExitCodes.Ok, string.Join(' ', args) + ": " + run.Stdout + run.Stderr);
        return run.Result();
    }

    /// <summary>Scans and analyses the source, compares it with the library, builds bundles, decides targets, accepts every finding.</summary>
    private async Task<(string Job, long Library)> PrepareAsync()
    {
        var job = (await OkAsync("eval", _corpus, "--stub", Path.Combine(_corpus, EvalTruth.FileName))).GetProperty("job_id").GetInt64().ToString();
        var library = (await OkAsync("scan", _library, "--role", "library")).GetProperty("job").GetProperty("id").GetInt64();
        await OkAsync("compare", job, "--against", library.ToString());
        await OkAsync("bundles", "build", job);

        var targets = Path.Combine(_dir, "targets.json");
        File.WriteAllText(targets, """
            { "schema_version": 1, "kind": "sfo_target_structure", "root": null, "source_job": null,
              "created_at": "2026-11-13T10:00:00Z", "origin": "rule", "model": null,
              "targets": [
                { "id": "photos", "path": "Fotos", "pattern": null, "holds": ["image"], "reason": null, "examples": [] },
                { "id": "documents", "path": "Dokumente", "pattern": null, "holds": ["document"], "reason": null, "examples": [] } ] }
            """);
        await OkAsync("decide", job, "--targets", targets);

        var findings = await OkAsync("findings", job, "--limit", "200");
        var keys = findings.GetProperty("items").EnumerateArray().Select(f => Str(f, "key")!).ToList();
        Assert.NotEmpty(keys);
        await OkAsync(["findings", "accept", job, .. keys]);
        return (job, library);
    }

    private async Task<List<JsonElement>> ActionsAsync(string planId) =>
        (await OkAsync("plan", "show", planId, "--limit", "500")).GetProperty("items").EnumerateArray().ToList();

    private bool Under(JsonElement a, string folder) =>
        Str(a, "relative_path") is { } rel && (rel == folder || rel.StartsWith(folder + "/", StringComparison.Ordinal));

    private static long Id(JsonElement a) => a.GetProperty("id").GetInt64();

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).GetString();

    private static List<string> Strings(JsonElement e, string name) =>
        e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToList();
}
