using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure;
using SmartFileOrganizer.Infrastructure.Ollama;
using SmartFileOrganizer.Infrastructure.Persistence;
using SmartFileOrganizer.Scanning.Engine;
using SmartFileOrganizer.Scanning.Rules;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Phase 1 (directory verdict) end to end: directories are generated on disk, scanned with the real
/// scan engine into a temp SQLite file, then judged with a scripted Ollama stand-in that counts calls.
/// </summary>
public class DirectoryVerdictTests : IDisposable
{
    private readonly string _dir;
    private readonly DatabaseContext _db;
    private readonly ScanJobRepository _jobRepo;
    private readonly FileRepository _fileRepo;
    private readonly DirectoryRepository _dirRepo;
    private readonly DirectoryClassificationRepository _dirClassRepo;
    private readonly DirectoryVerdictRepository _verdictRepo;
    private readonly ScriptedOllamaService _ollama = new();

    public DirectoryVerdictTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_verdict_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _db = new DatabaseContext(Path.Combine(_dir, "data.db"), NullLogger<DatabaseContext>.Instance);
        _db.InitializeAsync().GetAwaiter().GetResult();

        _jobRepo = new ScanJobRepository(_db);
        _fileRepo = new FileRepository(_db);
        _dirRepo = new DirectoryRepository(_db);
        _dirClassRepo = new DirectoryClassificationRepository(_db);
        _verdictRepo = new DirectoryVerdictRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ helpers

    private string MakeTree(string name) => Directory.CreateDirectory(Path.Combine(_dir, "tree", name)).FullName;

    private static void WriteFile(string path, int bytes = 16, DateTime? modifiedUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (modifiedUtc is { } t) File.SetLastWriteTimeUtc(path, t);
    }

    private async Task<long> ScanAsync(string root)
    {
        // Temp paths can contain "AppData" or user-data names; only the rules under test are active here.
        var rules = new ScanRuleSet
        {
            Name = "test",
            Rules = [new ScanRule("$Recycle.Bin", ScanRuleAction.Skip)]
        };
        var job = await _jobRepo.CreateAsync(new ScanJob
        {
            RootPath = root, Status = JobStatus.Created, CreatedAt = DateTime.UtcNow,
            Rules = ScanRulesFile.ToProfile(rules, "test")
        });

        var engine = new ScanEngine(new FileSystemAccessor(), _fileRepo, _dirRepo, _jobRepo, NullLogger<ScanEngine>.Instance);
        await engine.RunAsync(job, new Progress<ScanProgress>(_ => { }), CancellationToken.None);
        return job.Id;
    }

    private DirectoryPreAssessmentService NewService() => new(
        _dirRepo, _fileRepo, _dirClassRepo, _verdictRepo, _ollama, () => 4,
        new DirectoryAnalysisOptions(), NullLogger<DirectoryPreAssessmentService>.Instance);

    private async Task<DirectoryVerdict> VerdictAsync(long jobId, string path) =>
        await _verdictRepo.GetByDirectoryPathAsync(jobId, path)
        ?? throw new Xunit.Sdk.XunitException($"No verdict stored for {path}");

    private async Task AssertEveryDirectoryHasVerdictOrErrorAsync(long jobId)
    {
        var dirs = await _dirRepo.GetAllForJobOrderedByDepthAsync(jobId);
        var verdicts = (await _verdictRepo.GetForJobAsync(jobId)).ToDictionary(v => v.DirectoryNodeId);
        Assert.NotEmpty(dirs);
        foreach (var d in dirs)
        {
            Assert.True(verdicts.TryGetValue(d.Id, out var v), $"{d.RelativePath} has no row");
            Assert.True(v!.Verdict is not null || v.Error is not null, $"{d.RelativePath} has neither verdict nor error");
        }
    }

    // ------------------------------------------------------------------ Fertig, wenn

    [Fact]
    public async Task GitRepository_IsUnit_WithoutModelCall()
    {
        var repo = MakeTree("repo");
        WriteFile(Path.Combine(repo, ".git", "HEAD"));
        WriteFile(Path.Combine(repo, "README.md"));
        WriteFile(Path.Combine(repo, "src", "Program.cs"));
        WriteFile(Path.Combine(repo, "src", "Core", "Engine.cs"));

        var jobId = await ScanAsync(repo);
        var result = await NewService().RunAsync(jobId);

        Assert.Empty(_ollama.PreAssessInputs);
        Assert.Equal(0, result.ErrorCount);

        var root = await VerdictAsync(jobId, repo);
        Assert.Equal(DirectoryVerdictKind.Unit, root.Verdict);
        Assert.Equal(VerdictSource.Rule, root.Source);
        Assert.Equal(DirectoryVerdictRules.Marker, root.Rule);
        Assert.Contains("dir:.git", root.Profile.UnitMarkers);
        Assert.Contains(".git", root.Reason);
        Assert.Equal(root.DirectoryNodeId, root.UnitDirectoryNodeId);

        foreach (var inner in new[] { Path.Combine(repo, "src"), Path.Combine(repo, "src", "Core"), Path.Combine(repo, ".git") })
        {
            var v = await VerdictAsync(jobId, inner);
            Assert.Equal(DirectoryVerdictKind.Unit, v.Verdict);
            Assert.Equal(DirectoryVerdictRules.InsideUnit, v.Rule);
            Assert.Equal(root.DirectoryNodeId, v.UnitDirectoryNodeId);
        }

        // A unit is not analysed file by file.
        foreach (var path in new[] { repo, Path.Combine(repo, "src"), Path.Combine(repo, "src", "Core") })
            Assert.All(await _fileRepo.GetByDirectoryAsync(jobId, path), f => Assert.Equal(FileNodeStatus.Skipped, f.Status));

        // Fixed codes in the database, not enum numbers or C# names.
        await using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleAsync<(string Verdict, string Source)>(
            "SELECT verdict, source FROM directory_verdicts WHERE directory_node_id = @id", new { id = root.DirectoryNodeId });
        Assert.Equal(("unit", "rule"), row);

        await AssertEveryDirectoryHasVerdictOrErrorAsync(jobId);
    }

    [Fact]
    public async Task TwoHundredCameraJpgs_ProfileSuggestsUniform()
    {
        var trip = MakeTree("Urlaub");
        var start = new DateTime(2024, 5, 17, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 200; i++)
            WriteFile(Path.Combine(trip, $"IMG_{i:0000}.JPG"), 100 + i, start.AddHours(i));

        var jobId = await ScanAsync(trip);
        await NewService().RunAsync(jobId);

        var input = Assert.Single(_ollama.PreAssessInputs);
        var profile = input.Profile;
        Assert.Equal(200, profile.FileCount);
        Assert.Equal(DirectoryVerdictKind.Uniform, profile.Suggests);
        Assert.Equal(new[] { ".jpg" }, profile.Extensions.Select(e => e.Key));
        Assert.Equal(200, Assert.Single(profile.FileTypes, t => t.Key == nameof(FileType.Image)).Count);
        Assert.Equal(200, Assert.Single(profile.NamePatterns, p => p.Pattern == "camera").Count);
        Assert.Equal(8, profile.SpanDays);
        Assert.Empty(profile.UnitMarkers);

        // The model sees the profile and its hint.
        var prompt = OllamaService.BuildPreAssessmentPrompt(input);
        Assert.Contains("camera 200", prompt);
        Assert.Contains("points to 'uniform'", prompt);

        var stored = await VerdictAsync(jobId, trip);
        Assert.Equal(VerdictSource.Model, stored.Source);
        Assert.Equal(DirectoryVerdictKind.Uniform, stored.Verdict);
        Assert.Equal(RetentionValue.Keep, stored.Retention);
        Assert.Equal(DirectoryVerdictKind.Uniform, stored.Profile.Suggests); // profile JSON round-trips
        Assert.Null(stored.Rule);
    }

    [Fact]
    public async Task PdfExeJpgZip_ProfileSuggestsMixed()
    {
        var downloads = MakeTree("Downloads");
        foreach (var name in new[] { "rechnung.pdf", "setup.exe", "foto.jpg", "archiv.zip" })
            WriteFile(Path.Combine(downloads, name));

        var jobId = await ScanAsync(downloads);
        await NewService().RunAsync(jobId);

        var profile = Assert.Single(_ollama.PreAssessInputs).Profile;
        Assert.Equal(DirectoryVerdictKind.Mixed, profile.Suggests);
        Assert.Equal(4, profile.FileTypes.Count);
        Assert.Empty(profile.UnitMarkers); // an exe without dlls is an installer, not a program
        Assert.Equal(DirectoryVerdictKind.Mixed, (await VerdictAsync(jobId, downloads)).Verdict);
    }

    [Fact]
    public async Task EveryDirectoryOfAJob_GetsVerdictOrStoredError()
    {
        var root = MakeTree("Daten");
        Directory.CreateDirectory(Path.Combine(root, "leer"));
        Directory.CreateDirectory(Path.Combine(root, "Alt", "nichts"));
        WriteFile(Path.Combine(root, "Fotos", "2019", "IMG_0001.jpg"));
        WriteFile(Path.Combine(root, "Fotos", "2019", "IMG_0002.jpg"));
        WriteFile(Path.Combine(root, "Fotos", "2020", "DSC00001.jpg"));
        WriteFile(Path.Combine(root, "Fotos", "2020", "DSC00002.jpg"));
        WriteFile(Path.Combine(root, ".cache", "blob.bin"));
        WriteFile(Path.Combine(root, "$Recycle.Bin", "deleted.txt"));
        WriteFile(Path.Combine(root, "Programm", "tool.exe"));
        WriteFile(Path.Combine(root, "Programm", "tool.dll"));
        WriteFile(Path.Combine(root, "Ablage", "brief.docx"));
        WriteFile(Path.Combine(root, "Ablage", "song.mp3"));
        WriteFile(Path.Combine(root, "Ablage", "clip.mp4"));

        var jobId = await ScanAsync(root);
        var result = await NewService().RunAsync(jobId);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(new[] { "2019", "2020", "Ablage" }, _ollama.CalledDirectoryNames);

        async Task Expect(string relative, DirectoryVerdictKind kind, string? rule)
        {
            var v = await VerdictAsync(jobId, relative.Length == 0 ? root : Path.Combine(root, relative));
            Assert.True(kind == v.Verdict, $"{relative}: expected {kind}, got {v.Verdict} ({v.Rule}: {v.Reason})");
            Assert.Equal(rule, v.Rule);
            Assert.Equal(rule is null ? VerdictSource.Model : VerdictSource.Rule, v.Source);
            Assert.False(string.IsNullOrWhiteSpace(v.Reason));
        }

        await Expect("leer", DirectoryVerdictKind.Empty, DirectoryVerdictRules.Empty);
        await Expect(Path.Combine("Alt", "nichts"), DirectoryVerdictKind.Empty, DirectoryVerdictRules.Empty);
        await Expect("Alt", DirectoryVerdictKind.Empty, DirectoryVerdictRules.Container);
        await Expect(Path.Combine("Fotos", "2019"), DirectoryVerdictKind.Uniform, null);
        await Expect("Fotos", DirectoryVerdictKind.Uniform, DirectoryVerdictRules.Container);
        await Expect(".cache", DirectoryVerdictKind.System, DirectoryVerdictRules.ScanSkip); // the scan skips dot directories
        await Expect("$Recycle.Bin", DirectoryVerdictKind.System, DirectoryVerdictRules.ScanSkip);
        await Expect("Programm", DirectoryVerdictKind.Unit, DirectoryVerdictRules.Marker);
        await Expect("Ablage", DirectoryVerdictKind.Mixed, null);
        await Expect("", DirectoryVerdictKind.Mixed, DirectoryVerdictRules.Container);

        Assert.Equal(nameof(FileType.Image), (await VerdictAsync(jobId, Path.Combine(root, "Fotos"))).Profile.SubdirectoryDominantType);
        await AssertEveryDirectoryHasVerdictOrErrorAsync(jobId);

        // The read side for CLI and GUI.
        var query = new DirectoryQueryService(_dirRepo, _verdictRepo);
        var all = await query.GetVerdictsAsync(jobId);
        Assert.Equal((await _dirRepo.GetAllForJobOrderedByDepthAsync(jobId)).Count, all.Count);
        Assert.Equal(DirectoryVerdictKind.Mixed, (await query.GetVerdictAsync(jobId, root))!.Verdict);
    }

    [Fact]
    public async Task ModelError_IsStored_AndRetriedOnNextRun()
    {
        var root = MakeTree("Mix");
        WriteFile(Path.Combine(root, "a", "x.pdf"));
        WriteFile(Path.Combine(root, "b", "y.pdf"));

        var jobId = await ScanAsync(root);
        _ollama.Answer = input => input.Directory.Name == "b" ? null : DirectoryVerdictKind.Uniform;
        var first = await NewService().RunAsync(jobId);

        var failed = await VerdictAsync(jobId, Path.Combine(root, "b"));
        Assert.Null(failed.Verdict);
        Assert.Equal("scripted failure", failed.Error);
        Assert.Equal(VerdictSource.Model, failed.Source);
        Assert.NotNull((await VerdictAsync(jobId, root)).Error); // waits for its subdirectory
        Assert.Equal(2, first.ErrorCount);
        await AssertEveryDirectoryHasVerdictOrErrorAsync(jobId);

        _ollama.PreAssessInputs.Clear();
        _ollama.Answer = _ => DirectoryVerdictKind.Uniform;
        var second = await NewService().RunAsync(jobId);

        Assert.Equal(0, second.ErrorCount);
        Assert.Equal(new[] { "b" }, _ollama.CalledDirectoryNames);
        Assert.Equal(DirectoryVerdictKind.Uniform, (await VerdictAsync(jobId, Path.Combine(root, "b"))).Verdict);
        Assert.Equal(DirectoryVerdictKind.Uniform, (await VerdictAsync(jobId, root)).Verdict);
    }

    [Fact]
    public async Task UnitJudgedByModel_SparesCallsBelowIt()
    {
        var root = MakeTree("Spiele");
        WriteFile(Path.Combine(root, "Spiel", "start.exe"));
        WriteFile(Path.Combine(root, "Spiel", "readme.txt"));
        WriteFile(Path.Combine(root, "Spiel", "levels", "level1.dat"));
        WriteFile(Path.Combine(root, "Spiel", "levels", "deep", "level2.dat"));

        var jobId = await ScanAsync(root);
        _ollama.Answer = input => input.Directory.Name == "Spiel" ? DirectoryVerdictKind.Unit : DirectoryVerdictKind.Mixed;
        await NewService().RunAsync(jobId);

        Assert.Equal(new[] { "Spiel" }, _ollama.CalledDirectoryNames);
        var game = await VerdictAsync(jobId, Path.Combine(root, "Spiel"));
        Assert.Equal(game.DirectoryNodeId, game.UnitDirectoryNodeId);
        var level = await VerdictAsync(jobId, Path.Combine(root, "Spiel", "levels", "deep"));
        Assert.Equal(DirectoryVerdictRules.InsideUnit, level.Rule);
        Assert.Equal(game.DirectoryNodeId, level.UnitDirectoryNodeId);
        Assert.Equal(DirectoryVerdictKind.Uniform, (await VerdictAsync(jobId, root)).Verdict); // a collection of units
    }

    /// <summary>
    /// A phone backup has no direct files, so before issue 50 the container rule made it a uniform
    /// collection and the model judged its camera folder. The dump recognition of the bundles
    /// (<c>BundlePatterns.Recognise</c>) now decides it as one unit, and everything inside follows.
    /// </summary>
    [Fact]
    public async Task PhoneBackup_IsOneUnit_WithEverythingInside_WithoutModelCall()
    {
        var root = MakeTree("Platte");
        var phone = Path.Combine(root, "Handy-Backup 2021");
        for (var i = 1; i <= 5; i++)
            WriteFile(Path.Combine(phone, "DCIM", "Camera", $"IMG_2021031{i}_120000.jpg"));
        Directory.CreateDirectory(Path.Combine(phone, "LOST.DIR"));
        WriteFile(Path.Combine(phone, "Android", "data", "com.example.notes", "files", "notes.db"));
        WriteFile(Path.Combine(root, "Fotos", "IMG_0001.jpg"));

        var jobId = await ScanAsync(root);
        _ollama.Answer = _ => DirectoryVerdictKind.Uniform;
        await NewService().RunAsync(jobId);

        var dump = await VerdictAsync(jobId, phone);
        Assert.Equal(DirectoryVerdictKind.Unit, dump.Verdict);
        Assert.Equal(DirectoryVerdictRules.Dump, dump.Rule);
        Assert.Contains("phone", dump.Reason);
        Assert.Equal(dump.DirectoryNodeId, dump.UnitDirectoryNodeId);

        foreach (var inside in new[] { "DCIM", "DCIM/Camera", "LOST.DIR", "Android", "Android/data",
                     "Android/data/com.example.notes", "Android/data/com.example.notes/files" })
        {
            var v = await VerdictAsync(jobId, Path.Combine(phone, inside.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(DirectoryVerdictKind.Unit, v.Verdict);
            Assert.Equal(DirectoryVerdictRules.InsideUnit, v.Rule);
            Assert.Equal(dump.DirectoryNodeId, v.UnitDirectoryNodeId);
        }

        // Only the photo folder next to the dump needed the model.
        Assert.Equal(new[] { "Fotos" }, _ollama.CalledDirectoryNames);
        Assert.All(await _fileRepo.GetByDirectoryAsync(jobId, Path.Combine(phone, "DCIM", "Camera")),
            f => Assert.Equal(FileNodeStatus.Skipped, f.Status));
    }

    /// <summary>
    /// Folders inside a unit get their verdict from the unit and no pre-assessment row of their own. The job
    /// status counted only those rows, so after a complete Phase 1 it showed them as pending (three in the
    /// first measurement of issue 50).
    /// </summary>
    [Fact]
    public async Task JobStatus_CountsFoldersInsideAUnitAsJudged()
    {
        var root = MakeTree("Status");
        WriteFile(Path.Combine(root, "app", "package.json"));
        WriteFile(Path.Combine(root, "app", "src", "index.js"));
        WriteFile(Path.Combine(root, "app", "src", "lib", "util.js"));
        WriteFile(Path.Combine(root, "Fotos", "IMG_0001.jpg"));

        var jobId = await ScanAsync(root);
        await NewService().RunAsync(jobId);

        var stats = await new JobStatisticsRepository(_db).GetAsync(jobId, 0);
        Assert.Equal(5, stats.DirectoriesAiRelevant);
        Assert.Equal(stats.DirectoriesAiRelevant, stats.DirectoriesPreAssessed);
        Assert.Equal(0, stats.PreAssessmentErrors);
    }

    /// <summary>
    /// A scan of the phone itself is not one unit: that would leave no file to judge. The bundles split
    /// such a scan into its parts (docs/bundles.md); the verdicts judge its folders one by one.
    /// </summary>
    [Fact]
    public async Task PhoneScannedAtTheRoot_IsNotOneUnit()
    {
        var phone = MakeTree("Handy");
        WriteFile(Path.Combine(phone, "DCIM", "Camera", "IMG_20210311_120000.jpg"));
        Directory.CreateDirectory(Path.Combine(phone, "LOST.DIR"));
        WriteFile(Path.Combine(phone, "Android", "data", "com.example.notes", "files", "notes.db"));

        var jobId = await ScanAsync(phone);
        _ollama.Answer = _ => DirectoryVerdictKind.Uniform;
        await NewService().RunAsync(jobId);

        Assert.NotEqual(DirectoryVerdictRules.Dump, (await VerdictAsync(jobId, phone)).Rule);
        Assert.Contains("Camera", _ollama.CalledDirectoryNames);
    }

    [Fact]
    public async Task SecondRun_DoesNotCallTheModelAgain()
    {
        var root = MakeTree("Stabil");
        WriteFile(Path.Combine(root, "a.pdf"));

        var jobId = await ScanAsync(root);
        await NewService().RunAsync(jobId);
        Assert.Single(_ollama.PreAssessInputs);

        var again = await NewService().RunAsync(jobId);
        Assert.Single(_ollama.PreAssessInputs);
        Assert.Equal("All directories already judged.", again.StatusText);
    }

    [Fact]
    public void HiddenDirectoryTheScanListed_IsSystem()
    {
        // The scan lists dot directories below user-data paths (for example ".thumbnails" under "Pictures").
        var dir = new DirectoryNode { Id = 7, Name = ".thumbnails", DirStatus = DirectoryStatus.Scanned };
        var profile = DirectoryProfileBuilder.Build(
            [new DirectoryFileInfo { Id = 1, Name = "a.png", Extension = ".png", FileType = (int)FileType.Image }], []);

        var (next, verdict) = DirectoryVerdictRules.Evaluate(dir, profile, null, null);

        Assert.Equal(DirectoryVerdictRules.Next.Decided, next);
        Assert.Equal(DirectoryVerdictKind.System, verdict!.Verdict);
        Assert.Equal(DirectoryVerdictRules.Hidden, verdict.Rule);
    }

    // ------------------------------------------------------------------ codes

    [Fact]
    public void Codes_AreTheGermanValuesOfTheIssue()
    {
        Assert.Equal(new[] { "unit", "uniform", "mixed", "empty", "system" }, VerdictCodes.All<DirectoryVerdictKind>());
        Assert.Equal(new[] { "keep", "review", "disposable" }, VerdictCodes.All<RetentionValue>());
        Assert.Equal(new[] { "rule", "model" }, VerdictCodes.All<VerdictSource>());

        Assert.True(VerdictCodes.TryParse<DirectoryVerdictKind>("Uniform", out var k));
        Assert.Equal(DirectoryVerdictKind.Uniform, k);
        Assert.False(VerdictCodes.TryParse<DirectoryVerdictKind>("2", out _));

        var json = System.Text.Json.JsonSerializer.Serialize(new DirectoryProfile { Suggests = DirectoryVerdictKind.Mixed },
            DirectoryVerdictRepository.ProfileJsonOptions);
        Assert.Contains("\"suggests\":\"mixed\"", json);
    }
}
