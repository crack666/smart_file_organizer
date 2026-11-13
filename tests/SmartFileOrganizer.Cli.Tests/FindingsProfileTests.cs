using System.Text.Json;
using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Cli.Contract;
using Xunit.Abstractions;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// Findings on a generated old user profile, shaped like the first real run of
/// https://github.com/crack666/smart_file_organizer/issues/64: a tool unpacked twice on the desktop, papers and
/// photos between the program folders, and a large mixed download folder. Analysed with scripted answers; every name
/// and every file is invented.
/// </summary>
public class FindingsProfileTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _corpus;
    private readonly string _tree;
    private readonly string _library;
    private readonly List<TruthDirectoryEntry> _dirs = [new(".", "uniform")];
    private readonly List<TruthFileEntry> _files = [];

    private const string Profile = "Profil/max";

    public FindingsProfileTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_findings_profile_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _corpus = Path.Combine(_dir, "corpus");
        _tree = Path.Combine(_corpus, "tree");
        _library = Path.Combine(_dir, "library");

        Folder("Profil", "uniform");
        Folder(Profile, "dump");
        Write($"{Profile}/NTUSER.DAT", "system_file", "regf hive");
        Folder($"{Profile}/AppData", "system");
        Folder($"{Profile}/AppData/Local", "system");
        for (var i = 0; i < 5; i++)
            Write($"{Profile}/AppData/Local/cache_{i}.bin", null, $"cache {i}");

        // The desktop: the same tool unpacked twice, and own papers and photos between the program folders.
        Folder($"{Profile}/Desktop", "mixed");
        foreach (var copy in new[] { "Packer 4.2", "Packer 4.2 (2)" })
        {
            Folder($"{Profile}/Desktop/{copy}", "unit");
            Write($"{Profile}/Desktop/{copy}/packer.exe", null, "MZ packer program");
            Write($"{Profile}/Desktop/{copy}/packer.dll", null, "MZ packer library");
            Folder($"{Profile}/Desktop/{copy}/lang", "unit");
            for (var i = 0; i < 20; i++)
                Write($"{Profile}/Desktop/{copy}/lang/lang_{i:00}.lng", null, $"language file {i} " + new string('x', 60_000));
            Folder($"{Profile}/Desktop/{copy}/sounds", "unit");
            Write($"{Profile}/Desktop/{copy}/sounds/done.wav", null, "RIFF sound of the tool");
            // The same file twice inside one program: no finding.
            Folder($"{Profile}/Desktop/{copy}/backup", "unit");
            Write($"{Profile}/Desktop/{copy}/backup/lang_00.lng", null, "language file 0 " + new string('x', 60_000));
        }
        Write($"{Profile}/Desktop/Lebenslauf_2015.docx", "document", "PK curriculum vitae");
        Write($"{Profile}/Desktop/Bewerbung_Schreiner.docx", "letter", "PK application letter");
        Write($"{Profile}/Desktop/IMG_20150704_120000.jpg", "photo", "summer photo");

        // A large mixed download folder: papers, photos, videos, installers and program archives.
        Folder($"{Profile}/Downloads", "mixed");
        for (var i = 0; i < 12; i++)
            Write($"{Profile}/Downloads/Rechnung_{i:00}.pdf", "invoice", $"%PDF-1.4 invoice {i}");
        for (var i = 0; i < 8; i++)
            Write($"{Profile}/Downloads/IMG_20140{i % 9 + 1}01_100000.jpg", "photo", $"download photo {i}");
        for (var i = 0; i < 3; i++)
            Write($"{Profile}/Downloads/VID_2014030{i + 1}_100000.mp4", "video", $"video {i}");
        for (var i = 0; i < 6; i++)
            Write($"{Profile}/Downloads/setup_tool_{i}.exe", "software_installer", $"MZ installer {i}");
        for (var i = 0; i < 4; i++)
            Write($"{Profile}/Downloads/driver_pack_{i}.zip", "archive", $"PK driver pack {i}");

        Folder($"{Profile}/Documents", "uniform");
        for (var i = 0; i < 3; i++)
            Write($"{Profile}/Documents/Brief_{i}.docx", "letter", $"PK letter {i}");

        // Two copies of the same holiday photos outside every program: one duplicate finding of files.
        Folder("Fotos alt", "uniform");
        Folder("Fotos alt Kopie", "uniform");
        for (var i = 0; i < 12; i++)
        {
            Write($"Fotos alt/IMG_20120801_10{i:00}00.jpg", "photo", $"holiday {i} " + new string('p', 100_000));
            Write($"Fotos alt Kopie/IMG_20120801_10{i:00}00.jpg", "photo", $"holiday {i} " + new string('p', 100_000));
        }

        var truth = new EvalTruthDocument(EvalTruth.SchemaVersion, new EvalCorpusInfo("findings-profile-test", 1, 64), "tree", _dirs, _files);
        File.WriteAllText(Path.Combine(_corpus, EvalTruth.FileName), JsonSerializer.Serialize(truth, EvalJson.Options));

        Directory.CreateDirectory(Path.Combine(_library, "Archiv"));
        File.WriteAllText(Path.Combine(_library, "Archiv", "notiz.txt"), "nothing of the source");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Folder(string path, string verdict) => _dirs.Add(new TruthDirectoryEntry(path, verdict));

    private void Write(string relative, string? category, string content)
    {
        var path = Path.Combine(_tree, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _files.Add(new TruthFileEntry(relative, category, []));
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

    private async Task<(string Job, List<JsonElement> Items)> FindingsAsync(bool withLibrary)
    {
        var job = (await OkAsync("eval", _corpus, "--stub", Path.Combine(_corpus, EvalTruth.FileName))).GetProperty("job_id").GetInt64().ToString();
        if (withLibrary)
        {
            var library = (await OkAsync("scan", _library, "--role", "library")).GetProperty("job").GetProperty("id").GetInt64().ToString();
            await OkAsync("compare", job, "--against", library);
        }
        else
        {
            await OkAsync("compare", job);
        }
        await OkAsync("bundles", "build", job);
        var items = (await OkAsync("findings", job, "--limit", "200")).GetProperty("items").EnumerateArray().ToList();
        foreach (var f in items)
            _output.WriteLine($"{f.GetProperty("rank")} {Str(f, "kind")} {Str(f, "certainty")} {Str(f, "group")} {f.GetProperty("files")} {Str(f, "title")} -> {f.GetProperty("action").GetProperty("kind")}");
        return (job, items);
    }

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetString();

    private static List<string> Strings(JsonElement e, string name) =>
        e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToList();

    /// <summary>Findings 1 and 2: one duplicate per copied program, none for files inside it; keep one copy, the rest can go.</summary>
    [Fact]
    public async Task ProgramUnpackedTwice_IsOneFolderDuplicate_AndNoFileInsideIsReported()
    {
        var (_, items) = await FindingsAsync(withLibrary: true);
        var duplicates = items.Where(f => Str(f, "kind") == "duplicate").ToList();
        Assert.Equal(2, duplicates.Count);

        var program = Assert.Single(duplicates, f => Str(f.GetProperty("duplicates"), "scope") == "folders");
        Assert.Equal([$"{Profile}/Desktop/Packer 4.2", $"{Profile}/Desktop/Packer 4.2 (2)"], Strings(program.GetProperty("duplicates"), "directories"));
        Assert.Equal($"{Profile}/Desktop/Packer 4.2", Str(program.GetProperty("duplicates"), "keep"));
        Assert.Equal("delete_source", program.GetProperty("action").GetProperty("kind").GetString());

        var photos = Assert.Single(duplicates, f => Str(f.GetProperty("duplicates"), "scope") == "files");
        Assert.Equal(12, photos.GetProperty("duplicates").GetProperty("redundant_files").GetInt64());
        Assert.Equal("delete_source", photos.GetProperty("action").GetProperty("kind").GetString());
        Assert.Equal("Fotos alt", Str(photos.GetProperty("duplicates"), "keep"));
    }

    /// <summary>Finding 2 without a library: nothing is removed.</summary>
    [Fact]
    public async Task WithoutLibrary_DuplicatesAreLeft()
    {
        var (_, items) = await FindingsAsync(withLibrary: false);
        var duplicates = items.Where(f => Str(f, "kind") == "duplicate").ToList();
        Assert.Equal(2, duplicates.Count);
        Assert.All(duplicates, f => Assert.Equal("leave", f.GetProperty("action").GetProperty("kind").GetString()));
    }

    /// <summary>Finding 4: a mixed download folder falls into one finding per kind, each with examples; installers and archives are none.</summary>
    [Fact]
    public async Task MixedDownloads_FallIntoOneFindingPerKind()
    {
        var (_, items) = await FindingsAsync(withLibrary: true);
        var downloads = items.Where(f => Str(f, "relative_path") == $"{Profile}/Downloads" && Str(f, "kind") == "not_backed_up").ToList();
        Assert.Equal(["documents", "images", "videos"], downloads.Select(f => Str(f, "group")!).Order(StringComparer.Ordinal));
        Assert.Equal(12, downloads.Single(f => Str(f, "group") == "documents").GetProperty("files").GetInt64());
        Assert.Equal(8, downloads.Single(f => Str(f, "group") == "images").GetProperty("files").GetInt64());
        Assert.Equal(3, downloads.Single(f => Str(f, "group") == "videos").GetProperty("files").GetInt64());
        Assert.All(downloads, f =>
        {
            Assert.NotEmpty(Strings(f, "examples"));
            Assert.NotEqual(JsonValueKind.Null, f.GetProperty("period").ValueKind);
        });
        Assert.DoesNotContain(items, f => Str(f, "group") is "installers" or "archives");
    }

    /// <summary>Findings 5 and 7: the sound of a program is no finding; the papers between the program folders are in_noise.</summary>
    [Fact]
    public async Task DesktopBetweenPrograms_IsNoise_AndProgramSoundsAreNoFinding()
    {
        var (_, items) = await FindingsAsync(withLibrary: true);
        Assert.DoesNotContain(items, f => Str(f, "kind") is "not_backed_up" or "in_noise"
                                           && (Str(f, "relative_path") ?? "").Contains("Packer", StringComparison.Ordinal));
        Assert.DoesNotContain(items, f => Str(f, "kind") is "not_backed_up" or "in_noise"
                                           && Strings(f, "examples").Any(e => e.EndsWith(".wav", StringComparison.Ordinal)));

        var desktop = items.Where(f => Str(f, "kind") == "in_noise" && Str(f, "relative_path") == $"{Profile}/Desktop").ToList();
        Assert.Equal(["documents", "images"], desktop.Select(f => Str(f, "group")!).Order(StringComparer.Ordinal));
        Assert.Equal(2, desktop.Single(f => Str(f, "group") == "documents").GetProperty("files").GetInt64());
        Assert.DoesNotContain(items, f => Str(f, "kind") == "not_backed_up" && Str(f, "relative_path") == $"{Profile}/Desktop");
    }
}
