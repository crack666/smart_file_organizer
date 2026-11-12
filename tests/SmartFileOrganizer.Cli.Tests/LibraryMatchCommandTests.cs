using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Hosting.Tests;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>sfo library show</c> and <c>sfo library match</c> against a generated library and a generated source
/// (https://github.com/crack666/smart_file_organizer/issues/25). Names are invented. The model is a scripted
/// stand-in; no test talks to a real Ollama.
/// </summary>
public class LibraryMatchCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _library;
    private readonly string _source;

    public LibraryMatchCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_match_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _library = Path.Combine(_dir, "ablage");
        _source = Path.Combine(_dir, "fund");

        // The library: photos by occasion, documents by kind and year, travels named in several ways, a catch-all, a phone copy.
        Photos(_library, "Fotos/Skifahrt 2015", new DateTime(2015, 2, 7, 10, 0, 0), 6, "ski");
        Photos(_library, "Fotos/Grillen bei Chris Sommer 2016", new DateTime(2016, 7, 16, 18, 0, 0), 5, "grill");
        Photos(_library, "Fotos/Hochzeit Anna 2017", new DateTime(2017, 5, 20, 14, 0, 0), 5, "hochzeit");
        Photos(_library, "Fotos/Ostern 2018", new DateTime(2018, 4, 1, 11, 0, 0), 4, "ostern");
        foreach (var kind in new[] { "Rechnungen", "Versicherung" })
        {
            foreach (var year in new[] { 2014, 2015, 2016 })
            {
                for (var i = 1; i <= 2; i++)
                    Write(_library, $"Dokumente/{kind}/{year}/{kind.ToLowerInvariant()}_{year}_{i}.pdf", $"{kind} {year} {i}", new DateTime(year, 3, i * 5));
            }
        }
        Photos(_library, "Reisen/Paris 2016", new DateTime(2016, 9, 3, 9, 0, 0), 3, "paris");
        Photos(_library, "Reisen/2017 London", new DateTime(2017, 6, 10, 9, 0, 0), 3, "london");
        Photos(_library, "Reisen/2018-07-01 Prag", new DateTime(2018, 7, 1, 9, 0, 0), 3, "prag");
        Photos(_library, "Reisen/Rom 2019", new DateTime(2019, 4, 20, 9, 0, 0), 3, "rom");
        Photos(_library, "Reisen/Wien 2020", new DateTime(2020, 2, 14, 9, 0, 0), 3, "wien");
        for (var i = 1; i <= 4; i++)
            Write(_library, $"Archiv/Sonstiges/notiz_{i}.txt", $"notiz {i}", new DateTime(2019, 1, i));
        Write(_library, "Handy/DCIM/Camera/IMG_20130101_100000.jpg", "phone", new DateTime(2013, 1, 1));
        Directory.CreateDirectory(Path.Combine(_library, "Handy", "LOST.DIR"));
        Directory.CreateDirectory(Path.Combine(_library, "Handy", "Android", "data", "com.example.chat"));

        // The source: loose photos of the ski trip, loose photos of an unknown summer day, a named folder the library
        // does not have, part of the ski trip under its own name, an invoice and a note.
        Photos(_source, "Neuer Ordner", new DateTime(2015, 2, 8, 9, 0, 0), 4, "loose ski");
        Photos(_source, "DCIM/100CANON", new DateTime(2021, 8, 14, 15, 0, 0), 4, "summer");
        Photos(_source, "Zelten am See 2019", new DateTime(2019, 8, 10, 12, 0, 0), 5, "zelten");
        Directory.CreateDirectory(Path.Combine(_source, "Fotos alt", "Skifahrt 2015"));
        foreach (var name in new[] { "IMG_20150207_100000.jpg", "IMG_20150207_103000.jpg", "IMG_20150207_110000.jpg" })
            File.Copy(Path.Combine(_library, "Fotos", "Skifahrt 2015", name), Path.Combine(_source, "Fotos alt", "Skifahrt 2015", name));
        Write(_source, "Fotos alt/Skifahrt 2015/IMG_20150208_160000.jpg", "a new ski photo", new DateTime(2015, 2, 8, 16, 0, 0));
        Write(_source, "Kram/rechnung_strom.pdf", "invoice", new DateTime(2016, 3, 20));
        Write(_source, "Kram/notiz_einkauf.txt", "note", new DateTime(2019, 1, 3));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Photos(string root, string folder, DateTime start, int count, string salt)
    {
        for (var i = 0; i < count; i++)
        {
            var time = start.AddMinutes(30 * i);
            Write(root, $"{folder}/IMG_{time:yyyyMMdd_HHmmss}.jpg", $"{salt} {i}", time);
        }
    }

    private static string Write(string root, string relative, string content, DateTime time)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTime(path, time);
        return path;
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(int Exit, JsonElement Doc, string Stderr)> RunAsync(Action<IServiceCollection>? configure, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--json", "--db", _dbPath],
            new CliHost { Out = stdout, Error = stderr, ConfigureServices = configure });
        using var doc = JsonDocument.Parse(stdout.ToString());
        return (exit, doc.RootElement.Clone(), stderr.ToString());
    }

    private async Task<JsonElement> OkAsync(Action<IServiceCollection>? configure, params string[] args)
    {
        var (exit, doc, stderr) = await RunAsync(configure, args);
        Assert.True(exit == ExitCodes.Ok, doc + stderr);
        return doc.GetProperty("result");
    }

    private Task<JsonElement> OkAsync(params string[] args) => OkAsync(null, args);

    private async Task<long> ScanAsync(string path, string role) =>
        (await OkAsync("scan", path, "--role", role)).GetProperty("job").GetProperty("id").GetInt64();

    private async Task<(long Library, long Source)> PrepareAsync(bool compare = true)
    {
        var library = await ScanAsync(_library, "library");
        var source = await ScanAsync(_source, "source");
        if (compare)
            await OkAsync("compare", source.ToString(), "--against", library.ToString());
        await OkAsync("bundles", "build", source.ToString());
        return (library, source);
    }

    private static List<JsonElement> Candidates(JsonElement result) => result.GetProperty("candidates").EnumerateArray().ToList();

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static Action<IServiceCollection> WithModel(ScriptedAdvisor advisor) => services =>
    {
        services.AddSingleton<IOllamaService>(new FakeOllamaService());
        services.AddSingleton<ILibraryAdvisor>(advisor);
    };

    private static string Snapshot(string root) => string.Join("\n",
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => File.Exists(p) ? $"{p}|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p):O}" : p));

    // ------------------------------------------------------------------ done when

    /// <summary>Loose photos from the days of an occasion find the occasion folder first.</summary>
    [Fact]
    public async Task LoosePhotos_OfTheSameDays_FindTheOccasionFolderFirst()
    {
        var (library, source) = await PrepareAsync();

        var byPath = await OkAsync("library", "match", source.ToString(), "Neuer Ordner", "--no-model");

        Assert.Equal("ok", Str(byPath, "status"));
        Assert.Equal(library, byPath.GetProperty("library").GetProperty("id").GetInt64());
        Assert.Equal("comparison", Str(byPath, "library_from"));
        Assert.False(byPath.GetProperty("unit").GetProperty("keep_whole").GetBoolean());
        var first = Candidates(byPath)[0];
        Assert.Equal("into_existing", Str(first, "placement"));
        Assert.Equal("Fotos/Skifahrt 2015", Str(first, "target_relative"));
        Assert.Equal("likely", Str(first, "certainty"));
        Assert.Contains("2015-02-08", Str(first, "reason"));
        Assert.Contains(first.GetProperty("signals").EnumerateArray(), s => Str(s, "signal") == "period" && s.GetProperty("agrees").GetBoolean());
        Assert.Equal(JsonValueKind.Null, byPath.GetProperty("new_folder").ValueKind);

        // The same photos as an event bundle.
        var events = (await OkAsync("bundles", "list", source.ToString(), "--all", "--kind", "event", "--limit", "100"))
            .GetProperty("items").EnumerateArray().ToList();
        var ski = events.Single(e => e.GetProperty("period").GetProperty("from").GetString()!.StartsWith("2015-02-08", StringComparison.Ordinal));
        var byBundle = await OkAsync("library", "match", source.ToString(), "--bundle", ski.GetProperty("id").GetInt64().ToString(), "--no-model");
        Assert.Equal("bundle", Str(byBundle.GetProperty("unit"), "kind"));
        Assert.Equal("Fotos/Skifahrt 2015", Str(Candidates(byBundle)[0], "target_relative"));
    }

    /// <summary>A named folder keeps its name and goes whole next to its kind: no rename, no split.</summary>
    [Fact]
    public async Task NamedSourceFolder_IsNeitherRenamedNorSplit()
    {
        var (_, source) = await PrepareAsync();

        var result = await OkAsync("library", "match", source.ToString(), "Zelten am See 2019", "--no-model", "--limit", "10");

        var unit = result.GetProperty("unit");
        Assert.True(unit.GetProperty("keep_whole").GetBoolean());
        Assert.Equal("Zelten am See 2019", Str(unit, "name"));
        var candidates = Candidates(result);
        Assert.NotEmpty(candidates);
        foreach (var c in candidates)
        {
            // Every proposal moves the folder as one, under its own name.
            Assert.True(c.GetProperty("keeps_name").GetBoolean());
            Assert.Equal("as_subfolder", Str(c, "placement"));
            Assert.EndsWith("/Zelten am See 2019", Str(c, "destination_relative"));
            Assert.Equal(Path.Combine(_library, Str(c, "destination_relative").Replace('/', Path.DirectorySeparatorChar)), Str(c, "destination"));
        }
        // Next to the other occasions; the travels are named in several ways, which is noted but does not rename it.
        Assert.Contains(candidates.Take(2), c => Str(c, "target_relative") == "Fotos");
        var travels = candidates.FirstOrDefault(c => Str(c, "target_relative") == "Reisen");
        if (travels.ValueKind != JsonValueKind.Undefined)
            Assert.Contains("keeps its own name", Str(travels.GetProperty("weak_place"), "note"));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("new_folder").ValueKind);

        // As a folder bundle it is the same.
        var bundles = (await OkAsync("bundles", "list", source.ToString(), "--kind", "folder", "--limit", "100")).GetProperty("items").EnumerateArray();
        var zelten = bundles.Single(b => Str(b, "suggested_name").StartsWith("Zelten", StringComparison.Ordinal));
        var byBundle = await OkAsync("library", "match", source.ToString(), "--bundle", zelten.GetProperty("id").GetInt64().ToString(), "--no-model");
        Assert.All(Candidates(byBundle), c => Assert.EndsWith("/Zelten am See 2019", Str(c, "destination_relative")));
    }

    // ------------------------------------------------------------------ signals and certainty

    [Fact]
    public async Task PartOfAnOccasion_UnderItsOwnName_GoesIntoTheSameFolder_Certainly()
    {
        var (_, source) = await PrepareAsync();

        var result = await OkAsync("library", "match", source.ToString(), "Fotos alt/Skifahrt 2015", "--no-model");

        Assert.Equal(3, result.GetProperty("unit").GetProperty("comparison").GetProperty("backed_up").GetInt64());
        var first = Candidates(result)[0];
        Assert.Equal("into_existing", Str(first, "placement"));
        Assert.Equal("Fotos/Skifahrt 2015", Str(first, "destination_relative"));
        Assert.Equal("certain", Str(first, "certainty"));
        Assert.Contains("3 of 4 files already lie here with the same content", Str(first, "reason"));
    }

    [Fact]
    public async Task AnInvoice_FindsTheInvoices_ByItsName_AndTheCatchAllIsAWeakPlace()
    {
        var (_, source) = await PrepareAsync();

        var invoice = await OkAsync("library", "match", source.ToString(), "Kram/rechnung_strom.pdf", "--no-model");
        var first = Candidates(invoice)[0];
        Assert.Equal("file", Str(invoice.GetProperty("unit"), "kind"));
        Assert.Equal("Dokumente/Rechnungen/2016", Str(first, "target_relative"));
        Assert.Contains("rechnung", Str(first, "reason"));

        var note = await OkAsync("library", "match", source.ToString(), "Kram/notiz_einkauf.txt", "--no-model");
        var catchAll = Candidates(note).Single(c => Str(c, "target_relative") == "Archiv/Sonstiges");
        Assert.Contains(catchAll.GetProperty("weak_place").GetProperty("findings").EnumerateArray(), f => Str(f, "kind") == "placeholder_name");
        Assert.Contains(Str(catchAll, "certainty"), new[] { "uncertain", "review" });
    }

    // ------------------------------------------------------------------ no fitting target

    [Fact]
    public async Task NoFittingFolder_ProposesANewOne_InTheStyleOfTheLibrary()
    {
        var (_, source) = await PrepareAsync();

        var rules = await OkAsync("library", "match", source.ToString(), "DCIM/100CANON", "--no-model");

        Assert.Empty(Candidates(rules));
        var proposal = rules.GetProperty("new_folder");
        Assert.Equal("Fotos", Str(proposal, "parent_relative"));
        Assert.Equal("{occasion} {year}", Str(proposal, "pattern"));
        Assert.Equal("{occasion} 2021", Str(proposal, "name"));
        Assert.False(proposal.GetProperty("name_complete").GetBoolean());
        Assert.Equal("rule", Str(proposal, "name_source"));
        Assert.Contains(Str(proposal, "certainty"), new[] { "uncertain", "review" });

        var advisor = new ScriptedAdvisor("""{ "name": "Sommer 2021", "reason": "Monat August, wie die Nachbarn mit Jahr" }""");
        var named = await OkAsync(WithModel(advisor), "library", "match", source.ToString(), "DCIM/100CANON");
        var withModel = named.GetProperty("new_folder");
        Assert.Equal("Sommer 2021", Str(withModel, "name"));
        Assert.Equal("model", Str(withModel, "name_source"));
        Assert.Equal("Fotos/Sommer 2021", Str(withModel, "destination_relative"));
        Assert.Equal("ok", Str(named.GetProperty("advice"), "status"));

        // The model saw names, counts and the pattern, never a file name.
        var facts = Assert.Single(advisor.Requests).Facts;
        Assert.Contains("{occasion} {year}", facts);
        Assert.Contains("Skifahrt 2015", facts);
        Assert.DoesNotContain("IMG_", facts);

        // A name that breaks the pattern is refused; the rule's name stays.
        var bad = new ScriptedAdvisor("""{ "name": "Fotos/Sommer", "reason": "x" }""");
        var refused = await OkAsync(WithModel(bad), "library", "match", source.ToString(), "DCIM/100CANON", "--refresh");
        Assert.Equal("{occasion} 2021", Str(refused.GetProperty("new_folder"), "name"));
        Assert.Equal("failed", Str(refused.GetProperty("advice"), "status"));
    }

    // ------------------------------------------------------------------ without a library

    [Fact]
    public async Task WithoutALibrary_MatchDoesNothing_AndSaysSo()
    {
        var source = await ScanAsync(_source, "source");

        var (exit, doc, stderr) = await RunAsync(null, "library", "match", source.ToString(), "Neuer Ordner");

        Assert.Equal(ExitCodes.Ok, exit);
        var result = doc.GetProperty("result");
        Assert.Equal("no_library", Str(result, "status"));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("library").ValueKind);
        Assert.Empty(Candidates(result));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("new_folder").ValueKind);
        Assert.Contains("--role library", Str(result, "message"));
        Assert.Contains("library propose", Str(result, "message"));

        // A map needs a library, too.
        var (showExit, showDoc, _) = await RunAsync(null, "library", "show", source.ToString(), "--no-model");
        Assert.Equal(ExitCodes.Failure, showExit);
        Assert.Equal("wrong_role", showDoc.GetProperty("error").GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------ show

    [Fact]
    public async Task Show_DescribesTheLibrary_AndWritesAnEditablePatternsFile()
    {
        var (library, source) = await PrepareAsync(compare: false);
        var before = Snapshot(_library) + Snapshot(_source);

        var map = await OkAsync("library", "show", library.ToString(), "--no-model", "--limit", "1000");

        var items = map.GetProperty("items").EnumerateArray().ToDictionary(i => Str(i, "relative_path"));
        var fotos = items["Fotos"];
        Assert.Equal("collection", Str(fotos, "role"));
        Assert.Equal("occasion", Str(fotos, "children_role"));
        Assert.Equal("{occasion} {year}", Str(fotos, "pattern"));
        Assert.Equal("occasion", Str(items["Fotos/Skifahrt 2015"], "role"));
        Assert.Equal("2015-02-07T10:00:00", Str(items["Fotos/Skifahrt 2015"].GetProperty("period"), "from"));
        Assert.Equal("file_name", Str(items["Fotos/Skifahrt 2015"].GetProperty("period"), "time_source"));
        Assert.Equal("time", Str(items["Dokumente/Rechnungen/2015"], "role"));
        Assert.Equal("{year}", Str(items["Dokumente/Rechnungen"], "pattern"));
        Assert.Equal("dump", Str(items["Handy"], "role"));
        Assert.DoesNotContain(items.Keys, k => k.StartsWith("Handy/", StringComparison.Ordinal));
        Assert.True(items["Reisen"].GetProperty("weak").GetBoolean());
        Assert.Contains(items["Reisen"].GetProperty("findings").EnumerateArray(), f => Str(f, "kind") == "naming_inconsistent");
        Assert.Equal("catch_all", Str(items["Archiv/Sonstiges"], "role"));
        Assert.All(items.Values, i => Assert.False(string.IsNullOrWhiteSpace(Str(i, "purpose"))));

        // Paging, depth, search and period filters.
        var top = await OkAsync("library", "show", library.ToString(), "--no-model", "--depth", "1");
        Assert.All(top.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("depth").GetInt32() <= 1));
        var page = await OkAsync("library", "show", library.ToString(), "--no-model", "--limit", "2");
        Assert.Equal(2, page.GetProperty("next_offset").GetInt32());
        var ski = await OkAsync("library", "show", library.ToString(), "--no-model", "--search", "ski");
        Assert.Equal(["Fotos/Skifahrt 2015"], ski.GetProperty("items").EnumerateArray().Select(i => Str(i, "relative_path")));
        var in2017 = await OkAsync("library", "show", library.ToString(), "--no-model", "--under", "Fotos", "--from", "2017", "--to", "2017", "--role", "occasion");
        Assert.Equal(["Fotos/Hochzeit Anna 2017"], in2017.GetProperty("items").EnumerateArray().Select(i => Str(i, "relative_path")));

        // The patterns file lies next to the database, was written once, and hand edits win.
        var patterns = map.GetProperty("patterns");
        var file = Str(patterns, "file");
        Assert.Equal(Path.Combine(Path.GetDirectoryName(_dbPath)!, "library-patterns", $"job-{library}.json"), file);
        Assert.True(patterns.GetProperty("created").GetBoolean());
        Assert.Contains(patterns.GetProperty("targets").EnumerateArray(), t => Str(t, "path") == "Fotos" && Str(t, "pattern") == "{occasion} {year}");
        var target = TargetStructureFile.Load(file);
        File.WriteAllText(file, TargetStructureFile.Serialize(target with
        {
            Targets = target.Targets.Select(t => t.Path == "Fotos" ? t with { Pattern = "{year} {occasion}" } : t).ToList()
        }));

        var again = await OkAsync("library", "show", library.ToString(), "--no-model", "--search", "fotos");
        Assert.False(again.GetProperty("patterns").GetProperty("created").GetBoolean());
        Assert.Equal("{year} {occasion}", Str(again.GetProperty("items")[0], "pattern"));
        var newFolder = (await OkAsync("library", "match", source.ToString(), "DCIM/100CANON", "--no-model", "--against", library.ToString()))
            .GetProperty("new_folder");
        Assert.Equal("2021 {occasion}", Str(newFolder, "name"));

        var reset = await OkAsync("library", "show", library.ToString(), "--no-model", "--search", "fotos", "--reset-patterns");
        Assert.True(reset.GetProperty("patterns").GetProperty("created").GetBoolean());
        Assert.Equal("{occasion} {year}", Str(reset.GetProperty("items")[0], "pattern"));

        File.WriteAllText(file, "{ \"kind\": \"nonsense\" }");
        var (exit, broken, _) = await RunAsync(null, "library", "show", library.ToString(), "--no-model");
        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal("library_patterns_invalid", broken.GetProperty("error").GetProperty("code").GetString());

        // Nothing in the library or the source was changed.
        Assert.Equal(before, Snapshot(_library) + Snapshot(_source));
    }

    [Fact]
    public async Task Show_WithModel_TakesItsPurposes_ForKnownFoldersOnly()
    {
        var (library, _) = await PrepareAsync(compare: false);
        var advisor = new ScriptedAdvisor("""
            { "purposes": [
                { "path": "Fotos", "purpose": "Fotos nach Anlass, je Ordner ein Anlass mit Jahr" },
                { "path": "Nirgendwo", "purpose": "not in the tree" } ] }
            """);

        var map = await OkAsync(WithModel(advisor), "library", "show", library.ToString(), "--depth", "1");

        var fotos = map.GetProperty("items").EnumerateArray().Single(i => Str(i, "relative_path") == "Fotos");
        Assert.Equal("Fotos nach Anlass, je Ordner ein Anlass mit Jahr", Str(fotos, "purpose"));
        Assert.Equal("model", Str(fotos, "purpose_source"));
        Assert.Equal(1, map.GetProperty("advice").GetProperty("dropped").GetInt32());
        var facts = Assert.Single(advisor.Requests).Facts;
        Assert.Contains("Fotos | collection | occasion", facts);
        Assert.DoesNotContain("IMG_", facts);
        Assert.DoesNotContain("rechnungen_2014", facts);
    }

    [Fact]
    public async Task Match_RefusesALibraryAsSource_AndUnknownPathsAndBundles()
    {
        var (library, source) = await PrepareAsync(compare: false);

        var (_, wrong, _) = await RunAsync(null, "library", "match", library.ToString(), "Fotos", "--no-model");
        Assert.Equal("wrong_role", wrong.GetProperty("error").GetProperty("code").GetString());

        var (_, missing, _) = await RunAsync(null, "library", "match", source.ToString(), "nowhere", "--no-model");
        Assert.Equal("path_not_found", missing.GetProperty("error").GetProperty("code").GetString());

        var (_, bundle, _) = await RunAsync(null, "library", "match", source.ToString(), "--bundle", "99999", "--no-model");
        Assert.Equal("bundle_not_found", bundle.GetProperty("error").GetProperty("code").GetString());

        var (usage, _, _) = await RunAsync(null, "library", "match", source.ToString(), "--no-model");
        Assert.Equal(ExitCodes.Usage, usage);

        // Without a comparison, the content signal is missing and said to be.
        var (_, plain, stderr) = await RunAsync(null, "library", "match", source.ToString(), "Neuer Ordner", "--no-model");
        Assert.Equal("latest", Str(plain.GetProperty("result"), "library_from"));
        Assert.Contains(plain.GetProperty("result").GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("sfo compare"));
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
