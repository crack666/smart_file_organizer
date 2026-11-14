using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Infrastructure.Persistence;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// The controller workflow (https://github.com/crack666/smart_file_organizer/issues/95): sfo gives the facts
/// (<c>findings candidates</c>, <c>library folder</c>), a controlling model decides outside sfo and records its decision
/// (<c>findings place</c>); the local model describes a folder only on demand (<c>library describe</c>). Same generated
/// library and source as <see cref="FindingTargetsCommandTests"/>; no real model.
/// </summary>
public partial class FindingTargetsCommandTests
{
    /// <summary>A listener on a free local port as the configured model server; counts every connection.</summary>
    private sealed class ConnectionCounter : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;
        private int _connections;

        public ConnectionCounter(string dbPath)
        {
            _listener.Start();
            _accepting = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        Interlocked.Increment(ref _connections);
                    }
                }
                catch (OperationCanceledException) { }
            });
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(dbPath)!, "settings.json"),
                $$"""{ "Ollama": { "BaseUrl": "http://127.0.0.1:{{((System.Net.IPEndPoint)_listener.LocalEndpoint).Port}}" } }""");
        }

        public int Connections => Volatile.Read(ref _connections);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _accepting;
            _listener.Stop();
            _stop.Dispose();
        }
    }

    /// <summary>The temp folder lies in the user profile, a protected place: a fake profile inside it instead.</summary>
    private Action<IServiceCollection> Places(params string[] extra) =>
        s => s.AddSingleton<IProtectedPlaces>(new FixedPlaces([Path.Combine(_dir, "profile"), .. extra]));

    private async Task<Dictionary<string, JsonElement>> FindingsByFolderAsync(string job) =>
        (await OkAsync("findings", job, "--limit", "100")).GetProperty("items").EnumerateArray()
        .Where(f => Str(f, "kind") == "not_backed_up" && f.GetProperty("relative_path").ValueKind == JsonValueKind.String)
        .ToDictionary(f => Str(f, "relative_path"), f => f);

    private static JsonElement Candidate(JsonElement candidates, string key) =>
        candidates.GetProperty("items").EnumerateArray().Single(i => Str(i, "key") == key);

    // ------------------------------------------------------------------ done when

    /// <summary>
    /// Done when: sfo findings candidates, sfo library folder and sfo findings targets without an option open no connection
    /// to the model server. The candidates name the ski trip folder by the rules, with what it holds, and every library
    /// with its free space and inbox.
    /// </summary>
    [Fact]
    public async Task Candidates_Folder_AndRulesOnlyTargets_OpenNoConnectionToTheModel()
    {
        await using var counter = new ConnectionCounter(_dbPath);
        var (library, source) = await PrepareAsync();
        var job = source.ToString();
        var key = Str(PhotoBrief(await OkAsync("findings", job, "--summary")), "key");

        var candidates = await OkAsync("findings", "candidates", job);
        Assert.Equal("known", Str(candidates, "status"));
        var root = candidates.GetProperty("libraries").EnumerateArray().Single();
        Assert.Equal(library, root.GetProperty("library_job_id").GetInt64());
        Assert.True(root.GetProperty("reachable").GetBoolean());
        Assert.Equal("_Inbox", Str(root, "inbox_folder"));
        var item = Candidate(candidates, key);
        Assert.Equal(4, item.GetProperty("files").GetInt64());
        Assert.Contains("skiing", Str(item.GetProperty("groups")[0].GetProperty("described"), "text"));
        var ski = item.GetProperty("groups")[0].GetProperty("candidates").EnumerateArray()
            .Single(c => Str(c, "destination_relative") == "Fotos/Skifahrt 2015");
        var folder = ski.GetProperty("folder");
        Assert.Equal(5, folder.GetProperty("files").GetInt64());
        Assert.False(folder.GetProperty("described").GetBoolean());
        Assert.False(folder.GetProperty("sensitive").GetBoolean());
        Assert.Contains(folder.GetProperty("file_names").EnumerateArray(), n => n.GetString()!.StartsWith("IMG_2015", StringComparison.Ordinal));

        var look = await OkAsync("library", "folder", library.ToString(), "Fotos");
        Assert.Equal(["Grillen 2016", "Skifahrt 2015"], look.GetProperty("subfolders").EnumerateArray().Select(s => s.GetString()).Order().ToList());

        var targets = await OkAsync("findings", "targets", job);
        Assert.Empty(targets.GetProperty("model_calls").EnumerateArray());
        Assert.All(targets.GetProperty("items").EnumerateArray(), i => Assert.Equal("rules", Str(i, "decided_by")));

        Assert.Equal(0, counter.Connections);
    }

    /// <summary>
    /// Done when: a marked file of the finding and a marked library folder appear in the candidates without description,
    /// excerpt or file names: the file with path and markers, the folder with its markers only.
    /// </summary>
    [Fact]
    public async Task Candidates_ShowAMarkedFileAndAMarkedFolder_WithoutDescriptionOrNames()
    {
        Write(_source, "Neuer Ordner/passwords.txt", "user: nobody", new DateTime(2015, 2, 8, 12, 0, 0));
        var (library, source) = await PrepareAsync();
        var job = source.ToString();
        var (configure, _, _) = WithModel();
        await OkAsync(configure, "library", "describe", library.ToString(), "Fotos/Skifahrt 2015");
        var key = (await FindingsByFolderAsync(job))["Neuer Ordner"].GetProperty("key").GetString()!;

        // Described and not marked: the description is listed.
        var before = Candidate(await OkAsync("findings", "candidates", job), key);
        var skiBefore = before.GetProperty("groups")[0].GetProperty("candidates").EnumerateArray()
            .Single(c => Str(c, "destination_relative") == "Fotos/Skifahrt 2015").GetProperty("folder");
        Assert.True(skiBefore.GetProperty("described").GetBoolean());
        Assert.Contains("skiing", Str(skiBefore.GetProperty("description"), "text"));

        // The marked file is a finding of its own (sensitive), never a candidate unit: no name, no content anywhere.
        var all = (await OkAsync("findings", "candidates", job)).ToString();
        Assert.DoesNotContain("passwords", all);
        Assert.DoesNotContain("nobody", all);
        Assert.Empty(before.GetProperty("sensitive_files").EnumerateArray());

        // The source folder and the library folder are marked: the files of the finding show path and markers only, the
        // library folder no description, no file names and no subfolders any more.
        var db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        await using (var conn = db.CreateConnection())
        {
            await conn.OpenAsync();
            const string mark = "UPDATE directory_nodes SET sensitive_markers = 'credentials' WHERE job_id = @job AND full_path = @path";
            Assert.Equal(1, await conn.ExecuteAsync(mark, new { job = library, path = Path.Combine(_library, "Fotos", "Skifahrt 2015") }));
            Assert.Equal(1, await conn.ExecuteAsync(mark, new { job = source, path = Path.Combine(_source, "Neuer Ordner") }));
        }
        var after = (await OkAsync("findings", "candidates", job)).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("relative_path").GetString() == "Neuer Ordner");
        var files = after.GetProperty("sensitive_files").EnumerateArray().ToList();
        Assert.Equal(4, files.Count);
        Assert.All(files, f =>
        {
            Assert.StartsWith("Neuer Ordner/IMG_", Str(f, "relative_path"));
            Assert.Equal(["credentials"], f.GetProperty("markers").EnumerateArray().Select(m => m.GetString()).ToList());
        });
        Assert.Empty(after.GetProperty("examples").EnumerateArray());
        var group = after.GetProperty("groups")[0];
        Assert.Equal(0, group.GetProperty("described").GetProperty("described").GetInt32());
        Assert.DoesNotContain("skiing", group.GetProperty("described").ToString());
        Assert.Empty(group.GetProperty("examples").EnumerateArray());
        var skiAfter = after.GetProperty("groups")[0].GetProperty("candidates").EnumerateArray()
            .Single(c => Str(c, "destination_relative") == "Fotos/Skifahrt 2015").GetProperty("folder");
        Assert.True(skiAfter.GetProperty("sensitive").GetBoolean());
        Assert.Contains("credentials", skiAfter.GetProperty("markers").EnumerateArray().Select(m => m.GetString()));
        Assert.False(skiAfter.GetProperty("described").GetBoolean());
        Assert.Equal(JsonValueKind.Null, skiAfter.GetProperty("description").ValueKind);
        Assert.Empty(skiAfter.GetProperty("file_names").EnumerateArray());
        Assert.Empty(skiAfter.GetProperty("subfolders").EnumerateArray());
        Assert.DoesNotContain("IMG_2015020", skiAfter.ToString());

        // sfo library folder and sfo library describe keep it as well.
        var look = await OkAsync("library", "folder", library.ToString(), "Fotos/Skifahrt 2015");
        Assert.Empty(look.GetProperty("files").EnumerateArray());
        var (exit, refused) = await RunAsync(configure, "library", "describe", library.ToString(), "Fotos/Skifahrt 2015", "--refresh");
        Assert.Equal(ExitCodes.Ok, exit);
        Assert.False(refused.GetProperty("result").GetProperty("described").GetBoolean());
        Assert.Contains("sensitive", refused.GetProperty("result").GetProperty("refused").GetString());
    }

    /// <summary>
    /// Done when: decisions for an existing folder, a new folder, the inbox and no target, one by options and the others
    /// from a file, show in sfo findings with decided_by controller, become moves in the plan (no_target: none), and stay
    /// after a later sfo findings targets, even with --local-model --refresh.
    /// </summary>
    [Fact]
    public async Task Decisions_ShowAsController_AreTakenByThePlan_AndSurviveLaterTargetRuns()
    {
        Photos(_source, "Sonstiges", new DateTime(2018, 5, 1, 10, 0, 0), 3, "src");
        Photos(_source, "Kram", new DateTime(2019, 8, 1, 10, 0, 0), 3, "src");
        Photos(_source, "Zeug", new DateTime(2020, 10, 1, 10, 0, 0), 3, "src");
        var (library, source) = await PrepareAsync();
        var job = source.ToString();
        var keys = (await FindingsByFolderAsync(job)).ToDictionary(f => f.Key, f => Str(f.Value, "key"));
        var places = Places();

        var single = await OkAsync(places, "findings", "place", job, keys["Neuer Ordner"], "--status", "existing_folder",
            "--library", library.ToString(), "--folder", "Fotos/Skifahrt 2015", "--reason", "Same ski trip, one day later.");
        Assert.Equal(1, single.GetProperty("stored").GetInt32());
        await OkAsync(places, "findings", "place", job, keys["Zeug"], "--status", "no_target", "--reason", "Screenshots of nothing in particular.");

        var file = Path.Combine(_dir, "decisions.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            decisions = new object[]
            {
                new { key = keys["Sonstiges"], status = "new_folder", folder = "Fotos/Ausflug 2018", reason = "A day trip; no folder holds it yet." },
                new { key = keys["Kram"], status = "inbox", library_job_id = library, reason = "Unclear what it shows." }
            }
        }));
        var batch = await OkAsync(places, "findings", "place", job, "--file", file);
        Assert.Equal(2, batch.GetProperty("stored").GetInt32());

        void Check(Dictionary<string, JsonElement> byFolder)
        {
            Assert.All(new[] { "Neuer Ordner", "Sonstiges", "Kram", "Zeug" }, f => Assert.Equal("controller", Str(byFolder[f], "target_decided_by")));
            Assert.Equal("existing_folder", Str(byFolder["Neuer Ordner"], "target_status"));
            Assert.Equal("Fotos/Skifahrt 2015", Str(byFolder["Neuer Ordner"].GetProperty("target"), "relative_path"));
            Assert.Equal("new_folder", Str(byFolder["Sonstiges"], "target_status"));
            Assert.Equal("Fotos/Ausflug 2018", Str(byFolder["Sonstiges"].GetProperty("target"), "relative_path"));
            Assert.Equal("inbox", Str(byFolder["Kram"], "target_status"));
            Assert.Equal("_Inbox/fund", Str(byFolder["Kram"].GetProperty("target"), "relative_path"));
            Assert.Equal("no_target", Str(byFolder["Zeug"], "target_status"));
            Assert.Equal(JsonValueKind.Null, byFolder["Zeug"].GetProperty("target").ValueKind);
        }
        Check(await FindingsByFolderAsync(job));

        // Later target runs keep every decision, with and without the local model.
        var rules = await OkAsync("findings", "targets", job);
        Assert.All(rules.GetProperty("items").EnumerateArray().Where(i => keys.ContainsValue(Str(i, "key"))),
            i => Assert.Equal("controller", Str(i, "decided_by")));
        var (configure, _, _) = WithModel();
        await OkAsync(configure, "findings", "targets", job, "--local-model", "--refresh");
        Check(await FindingsByFolderAsync(job));

        // The plan moves the files to the decided places; no_target stays.
        await OkAsync(null, ["findings", "accept", job, .. keys.Values]);
        var plan = (await OkAsync(places, "plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        async Task<List<JsonElement>> Actions(string key) =>
            (await OkAsync(places, "plan", "show", plan, "--finding", key)).GetProperty("items").EnumerateArray().ToList();

        static void MovesInto(List<JsonElement> actions, string folder)
        {
            Assert.NotEmpty(actions);
            Assert.All(actions, a =>
            {
                Assert.Equal("move", Str(a, "kind"));
                Assert.Equal(folder, Path.GetDirectoryName(Str(a, "target")));
                Assert.Empty(a.GetProperty("problems").EnumerateArray());
            });
        }
        MovesInto(await Actions(keys["Neuer Ordner"]), Path.Combine(_library, "Fotos", "Skifahrt 2015"));
        MovesInto(await Actions(keys["Sonstiges"]), Path.Combine(_library, "Fotos", "Ausflug 2018"));
        MovesInto(await Actions(keys["Kram"]), Path.Combine(_library, "_Inbox", "fund"));
        Assert.DoesNotContain(await Actions(keys["Zeug"]), a => Str(a, "kind") == "move");

        // Withdrawn, the finding is matched by the rules again.
        await OkAsync(places, "findings", "place", job, keys["Zeug"], "--withdraw");
        Assert.Equal("not_matched", Str((await FindingsByFolderAsync(job))["Zeug"], "target_status"));
    }

    /// <summary>
    /// Done when: a target outside every library, in a protected place or with an invalid path is refused with a named
    /// problem, and a file with one refused decision stores none of its decisions.
    /// </summary>
    [Fact]
    public async Task ATargetOutsideTheLibrariesOrInAProtectedPlace_IsRefused_AndAFileStoresNothing()
    {
        var (library, source) = await PrepareAsync();
        var job = source.ToString();
        var key = Str(PhotoBrief(await OkAsync("findings", job, "--summary")), "key");

        async Task<IReadOnlyList<string>> Refused(Action<IServiceCollection> configure, params string[] args)
        {
            var (exit, doc) = await RunAsync(configure, ["findings", "place", job, .. args]);
            Assert.Equal(ExitCodes.Failure, exit);
            Assert.Equal(ErrorCodes.PlacementRefused, doc.GetProperty("error").GetProperty("code").GetString());
            return doc.GetProperty("result").GetProperty("refused").EnumerateArray()
                .SelectMany(r => r.GetProperty("problems").EnumerateArray().Select(p => p.GetString()!)).ToList();
        }

        Assert.Contains("outside_library", await Refused(Places(), key, "--status", "existing_folder",
            "--folder", Path.Combine(_dir, "elsewhere"), "--reason", "test"));
        Assert.Contains("target_protected", await Refused(Places(Path.Combine(_library, "Fotos")), key, "--status", "existing_folder",
            "--folder", "Fotos/Skifahrt 2015", "--reason", "test"));
        Assert.Contains("invalid_path", await Refused(Places(), key, "--status", "new_folder", "--folder", "Fotos/../Neu", "--reason", "test"));
        Assert.Contains("folder_not_found", await Refused(Places(), key, "--status", "existing_folder", "--folder", "Fotos/Gibt es nicht", "--reason", "test"));
        Assert.Contains("folder_exists", await Refused(Places(), key, "--status", "new_folder", "--folder", "Fotos/Grillen 2016", "--reason", "test"));
        Assert.Contains("parent_not_found", await Refused(Places(), key, "--status", "new_folder", "--folder", "Fehlt/Neu", "--reason", "test"));
        Assert.Contains("reason_missing", await Refused(Places(), key, "--status", "inbox"));
        Assert.Contains("part_not_found", await Refused(Places(), key, "--status", "inbox", "--part", "1", "--reason", "test"));

        // One good and one bad decision in a file: nothing is stored.
        var file = Path.Combine(_dir, "decisions.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            decisions = new object[]
            {
                new { key, status = "existing_folder", library_job_id = library, folder = "Fotos/Skifahrt 2015", reason = "fits" },
                new { key, status = "existing_folder", folder = Path.Combine(_dir, "elsewhere"), reason = "outside" }
            }
        }));
        var (exit, doc) = await RunAsync(Places(), "findings", "place", job, "--file", file);
        Assert.Equal(ExitCodes.Failure, exit);
        var refused = doc.GetProperty("result").GetProperty("refused").EnumerateArray().Single();
        Assert.Equal(1, refused.GetProperty("index").GetInt32());
        Assert.Equal(0, doc.GetProperty("result").GetProperty("stored").GetInt32());
        Assert.Equal("not_matched", Str(PhotoBrief(await OkAsync("findings", job, "--summary")), "target_status"));
    }

    /// <summary>
    /// Done when: sfo library describe asks the model about files of that one folder only and stores its description;
    /// a second call reads the stored one without asking again.
    /// </summary>
    [Fact]
    public async Task DescribeFolder_AsksTheModelForThatFolderOnly_AndStoresIt()
    {
        var (library, _) = await PrepareAsync();
        var (configure, model, judge) = WithModel();

        var described = await OkAsync(configure, "library", "describe", library.ToString(), "Fotos/Skifahrt 2015");
        Assert.True(described.GetProperty("described").GetBoolean());
        Assert.Contains("skiing", Str(described.GetProperty("folder").GetProperty("description"), "text"));
        Assert.NotEmpty(model.Described);
        Assert.All(model.Described, p => Assert.StartsWith("Fotos/Skifahrt 2015/", p));
        Assert.Empty(judge.Requests);

        var db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        await using (var conn = db.CreateConnection())
        {
            await conn.OpenAsync();
            var stored = (await conn.QueryAsync<string>("SELECT relative_path FROM folder_descriptions WHERE job_id = @library", new { library })).ToList();
            Assert.Equal(["Fotos/Skifahrt 2015"], stored);
        }

        var asked = model.Described.Count;
        var again = await OkAsync(configure, "library", "describe", library.ToString(), "Fotos/Skifahrt 2015");
        Assert.True(again.GetProperty("stored").GetBoolean());
        Assert.Equal(asked, model.Described.Count);
    }
}
