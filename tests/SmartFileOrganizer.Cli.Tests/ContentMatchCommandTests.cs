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
/// <c>sfo library match</c> by content (https://github.com/crack666/smart_file_organizer/issues/56): time and kind of
/// files choose the candidates, what the files show decides. A generated library and source with invented names; the
/// descriptions of the source files are stored as the analysis would store them, the library folders are described by
/// a scripted model from a sample of their files, and a scripted judge compares the descriptions. No real model.
/// </summary>
public partial class ContentMatchCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _library;
    private readonly string _source;

    public ContentMatchCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_content_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _library = Path.Combine(_dir, "ablage");
        _source = Path.Combine(_dir, "fund");

        // The library: occasions with their year, and invoices by provider.
        Photos(_library, "Fotos/Skifahrt 2015", new DateTime(2015, 2, 7, 10, 0, 0), 5);
        Photos(_library, "Fotos/Grillen 2016", new DateTime(2016, 7, 16, 18, 0, 0), 4);
        Photos(_library, "Fotos/Ostern 2018", new DateTime(2018, 4, 1, 11, 0, 0), 4);
        foreach (var provider in new[] { "Stadtwerke", "Telefon" })
        {
            for (var m = 1; m <= 3; m++)
                Write(_library, $"Dokumente/Rechnungen {provider}/rechnung_2023_0{m}.pdf", $"{provider} {m}", new DateTime(2023, m, 10));
        }

        // The source: loose ski photos and, the same evening, photos of a birthday (no folder for it yet); an invoice.
        Photos(_source, "Neuer Ordner", new DateTime(2015, 2, 8, 9, 0, 0), 4);
        Photos(_source, "Import", new DateTime(2015, 2, 8, 20, 0, 0), 4);
        Write(_source, "Rechnung_2023_11.pdf", "invoice", new DateTime(2023, 11, 20));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Photos(string root, string folder, DateTime start, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var time = start.AddMinutes(30 * i);
            Write(root, $"{folder}/IMG_{time:yyyyMMdd_HHmmss}.jpg", $"{folder} {i}", time);
        }
    }

    private static void Write(string root, string relative, string content, DateTime time)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTime(path, time);
    }

    // ------------------------------------------------------------------ descriptions

    private static FileFact F(FileFactKey key, string value) => new(key, value, FactSource.Model);

    private static readonly FileFact[] Ski =
    [
        F(FileFactKey.Occasion, "holiday"), F(FileFactKey.Scene, "snowy slope"), F(FileFactKey.Activity, "skiing"),
        F(FileFactKey.Setting, "outdoor"), F(FileFactKey.Season, "winter"), F(FileFactKey.People, "true")
    ];

    private static readonly FileFact[] Birthday =
    [
        F(FileFactKey.Occasion, "celebration"), F(FileFactKey.Scene, "living room with cake"), F(FileFactKey.Activity, "blowing out candles"),
        F(FileFactKey.Setting, "indoor"), F(FileFactKey.People, "true")
    ];

    private static readonly FileFact[] Barbecue =
    [
        F(FileFactKey.Occasion, "celebration"), F(FileFactKey.Scene, "garden grill"), F(FileFactKey.Activity, "grilling"),
        F(FileFactKey.Setting, "outdoor"), F(FileFactKey.Season, "summer")
    ];

    private static FileFact[] Invoice(string issuer) =>
        [F(FileFactKey.DocumentKind, "invoice"), F(FileFactKey.Issuer, issuer), F(FileFactKey.Year, "2023")];

    /// <summary>What the scripted model sees in a library file, by the folder it lies in.</summary>
    private static IReadOnlyList<FileFact> LibraryFacts(string relativePath) => relativePath switch
    {
        _ when relativePath.Contains("Skifahrt", StringComparison.Ordinal) => Ski,
        _ when relativePath.Contains("Grillen", StringComparison.Ordinal) => Barbecue,
        _ when relativePath.Contains("Ostern", StringComparison.Ordinal) =>
            [F(FileFactKey.Occasion, "celebration"), F(FileFactKey.Scene, "egg hunt in a garden"), F(FileFactKey.Season, "spring")],
        _ when relativePath.Contains("Stadtwerke", StringComparison.Ordinal) => Invoice("Stadtwerke Musterstadt"),
        _ when relativePath.Contains("Telefon", StringComparison.Ordinal) => Invoice("Fonnet GmbH"),
        _ => []
    };

    /// <summary>Stores description facts for the source files as <c>sfo analyze</c> would (source model).</summary>
    private async Task DescribeSourceAsync(long job, bool birthday = true)
    {
        var db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        var facts = new FileFactRepository(db);
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        var files = await conn.QueryAsync<(long Id, string RelativePath)>(
            "SELECT id, relative_path FROM file_nodes WHERE job_id = @job", new { job });
        foreach (var (id, relative) in files)
        {
            var path = relative.Replace('\\', '/');
            IReadOnlyList<FileFact> described = path.StartsWith("Neuer Ordner/", StringComparison.Ordinal) ? Ski
                : path.StartsWith("Import/", StringComparison.Ordinal) && birthday ? Birthday
                : path.EndsWith(".pdf", StringComparison.Ordinal) ? Invoice("Stadtwerke Musterstadt")
                : [];
            if (described.Count > 0)
                await facts.ReplaceAsync(id, described);
        }
    }

    /// <summary>Describes library files from their folder, as the real model would from the picture.</summary>
    private sealed class DescribingOllama : IOllamaService
    {
        public ConcurrentBag<string> Described { get; } = [];

        public Task<FileClassification> ClassifyAsync(OllamaClassificationInput input, CancellationToken ct = default)
        {
            Described.Add(input.File.RelativePath.Replace('\\', '/'));
            return Task.FromResult(new FileClassification
            {
                FileNodeId = input.File.Id,
                Category = input.File.FileType == FileType.Image ? FileCategory.Photo : FileCategory.Invoice,
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

    /// <summary>Hands the file to the model without reading it (the library files are text with photo names).</summary>
    private sealed class NoReadPreparer : IClassificationInputPreparer
    {
        public Task<OllamaClassificationInput> PrepareAsync(FileNode file, CancellationToken ct = default) =>
            Task.FromResult(new OllamaClassificationInput { File = file });
    }

    /// <summary>
    /// Answers the content check like a careful model: the same occasion (or the same issuer) fits, another one
    /// contradicts. Reads only the facts text it is given, so it also shows what the model sees.
    /// </summary>
    private sealed partial class JudgingAdvisor : ILibraryAdvisor
    {
        public ConcurrentQueue<LibraryAdviceRequest> Requests { get; } = new();

        public Task<LibraryAdviceReply> AskAsync(LibraryAdviceRequest request, CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            if (request.Task != LibraryAdviceTask.Fit)
                return Task.FromResult(new LibraryAdviceReply("""{ "name": "Geburtstag 2015", "reason": "scripted" }""", "scripted", null, null));
            var lines = request.Facts.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var unit = Key(lines[0]);
            var verdicts = lines.Where(l => CandidateRegex().IsMatch(l)).Select(l =>
            {
                var id = l[..l.IndexOf(' ')];
                var fit = Key(l) == unit ? "fits" : "contradicts";
                return new { id, fit, reason = fit == "fits" ? $"both show {unit}" : $"the files show {unit}, the folder {Key(l)}" };
            });
            var content = JsonSerializer.Serialize(new { verdicts });
            return Task.FromResult(new LibraryAdviceReply(content, "scripted-judge", content, null));
        }

        private static string Key(string line) =>
            IssuerRegex().Match(line) is { Success: true } i ? i.Groups[1].Value : OccasionRegex().Match(line).Groups[1].Value;

        [GeneratedRegex(@"^c\d+ \|")]
        private static partial Regex CandidateRegex();

        [GeneratedRegex(@"occasion: ([a-z]+) ")]
        private static partial Regex OccasionRegex();

        [GeneratedRegex(@"issuer: ([^0-9;]+?) \d")]
        private static partial Regex IssuerRegex();
    }

    // ------------------------------------------------------------------ helpers

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

    private async Task<(long Library, long Source)> PrepareAsync(bool birthday = true)
    {
        var library = (await OkAsync("scan", _library, "--role", "library", "--structure-only")).GetProperty("job").GetProperty("id").GetInt64();
        var source = (await OkAsync("scan", _source, "--role", "source")).GetProperty("job").GetProperty("id").GetInt64();
        await OkAsync("compare", source.ToString(), "--against", library.ToString());
        await OkAsync("bundles", "build", source.ToString());
        await DescribeSourceAsync(source, birthday);
        return (library, source);
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

    private async Task<long> EventFromAsync(long source, string start)
    {
        var events = (await OkAsync("bundles", "list", source.ToString(), "--all", "--kind", "event", "--limit", "100"))
            .GetProperty("items").EnumerateArray();
        return events.Single(e => e.GetProperty("period").GetProperty("from").GetString()!.StartsWith(start, StringComparison.Ordinal))
            .GetProperty("id").GetInt64();
    }

    private static List<JsonElement> Candidates(JsonElement result) => result.GetProperty("candidates").EnumerateArray().ToList();

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    // ------------------------------------------------------------------ done when

    /// <summary>
    /// Two bundles of the same day, a ski trip in the morning and a birthday in the evening, get different proposals:
    /// the ski photos the ski trip folder (content and period agree: certain), the birthday photos not, although the
    /// period fits just as well; it gets a new folder, because no folder of the library holds that occasion.
    /// </summary>
    [Fact]
    public async Task TwoBundlesOfTheSameWeek_GetDifferentPlaces_TheBirthdayNotTheSkiTrip()
    {
        var (_, source) = await PrepareAsync();
        var ski = await EventFromAsync(source, "2015-02-08T09");
        var birthday = await EventFromAsync(source, "2015-02-08T20");
        var (configure, model, judge) = WithModel();

        var skiMatch = await OkAsync(configure, "library", "match", source.ToString(), "--bundle", ski.ToString());
        var birthdayMatch = await OkAsync(configure, "library", "match", source.ToString(), "--bundle", birthday.ToString());

        // The ski photos: the ski trip folder, certain, and the reason names what was seen.
        Assert.Equal("checked", Str(skiMatch.GetProperty("content_check"), "status"));
        var first = Candidates(skiMatch)[0];
        Assert.Equal("Fotos/Skifahrt 2015", Str(first, "target_relative"));
        Assert.Equal("certain", Str(first, "certainty"));
        Assert.Equal("fits", Str(first.GetProperty("content"), "fit"));
        Assert.Contains("snowy slope", Str(first, "reason"));
        Assert.Contains("skiing", Str(first, "reason"));
        Assert.Contains("holiday", Str(skiMatch.GetProperty("unit").GetProperty("description"), "text"));

        // The birthday: not the ski trip folder, though its period fits as well as for the ski photos.
        Assert.DoesNotContain(Candidates(birthdayMatch), c => Str(c, "target_relative") == "Fotos/Skifahrt 2015");
        var rejected = Assert.Single(birthdayMatch.GetProperty("rejected").EnumerateArray());
        Assert.Equal("Fotos/Skifahrt 2015", Str(rejected, "target_relative"));
        // An occasion without a folder: a new one next to the other occasions, and the reason says why not the ski trip.
        var newFolder = birthdayMatch.GetProperty("new_folder");
        Assert.Equal("Fotos", Str(newFolder, "parent_relative"));
        Assert.Contains("Fotos/Skifahrt 2015 fits by time or name, not by content", Str(newFolder, "reason"));
        Assert.NotEqual(Str(first, "destination_relative"), Str(newFolder, "destination_relative"));

        // The model saw descriptions only: no file names, no pictures; and the folder was sampled once (stored).
        var fit = judge.Requests.First(r => r.Task == LibraryAdviceTask.Fit);
        Assert.Contains("Fotos/Skifahrt 2015", fit.Facts);
        Assert.DoesNotContain("IMG_", fit.Facts);
        Assert.InRange(model.Described.Count(p => p.StartsWith("Fotos/Skifahrt 2015/", StringComparison.Ordinal)), 1, 4);
        var before = model.Described.Count;
        await OkAsync(configure, "library", "match", source.ToString(), "--bundle", ski.ToString());
        Assert.Equal(before, model.Described.Count);
    }

    /// <summary>Without fitting content no proposal is better than uncertain: without a model, and without a description.</summary>
    [Fact]
    public async Task WithoutFittingContent_NoProposalIsBetterThanUncertain()
    {
        var (_, source) = await PrepareAsync(birthday: false);
        var ski = await EventFromAsync(source, "2015-02-08T09");
        var birthday = await EventFromAsync(source, "2015-02-08T20");

        // Without a model: the old signals still find the folder, but only as uncertain, and say why.
        var noModel = await OkAsync("library", "match", source.ToString(), "--bundle", ski.ToString(), "--no-model");
        Assert.Equal("no_model", Str(noModel.GetProperty("content_check"), "status"));
        var first = Candidates(noModel)[0];
        Assert.Equal("Fotos/Skifahrt 2015", Str(first, "target_relative"));
        Assert.Equal("uncertain", Str(first, "certainty"));
        Assert.Contains("the content was not checked (no model)", Str(first, "reason"));
        Assert.Contains(first.GetProperty("signals").EnumerateArray(), s => Str(s, "signal") == "period" && s.GetProperty("agrees").GetBoolean());

        // With a model, but the birthday photos were never described: nothing to compare, still uncertain at best.
        var (configure, _, judge) = WithModel();
        var undescribed = await OkAsync(configure, "library", "match", source.ToString(), "--bundle", birthday.ToString());
        Assert.Equal("no_description", Str(undescribed.GetProperty("content_check"), "status"));
        Assert.All(Candidates(undescribed), c => Assert.Contains(Str(c, "certainty"), new[] { "uncertain", "review" }));
        Assert.DoesNotContain(judge.Requests, r => r.Task == LibraryAdviceTask.Fit);

        // Every proposal of every unit without the same files at the target: at most uncertain without the content.
        foreach (var path in new[] { "Neuer Ordner", "Import", "Rechnung_2023_11.pdf" })
        {
            var result = await OkAsync("library", "match", source.ToString(), path, "--no-model", "--limit", "10");
            Assert.All(Candidates(result), c => Assert.Contains(Str(c, "certainty"), new[] { "uncertain", "review" }));
        }
    }

    /// <summary>Documents the same way: an invoice goes to the invoices of its provider, not to those of another one of the same year.</summary>
    [Fact]
    public async Task AnInvoice_GoesToTheInvoicesOfItsProvider()
    {
        var (_, source) = await PrepareAsync();
        var (configure, _, _) = WithModel();

        var result = await OkAsync(configure, "library", "match", source.ToString(), "Rechnung_2023_11.pdf");

        var first = Candidates(result)[0];
        Assert.Equal("Dokumente/Rechnungen Stadtwerke", Str(first, "target_relative"));
        Assert.Equal("fits", Str(first.GetProperty("content"), "fit"));
        Assert.Contains("Stadtwerke Musterstadt", Str(first, "reason"));
        Assert.DoesNotContain(Candidates(result), c => Str(c, "target_relative") == "Dokumente/Rechnungen Telefon");
        Assert.Contains(result.GetProperty("rejected").EnumerateArray(), r => Str(r, "target_relative") == "Dokumente/Rechnungen Telefon");
    }
}
