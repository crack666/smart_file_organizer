using System.Text.Json;
using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Cli.Contract;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// A user profile is a dump, not a unit (https://github.com/crack666/smart_file_organizer/issues/59): its documents,
/// desktop and downloads are analysed, its app data and caches are not, and that does not depend on whether the
/// profile is the scan root or lies below it. Runs <c>sfo eval --stub</c> in-process, so no model runs; every name
/// and file is invented.
/// </summary>
public class DumpPartsCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sfo_dumpparts_{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public DumpPartsCommandTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "state", "data.db");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Folders and files of a corpus with the answers the scripted model gives.</summary>
    private sealed class Corpus(string root)
    {
        public string Root { get; } = root;
        public string Tree => Path.Combine(Root, "tree");
        public List<TruthDirectoryEntry> Dirs { get; } = [new(".", null)];
        public List<TruthFileEntry> Files { get; } = [];

        public void Folder(string path, string? verdict)
        {
            Directory.CreateDirectory(Full(path));
            Dirs.Add(new TruthDirectoryEntry(path, verdict));
        }

        public void Write(string path, string? category, string content = "x", IReadOnlyList<string>? sensitive = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Full(path))!);
            File.WriteAllText(Full(path), content);
            Files.Add(new TruthFileEntry(path, category, sensitive ?? []));
        }

        /// <summary>A Windows profile with a newer AppData (skipped by the scan rules) and older noise folders the scan lists.</summary>
        public void Profile(string at)
        {
            if (at == ".")
            {
                Directory.CreateDirectory(Tree);
                Dirs[0] = new TruthDirectoryEntry(".", "dump");
            }
            else
            {
                Folder(at, "dump");
            }
            static string P(string at, string rest) => at == "." ? rest : $"{at}/{rest}";
            Write(P(at, $"NTUSER.DAT"), null, "regf");
            Folder(P(at, $"AppData"), "system");
            Folder(P(at, $"AppData/Local"), null);
            Folder(P(at, $"AppData/Local/Temp"), null);
            Folder(P(at, $"Lokale Einstellungen"), "system");
            Folder(P(at, $"Lokale Einstellungen/Temp"), "system");
            for (var i = 0; i < 8; i++)
                Write(P(at, $"Lokale Einstellungen/Temp/~DF{i:000}.tmp"), null, $"tmp {i}");
            Folder(P(at, $"Anwendungsdaten"), "system");
            Folder(P(at, $"Anwendungsdaten/Mozilla"), "system");
            Write(P(at, $"Anwendungsdaten/Mozilla/prefs.js"), null, "user_pref(1);");
            Folder(P(at, $"Documents"), "mixed");
            Write(P(at, $"Documents/Brief_an_Vermieter.txt"), "letter", "Sehr geehrte Frau Beispiel, ...");
            Write(P(at, $"Documents/Rezept.txt"), "document", "Mehl, Zucker, Butter");
            Folder(P(at, $"Desktop"), "mixed");
            Write(P(at, $"Desktop/Zugangsdaten_Router.txt"), null, "router: dummy", ["credentials"]);
            Write(P(at, $"Desktop/notiz.txt"), "document", "Reifen wechseln");
            Folder(P(at, $"Downloads"), "mixed");
            Write(P(at, $"Downloads/Handbuch.txt"), "document", "Kapitel 1");
        }

        public void Save()
        {
            var truth = new EvalTruthDocument(EvalTruth.SchemaVersion, new EvalCorpusInfo("dump-parts-test", 1, 59), "tree", Dirs, Files);
            File.WriteAllText(Path.Combine(Root, EvalTruth.FileName), JsonSerializer.Serialize(truth, EvalJson.Options));
        }

        private string Full(string path) => path == "." ? Tree : Path.Combine(Tree, path.Replace('/', Path.DirectorySeparatorChar));
    }

    private async Task<JsonElement> OkAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--json", "--db", _dbPath], new CliHost { Out = stdout, Error = stderr });
        Assert.True(exit == ExitCodes.Ok, string.Join(' ', args) + ": " + stdout + stderr);
        using var doc = JsonDocument.Parse(stdout.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    /// <summary>Scans and analyses with the answers of the corpus; returns the job id.</summary>
    private async Task<string> AnalyseAsync(Corpus corpus)
    {
        corpus.Save();
        var run = await OkAsync("eval", corpus.Root, "--stub", Path.Combine(corpus.Root, EvalTruth.FileName));
        return run.GetProperty("job_id").GetInt64().ToString();
    }

    private async Task<List<JsonElement>> ItemsAsync(params string[] args) =>
        (await OkAsync([.. args, "--limit", "1000"])).GetProperty("items").EnumerateArray().ToList();

    /// <summary>Paths below <paramref name="profile"/> (forward slashes) of the files that carry a category.</summary>
    private async Task<SortedSet<string>> FilesWithResultAsync(string job, string profile)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in await ItemsAsync("files", job))
        {
            var path = f.GetProperty("path").GetString()!;
            if (!path.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            if (f.GetProperty("category").ValueKind == JsonValueKind.String)
                result.Add(Path.GetRelativePath(profile, path).Replace('\\', '/'));
        }
        return result;
    }

    private static string? VerdictOf(List<JsonElement> dirs, string path) =>
        dirs.Single(d => d.GetProperty("path").GetString() == path).GetProperty("verdict").GetString();

    [Fact]
    public async Task Profile_DocumentsCarryAResult_AppDataDoesNot_WhetherScannedAtTheRootOrBelow()
    {
        // The profile itself is the scan root.
        var alone = new Corpus(Path.Combine(_dir, "alone"));
        alone.Profile(".");
        var aloneJob = await AnalyseAsync(alone);

        // The same profile one level below, next to a second one: a Users folder of an old computer.
        var users = new Corpus(Path.Combine(_dir, "users"));
        users.Dirs[0] = new TruthDirectoryEntry(".", "uniform");
        users.Profile("max");
        users.Profile("lisa");
        var usersJob = await AnalyseAsync(users);

        var fromRoot = await FilesWithResultAsync(aloneJob, alone.Tree);
        var fromBelow = await FilesWithResultAsync(usersJob, Path.Combine(users.Tree, "max"));

        Assert.Equal(fromRoot, fromBelow);
        Assert.Contains("Documents/Brief_an_Vermieter.txt", fromRoot);
        Assert.Contains("Documents/Rezept.txt", fromRoot);
        Assert.Contains("Desktop/notiz.txt", fromRoot);
        Assert.Contains("Downloads/Handbuch.txt", fromRoot);
        Assert.DoesNotContain(fromRoot, p => p.StartsWith("AppData/", StringComparison.Ordinal)
                                             || p.StartsWith("Lokale Einstellungen/", StringComparison.Ordinal)
                                             || p.StartsWith("Anwendungsdaten/", StringComparison.Ordinal));

        // The noise was not analysed, and the sensitive file on the desktop stays protected.
        var noise = await ItemsAsync("files", usersJob, "--dir", Path.Combine(users.Tree, "max", "Lokale Einstellungen", "Temp"));
        Assert.Equal(8, noise.Count);
        Assert.All(noise, f => Assert.Equal("skipped", f.GetProperty("status").GetString()));
        var secret = Assert.Single(await ItemsAsync("files", usersJob, "--dir", Path.Combine(users.Tree, "max", "Desktop")),
            f => f.GetProperty("name").GetString() == "Zugangsdaten_Router.txt");
        Assert.True(secret.GetProperty("protected").GetBoolean());

        // Same verdicts for the profile and its parts in both scans.
        var aloneDirs = await ItemsAsync("dirs", aloneJob);
        var usersDirs = await ItemsAsync("dirs", usersJob);
        foreach (var part in new[] { "", "AppData", "Lokale Einstellungen", "Lokale Einstellungen/Temp", "Anwendungsdaten", "Documents", "Desktop", "Downloads" })
        {
            var rel = part.Replace('/', Path.DirectorySeparatorChar);
            Assert.Equal(VerdictOf(aloneDirs, part.Length == 0 ? alone.Tree : Path.Combine(alone.Tree, rel)),
                VerdictOf(usersDirs, part.Length == 0 ? Path.Combine(users.Tree, "max") : Path.Combine(users.Tree, "max", rel)));
        }
        Assert.Equal("dump", VerdictOf(aloneDirs, alone.Tree));
    }

    [Fact]
    public async Task UsersFolderWithTwoProfiles_IsTwoDumps()
    {
        var users = new Corpus(Path.Combine(_dir, "users"));
        users.Dirs[0] = new TruthDirectoryEntry(".", "uniform");
        users.Profile("max");
        users.Profile("lisa");
        var job = await AnalyseAsync(users);

        var dirs = await ItemsAsync("dirs", job);
        var dumps = dirs.Where(d => d.GetProperty("verdict").GetString() == "dump")
            .Select(d => Path.GetFileName(d.GetProperty("path").GetString()!)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["lisa", "max"], dumps);
        // The Users folder is a collection of the two, judged from them without a model call.
        var root = dirs.Single(d => d.GetProperty("path").GetString() == users.Tree);
        Assert.Equal("uniform", root.GetProperty("verdict").GetString());
        Assert.Equal("rule", root.GetProperty("verdict_source").GetString());

        // The bundles find two dumps as well.
        await OkAsync("bundles", "build", job);
        var bundles = (await OkAsync("bundles", "list", job, "--all", "--limit", "500")).GetProperty("items").EnumerateArray()
            .Where(b => b.GetProperty("kind").GetString() == "dump").ToList();
        Assert.Equal(2, bundles.Count);
    }
}
