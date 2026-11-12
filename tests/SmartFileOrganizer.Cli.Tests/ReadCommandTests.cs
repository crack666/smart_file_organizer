using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Hosting;
using Xunit.Abstractions;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// The read commands (<c>dirs</c>, <c>files</c>, <c>show</c>, <c>summary</c>, <c>export</c>) against a prepared
/// database: a real scan of a temp folder, then verdicts and classifications written through the
/// repositories. No Ollama is involved.
/// </summary>
public class ReadCommandTests : IDisposable
{
    private const string EnvSecret = "SECRET_TOKEN_VALUE";

    private readonly ITestOutputHelper _output;
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _root;

    public ReadCommandTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_read_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _root = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(Path.Combine(_root, "keys"));
        Directory.CreateDirectory(Path.Combine(_root, "photos"));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "Shopping list");
        File.WriteAllText(Path.Combine(_root, "docs", "letter.txt"), "Dear landlord");
        File.WriteAllText(Path.Combine(_root, "docs", ".env"), $"API_KEY={EnvSecret}");
        File.WriteAllText(Path.Combine(_root, "keys", "id_rsa"), "not a real key");
        File.WriteAllBytes(Path.Combine(_root, "photos", "pic.png"), [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_root, "photos", "big.png"), new byte[5000]);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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

    private async Task<Run> SfoAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost { Out = stdout, Error = stderr };
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], host);
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<Run> OkAsync(params string[] args)
    {
        var run = await SfoAsync([.. args, "--json"]);
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        var doc = run.Json();
        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.Equal(args[0], doc.GetProperty("command").GetString());
        return run;
    }

    private static List<string> Names(JsonElement page) =>
        page.GetProperty("items").EnumerateArray()
            .Select(i => Path.GetFileName(i.GetProperty("path").GetString()!))
            .ToList();

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

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

    /// <summary>Scans the temp folder and adds verdicts and classifications by hand.</summary>
    private async Task<long> PrepareAsync()
    {
        var scan = await OkAsync("scan", _root);
        var jobId = scan.Result().GetProperty("job").GetProperty("id").GetInt64();

        await using var sp = CoreServices();
        await sp.InitializeSmartFileOrganizerDatabaseAsync();
        var dirs = sp.GetRequiredService<IDirectoryRepository>();
        var files = sp.GetRequiredService<IFileRepository>();
        var verdicts = sp.GetRequiredService<IDirectoryVerdictRepository>();
        var ai = sp.GetRequiredService<IClassificationRepository>();

        async Task Verdict(string dir, DirectoryVerdictKind? kind, RetentionValue? retention, string? purpose, string? error = null)
        {
            var node = await dirs.GetByPathAsync(jobId, dir) ?? throw new InvalidOperationException(dir);
            await verdicts.UpsertAsync(new DirectoryVerdict
            {
                DirectoryNodeId = node.Id,
                Verdict = kind,
                Source = VerdictSource.Modell,
                Purpose = purpose,
                Reason = purpose is null ? null : "Judged from names.",
                Retention = retention,
                Profile = new DirectoryProfile { FileCount = node.DirectFileCount },
                ModelUsed = "fake",
                RawResponse = purpose,
                JudgedAt = DateTime.UtcNow,
                Error = error
            });
        }

        // The purpose names a marked file, so it must not leave the process.
        await Verdict(P("docs"), DirectoryVerdictKind.Gemischt, RetentionValue.Pruefen, "Letters and the .env of a project.");
        await Verdict(P("photos"), DirectoryVerdictKind.Einheitlich, RetentionValue.Behalten, "Holiday photos.");
        await Verdict(P("keys"), null, null, null, error: "Ollama request timed out.");

        async Task Classify(string dir, string name, FileCategory category, double confidence, string summary)
        {
            var file = (await files.GetByDirectoryAsync(jobId, dir)).Single(f => f.Name == name);
            await ai.UpsertAsync(new FileClassification
            {
                FileNodeId = file.Id,
                Category = category,
                Importance = Importance.Medium,
                Confidence = confidence,
                Summary = summary,
                ModelUsed = "fake",
                AnalyzedAt = DateTime.UtcNow,
                RawResponse = summary,
                IsAiResult = true
            });
            await files.UpdateStatusAsync(file.Id, FileNodeStatus.AiAnalyzed);
        }

        await Classify(P("docs"), "letter.txt", FileCategory.Invoice, 0.4, "Rent letter.");
        await Classify(P("photos"), "pic.png", FileCategory.Photo, 0.9, "A beach.");
        await Classify(P("docs"), ".env", FileCategory.TrashCandidate, 0.8, $"Holds {EnvSecret}.");
        return jobId;
    }

    // ------------------------------------------------------------------ dirs

    [Fact]
    public async Task Dirs_PagesBySize_WithTotalLimitOffset()
    {
        var job = (await PrepareAsync()).ToString();

        var first = (await OkAsync("dirs", job, "--sort", "size", "--limit", "2")).Result();
        Assert.Equal(4, first.GetProperty("total").GetInt64());
        Assert.Equal(2, first.GetProperty("limit").GetInt32());
        Assert.Equal(0, first.GetProperty("offset").GetInt32());
        Assert.Equal(2, first.GetProperty("next_offset").GetInt32());
        Assert.Equal(["data", "photos"], Names(first));

        var root = first.GetProperty("items")[0];
        Assert.Equal(6, root.GetProperty("files").GetInt64());
        Assert.Equal(1, root.GetProperty("direct_files").GetInt64());
        Assert.Equal(2, root.GetProperty("sensitive_files").GetInt64());

        var second = (await OkAsync("dirs", job, "--sort", "size", "--limit", "2", "--offset", "2")).Result();
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next_offset").ValueKind);
        Assert.Equal(2, second.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Dirs_FiltersOnVerdictRetentionSensitiveSizeAndScope()
    {
        var job = (await PrepareAsync()).ToString();

        Assert.Equal(["docs"], Names((await OkAsync("dirs", job, "--verdict", DirectoryVerdictKind.Gemischt.ToCode())).Result()));
        Assert.Equal(["keys"], Names((await OkAsync("dirs", job, "--verdict", "error")).Result()));
        Assert.Equal(["data"], Names((await OkAsync("dirs", job, "--verdict", "none")).Result()));
        Assert.Equal(["photos"], Names((await OkAsync("dirs", job, "--retention", RetentionValue.Behalten.ToCode())).Result()));
        Assert.Equal(["data", "docs", "keys"], Names((await OkAsync("dirs", job, "--sensitive", "yes")).Result()));
        Assert.Equal(["photos"], Names((await OkAsync("dirs", job, "--sensitive", "no")).Result()));
        Assert.Equal(["data", "photos"], Names((await OkAsync("dirs", job, "--min-size", "4K")).Result()));
        Assert.Equal(["photos"], Names((await OkAsync("dirs", job, "--under", "photos")).Result()));
        Assert.Equal(["data"], Names((await OkAsync("dirs", job, "--depth", "0")).Result()));

        var keys = (await OkAsync("dirs", job, "--verdict", "error")).Result().GetProperty("items")[0];
        Assert.Equal("error", keys.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Dirs_WithholdsPurposeNamingAMarkedFile_UnlessIncludeSensitive()
    {
        var job = (await PrepareAsync()).ToString();

        var docs = (await OkAsync("dirs", job, "--under", "docs")).Result().GetProperty("items")[0];
        Assert.Equal(DirectoryTextGuard.NeutralHint, docs.GetProperty("purpose").GetString());
        Assert.True(docs.GetProperty("text_withheld").GetBoolean());

        var photos = (await OkAsync("dirs", job, "--under", "photos")).Result().GetProperty("items")[0];
        Assert.Equal("Holiday photos.", photos.GetProperty("purpose").GetString());
        Assert.False(photos.GetProperty("text_withheld").GetBoolean());

        var open = (await OkAsync("dirs", job, "--under", "docs", "--include-sensitive")).Result().GetProperty("items")[0];
        Assert.Contains(".env", open.GetProperty("purpose").GetString());
    }

    // ------------------------------------------------------------------ files

    [Fact]
    public async Task Files_FiltersAndProtectsMarkedFiles()
    {
        var job = (await PrepareAsync()).ToString();

        var all = (await OkAsync("files", job)).Result();
        Assert.Equal(6, all.GetProperty("total").GetInt64());
        Assert.DoesNotContain(EnvSecret, all.GetRawText());

        Assert.Equal(["letter.txt"], Names((await OkAsync("files", job, "--category", "invoice")).Result()));
        Assert.Equal(["letter.txt"], Names((await OkAsync("files", job, "--max-confidence", "0.5")).Result()));
        Assert.Equal(["pic.png"], Names((await OkAsync("files", job, "--max-confidence", "0.95", "--type", "image")).Result()));
        // The model said trash_candidate for .env; the guard reports review_needed, and the filter follows the guard.
        Assert.Empty(Names((await OkAsync("files", job, "--category", "trash_candidate")).Result()));
        Assert.Equal([".env", "id_rsa"], Names((await OkAsync("files", job, "--category", "review_needed")).Result()));
        Assert.Equal([".env", "letter.txt"], Names((await OkAsync("files", job, "--dir", "docs")).Result()));
        Assert.Equal(["big.png", "pic.png"], Names((await OkAsync("files", job, "--dir", P("photos"), "--sort", "size")).Result()));
        Assert.Equal(6, (await OkAsync("files", job, "--dir", ".", "--recursive")).Result().GetProperty("total").GetInt64());
        Assert.Equal(3, (await OkAsync("files", job, "--status", "ai_analyzed")).Result().GetProperty("total").GetInt64());
        Assert.Equal(3, (await OkAsync("files", job, "--status", "pending")).Result().GetProperty("total").GetInt64());
        Assert.Equal(["big.png", "pic.png"], Names((await OkAsync("files", job, "--verdict", DirectoryVerdictKind.Einheitlich.ToCode())).Result()));
        Assert.Equal(["id_rsa"], Names((await OkAsync("files", job, "--verdict", "error")).Result()));

        var marked = (await OkAsync("files", job, "--sensitive", "yes")).Result();
        Assert.Equal([".env", "id_rsa"], Names(marked));
        foreach (var item in marked.GetProperty("items").EnumerateArray())
        {
            Assert.True(item.GetProperty("protected").GetBoolean());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("confidence").ValueKind);
            Assert.Equal("review_needed", item.GetProperty("category").GetString());
        }

        var paged = (await OkAsync("files", job, "--limit", "4", "--offset", "4")).Result();
        Assert.Equal(2, paged.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, paged.GetProperty("next_offset").ValueKind);
    }

    [Fact]
    public async Task Files_IncludeSensitive_ShowsModelSummary()
    {
        var job = (await PrepareAsync()).ToString();

        var run = await OkAsync("files", job, "--sensitive", "yes", "--include-sensitive");

        Assert.Contains(EnvSecret, run.Stdout);
    }

    // ------------------------------------------------------------------ show

    [Fact]
    public async Task Show_File_IsProtected_AndCarriesDirectoryVerdict()
    {
        var job = (await PrepareAsync()).ToString();

        var result = (await OkAsync("show", job, Path.Combine("docs", ".env"))).Result();

        Assert.Equal("file", result.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("directory").ValueKind);
        var file = result.GetProperty("file");
        Assert.True(file.GetProperty("protected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("raw_response").ValueKind);
        Assert.Equal(DirectoryVerdictKind.Gemischt.ToCode(), file.GetProperty("dir_verdict").GetString());
        Assert.DoesNotContain(EnvSecret, result.GetRawText());

        var letter = (await OkAsync("show", job, P("docs", "letter.txt"))).Result().GetProperty("file");
        Assert.Equal("invoice", letter.GetProperty("category").GetString());
        Assert.Equal("Rent letter.", letter.GetProperty("raw_response").GetString());
    }

    [Fact]
    public async Task Show_Directory_HasVerdictProfileAndWithheldTexts()
    {
        var job = (await PrepareAsync()).ToString();

        var dir = (await OkAsync("show", job, "docs")).Result().GetProperty("directory");

        Assert.Equal(DirectoryVerdictKind.Gemischt.ToCode(), dir.GetProperty("verdict").GetString());
        Assert.Equal(VerdictSource.Modell.ToCode(), dir.GetProperty("verdict_source").GetString());
        Assert.Equal(DirectoryTextGuard.NeutralHint, dir.GetProperty("purpose").GetString());
        Assert.Equal("Judged from names.", dir.GetProperty("reason").GetString());
        Assert.Equal(DirectoryTextGuard.NeutralHint, dir.GetProperty("verdict_raw_response").GetString());
        Assert.Equal(2, dir.GetProperty("profile").GetProperty("file_count").GetInt32());
        Assert.Equal(1, dir.GetProperty("sensitive_files").GetInt64());

        var root = (await OkAsync("show", job, ".")).Result().GetProperty("directory");
        Assert.Equal(0, root.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task Show_UnknownPath_IsPathNotFound()
    {
        var job = (await PrepareAsync()).ToString();

        var run = await SfoAsync("show", job, "nowhere", "--json");

        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Equal(ErrorCodes.PathNotFound, run.Json().GetProperty("error").GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------ summary / export

    [Fact]
    public async Task Summary_CountsMatchTheListings()
    {
        var job = (await PrepareAsync()).ToString();

        var result = (await OkAsync("summary", job)).Result();

        Assert.Equal(4, result.GetProperty("directories").GetInt64());
        var files = result.GetProperty("files");
        Assert.Equal(6, files.GetProperty("total").GetInt64());
        Assert.Equal(2, files.GetProperty("sensitive").GetInt64());
        // Waiting for classification: notes.txt (document) and big.png (image); id_rsa is not eligible.
        Assert.Equal(2, files.GetProperty("pending").GetInt64());
        Assert.Equal(1, result.GetProperty("by_category").GetProperty("invoice").GetInt64());
        Assert.Equal(2, result.GetProperty("by_category").GetProperty("review_needed").GetInt64());
        Assert.Equal(1, result.GetProperty("directories_by_verdict").GetProperty("error").GetInt64());
        Assert.Equal(1, result.GetProperty("directories_by_verdict").GetProperty("none").GetInt64());
        var largest = result.GetProperty("largest_directories");
        Assert.Equal("photos", largest[0].GetProperty("relative_path").GetString());
        Assert.Equal(DirectoryVerdictKind.Einheitlich.ToCode(), largest[0].GetProperty("verdict").GetString());
        Assert.False(largest[0].TryGetProperty("purpose", out _));
        Assert.DoesNotContain(EnvSecret, result.GetRawText());
    }

    [Fact]
    public async Task Export_WritesProtectedCsv()
    {
        var job = (await PrepareAsync()).ToString();
        var target = Path.Combine(_dir, "out.csv");

        var result = (await OkAsync("export", job, "--csv", target)).Result();

        Assert.Equal(6, result.GetProperty("rows").GetInt32());
        Assert.Equal(Path.GetFullPath(target), result.GetProperty("path").GetString());
        var csv = await File.ReadAllTextAsync(target);
        Assert.Contains("letter.txt", csv);
        Assert.DoesNotContain(EnvSecret, csv);
    }

    [Fact]
    public async Task Export_MissingFolder_IsPathNotFound()
    {
        var job = (await PrepareAsync()).ToString();

        var run = await SfoAsync("export", job, "--csv", Path.Combine(_dir, "no", "such", "out.csv"), "--json");

        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Equal(ErrorCodes.PathNotFound, run.Json().GetProperty("error").GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------ contract

    [Theory]
    [InlineData("dirs")]
    [InlineData("files")]
    [InlineData("show", ".")]
    [InlineData("summary")]
    [InlineData("export", "--csv", "x.csv")]
    public async Task UnknownJob_IsJobNotFound(string command, params string[] rest)
    {
        await PrepareAsync();

        var run = await SfoAsync([command, "999", .. rest, "--json"]);

        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Equal(ErrorCodes.JobNotFound, run.Json().GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("dirs", "1", "--limit", "0")]
    [InlineData("dirs", "1", "--limit", "1001")]
    [InlineData("dirs", "1", "--offset", "-1")]
    [InlineData("dirs", "1", "--verdict", "bogus")]
    [InlineData("dirs", "1", "--sensitive", "maybe")]
    [InlineData("dirs", "1", "--min-size", "lots")]
    [InlineData("dirs", "1", "--sort", "colour")]
    [InlineData("files", "1", "--category", "bogus")]
    [InlineData("files", "1", "--status", "7")]
    [InlineData("files", "1", "--max-confidence", "2")]
    [InlineData("summary", "1", "--top", "100")]
    [InlineData("export", "1")]
    [InlineData("show", "1")]
    public async Task WrongInvocation_IsUsage(params string[] args)
    {
        var run = await SfoAsync([.. args, "--json"]);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Equal(ErrorCodes.Usage, run.Json().GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ReadCommands_DoNotChangeTheJob()
    {
        var job = await PrepareAsync();
        var before = (await OkAsync("jobs")).Result().GetRawText();

        foreach (var args in new[] { new[] { "dirs" }, ["files"], ["show", "."], ["summary"] })
            await OkAsync([args[0], job.ToString(), .. args.Skip(1)]);

        Assert.Equal(before, (await OkAsync("jobs")).Result().GetRawText());
    }

    [Fact]
    public async Task WithoutJson_PrintsTextWithPageFooter()
    {
        var job = (await PrepareAsync()).ToString();

        var run = await SfoAsync("files", job, "--limit", "2");

        Assert.Equal(ExitCodes.Ok, run.ExitCode);
        Assert.Contains("1-2 of 6 (next: --offset 2)", run.Stdout);
    }

    // ------------------------------------------------------------------ size

    /// <summary>Fertig-wenn of issue 6: the summary of a 10,000-file job stays under 20 kB of JSON.</summary>
    [Fact]
    public async Task Summary_OfTenThousandFiles_StaysUnder20KB()
    {
        var root = Path.Combine(_dir, "generated", "a-fairly-long-root-folder-name-to-make-paths-realistic");
        long jobId;

        await using (var sp = CoreServices())
        {
            await sp.InitializeSmartFileOrganizerDatabaseAsync();
            var job = await sp.GetRequiredService<IScanJobRepository>().CreateAsync(new ScanJob
            {
                RootPath = root,
                Status = JobStatus.Completed,
                CreatedAt = DateTime.UtcNow,
                TotalFiles = 10_000,
                ProcessedFiles = 10_000
            });
            jobId = job.Id;

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
                    Status = (FileNodeStatus)(i % 7), ScannedAt = DateTime.UtcNow
                });
            }
            await sp.GetRequiredService<IFileRepository>().InsertBatchAsync(fileNodes);
        }

        // Default --top and the largest allowed value: both must stay under the limit.
        foreach (var top in new[] { 10, JobReadServiceMaxTop })
        {
            var run = await OkAsync("summary", jobId.ToString(), "--top", top.ToString());
            var bytes = Encoding.UTF8.GetByteCount(run.Stdout);
            _output.WriteLine($"sfo summary --json --top {top} for 10,000 files: {bytes} bytes");

            Assert.Equal(10_000, run.Result().GetProperty("files").GetProperty("total").GetInt64());
            Assert.Equal(top, run.Result().GetProperty("largest_directories").GetArrayLength());
            Assert.True(bytes < 20_000, $"summary is {bytes} bytes");
        }

        var dirs = (await OkAsync("dirs", jobId.ToString())).Result();
        Assert.Equal(221, dirs.GetProperty("total").GetInt64());
        Assert.Equal(200, dirs.GetProperty("items").GetArrayLength());
        Assert.Equal(200, dirs.GetProperty("next_offset").GetInt32());
    }

    private const int JobReadServiceMaxTop = SmartFileOrganizer.Application.Services.JobReadService.MaxTop;
}
