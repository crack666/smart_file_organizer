using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Persistence;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>sfo findings targets</c> (https://github.com/crack666/smart_file_organizer/issues/83): the match of
/// <c>sfo library match</c> for every finding that should move, stored per finding, shown by the findings and taken over
/// by the plan. A generated library and source with invented names in a temp folder; the source files carry descriptions
/// as the analysis would store them, library folders are described by a scripted model and a scripted judge compares the
/// descriptions. No real model.
/// </summary>
public partial class FindingTargetsCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _library;
    private readonly string _source;

    public FindingTargetsCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_ftargets_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _library = Path.Combine(_dir, "ablage");
        _source = Path.Combine(_dir, "fund");

        Photos(_library, "Fotos/Skifahrt 2015", new DateTime(2015, 2, 7, 10, 0, 0), 5, "lib");
        Photos(_library, "Fotos/Grillen 2016", new DateTime(2016, 7, 16, 18, 0, 0), 4, "lib");
        for (var m = 1; m <= 3; m++)
            Write(_library, $"Dokumente/Rechnungen Stadtwerke/rechnung_2023_0{m}.pdf", $"Stadtwerke {m}", new DateTime(2023, m, 10));

        // The source: loose ski photos in a folder without a meaningful name, nowhere in the library yet.
        Photos(_source, "Neuer Ordner", new DateTime(2015, 2, 8, 9, 0, 0), 4, "src");
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
            Write(root, $"{folder}/IMG_{time:yyyyMMdd_HHmmss}.jpg", $"{salt} {folder} {i}", time);
        }
    }

    private static void Write(string root, string relative, string content, DateTime time)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTime(path, time);
    }

    // ------------------------------------------------------------------ the scripted model

    private static FileFact F(FileFactKey key, string value) => new(key, value, FactSource.Model);

    private static readonly FileFact[] Ski =
    [
        F(FileFactKey.Occasion, "holiday"), F(FileFactKey.Scene, "snowy slope"), F(FileFactKey.Activity, "skiing"),
        F(FileFactKey.Setting, "outdoor"), F(FileFactKey.Season, "winter")
    ];

    private static IReadOnlyList<FileFact> LibraryFacts(string relativePath) => relativePath switch
    {
        _ when relativePath.Contains("Skifahrt", StringComparison.Ordinal) => Ski,
        _ when relativePath.Contains("Grillen", StringComparison.Ordinal) =>
            [F(FileFactKey.Occasion, "celebration"), F(FileFactKey.Scene, "garden grill"), F(FileFactKey.Season, "summer")],
        _ when relativePath.Contains("Stadtwerke", StringComparison.Ordinal) =>
            [F(FileFactKey.DocumentKind, "invoice"), F(FileFactKey.Issuer, "Stadtwerke Musterstadt")],
        _ => []
    };

    /// <summary>Stores description facts for the source photos as <c>sfo analyze</c> would.</summary>
    private async Task DescribeSourceAsync(long job)
    {
        var db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        var facts = new FileFactRepository(db);
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var ids = await conn.QueryAsync<long>("SELECT id FROM file_nodes WHERE job_id = @job", new { job });
        foreach (var id in ids)
            await facts.ReplaceAsync(id, Ski);
    }

    private sealed class DescribingOllama : IOllamaService
    {
        public ConcurrentBag<string> Described { get; } = [];

        public Task<FileClassification> ClassifyAsync(OllamaClassificationInput input, CancellationToken ct = default)
        {
            Described.Add(input.File.RelativePath.Replace('\\', '/'));
            return Task.FromResult(new FileClassification
            {
                FileNodeId = input.File.Id,
                Category = FileCategory.Photo,
                ModelUsed = "scripted",
                AnalyzedAt = DateTime.UtcNow,
                IsAiResult = true,
                DescribedFacts = LibraryFacts(input.File.RelativePath.Replace('\\', '/')).ToList()
            });
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OllamaLocalModel>>([]);
        public Task WarmModelAsync(string? model = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DirectoryPreAssessmentOutcome> PreAssessDirectoryAsync(DirectoryPreAssessmentInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DirectoryClassificationResult> SummarizeDirectoryAsync(DirectorySummaryInput input, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoReadPreparer : IClassificationInputPreparer
    {
        public Task<OllamaClassificationInput> PrepareAsync(FileNode file, CancellationToken ct = default) =>
            Task.FromResult(new OllamaClassificationInput { File = file });
    }

    /// <summary>The same occasion fits, another one contradicts; names of new folders are scripted.</summary>
    private sealed partial class JudgingAdvisor : ILibraryAdvisor
    {
        public ConcurrentQueue<LibraryAdviceRequest> Requests { get; } = new();

        public Task<LibraryAdviceReply> AskAsync(LibraryAdviceRequest request, CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            if (request.Task != LibraryAdviceTask.Fit)
                return Task.FromResult(new LibraryAdviceReply("""{ "name": "Ausflug 2015", "reason": "scripted" }""", "scripted", null, null));
            var lines = request.Facts.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var unit = Occasion(lines[0]);
            var verdicts = lines.Where(l => CandidateRegex().IsMatch(l)).Select(l =>
            {
                var fit = Occasion(l) == unit ? "fits" : "contradicts";
                return new { id = l[..l.IndexOf(' ')], fit, reason = fit == "fits" ? $"both show {unit}" : "another occasion" };
            });
            var content = JsonSerializer.Serialize(new { verdicts });
            return Task.FromResult(new LibraryAdviceReply(content, "scripted-judge", content, null));
        }

        private static string Occasion(string line) => OccasionRegex().Match(line).Groups[1].Value;

        [GeneratedRegex(@"^c\d+ \|")]
        private static partial Regex CandidateRegex();

        [GeneratedRegex(@"occasion: ([a-z]+) ")]
        private static partial Regex OccasionRegex();
    }

    private (Action<IServiceCollection> Configure, DescribingOllama Model, JudgingAdvisor Judge) WithModel()
    {
        var model = new DescribingOllama();
        var judge = new JudgingAdvisor();
        return (services =>
        {
            services.AddSingleton<IOllamaService>(model);
            services.AddSingleton<IClassificationInputPreparer>(new NoReadPreparer());
            services.AddSingleton<ILibraryAdvisor>(judge);
        }, model, judge);
    }

    // ------------------------------------------------------------------ helpers

    private sealed class FixedPlaces(params string[] places) : IProtectedPlaces
    {
        public IReadOnlyList<string> Get() => places;
    }

    private async Task<JsonElement> OkAsync(Action<IServiceCollection>? configure, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--json", "--db", _dbPath],
            new CliHost { Out = stdout, Error = stderr, ConfigureServices = configure });
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.True(exit == ExitCodes.Ok, doc.RootElement + stderr.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    private Task<JsonElement> OkAsync(params string[] args) => OkAsync(null, args);

    private async Task<(int Exit, JsonElement Doc)> RunAsync(Action<IServiceCollection>? configure, params string[] args)
    {
        var stdout = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--json", "--db", _dbPath],
            new CliHost { Out = stdout, Error = new StringWriter(), ConfigureServices = configure });
        using var doc = JsonDocument.Parse(stdout.ToString());
        return (exit, doc.RootElement.Clone());
    }

    private async Task<(long Library, long Source)> PrepareAsync()
    {
        var library = (await OkAsync("scan", _library, "--role", "library", "--structure-only")).GetProperty("job").GetProperty("id").GetInt64();
        var source = (await OkAsync("scan", _source, "--role", "source")).GetProperty("job").GetProperty("id").GetInt64();
        await OkAsync("compare", source.ToString(), "--against", library.ToString());
        await OkAsync("bundles", "build", source.ToString());
        await DescribeSourceAsync(source);
        return (library, source);
    }

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    /// <summary>The not_backed_up finding about the loose photos.</summary>
    private static JsonElement PhotoBrief(JsonElement summary) =>
        summary.GetProperty("top").EnumerateArray().Single(b => Str(b, "kind") == "not_backed_up" && Str(b, "relative_path") == "Neuer Ordner");

    // ------------------------------------------------------------------ done when

    /// <summary>
    /// After the target match the summary names the ski trip folder for the loose ski photos, with the reason, and the plan
    /// makes a move into that folder out of it. Before, the finding stood without a target and the plan could only leave it.
    /// </summary>
    [Fact]
    public async Task AfterTheMatch_TheSummaryNamesTheTarget_AndThePlanMovesThere()
    {
        var (library, source) = await PrepareAsync();
        var job = source.ToString();

        var before = await OkAsync("findings", job, "--summary");
        var brief = PhotoBrief(before);
        Assert.Equal(JsonValueKind.Null, brief.GetProperty("target").ValueKind);
        Assert.Equal("not_matched", Str(brief, "target_status"));
        Assert.Equal(1, before.GetProperty("storage").GetProperty("unplaced_by_reason").GetProperty("not_matched").GetInt32());
        Assert.Contains($"sfo findings targets {job}", before.GetProperty("missing_steps").EnumerateArray().Select(s => s.GetString()));

        var (configure, _, _) = WithModel();
        var run = await OkAsync(configure, "findings", "targets", job);
        var item = run.GetProperty("items").EnumerateArray().Single(i => Str(i, "key") == Str(brief, "key"));
        Assert.Equal("existing_folder", Str(item, "status"));
        Assert.Equal("Fotos/Skifahrt 2015", Str(item, "relative_path"));
        Assert.Equal("fits", Str(item, "content_check"));
        Assert.Contains(run.GetProperty("model_calls").EnumerateArray(), c => Str(c, "kind") == "content_fit");

        var after = await OkAsync("findings", job, "--summary");
        var placed = PhotoBrief(after);
        var target = placed.GetProperty("target");
        Assert.Equal("existing_folder", Str(placed, "target_status"));
        Assert.Equal("Fotos/Skifahrt 2015", Str(target, "relative_path"));
        Assert.Equal(library, target.GetProperty("library_job_id").GetInt64());
        Assert.Equal("fits", Str(target, "content_check"));
        Assert.Contains("snowy slope", Str(target, "reason"));
        Assert.Equal("move", Str(placed, "action"));
        Assert.Equal(0, after.GetProperty("storage").GetProperty("unplaced_findings").GetInt32());
        Assert.DoesNotContain($"sfo findings targets {job}", after.GetProperty("missing_steps").EnumerateArray().Select(s => s.GetString()));

        // findings show names it as well.
        var shown = await OkAsync("findings", "show", job, Str(placed, "key"));
        Assert.Equal("Fotos/Skifahrt 2015", Str(shown.GetProperty("finding").GetProperty("target"), "relative_path"));

        // The plan takes it over: one move per photo into the ski trip folder of the library.
        await OkAsync("findings", "accept", job, Str(placed, "key"));
        // The temp folder lies in the user profile, a protected place on this machine: a fake profile inside it instead.
        Action<IServiceCollection> places = s => s.AddSingleton<IProtectedPlaces>(new FixedPlaces(Path.Combine(_dir, "profile")));
        var plan = await OkAsync(places, "plan", "create", job);
        var shownPlan = await OkAsync(places, "plan", "show", plan.GetProperty("plan_id").GetInt64().ToString(), "--finding", Str(placed, "key"));
        var actions = shownPlan.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(4, actions.Count);
        var skiFolder = Path.Combine(_library, "Fotos", "Skifahrt 2015");
        Assert.All(actions, a =>
        {
            Assert.Equal("move", Str(a, "kind"));
            Assert.Equal(skiFolder, Path.GetDirectoryName(Str(a, "target")));
            Assert.Equal(library, a.GetProperty("library_job_id").GetInt64());
            Assert.Empty(a.GetProperty("problems").EnumerateArray());
        });
    }

    /// <summary>A second run on unchanged facts asks the model nothing: every finding keeps its stored match.</summary>
    [Fact]
    public async Task ASecondRunOnUnchangedFacts_CallsNoModel()
    {
        var (_, source) = await PrepareAsync();
        var (configure, model, judge) = WithModel();

        var first = await OkAsync(configure, "findings", "targets", source.ToString());
        Assert.True(first.GetProperty("matched").GetInt32() > 0);
        var asked = judge.Requests.Count;
        var described = model.Described.Count;
        Assert.True(asked > 0);

        var second = await OkAsync(configure, "findings", "targets", source.ToString());
        Assert.Equal(asked, judge.Requests.Count);
        Assert.Equal(described, model.Described.Count);
        Assert.Equal(0, second.GetProperty("matched").GetInt32());
        Assert.Equal(second.GetProperty("considered").GetInt32(), second.GetProperty("reused").GetInt32());
        Assert.Empty(second.GetProperty("model_calls").EnumerateArray());
        Assert.All(second.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("reused").GetBoolean()));

        // Changed facts (the photos are described anew) are matched again.
        var db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        await using (var conn = db.CreateConnection())
        {
            await conn.OpenAsync();
            var id = await conn.ExecuteScalarAsync<long>("SELECT id FROM file_nodes WHERE job_id = @source ORDER BY id LIMIT 1", new { source });
            await new FileFactRepository(db).ReplaceAsync(id, [.. Ski, F(FileFactKey.People, "true")]);
        }
        var third = await OkAsync(configure, "findings", "targets", source.ToString());
        Assert.Equal(1, third.GetProperty("matched").GetInt32());
    }

    /// <summary>
    /// A permanent storage with two roots: a bundle whose files lie in the second library counts as backed up, and one
    /// whose files lie in the first one as well. Until Issue 83 the bundles read only the newest single comparison.
    /// </summary>
    [Fact]
    public async Task ABundleInTheSecondOfTwoLibraries_CountsAsBackedUp()
    {
        var photos = Path.Combine(_dir, "photos");
        var videos = Path.Combine(_dir, "videos");
        var stick = Path.Combine(_dir, "stick");
        for (var i = 0; i < 3; i++)
        {
            Write(photos, $"Hochzeit 2019/IMG_{i}.jpg", $"wedding {i}", new DateTime(2019, 6, 1));
            Write(videos, $"Konzert 2020/VID_{i}.mp4", $"concert {i}", new DateTime(2020, 9, 1));
            Write(stick, $"Hochzeit 2019/IMG_{i}.jpg", $"wedding {i}", new DateTime(2019, 6, 1));
            Write(stick, $"Konzert 2020/VID_{i}.mp4", $"concert {i}", new DateTime(2020, 9, 1));
        }

        var first = (await OkAsync("scan", photos, "--role", "library")).GetProperty("job").GetProperty("id").GetInt64();
        var second = (await OkAsync("scan", videos, "--role", "library")).GetProperty("job").GetProperty("id").GetInt64();
        var source = (await OkAsync("scan", stick, "--role", "source")).GetProperty("job").GetProperty("id").GetInt64().ToString();
        await OkAsync("compare", source, "--against", first.ToString(), "--against", second.ToString());
        var build = await OkAsync("bundles", "build", source);

        var bundles = (await OkAsync("bundles", "list", source)).GetProperty("items").EnumerateArray()
            .Where(b => Str(b, "kind") == "folder").ToList();
        Assert.Equal(2, bundles.Count);
        Assert.All(bundles, b =>
        {
            var comparison = b.GetProperty("comparison");
            Assert.Equal(3, comparison.GetProperty("backed_up").GetInt64());
            Assert.False(comparison.TryGetProperty("new", out _), b.ToString());
        });
        var ids = build.GetProperty("comparison").GetProperty("library_job_ids").EnumerateArray().Select(x => x.GetInt64()).ToList();
        Assert.Equal([first, second], ids);
    }

    /// <summary>
    /// Without a model no existing folder is the target (Issue 85; until then it was the ski trip folder as uncertain): the
    /// new folder needs a name the rules cannot complete, so the finding says no_model. A later run with a model checks it.
    /// </summary>
    [Fact]
    public async Task WithoutAModel_NoExistingFolderIsTheTarget()
    {
        var (_, source) = await PrepareAsync();

        var run = await OkAsync("findings", "targets", source.ToString(), "--no-model");
        var item = run.GetProperty("items").EnumerateArray().Single(i => Str(i, "kind") == "not_backed_up");
        Assert.Equal("no_model", Str(item, "status"));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("relative_path").ValueKind);
        Assert.Equal("not_checked", Str(item, "content_check"));

        var (configure, _, judge) = WithModel();
        var withModel = await OkAsync(configure, "findings", "targets", source.ToString());
        Assert.Equal(1, withModel.GetProperty("matched").GetInt32());
        Assert.Contains(judge.Requests, r => r.Task == LibraryAdviceTask.Fit);
    }

    /// <summary>
    /// Done when of Issue 85: a library that cannot be read (the share is not connected) gives a named failure, not targets.
    /// Until then the run reported success with reasons such as "the folder's files could not be described". sfo doctor
    /// names the root as well.
    /// </summary>
    [Fact]
    public async Task ALibraryThatCannotBeRead_IsANamedFailure_NotTargets()
    {
        var (library, source) = await PrepareAsync();
        // The share goes away after the scan: the folder is renamed, as if it were not connected.
        Directory.Move(_library, _library + "-weg");
        var (configure, model, judge) = WithModel();

        var (exit, doc) = await RunAsync(configure, "findings", "targets", source.ToString());

        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal("library_unreachable", doc.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains($"library {library}", doc.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(judge.Requests);
        Assert.Empty(model.Described);
        // Nothing was stored: the findings still wait for a match.
        var summary = await OkAsync("findings", source.ToString(), "--summary");
        Assert.Equal("not_matched", Str(PhotoBrief(summary), "target_status"));

        var (matchExit, match) = await RunAsync(configure, "library", "match", source.ToString(), "Neuer Ordner");
        Assert.Equal(ExitCodes.Failure, matchExit);
        Assert.Equal("library_unreachable", match.GetProperty("error").GetProperty("code").GetString());

        var (_, doctor) = await RunAsync(configure, "doctor");
        var checks = doctor.GetProperty("result").GetProperty("libraries").EnumerateArray().ToList();
        var check = Assert.Single(checks);
        Assert.Equal(library, check.GetProperty("job_id").GetInt64());
        Assert.False(check.GetProperty("readable").GetBoolean());
        Assert.False(doctor.GetProperty("result").GetProperty("ok").GetBoolean());

        // Connected again: the run goes through.
        Directory.Move(_library + "-weg", _library);
        var run = await OkAsync(configure, "findings", "targets", source.ToString());
        Assert.True(run.GetProperty("matched").GetInt32() > 0);
    }

    /// <summary>Loose photos and a library of papers only: no folder, and no new folder either, on purpose (Issue 85).</summary>
    [Fact]
    public async Task PhotosAndALibraryOfPapersOnly_GiveNoTarget()
    {
        var papers = Path.Combine(_dir, "papiere");
        for (var m = 1; m <= 4; m++)
            Write(papers, $"Vertraege/vertrag_{m}.pdf", $"Vertrag {m}", new DateTime(2020, m, 1));
        var library = (await OkAsync("scan", papers, "--role", "library", "--structure-only")).GetProperty("job").GetProperty("id").GetInt64();
        var source = (await OkAsync("scan", _source, "--role", "source")).GetProperty("job").GetProperty("id").GetInt64();
        await OkAsync("compare", source.ToString(), "--against", library.ToString());
        await OkAsync("bundles", "build", source.ToString());
        await DescribeSourceAsync(source);
        var (configure, _, _) = WithModel();

        var run = await OkAsync(configure, "findings", "targets", source.ToString());

        var item = run.GetProperty("items").EnumerateArray().Single(i => Str(i, "kind") == "not_backed_up");
        Assert.Equal("no_target", Str(item, "status"));
        Assert.Contains("holds no own photos and videos", Str(item, "reason"));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("path").ValueKind);
    }
}
