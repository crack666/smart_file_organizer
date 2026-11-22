using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Exchange;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Hosting;
using SmartFileOrganizer.Infrastructure.Persistence;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// The open exchange format (https://github.com/crack666/smart_file_organizer/issues/34): <c>sfo export --format jsonl</c>,
/// <c>sfo import-results</c> and the views <c>v_files</c> and <c>v_directories</c>, against a real scan of a temp
/// folder with verdicts and results written through the repositories. No model is involved.
/// </summary>
public class ExchangeFormatTests : IDisposable
{
    private const string EnvSecret = "SECRET_TOKEN_VALUE";

    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _root;

    public ExchangeFormatTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_exchange_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _root = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(Path.Combine(_root, "photos", "Urlaub 2024"));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "Shopping list");
        File.WriteAllText(Path.Combine(_root, "docs", "letter.txt"), "Dear landlord");
        File.WriteAllText(Path.Combine(_root, "docs", ".env"), $"API_KEY={EnvSecret}");
        File.WriteAllBytes(Path.Combine(_root, "photos", "Urlaub 2024", "Strand.png"), [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath, "--json"], host);
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<Run> OkAsync(params string[] args)
    {
        var run = await SfoAsync(args);
        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        return run;
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

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private async Task<long> ScanAsync() =>
        (await OkAsync("scan", _root)).Result().GetProperty("job").GetProperty("id").GetInt64();

    /// <summary>Scans the temp folder and adds a verdict, results and a correction by hand.</summary>
    private async Task<long> PrepareAsync()
    {
        var jobId = await ScanAsync();

        await using var sp = CoreServices();
        await sp.InitializeSmartFileOrganizerDatabaseAsync();
        var dirs = sp.GetRequiredService<IDirectoryRepository>();
        var files = sp.GetRequiredService<IFileRepository>();
        var ai = sp.GetRequiredService<IClassificationRepository>();

        var photos = await dirs.GetByPathAsync(jobId, P("photos", "Urlaub 2024")) ?? throw new InvalidOperationException();
        await sp.GetRequiredService<IDirectoryVerdictRepository>().UpsertAsync(new DirectoryVerdict
        {
            DirectoryNodeId = photos.Id,
            Verdict = DirectoryVerdictKind.Uniform,
            Source = VerdictSource.Model,
            Purpose = "Holiday photos.",
            Reason = "One trip.",
            Retention = RetentionValue.Keep,
            Profile = new DirectoryProfile { FileCount = 1, TotalBytes = 7 },
            ModelUsed = "fake",
            RawResponse = "{}",
            JudgedAt = DateTime.UtcNow
        });

        async Task Classify(string dir, string name, FileCategory category, double confidence, string summary,
            KeepHint? keep = null, ResultCertainty? certainty = ResultCertainty.Likely)
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
                IsAiResult = true,
                Origin = ResultOrigin.Model,
                KeepHint = keep,
                Certainty = certainty
            });
            await files.UpdateStatusAsync(file.Id, FileNodeStatus.AiAnalyzed);
        }

        await Classify(P("docs"), "letter.txt", FileCategory.Letter, 0.4, "Letter to the landlord.", KeepHint.WorthKeeping);
        await Classify(P("photos", "Urlaub 2024"), "Strand.png", FileCategory.Photo, 0.9, "A beach in the sun.");
        await Classify(P("docs"), ".env", FileCategory.TrashCandidate, 0.8, $"Holds {EnvSecret}.");
        return jobId;
    }

    private async Task<string> ExportAsync(long job, string name = "out")
    {
        var folder = Path.Combine(_dir, name);
        var result = (await OkAsync("export", job.ToString(), "--format", "jsonl", "--out", folder)).Result();
        Assert.Equal(ExchangeFormat.Version, result.GetProperty("format_version").GetString());
        return folder;
    }

    private static List<JsonElement> Lines(string path) =>
        File.ReadAllLines(path).Where(l => l.Length > 0).Select(l =>
        {
            using var doc = JsonDocument.Parse(l);
            return doc.RootElement.Clone();
        }).ToList();

    private static IReadOnlyList<string> SchemaKeys(string schema) =>
        JsonNode.Parse(ExchangeSchemas.Text(schema))!["properties"]!.AsObject().Select(p => p.Key).ToList();

    // ------------------------------------------------------------------ export

    [Fact]
    public async Task Export_EveryLineMatchesItsSchema_AndCarriesEveryKey()
    {
        var job = await PrepareAsync();
        var folder = await ExportAsync(job);

        using var head = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, ExchangeFormat.JobFile)));
        Assert.Empty(ExchangeSchemas.Validate(ExchangeSchemas.JobSchema, head.RootElement));

        var directories = Lines(Path.Combine(folder, ExchangeFormat.DirectoriesFile));
        var files = Lines(Path.Combine(folder, ExchangeFormat.FilesFile));
        Assert.Equal(head.RootElement.GetProperty("directories").GetInt32(), directories.Count);
        Assert.Equal(head.RootElement.GetProperty("files").GetInt32(), files.Count);
        Assert.Equal(4, directories.Count);
        Assert.Equal(4, files.Count);

        foreach (var (schema, lines) in new[] { (ExchangeSchemas.DirectorySchema, directories), (ExchangeSchemas.FileSchema, files) })
        {
            foreach (var line in lines)
            {
                var problems = ExchangeSchemas.Validate(schema, line);
                Assert.True(problems.Count == 0, $"{line.GetRawText()}: {string.Join("; ", problems)}");
                Assert.Equal(SchemaKeys(schema), line.EnumerateObject().Select(p => p.Name).ToList());
            }
        }

        var photos = directories.Single(d => d.GetProperty("relative_path").GetString() == "photos/Urlaub 2024");
        var verdict = photos.GetProperty("verdict");
        Assert.Equal("uniform", verdict.GetProperty("verdict").GetString());
        Assert.Equal("keep", verdict.GetProperty("retention").GetString());
        Assert.Equal(1, verdict.GetProperty("profile").GetProperty("file_count").GetInt32());
        Assert.EndsWith("Z", verdict.GetProperty("judged_at").GetString());
        var parent = directories.Single(d => d.GetProperty("relative_path").GetString() == "photos");
        Assert.Equal(parent.GetProperty("id").GetInt64(), photos.GetProperty("parent_id").GetInt64());

        var beach = files.Single(f => f.GetProperty("relative_path").GetString() == "photos/Urlaub 2024/Strand.png");
        Assert.Equal(photos.GetProperty("id").GetInt64(), beach.GetProperty("directory_id").GetInt64());
        Assert.Equal("image", beach.GetProperty("file_type").GetString());
        Assert.Equal("ai_analyzed", beach.GetProperty("status").GetString());
        var result = beach.GetProperty("result");
        Assert.Equal("photo", result.GetProperty("category").GetString());
        Assert.Equal("model", result.GetProperty("origin").GetString());
        Assert.Equal("medium", result.GetProperty("importance").GetString());
        Assert.Equal("likely", result.GetProperty("certainty").GetString());
        Assert.Equal("A beach in the sun.", result.GetProperty("summary").GetString());

        var notes = files.Single(f => f.GetProperty("relative_path").GetString() == "notes.txt");
        Assert.Equal(JsonValueKind.Null, notes.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Export_AccessData_KeepsItsClassificationButNotItsText()
    {
        var job = await PrepareAsync();
        var folder = await ExportAsync(job);

        foreach (var file in Directory.GetFiles(folder))
            Assert.DoesNotContain(EnvSecret, await File.ReadAllTextAsync(file));

        var env = Lines(Path.Combine(folder, ExchangeFormat.FilesFile)).Single(f => f.GetProperty("name").GetString() == ".env");
        Assert.True(env.GetProperty("protected").GetBoolean());
        Assert.Contains("credentials", env.GetProperty("sensitive").EnumerateArray().Select(m => m.GetString()));
        var result = env.GetProperty("result");
        // Never proposed as disposable: the marker raises the model's trash_candidate to review, by rule.
        Assert.Equal("review_needed", result.GetProperty("category").GetString());
        Assert.Equal("rule", result.GetProperty("category_source").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("raw_response").ValueKind);
    }

    [Fact]
    public async Task Export_UsageErrors()
    {
        var job = (await PrepareAsync()).ToString();

        foreach (var args in new[]
                 {
                     new[] { "export", job, "--format", "jsonl" },
                     ["export", job, "--format", "jsonl", "--out", _dir, "--csv", Path.Combine(_dir, "x.csv")],
                     ["export", job, "--format", "xml", "--out", _dir],
                     ["export", job, "--csv", Path.Combine(_dir, "x.csv"), "--out", _dir]
                 })
        {
            var run = await SfoAsync(args);
            Assert.True(run.ExitCode == ExitCodes.Usage, string.Join(' ', args) + ": " + run.Stdout + run.Stderr);
        }

        var missing = await SfoAsync("export", job, "--out", Path.Combine(_dir, "no", "such", "out"));
        Assert.Equal(ErrorCodes.PathNotFound, missing.Json().GetProperty("error").GetProperty("code").GetString());
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task ExportImportExport_GivesTheSameJudgements()
    {
        var first = await PrepareAsync();
        var exported = await ExportAsync(first, "first");

        // A second scan of the same folder: no results yet.
        var second = await ScanAsync();
        var import = (await OkAsync("import-results", second.ToString(), Path.Combine(exported, ExchangeFormat.FilesFile))).Result();
        Assert.Equal(4, import.GetProperty("lines").GetInt32());
        Assert.Equal(3, import.GetProperty("imported").GetInt32());
        Assert.Equal(1, import.GetProperty("without_result").GetInt32());
        Assert.Equal(0, import.GetProperty("unmatched").GetInt32());

        var again = await ExportAsync(second, "second");

        static Dictionary<string, JsonElement> ByPath(string folder) =>
            Lines(Path.Combine(folder, ExchangeFormat.FilesFile)).ToDictionary(f => f.GetProperty("relative_path").GetString()!);
        var before = ByPath(exported);
        var after = ByPath(again);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());

        string[] judgement = ["category", "importance", "confidence", "certainty", "summary", "keep_hint", "model"];
        foreach (var (path, line) in before)
        {
            var b = line.GetProperty("result");
            var a = after[path].GetProperty("result");
            Assert.Equal(b.ValueKind, a.ValueKind);
            Assert.Equal(line.GetProperty("sensitive").GetRawText(), after[path].GetProperty("sensitive").GetRawText());
            if (b.ValueKind == JsonValueKind.Null) continue;
            foreach (var key in judgement)
                Assert.True(b.GetProperty(key).GetRawText() == a.GetProperty(key).GetRawText(), $"{path}.{key}: {b.GetProperty(key)} vs {a.GetProperty(key)}");
            Assert.Equal("external", a.GetProperty("origin").GetString());
        }
    }

    [Fact]
    public async Task Import_OneInvalidLine_StoresNothing()
    {
        var job = await ScanAsync();
        var file = Path.Combine(_dir, "results.jsonl");
        await File.WriteAllLinesAsync(file,
        [
            """{"relative_path":"notes.txt","result":{"category":"document","importance":"low","confidence":0.7}}""",
            """{"relative_path":"docs/letter.txt","result":{"category":"Holiday"}}"""
        ]);

        var run = await SfoAsync("import-results", job.ToString(), file);

        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        var error = run.Json().GetProperty("error");
        Assert.Equal(ErrorCodes.ImportInvalid, error.GetProperty("code").GetString());
        Assert.Contains("line 2", error.GetProperty("message").GetString());
        var notes = (await OkAsync("show", job.ToString(), "notes.txt")).Result().GetProperty("file");
        Assert.Equal(JsonValueKind.Null, notes.GetProperty("category").ValueKind);
    }

    [Fact]
    public async Task Import_MinimalLines_MatchByPath_AndCountTheRest()
    {
        var job = await ScanAsync();
        var file = Path.Combine(_dir, "results.jsonl");
        await File.WriteAllLinesAsync(file,
        [
            """{"relative_path":"notes.txt","result":{"category":"document","importance":"low","confidence":0.7,"model":"other-tool"}}""",
            """{"relative_path":"DOCS/Letter.txt","result":{"category":"letter"}}""",
            """{"relative_path":"gone/old.txt","result":{"category":"document"}}""",
            ""
        ]);

        var result = (await OkAsync("import-results", job.ToString(), file)).Result();

        Assert.Equal(3, result.GetProperty("lines").GetInt32());
        Assert.Equal(2, result.GetProperty("imported").GetInt32());
        Assert.Equal(1, result.GetProperty("unmatched").GetInt32());
        Assert.Equal("gone/old.txt", result.GetProperty("unmatched_paths")[0].GetString());

        await using var sp = CoreServices();
        var files = sp.GetRequiredService<IFileRepository>();
        var notes = (await files.GetByDirectoryAsync(job, _root)).Single(f => f.Name == "notes.txt");
        var stored = await sp.GetRequiredService<IClassificationRepository>().GetByFileNodeIdAsync(notes.Id);
        Assert.NotNull(stored);
        Assert.Equal(FileCategory.Document, stored.Category);
        Assert.Equal(ResultOrigin.External, stored.Origin);
        Assert.Equal("other-tool", stored.ModelUsed);
        Assert.False(stored.IsAiResult);
    }

    // ------------------------------------------------------------------ views

    [Fact]
    public async Task Views_HaveTheirPinnedColumns_AndShowCodes()
    {
        var job = await PrepareAsync();
        await using (var sp = CoreServices())
        {
            var files = sp.GetRequiredService<IFileRepository>();
            var letter = (await files.GetByDirectoryAsync(job, P("docs"))).Single(f => f.Name == "letter.txt");
            await sp.GetRequiredService<IUserOverrideRepository>().UpsertAsync(new UserOverride
            {
                FileNodeId = letter.Id, OverriddenCategory = FileCategory.PersonalDocument, OverriddenAt = DateTime.UtcNow
            });
        }

        await using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        await conn.OpenAsync();

        List<string> Columns(string view)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT name FROM pragma_table_info('{view}')";
            using var reader = cmd.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            return names;
        }
        Assert.Equal(ExchangeViews.FileColumns, Columns(ExchangeViews.Files));
        Assert.Equal(ExchangeViews.DirectoryColumns, Columns(ExchangeViews.Directories));

        Dictionary<string, object?> Row(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read(), sql);
            return Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i));
        }

        var beach = Row($"SELECT * FROM v_files WHERE job_id = {job} AND relative_path = 'photos/Urlaub 2024/Strand.png'");
        Assert.Equal("image", beach["file_type"]);
        Assert.Equal("ai_analyzed", beach["status"]);
        Assert.Equal("photo", beach["category"]);
        Assert.Equal("model", beach["category_source"]);
        Assert.Equal("medium", beach["importance"]);
        Assert.NotNull(beach["directory_id"]);

        var letterRow = Row($"SELECT * FROM v_files WHERE job_id = {job} AND relative_path = 'docs/letter.txt'");
        Assert.Equal("personal_document", letterRow["category"]);
        Assert.Equal("human", letterRow["category_source"]);
        Assert.Equal("model", letterRow["result_origin"]);

        var photos = Row($"SELECT * FROM v_directories WHERE job_id = {job} AND relative_path = 'photos/Urlaub 2024'");
        Assert.Equal("uniform", photos["verdict"]);
        Assert.Equal("keep", photos["retention"]);
        Assert.Equal("scanned", photos["status"]);
        Assert.Equal(".", Row($"SELECT relative_path FROM v_directories WHERE job_id = {job} AND depth = 0")["relative_path"]);
    }
}
