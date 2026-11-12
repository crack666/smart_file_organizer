using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Cli.Commands;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Hosting;
using SmartFileOrganizer.Hosting.Tests;
using Xunit.Abstractions;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>sfo scan --structure-only</c>, <c>sfo library review</c> and <c>sfo library propose</c> against a generated
/// tree with invented names (https://github.com/crack666/smart_file_organizer/issues/29). The model is a
/// scripted stand-in or a closed port; no test talks to a real Ollama.
/// </summary>
public class LibraryCommandTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _root;

    public LibraryCommandTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_library_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _root = Path.Combine(_dir, "ablage");

        foreach (var name in new[] { "Skifahrt 2015", "Grillen 2016", "Hochzeit 2017", "Ostern 2018", "Zelten 2020" })
            Write($"Fotos/{name}", "IMG_", ".jpg", 3, name.Length);
        foreach (var name in new[] { "Urlaub Italien", "Italien Urlaub 2", "Paris 2016", "2017 London", "2018-07-01 Prag" })
            Write($"Reisen/{name}", "DSC", ".jpg", 2, name.Length + 50);
        Write("Dokumente/Rechnungen", "rechnung", ".pdf", 3, 10);
        Write("Archiv/Rechnungen", "beleg", ".pdf", 2, 11);
        Write("Archiv/Sonstiges", "kram", ".txt", 4, 12);
        Directory.CreateDirectory(Path.Combine(_root, "Archiv", "Neuer Ordner"));
        Write("Bilder", "scan", ".pdf", 12, 13);
        Write("Handy/DCIM/Camera", "IMG_", ".jpg", 2, 14);
        Directory.CreateDirectory(Path.Combine(_root, "Handy", "LOST.DIR"));
        Directory.CreateDirectory(Path.Combine(_root, "Handy", "Android", "data", "com.example.chat", "cache"));
        File.WriteAllText(Path.Combine(_root, "Dokumente", "geheim-tresor.kdbx"), "not a real password store");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Write(string relativeDir, string prefix, string ext, int count, int salt)
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        for (var i = 1; i <= count; i++)
            File.WriteAllBytes(Path.Combine(dir, $"{prefix}{i:0000}{ext}"), new byte[100 + salt * 10 + i]);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Run(int ExitCode, string Stdout, string Stderr)
    {
        public JsonElement Json()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.Clone();
        }

        public JsonElement Result() => Json().GetProperty("result");
    }

    private async Task<Run> SfoAsync(Action<IServiceCollection>? configure, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost { Out = stdout, Error = stderr, ConfigureServices = configure };
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], host);
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<Run> OkAsync(Action<IServiceCollection>? configure, params string[] args)
    {
        var run = await SfoAsync(configure, [.. args, "--json"]);
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        return run;
    }

    private async Task<long> ScanStructureAsync(string role = "library")
    {
        var scan = await OkAsync(null, "scan", _root, "--structure-only", "--role", role);
        var job = scan.Result().GetProperty("job");
        Assert.True(job.GetProperty("structure_only").GetBoolean());
        return job.GetProperty("id").GetInt64();
    }

    private static Action<IServiceCollection> WithModel(ScriptedAdvisor advisor) => services =>
    {
        services.AddSingleton<IOllamaService>(new FakeOllamaService());
        services.AddSingleton<ILibraryAdvisor>(advisor);
    };

    private static List<JsonElement> Items(JsonElement result) => result.GetProperty("items").EnumerateArray().ToList();

    private static HashSet<string> Kinds(JsonElement result) =>
        Items(result).Select(i => i.GetProperty("kind").GetString()!).ToHashSet();

    // ------------------------------------------------------------------ review

    [Fact]
    public async Task Review_WithoutModel_ListsFindingsOfTheRules_AndLeavesTheTreeUntouched()
    {
        var before = Snapshot();
        var job = await ScanStructureAsync();

        var run = await OkAsync(null, "library", "review", job.ToString(), "--no-model");

        var doc = run.Json();
        Assert.Equal("library review", doc.GetProperty("command").GetString());
        var result = doc.GetProperty("result");
        Assert.Equal("skipped", result.GetProperty("advice").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("role_note").ValueKind);
        Assert.Equal(1, result.GetProperty("structure").GetProperty("dumps").GetInt32());
        var kinds = Kinds(result);
        foreach (var expected in new[] { "dump", "naming_inconsistent", "same_name_elsewhere", "similar_names", "placeholder_name", "empty", "type_mismatch", "sensitive_location" })
            Assert.Contains(expected, kinds);

        // Ordered by impact: every high before every medium before every low.
        var impacts = Items(result).Select(i => i.GetProperty("impact").GetString()).ToList();
        Assert.Equal(impacts.OrderBy(i => i switch { "high" => 0, "medium" => 1, _ => 2 }).ToList(), impacts);

        // The sensitive file is named with path and marker only.
        var sensitive = Items(result).Single(i => i.GetProperty("kind").GetString() == "sensitive_location");
        Assert.Equal("Dokumente/geheim-tresor.kdbx", sensitive.GetProperty("items")[0].GetString());

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task Review_OfASourceJob_RunsButSaysItIsMeantForALibrary()
    {
        var job = await ScanStructureAsync("source");

        var run = await OkAsync(null, "library", "review", job.ToString(), "--no-model");

        var note = run.Result().GetProperty("role_note").GetString();
        Assert.Contains("role source", note);
        Assert.Contains("--role library", note);
        Assert.NotEmpty(Items(run.Result()));
    }

    [Fact]
    public async Task Review_PagesAndFilters_LikeTheReadCommands()
    {
        var job = await ScanStructureAsync();

        var first = (await OkAsync(null, "library", "review", job.ToString(), "--no-model", "--limit", "2")).Result();
        Assert.Equal(2, Items(first).Count);
        Assert.Equal(2, first.GetProperty("next_offset").GetInt32());
        var total = first.GetProperty("total").GetInt32();

        var rest = (await OkAsync(null, "library", "review", job.ToString(), "--no-model", "--offset", "2", "--limit", "1000")).Result();
        Assert.Equal(total - 2, Items(rest).Count);
        Assert.Equal(JsonValueKind.Null, rest.GetProperty("next_offset").ValueKind);

        var empty = (await OkAsync(null, "library", "review", job.ToString(), "--no-model", "--kind", "empty")).Result();
        Assert.All(Items(empty), i => Assert.Equal("empty", i.GetProperty("kind").GetString()));

        var wrong = await SfoAsync(null, "library", "review", job.ToString(), "--kind", "nonsense", "--json");
        Assert.Equal(ExitCodes.Usage, wrong.ExitCode);
    }

    [Fact]
    public async Task Review_WithModel_ReturnsCheckedAdvice_AndReusesItWhileTheFactsAreUnchanged()
    {
        var job = await ScanStructureAsync();
        var advisor = new ScriptedAdvisor("""
            {
              "good": [ { "path": "Fotos", "reason": "named by occasion and year" },
                        { "path": "Nowhere", "reason": "not in the tree" } ],
              "roles": [ { "path": "Fotos", "role": "collection", "children_role": "occasion", "reason": "trips and parties" },
                         { "path": "Reisen", "role": "nonsense" } ],
              "changes": [ { "action": "rename", "path": "Reisen/2017 London", "target": "London 2017", "reason": "follow the habit <occasion> <year>", "finding": "naming_inconsistent" },
                           { "action": "delete", "path": "Archiv", "reason": "not an action" } ],
              "outline": [ { "path": "Fotos", "purpose": "photos by occasion", "pattern": "{occasion} {year}" } ]
            }
            """);

        var advice = (await OkAsync(WithModel(advisor), "library", "review", job.ToString())).Result().GetProperty("advice");

        Assert.Equal("ok", advice.GetProperty("status").GetString());
        Assert.Equal("Fotos", advice.GetProperty("good")[0].GetProperty("path").GetString());
        Assert.Equal(1, advice.GetProperty("good").GetArrayLength());
        Assert.Equal("occasion", advice.GetProperty("roles")[0].GetProperty("children_role").GetString());
        var change = advice.GetProperty("changes")[0];
        Assert.Equal("rename", change.GetProperty("action").GetString());
        Assert.Equal("London 2017", change.GetProperty("target").GetString());
        Assert.Equal(3, advice.GetProperty("dropped").GetInt32());

        // The model saw folder names and findings, but no file name, not even of the marked file.
        var facts = Assert.Single(advisor.Requests).Facts;
        Assert.Contains("Reisen/2017 London", facts);
        Assert.Contains("naming_inconsistent", facts);
        Assert.DoesNotContain("tresor", facts);
        Assert.DoesNotContain("IMG_0001", facts);

        var again = (await OkAsync(WithModel(advisor), "library", "review", job.ToString())).Result().GetProperty("advice");
        Assert.Equal("cached", again.GetProperty("status").GetString());
        Assert.Single(advisor.Requests);

        await OkAsync(WithModel(advisor), "library", "review", job.ToString(), "--refresh");
        Assert.Equal(2, advisor.Requests.Count);
    }

    [Fact]
    public async Task Review_WithOllamaUnreachable_FailsWithExitCode3_ButCarriesTheFindings()
    {
        var job = await ScanStructureAsync();
        WriteSettingsWithOllamaAt(ClosedLocalPort());

        var run = await SfoAsync(null, "library", "review", job.ToString(), "--json");

        Assert.Equal(ExitCodes.OllamaUnavailable, run.ExitCode);
        var doc = run.Json();
        Assert.Equal("ollama_unavailable", doc.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("unavailable", doc.GetProperty("result").GetProperty("advice").GetProperty("status").GetString());
        Assert.True(doc.GetProperty("result").GetProperty("total").GetInt32() > 0);
    }

    [Fact]
    public async Task Review_OfUnknownJob_OrWithBrokenWordFile_FailsWithItsCode()
    {
        var missing = await SfoAsync(null, "library", "review", "999", "--no-model", "--json");
        Assert.Equal("job_not_found", missing.Json().GetProperty("error").GetProperty("code").GetString());

        var job = await ScanStructureAsync();
        var words = Path.Combine(_dir, "words.json");
        File.WriteAllText(words, """{ "no_such_list": [] }""");
        var broken = await SfoAsync(null, "library", "review", job.ToString(), "--no-model", "--words", words, "--json");
        Assert.Equal(ExitCodes.Failure, broken.ExitCode);
        Assert.Equal("words_invalid", broken.Json().GetProperty("error").GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------ propose

    [Fact]
    public async Task Propose_WithoutModel_WritesATargetFileThePlanCanRead()
    {
        var job = await ScanStructureAsync();
        var file = Path.Combine(_dir, "targets.json");

        var result = (await OkAsync(null, "library", "propose", job.ToString(), "--no-model", "--out", file, "--lang", "de")).Result();

        Assert.Equal(file, result.GetProperty("path").GetString());
        var target = TargetStructureFile.Load(file);
        Assert.Equal(AdviceOrigin.Rule, target.Origin);
        Assert.Equal(job, target.SourceJob);
        Assert.Null(target.Root);
        var photos = Assert.Single(target.Targets, t => t.Id == "photos");
        Assert.Equal("Fotos", photos.Path);
        Assert.Equal("{Anlass} {Jahr}", photos.Pattern);
        Assert.Contains(target.Targets, t => t.Id == "documents");
        Assert.Contains(target.Targets, t => t.Id == "device_backups");
    }

    [Fact]
    public async Task Propose_WithModel_UsesItsTargets_AndRefusesToWriteIntoTheJob()
    {
        var job = await ScanStructureAsync();
        var advisor = new ScriptedAdvisor("""
            { "targets": [
                { "id": "fotos", "path": "Fotos", "pattern": "{Anlass} {Jahr}", "holds": ["image", "video"], "reason": "wie bisher", "examples": ["Skifahrt 2015"] },
                { "id": "bad", "path": "../outside", "holds": ["image"], "reason": "escapes the root" },
                { "id": "papiere", "path": "Dokumente/Rechnungen", "holds": ["document", "nonsense"], "reason": "Rechnungen" }
            ] }
            """);
        var file = Path.Combine(_dir, "model-targets.json");

        var result = (await OkAsync(WithModel(advisor), "library", "propose", job.ToString(), "--out", file, "--root", Path.Combine(_dir, "neu"))).Result();

        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.Equal(1, result.GetProperty("dropped").GetInt32());
        var target = TargetStructureFile.Load(file);
        Assert.Equal(AdviceOrigin.Model, target.Origin);
        Assert.Equal(["fotos", "papiere"], target.Targets.Select(t => t.Id));
        Assert.Equal(["document"], target.Targets[1].Holds);
        Assert.Equal(Path.Combine(_dir, "neu"), target.Root);
        Assert.Equal(LibraryAdviceTask.Propose, advisor.Requests.Single().Task);

        var inside = await SfoAsync(null, "library", "propose", job.ToString(), "--no-model", "--out", Path.Combine(_root, "t.json"), "--json");
        Assert.Equal("output_inside_job", inside.Json().GetProperty("error").GetProperty("code").GetString());
        Assert.False(File.Exists(Path.Combine(_root, "t.json")));
    }

    [Fact]
    public void TargetFile_WithProblems_IsRejectedWithEveryProblem()
    {
        var ex = Assert.Throws<TargetStructureException>(() => TargetStructureFile.Parse("""
            { "schema_version": 1, "kind": "sfo_target_structure", "root": "relative/root", "source_job": null,
              "created_at": "2026-11-12T10:00:00Z", "origin": "rule", "model": null,
              "targets": [ { "id": "A B", "path": "/abs", "pattern": null, "holds": ["pictures"], "reason": null, "examples": [] } ] }
            """));
        Assert.Equal(4, ex.Problems.Count);
    }

    // ------------------------------------------------------------------ size

    /// <summary>
    /// The review of a library with 10,000 folders stays short: one page of findings, each bounded in size.
    /// Measured size is written to the test output.
    /// </summary>
    [Fact]
    public async Task Review_OfTenThousandFolders_StaysShort()
    {
        var root = Path.Combine(_dir, "generated");
        long jobId;
        await using (var sp = CoreServices())
        {
            await sp.InitializeSmartFileOrganizerDatabaseAsync();
            var job = await sp.GetRequiredService<IScanJobRepository>().CreateAsync(new ScanJob
            {
                RootPath = root, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow, StructureOnly = true
            });
            jobId = job.Id;

            var dirs = new List<DirectoryNode>();
            var files = new List<FileNode>();
            DirectoryNode Add(string parent, string name, int depth)
            {
                var full = depth == 0 ? root : Path.Combine(parent, name);
                var node = new DirectoryNode
                {
                    JobId = jobId, FullPath = full, Name = name, ParentPath = depth == 0 ? Path.GetDirectoryName(root)! : parent,
                    RootPath = root, RelativePath = depth == 0 ? "." : Path.GetRelativePath(root, full), Depth = depth,
                    DirStatus = DirectoryStatus.Scanned, ScannedAt = DateTime.UtcNow
                };
                dirs.Add(node);
                return node;
            }
            void File(DirectoryNode dir, string name, long size)
            {
                files.Add(new FileNode
                {
                    JobId = jobId, FullPath = Path.Combine(dir.FullPath, name), Name = name, ParentPath = dir.FullPath, RootPath = root,
                    RelativePath = Path.GetRelativePath(root, Path.Combine(dir.FullPath, name)), RelativeDir = dir.RelativePath,
                    Depth = dir.Depth + 1, Size = size, Extension = Path.GetExtension(name), LastWriteTime = new DateTime(2016, 1, 1),
                    FileType = Path.GetExtension(name) == ".pdf" ? FileType.Document : FileType.Image,
                    Status = FileNodeStatus.Discovered, ScannedAt = DateTime.UtcNow
                });
            }

            var r = Add(string.Empty, "generated", 0);
            string[] patterns = ["Anlass {0} 2015", "2016 Anlass {0}", "Anlass {0}", "anlass {0} 2017", "2018-05-01 Anlass {0}"];
            for (var a = 0; a < 20; a++)
            {
                var area = Add(r.FullPath, $"Bereich {a:00}", 1);
                for (var g = 0; g < 25; g++)
                {
                    var group = Add(area.FullPath, string.Format(patterns[g % patterns.Length], g), 2);
                    for (var e = 0; e < 19; e++)
                    {
                        var leaf = Add(group.FullPath, e % 7 == 0 ? $"Neuer Ordner ({e})" : $"Teil {e} von {group.Name}", 3);
                        if (e % 3 != 0)
                            File(leaf, $"IMG_{e:0000}.jpg", 1000 + a * 100 + g * 10 + e);
                        if (e == 5)
                            File(leaf, $"brief_{g}.pdf", 500);
                    }
                }
            }
            await sp.GetRequiredService<IDirectoryRepository>().InsertBatchAsync(dirs);
            await sp.GetRequiredService<IFileRepository>().InsertBatchAsync(files);
            _output.WriteLine($"{dirs.Count} folders, {files.Count} files");
            Assert.True(dirs.Count >= 10_000);
        }

        var sw = Stopwatch.StartNew();
        var run = await OkAsync(null, "library", "review", jobId.ToString(), "--no-model");
        sw.Stop();
        var bytes = Encoding.UTF8.GetByteCount(run.Stdout);
        var result = run.Result();
        _output.WriteLine($"library review: {bytes} bytes of JSON for {Items(result).Count} of {result.GetProperty("total").GetInt32()} findings in {sw.ElapsedMilliseconds} ms");
        _output.WriteLine(string.Join(", ", result.GetProperty("by_kind").EnumerateObject().Select(k => $"{k.Name} {k.Value}")));

        Assert.Equal(LibraryCommand.DefaultLimit, Items(result).Count);
        Assert.True(bytes < 60_000, $"{bytes} bytes");
        Assert.All(Items(result), i => Assert.True(i.GetProperty("items").GetArrayLength() <= 5));
    }

    // ------------------------------------------------------------------ plumbing

    private ServiceProvider CoreServices()
    {
        var services = new ServiceCollection();
        services.AddSmartFileOrganizerCore(o =>
        {
            o.DatabasePath = _dbPath;
            o.ReloadSettingsOnChange = false;
        });
        return services.BuildServiceProvider();
    }

    /// <summary>Every file and folder of the judged tree with size and write time, to prove nothing changed.</summary>
    private string Snapshot() => string.Join("\n",
        Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => File.Exists(p) ? $"{p}|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p):O}" : p));

    private void WriteSettingsWithOllamaAt(int port)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dbPath)!, "settings.json"),
            $$"""{ "Ollama": { "BaseUrl": "http://127.0.0.1:{{port}}" } }""");
    }

    private static int ClosedLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Answers every question with the same text and records what it was asked.</summary>
    private sealed class ScriptedAdvisor(string content) : ILibraryAdvisor
    {
        public ConcurrentQueue<LibraryAdviceRequest> Requests { get; } = new();

        public Task<LibraryAdviceReply> AskAsync(LibraryAdviceRequest request, CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            return Task.FromResult(new LibraryAdviceReply(content, "scripted", content, null));
        }
    }
}
