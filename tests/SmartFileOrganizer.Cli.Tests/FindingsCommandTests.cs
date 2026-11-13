using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Hosting;
using Xunit.Abstractions;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>sfo findings</c> (https://github.com/crack666/smart_file_organizer/issues/26), run in-process on a generated
/// source and a generated library. The source is analysed with scripted answers (<c>sfo eval --stub</c>), so no
/// model runs. Every name and every file is invented.
/// </summary>
public class FindingsCommandTests : IDisposable
{
    /// <summary>Content of the dummy password list; must never appear in any output.</summary>
    private const string Secret = "dummy-secret-7Qx2-not-real";

    private readonly ITestOutputHelper _output;
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _corpus;
    private readonly string _tree;
    private readonly string _library;
    private readonly List<TruthDirectoryEntry> _dirs = [new(".", "mixed")];
    private readonly List<TruthFileEntry> _files = [];

    public FindingsCommandTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_findings_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _corpus = Path.Combine(_dir, "corpus");
        _tree = Path.Combine(_corpus, "tree");
        _library = Path.Combine(_dir, "library");

        // 300 loose camera photos in a folder without a meaningful name: one finding, not 300.
        Folder("Neuer Ordner", "uniform");
        for (var i = 0; i < 300; i++)
            Write($"Neuer Ordner/{Photo(i)}", "photo", $"loose photo {i}");

        // Twelve of them copied again: duplicates.
        Folder("Sicherung alt", "uniform");
        for (var i = 0; i < 12; i++)
            Copy($"Neuer Ordner/{Photo(i)}", $"Sicherung alt/{Photo(i)}", "photo");

        // A trip the library holds already: no finding.
        Folder("Urlaub Italien 2019", "uniform");
        for (var i = 0; i < 5; i++)
            Write($"Urlaub Italien 2019/IMG_20190612_1{i}0000.jpg", "photo", $"italy {i}");

        // Invoices with one photo among them: misplaced.
        Folder("Rechnungen 2023", "uniform");
        for (var i = 1; i <= 5; i++)
            Write($"Rechnungen 2023/2023-{i:00}_Rechnung.pdf", "invoice", $"%PDF-1.4 invoice {i}");
        Write("Rechnungen 2023/IMG_20230312_101500.jpg", "photo", "photo among invoices");

        // A mixed download folder: two photos and an invoice among an installer.
        Folder("Downloads", "mixed");
        Write("Downloads/IMG_20230812_143012.jpg", "photo", "download photo 1");
        Write("Downloads/IMG_20230812_143512.jpg", "photo", "download photo 2");
        Write("Downloads/Rechnung_Strom.pdf", "invoice", "%PDF-1.4 power");
        Write("Downloads/setup_tool_1.2.exe", "software_installer", "MZ installer");

        // A password list (sensitive by its name and content).
        Folder("Privat", "mixed");
        Write("Privat/passwoerter.txt", "document", "mail: " + Secret, ["credentials"]);
        Write("Privat/einkauf.txt", "document", "milk, bread");

        // An old user profile: app data with a browser profile and photos of a messenger among caches.
        Folder("Benutzer alt", "uniform");
        Folder("Benutzer alt/Max", "dump");
        Write("Benutzer alt/Max/NTUSER.DAT", "system_file", "regf");
        Folder("Benutzer alt/Max/Anwendungsdaten", "system");
        Folder("Benutzer alt/Max/Anwendungsdaten/Google", "system");
        Folder("Benutzer alt/Max/Anwendungsdaten/Google/Chrome", "system");
        Folder("Benutzer alt/Max/Anwendungsdaten/Google/Chrome/User Data", "system");
        foreach (var profile in new[] { "Default", "Profile 1" })
        {
            Folder($"Benutzer alt/Max/Anwendungsdaten/Google/Chrome/User Data/{profile}", "system");
            for (var i = 0; i < 15; i++)
                Write($"Benutzer alt/Max/Anwendungsdaten/Google/Chrome/User Data/{profile}/f_{i:000}.dat", "trash_candidate", $"cache {profile} {i}", ["credentials"]);
        }
        Folder("Benutzer alt/Max/Anwendungsdaten/Messenger", "system");
        Folder("Benutzer alt/Max/Anwendungsdaten/Messenger/media", "system");
        for (var i = 0; i < 3; i++)
            Write($"Benutzer alt/Max/Anwendungsdaten/Messenger/media/IMG_20190704_12{i}000.jpg", "photo", $"messenger photo {i}");
        Folder("Benutzer alt/Max/Anwendungsdaten/Updater", "system");
        for (var i = 0; i < 40; i++)
            Write($"Benutzer alt/Max/Anwendungsdaten/Updater/u_{i:000}.tmp", "trash_candidate", $"update {i}");
        Folder("Benutzer alt/Max/Desktop", "mixed");
        Write("Benutzer alt/Max/Desktop/notiz.txt", "document", "call the plumber");
        Folder("Benutzer alt/Max/Downloads", "mixed");
        Write("Benutzer alt/Max/Downloads/Handbuch_Router.pdf", "document", "%PDF-1.4 manual");
        Folder("Benutzer alt/Max/Dokumente", "uniform");
        for (var i = 0; i < 3; i++)
            Write($"Benutzer alt/Max/Dokumente/Brief_{i}.docx", "letter", $"PK letter {i}");

        var truth = new EvalTruthDocument(EvalTruth.SchemaVersion, new EvalCorpusInfo("findings-test", 1, 26), "tree", _dirs, _files);
        File.WriteAllText(Path.Combine(_corpus, EvalTruth.FileName), JsonSerializer.Serialize(truth, EvalJson.Options));

        // The library holds the trip.
        Directory.CreateDirectory(Path.Combine(_library, "Fotos", "2019", "2019-06 Italien"));
        foreach (var file in Directory.GetFiles(Path.Combine(_tree, "Urlaub Italien 2019")))
            File.Copy(file, Path.Combine(_library, "Fotos", "2019", "2019-06 Italien", Path.GetFileName(file)));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Photo(int i) => $"IMG_20180805_{10 + i / 60:00}{i % 60:00}00.jpg";

    private void Folder(string path, string verdict) => _dirs.Add(new TruthDirectoryEntry(path, verdict));

    private void Write(string relative, string category, string content, IReadOnlyList<string>? sensitive = null)
    {
        var path = Path.Combine(_tree, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _files.Add(new TruthFileEntry(relative, category, sensitive ?? []));
    }

    private void Copy(string from, string to, string category)
    {
        var target = Path.Combine(_tree, to.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(_tree, from.Replace('/', Path.DirectorySeparatorChar)), target);
        _files.Add(new TruthFileEntry(to, category, []));
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

    private async Task<Run> SfoAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--json", "--db", _dbPath], new CliHost { Out = stdout, Error = stderr });
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<JsonElement> OkAsync(params string[] args)
    {
        var run = await SfoAsync(args);
        Assert.True(run.ExitCode == ExitCodes.Ok, string.Join(' ', args) + ": " + run.Stdout + run.Stderr);
        return run.Result();
    }

    /// <summary>Scans and analyses the source with scripted answers, compares it, builds bundles.</summary>
    private async Task<string> PrepareAsync(bool withLibrary)
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
        return job;
    }

    private static List<JsonElement> Items(JsonElement result) => result.GetProperty("items").EnumerateArray().ToList();

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).GetString();

    private static List<string> Strings(JsonElement e, string name) =>
        e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToList();

    /// <summary>Fertig-wenn: each of the five kinds found correctly at least once, and 300 loose photos are one finding.</summary>
    [Fact]
    public async Task FindsEveryKind_AndSummarizesLoosePhotos()
    {
        var job = await PrepareAsync(withLibrary: true);
        var run = await SfoAsync("findings", job, "--limit", "200");
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        Assert.DoesNotContain(Secret, run.Stdout);
        var result = run.Result();
        var items = Items(result);
        foreach (var f in items)
            _output.WriteLine($"{f.GetProperty("rank")} {Str(f, "section")} {Str(f, "kind")} {Str(f, "certainty")} {f.GetProperty("files")} {Str(f, "title")}");

        // not_backed_up: the 300 loose photos are one finding with all 300 files.
        var loose = Assert.Single(items, f => Str(f, "kind") == "not_backed_up" && Str(f, "relative_path") == "Neuer Ordner");
        Assert.Equal(300, loose.GetProperty("files").GetInt64());
        Assert.Equal("move", loose.GetProperty("action").GetProperty("kind").GetString());
        Assert.Equal(300, loose.GetProperty("comparison").GetProperty("new").GetInt64());
        // The trip in the library is no finding.
        Assert.DoesNotContain(items, f => (Str(f, "relative_path") ?? "").Contains("Italien", StringComparison.Ordinal));

        // in_noise: photos and an invoice in the mixed download folder, one finding per kind; the installer not.
        var downloads = items.Where(f => Str(f, "kind") == "in_noise" && Str(f, "relative_path") == "Downloads").ToList();
        Assert.Equal(2, downloads.Single(f => Str(f, "group") == "images").GetProperty("files").GetInt64());
        Assert.Equal(1, downloads.Single(f => Str(f, "group") == "documents").GetProperty("files").GetInt64());
        Assert.Equal(2, downloads.Count);
        Assert.DoesNotContain(downloads, f => Strings(f, "examples").Contains("Downloads/setup_tool_1.2.exe"));
        // ... and the messenger photos in the app data of the old profile, as one finding.
        var appData = Assert.Single(items, f => Str(f, "kind") == "in_noise" && Str(f, "bundle_kind") == "dump_noise");
        Assert.Equal(3, appData.GetProperty("files").GetInt64());
        // The profile is a dump, not a unit (issue 59): its documents were judged by the model and are a sure
        // finding of their part of the dump, not a guess from the file type.
        var profileDocs = Assert.Single(items, f => Str(f, "kind") == "not_backed_up" && Str(f, "bundle_kind") == "dump_keep"
                                                    && (Str(f, "relative_path") ?? "").EndsWith("Dokumente", StringComparison.Ordinal));
        Assert.Equal(3, profileDocs.GetProperty("files").GetInt64());
        Assert.Equal("sure", Str(profileDocs, "section"));

        // misplaced: the photo among invoices, uncertain because no target content was checked.
        var misplaced = Assert.Single(items, f => Str(f, "kind") == "misplaced");
        Assert.Equal("Rechnungen 2023", Str(misplaced, "relative_path"));
        Assert.Equal("uncertain", Str(misplaced, "certainty"));
        Assert.Equal("unsure", Str(misplaced, "section"));
        Assert.Equal(["Rechnungen 2023/IMG_20230312_101500.jpg"], Strings(misplaced, "examples"));

        // sensitive: path and marker, single decision, never removed.
        var passwords = Assert.Single(items, f => Str(f, "kind") == "sensitive" && Str(f, "relative_path") == "Privat");
        Assert.Contains("credentials", Strings(passwords, "markers"));
        Assert.Equal("leave", passwords.GetProperty("action").GetProperty("kind").GetString());
        Assert.True(passwords.GetProperty("action").GetProperty("single_decision").GetBoolean());
        // The two browser profiles in app data make one finding, not one per folder.
        var browser = Assert.Single(items, f => Str(f, "kind") == "sensitive" && Str(f, "bundle_kind") == "dump_noise");
        Assert.Equal(30, browser.GetProperty("files").GetInt64());

        // duplicate: twelve redundant copies, keep the copy in the source folder of the photos.
        var duplicate = Assert.Single(items, f => Str(f, "kind") == "duplicate");
        Assert.Equal(12, duplicate.GetProperty("duplicates").GetProperty("redundant_files").GetInt64());
        Assert.Equal("certain", Str(duplicate, "certainty"));

        // Ranked: sure ones first, unsure ones last.
        var sections = items.Select(f => Str(f, "section")).ToList();
        Assert.Equal(sections.OrderBy(s => s == "unsure").ToList(), sections);
        Assert.Equal(Enumerable.Range(1, items.Count), items.Select(f => f.GetProperty("rank").GetInt32()));

        // Filters.
        var onlyMisplaced = Items(await OkAsync("findings", job, "--kind", "misplaced"));
        Assert.Equal(["misplaced"], onlyMisplaced.Select(f => Str(f, "kind")).Distinct());
        var sure = Items(await OkAsync("findings", job, "--min-certainty", "likely", "--limit", "200"));
        Assert.DoesNotContain(sure, f => Str(f, "section") == "unsure");
        var under = Items(await OkAsync("findings", job, "--under", "Rechnungen 2023", "--limit", "200"));
        Assert.All(under, f => Assert.StartsWith("Rechnungen 2023", Str(f, "relative_path") ?? ""));
        Assert.Contains(under, f => Str(f, "kind") == "misplaced");

        // Summary: one page, parseable, counts agree with the list.
        var summary = await SfoAsync("findings", job, "--summary");
        Assert.True(summary.ExitCode == ExitCodes.Ok, summary.Stdout);
        var s = summary.Result();
        Assert.Equal(result.GetProperty("total").GetInt64(), s.GetProperty("listed").GetInt64());
        foreach (var kind in new[] { "not_backed_up", "in_noise", "misplaced", "sensitive", "duplicate" })
            Assert.True(s.GetProperty("by_kind").GetProperty(kind).GetProperty("findings").GetInt64() >= 1, kind);
        Assert.Equal(0, s.GetProperty("missing_steps").GetArrayLength());

        // sfo summary carries the counts.
        var jobSummary = await OkAsync("summary", job);
        Assert.Equal(s.GetProperty("listed").GetInt64(), jobSummary.GetProperty("findings").GetProperty("listed").GetInt64());

        // show: the files through the output guard, the password list without its content.
        var shown = await SfoAsync("findings", "show", job, Str(passwords, "key")!);
        Assert.True(shown.ExitCode == ExitCodes.Ok, shown.Stdout);
        Assert.DoesNotContain(Secret, shown.Stdout);
        var file = Assert.Single(shown.Result().GetProperty("files").EnumerateArray(), f => Str(f, "name") == "passwoerter.txt");
        Assert.True(file.GetProperty("protected").GetBoolean());
    }

    /// <summary>Accept, reject and wrong change only the state of a finding; wrong keeps a test case.</summary>
    [Fact]
    public async Task Decisions_AreStored_AndChangeNoFile()
    {
        var job = await PrepareAsync(withLibrary: true);
        var before = Directory.GetFiles(_tree, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
        var items = Items(await OkAsync("findings", job, "--limit", "200"));
        var loose = Str(items.Single(f => Str(f, "kind") == "not_backed_up" && Str(f, "relative_path") == "Neuer Ordner"), "key")!;
        var misplaced = Str(items.Single(f => Str(f, "kind") == "misplaced"), "key")!;
        var duplicate = Str(items.Single(f => Str(f, "kind") == "duplicate"), "key")!;

        var accepted = Items(await OkAsync("findings", "accept", job, loose, "--note", "move to the library"));
        Assert.Equal("accepted", Str(accepted.Single(), "state"));
        Assert.Equal("human", Str(accepted.Single(), "decided_by"));

        var rejected = Items(await OkAsync("findings", "reject", job, misplaced, "--by", "agent"));
        Assert.Equal("agent", Str(rejected.Single(), "decided_by"));

        var wrong = Items(await OkAsync("findings", "wrong", job, duplicate));
        Assert.Equal(JsonValueKind.Number, wrong.Single().GetProperty("eval_case_id").ValueKind);

        // Rejected and wrong ones are not listed again; accepted ones are.
        var listed = Items(await OkAsync("findings", job, "--limit", "200"));
        Assert.Contains(listed, f => Str(f, "key") == loose && Str(f, "state") == "accepted");
        Assert.DoesNotContain(listed, f => Str(f, "key") == misplaced || Str(f, "key") == duplicate);
        var rejectedList = Items(await OkAsync("findings", job, "--state", "rejected"));
        Assert.Equal([misplaced], rejectedList.Select(f => Str(f, "key")));

        // The key is stable: a new bundle build gives the same keys.
        await OkAsync("bundles", "build", job);
        var again = Items(await OkAsync("findings", job, "--state", "accepted"));
        Assert.Equal([loose], again.Select(f => Str(f, "key")));

        // Reopen withdraws the decision.
        await OkAsync("findings", "reopen", job, misplaced);
        Assert.Contains(Items(await OkAsync("findings", job, "--limit", "200")), f => Str(f, "key") == misplaced);

        // The wrong finding is a test case of kind finding.
        await using (var sp = CoreServices())
        {
            var cases = await sp.GetRequiredService<IEvalCaseRepository>().GetAllAsync(EvalCaseKind.Finding);
            var c = Assert.Single(cases);
            Assert.Equal(EvalCaseOrigin.FindingReportedWrong, c.Origin);
            Assert.Equal("duplicate", c.Observed);
            Assert.Equal("none", c.Expected);
            Assert.EndsWith("#" + duplicate, c.SubjectPath);
        }

        // Unknown key.
        var unknown = await SfoAsync("findings", "accept", job, "duplicate-0000000000");
        Assert.Equal(ExitCodes.Failure, unknown.ExitCode);
        Assert.Equal(ErrorCodes.FindingNotFound, unknown.ErrorCode());

        // Nothing on disk changed.
        var after = Directory.GetFiles(_tree, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
        Assert.Equal(before, after);
    }

    /// <summary>Without a library, not_backed_up drops out and the command says which step is missing.</summary>
    [Fact]
    public async Task WithoutLibrary_NotBackedUpIsNotComputed()
    {
        var job = await PrepareAsync(withLibrary: false);
        var result = await OkAsync("findings", job, "--limit", "200");
        var items = Items(result);
        Assert.DoesNotContain(items, f => Str(f, "kind") == "not_backed_up");
        Assert.Equal("not_computed", result.GetProperty("coverage").GetProperty("not_backed_up").GetProperty("coverage").GetString());
        Assert.Contains(Strings(result, "missing_steps"), s => s.StartsWith($"sfo compare {job} --against", StringComparison.Ordinal));

        // The rest still works: noise, sensitive, duplicates (without a library: keep one, remove nothing).
        Assert.Contains(items, f => Str(f, "kind") == "in_noise");
        Assert.Contains(items, f => Str(f, "kind") == "sensitive");
        var duplicate = Assert.Single(items, f => Str(f, "kind") == "duplicate");
        Assert.Equal("leave", duplicate.GetProperty("action").GetProperty("kind").GetString());
        var misplaced = Assert.Single(items, f => Str(f, "kind") == "misplaced");
        Assert.Equal(JsonValueKind.Null, misplaced.GetProperty("target").ValueKind);
    }

    /// <summary>A scan alone: the command names the missing steps instead of failing.</summary>
    [Fact]
    public async Task ScanOnly_NamesMissingSteps()
    {
        var job = (await OkAsync("scan", _tree)).GetProperty("job").GetProperty("id").GetInt64().ToString();
        var result = await OkAsync("findings", job, "--summary");
        var missing = Strings(result, "missing_steps");
        Assert.Contains($"sfo analyze {job}", missing);
        Assert.Contains($"sfo bundles build {job}", missing);
        Assert.Equal("not_computed", result.GetProperty("coverage").GetProperty("duplicate").GetProperty("coverage").GetString());

        var library = (await OkAsync("scan", _library, "--role", "library")).GetProperty("job").GetProperty("id").GetInt64().ToString();
        var refused = await SfoAsync("findings", library);
        Assert.Equal(ErrorCodes.WrongRole, refused.ErrorCode());
    }

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

    /// <summary>Fertig-wenn: the summary of a 10,000-file job with a library comparison stays under 20 kB of JSON.</summary>
    [Fact]
    public async Task Summary_OfTenThousandFiles_StaysUnder20KB()
    {
        var root = Path.Combine(_dir, "generated", "a-fairly-long-root-folder-name-to-make-paths-realistic");
        long jobId;
        await using (var sp = CoreServices())
        {
            await sp.InitializeSmartFileOrganizerDatabaseAsync();
            var jobs = sp.GetRequiredService<IScanJobRepository>();
            var job = await jobs.CreateAsync(new ScanJob
            {
                RootPath = root, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow, TotalFiles = 10_000, ProcessedFiles = 10_000
            });
            jobId = job.Id;
            var library = await jobs.CreateAsync(new ScanJob
            {
                RootPath = Path.Combine(_dir, "generated-library"), Role = ScanRole.Library, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow
            });

            var dirNodes = new List<DirectoryNode>
            {
                new() { JobId = jobId, FullPath = root, Name = Path.GetFileName(root), ParentPath = Path.GetDirectoryName(root)!, RootPath = root, RelativePath = ".", ScannedAt = DateTime.UtcNow }
            };
            for (var t = 0; t < 20; t++)
            {
                var top = Path.Combine(root, $"top-level-folder-{t:00}-with-a-descriptive-and-rather-long-name");
                dirNodes.Add(new DirectoryNode { JobId = jobId, FullPath = top, Name = Path.GetFileName(top), ParentPath = root, RootPath = root, RelativePath = Path.GetRelativePath(root, top), Depth = 1, ScannedAt = DateTime.UtcNow });
                for (var s = 0; s < 10; s++)
                {
                    var sub = Path.Combine(top, $"subfolder-{s:00}-holding-photos-and-documents-from-many-years");
                    dirNodes.Add(new DirectoryNode { JobId = jobId, FullPath = sub, Name = Path.GetFileName(sub), ParentPath = top, RootPath = root, RelativePath = Path.GetRelativePath(root, sub), Depth = 2, ScannedAt = DateTime.UtcNow });
                }
            }
            await sp.GetRequiredService<IDirectoryRepository>().InsertBatchAsync(dirNodes);

            var leaves = dirNodes.Where(d => d.Depth == 2).ToList();
            string[] extensions = [".jpg", ".pdf", ".docx", ".mp4", ".zip", ".txt", ".png", ".xlsx"];
            var fileNodes = new List<FileNode>();
            for (var i = 0; i < 10_000; i++)
            {
                var dir = leaves[i % leaves.Count];
                var ext = extensions[i % extensions.Length];
                var name = i % 500 == 0 ? $"id_rsa_{i}" : $"some-file-with-a-long-name-{i:00000}{ext}";
                fileNodes.Add(new FileNode
                {
                    JobId = jobId, FullPath = Path.Combine(dir.FullPath, name), Name = name, ParentPath = dir.FullPath,
                    RootPath = root, RelativePath = Path.GetRelativePath(root, Path.Combine(dir.FullPath, name)),
                    RelativeDir = dir.RelativePath, Depth = 3, Size = 1000 + i * 37L, LastWriteTime = DateTime.UtcNow,
                    Extension = ext, FileType = SmartFileOrganizer.Scanning.Engine.FileTypeDetector.Detect(ext),
                    Status = FileNodeStatus.Discovered, ScannedAt = DateTime.UtcNow
                });
            }
            await sp.GetRequiredService<IFileRepository>().InsertBatchAsync(fileNodes);

            // A comparison with the library: most files new, every tenth backed up, pairs of copies across folders.
            var comparisons = sp.GetRequiredService<IContentComparisonRepository>();
            var stored = await comparisons.GetFilesAsync(jobId);
            var rows = stored.Select((f, i) => new FileComparison(
                f.Id, f.FullPath, f.Size, i % 10 == 0 ? ContentState.BackedUp : ContentState.New,
                null, null, i % 10 == 0 ? 1 : 0, i < 2000 ? i / 2 + 1 : null, null)).ToList();
            await comparisons.SaveComparisonAsync(new ComparisonHeader(0, jobId, library.Id, DateTime.UtcNow, true, 0), rows);
        }

        var listed = await OkAsync("findings", jobId.ToString(), "--limit", "500");
        _output.WriteLine($"findings for 10,000 files: {listed.GetProperty("total").GetInt64()}");
        Assert.True(listed.GetProperty("total").GetInt64() > 50);

        foreach (var top in new[] { FindingsCommandTopDefault, FindingsCommandTopMax })
        {
            var run = await SfoAsync("findings", jobId.ToString(), "--summary", "--top", top.ToString());
            Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
            var bytes = Encoding.UTF8.GetByteCount(run.Stdout);
            _output.WriteLine($"sfo findings --summary --json --top {top} for 10,000 files: {bytes} bytes");
            Assert.Equal(top, run.Result().GetProperty("top").GetArrayLength());
            Assert.True(bytes < 20_000, $"summary is {bytes} bytes");
        }
    }

    private const int FindingsCommandTopDefault = Commands.FindingsCommand.DefaultTop;
    private const int FindingsCommandTopMax = Commands.FindingsCommand.MaxTop;

    // ------------------------------------------------------------------ several libraries, targets, space (issue 74)

    private async Task<Run> SfoWithAsync(Action<IServiceCollection> configure, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--json", "--db", _dbPath],
            new CliHost { Out = stdout, Error = stderr, ConfigureServices = configure });
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Proposes the same place for every group, wherever it lies.</summary>
    private sealed class FixedAdvisor(string path) : Application.Findings.IFindingTargetAdvisor
    {
        public Task<IReadOnlyList<Application.Findings.FindingTarget?>> ProposeAsync(long sourceJobId, IReadOnlyList<long> libraryJobIds,
            IReadOnlyList<IReadOnlyList<FileNode>> groups, IReadOnlyDictionary<long, FileComparison> comparison, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Application.Findings.FindingTarget?>>(groups
                .Select(_ => (Application.Findings.FindingTarget?)new Application.Findings.FindingTarget(
                    path, Path.GetFileName(path), ResultCertainty.Likely, "a fixed place", 0, TargetContentCheck.NotChecked))
                .ToList());
    }

    private sealed class FixedProbe(long? free) : IFreeSpaceProbe
    {
        public long? AvailableBytes(string path) => free;
    }

    private long JobIdOf(JsonElement scan) => scan.GetProperty("job").GetProperty("id").GetInt64();

    /// <summary>Fertig-wenn: files only in the second of two libraries are backed up there; with the first alone they are not.</summary>
    [Fact]
    public async Task SecondLibrary_HoldsTheLoosePhotos_SoTheyAreNoLongerNotBackedUp()
    {
        var job = await PrepareAsync(withLibrary: true);
        var first = (await OkAsync("findings", job, "--summary")).GetProperty("inputs").GetProperty("library_job_id").GetInt64();

        // A second root of the library holds the loose photos.
        var second = Path.Combine(_dir, "library-photos");
        Directory.CreateDirectory(Path.Combine(second, "Bilder", "2018"));
        foreach (var file in Directory.GetFiles(Path.Combine(_tree, "Neuer Ordner")))
            File.Copy(file, Path.Combine(second, "Bilder", "2018", Path.GetFileName(file)));
        var secondId = JobIdOf(await OkAsync("scan", second, "--role", "library"));
        await OkAsync("compare", job, "--against", first.ToString(), "--against", secondId.ToString());

        var both = await OkAsync("findings", job, "--limit", "200");
        Assert.DoesNotContain(Items(both), f => Str(f, "kind") == "not_backed_up" && Str(f, "relative_path") == "Neuer Ordner");
        Assert.Equal([first, secondId], both.GetProperty("inputs").GetProperty("library_job_ids").EnumerateArray().Select(x => x.GetInt64()));

        var summary = await OkAsync("findings", job, "--summary");
        var byLibrary = summary.GetProperty("overview").GetProperty("backed_up").GetProperty("by_library").EnumerateArray().ToList();
        Assert.Equal(2, byLibrary.Count);
        var inSecond = byLibrary.Single(l => l.GetProperty("library_job_id").GetInt64() == secondId);
        Assert.True(inSecond.GetProperty("files").GetInt64() >= 300, inSecond.ToString());
        Assert.Equal(second, Str(inSecond, "root_path"));

        // With the first library alone they are not backed up.
        var alone = await OkAsync("findings", job, "--against", first.ToString(), "--limit", "200");
        var loose = Assert.Single(Items(alone), f => Str(f, "kind") == "not_backed_up" && Str(f, "relative_path") == "Neuer Ordner");
        Assert.Equal(300, loose.GetProperty("comparison").GetProperty("new").GetInt64());

        // A library without a stored comparison is named: an error, not a guess.
        var missing = await SfoAsync("findings", job, "--against", "9999");
        Assert.Equal(ExitCodes.Failure, missing.ExitCode);
        Assert.Equal(ErrorCodes.ComparisonNotFound, missing.ErrorCode());
    }

    /// <summary>Fertig-wenn: without a library no finding has a target path, and the summary says no permanent storage is known.</summary>
    [Fact]
    public async Task WithoutLibrary_NoFindingHasATarget_EvenWhenAnAdvisorProposesOne()
    {
        var job = await PrepareAsync(withLibrary: false);
        var profileVideos = Path.Combine(_dir, "home", "Videos");
        void Configure(IServiceCollection s) => s.AddSingleton<Application.Findings.IFindingTargetAdvisor>(new FixedAdvisor(profileVideos));

        var run = await SfoWithAsync(Configure, "findings", job, "--limit", "200");
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        var items = Items(run.Result());
        Assert.NotEmpty(items);
        Assert.All(items, f => Assert.Equal(JsonValueKind.Null, f.GetProperty("target").ValueKind));
        Assert.DoesNotContain(profileVideos, run.Stdout);

        var summary = await SfoWithAsync(Configure, "findings", job, "--summary");
        var storage = summary.Result().GetProperty("storage");
        Assert.Equal("no_library", Str(storage, "status"));
        Assert.Empty(storage.GetProperty("roots").EnumerateArray());
        Assert.Contains("No permanent storage", Str(storage, "message"));
        var overview = summary.Result().GetProperty("overview");
        Assert.True(overview.GetProperty("files").GetProperty("files").GetInt64() > 0);
        Assert.Equal(0, overview.GetProperty("backed_up").GetProperty("files").GetInt64());
    }

    /// <summary>Fertig-wenn: no target lies outside a library root; the summary names free and needed space per root and warns.</summary>
    [Fact]
    public async Task Targets_LieOnlyInLibraryRoots_AndTheSummaryWeighsTheirSpace()
    {
        var job = await PrepareAsync(withLibrary: true);

        // A place outside every library root, as a user profile folder would be: dropped, with a note.
        var outside = Path.Combine(_dir, "home", "Videos");
        var dropped = await SfoWithAsync(s => s.AddSingleton<Application.Findings.IFindingTargetAdvisor>(new FixedAdvisor(outside)),
            "findings", job, "--kind", "misplaced");
        Assert.True(dropped.ExitCode == ExitCodes.Ok, dropped.Stdout + dropped.Stderr);
        var misplaced = Assert.Single(Items(dropped.Result()));
        Assert.Equal(JsonValueKind.Null, misplaced.GetProperty("target").ValueKind);
        Assert.Contains(Strings(misplaced, "notes"), n => n.Contains("outside every library root", StringComparison.Ordinal));

        // A place inside the library: kept, with its library, and weighed against the free space of the root.
        var inside = Path.Combine(_library, "Fotos", "2023");
        void Inside(IServiceCollection s, long? free)
        {
            s.AddSingleton<Application.Findings.IFindingTargetAdvisor>(new FixedAdvisor(inside));
            s.AddSingleton<IFreeSpaceProbe>(new FixedProbe(free));
        }

        var kept = await SfoWithAsync(s => Inside(s, 1), "findings", job, "--kind", "misplaced");
        var target = Assert.Single(Items(kept.Result())).GetProperty("target");
        Assert.Equal(inside, Str(target, "path"));
        Assert.Equal(_library, Str(target, "library_root"));
        var libraryId = target.GetProperty("library_job_id").GetInt64();

        var tight = (await SfoWithAsync(s => Inside(s, 1), "findings", job, "--summary")).Result().GetProperty("storage");
        Assert.Equal("known", Str(tight, "status"));
        var root = Assert.Single(tight.GetProperty("roots").EnumerateArray());
        Assert.Equal(libraryId, root.GetProperty("library_job_id").GetInt64());
        Assert.Equal(1, root.GetProperty("free_bytes").GetInt64());
        Assert.True(root.GetProperty("needed_bytes").GetInt64() > 1);
        Assert.Equal("not_enough", Str(root, "space"));
        Assert.Contains(Strings(tight, "warnings"), w => w.Contains($"Library job {libraryId}", StringComparison.Ordinal));
        Assert.True(tight.GetProperty("unplaced_findings").GetInt32() > 0); // the loose photos still need a place

        var roomy = (await SfoWithAsync(s => Inside(s, long.MaxValue / 2), "findings", job, "--summary")).Result().GetProperty("storage");
        Assert.Equal("enough", Str(Assert.Single(roomy.GetProperty("roots").EnumerateArray()), "space"));
        Assert.Empty(roomy.GetProperty("warnings").EnumerateArray());

        var unknown = (await SfoWithAsync(s => Inside(s, null), "findings", job, "--summary")).Result().GetProperty("storage");
        var unknownRoot = Assert.Single(unknown.GetProperty("roots").EnumerateArray());
        Assert.Equal("unknown", Str(unknownRoot, "space"));
        Assert.Equal(JsonValueKind.Null, unknownRoot.GetProperty("free_bytes").ValueKind);
    }
}
