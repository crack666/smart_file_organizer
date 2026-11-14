using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Infrastructure.Evaluation;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// Video kind and origin as facts (https://github.com/crack666/smart_file_organizer/issues/103), through the CLI: the real
/// <see cref="OllamaService"/> against <see cref="FakeModelServer"/>, the calls counted. Small containers with set metadata
/// from <see cref="SyntheticVideo"/>; names follow the schemes of camera makers and programs, titles are invented.
/// </summary>
public class VideoKindCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _source;
    private readonly string _library;
    private readonly FakeModelServer _server = new();

    private const string Camera = "clip_a.mp4";
    private const string Recorder = "clip_b.mkv";
    private const string Film = "Some.Invented.Film.2009.1080p.BluRay.x264-GROUP.mkv";
    private const string Episode = "Example.Show.S02E05.mkv";
    private const string Nothing = "clip_c.mp4";

    public VideoKindCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_videokind_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _source = Path.Combine(_dir, "stick");
        _library = Path.Combine(_dir, "media");

        // The five cases of the issue, in one folder of the source.
        Write(_source, $"Videos/{Camera}", SyntheticVideo.Mp4(new SyntheticVideoSpec { Make = "GoPro", Model = "HERO12 Black", Location = true, MediaBytes = 3001 }));
        Write(_source, $"Videos/{Recorder}", SyntheticVideo.Mkv(new SyntheticVideoSpec { Software = "OBS Studio 30.1", DurationSeconds = 600, MediaBytes = 3002 }));
        Write(_source, $"Videos/{Film}", SyntheticVideo.Mkv(new SyntheticVideoSpec { DurationSeconds = 5400, AudioLanguages = ["ger", "eng"], MediaBytes = 3003 }));
        Write(_source, $"Videos/{Episode}", SyntheticVideo.Mkv(new SyntheticVideoSpec { DurationSeconds = 1500, MediaBytes = 3004 }));
        Write(_source, $"Videos/{Nothing}", SyntheticVideo.Mp4(new SyntheticVideoSpec { DurationSeconds = 40, MediaBytes = 3005 }));

        // A library with a film folder, a series and own clips.
        Write(_library, "Filme/Another Invented Film (2011)/Another.Invented.Film.2011.BluRay.x264.mkv",
            SyntheticVideo.Mkv(new SyntheticVideoSpec { DurationSeconds = 6000, MediaBytes = 4001 }));
        Write(_library, "Serien/Example Show/Staffel 1/Example.Show.S01E01.mkv", SyntheticVideo.Mkv(new SyntheticVideoSpec { DurationSeconds = 1500, MediaBytes = 4002 }));
        Write(_library, "Serien/Example Show/Staffel 1/Example.Show.S01E02.mkv", SyntheticVideo.Mkv(new SyntheticVideoSpec { DurationSeconds = 1500, MediaBytes = 4003 }));
        Write(_library, "Clips/GX010001.MP4", SyntheticVideo.Mp4(new SyntheticVideoSpec { Make = "GoPro", Model = "HERO11 Black", MediaBytes = 4004 }));

        WriteSettings();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    // ── Fertig, wenn ─────────────────────────────────────────────────────

    /// <summary>
    /// Done when: camera metadata, recording-program metadata, a film name, season and episode, and a file with nothing give
    /// the right facts; the film and the episode cost no model call, the camera and recorder clips are described but not
    /// asked for their kind, and only the file with nothing is asked. A later refresh reads the same without any call.
    /// </summary>
    [Fact]
    public async Task FiveCases_FactsAreRight_AndOnlyTheOpenOneAsksTheModelForTheKind()
    {
        var source = await ScanAsync(_source);
        var run = await SfoAsync("analyze", source.ToString(), "--json");
        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);

        Assert.Equal(0, _server.Answered("classification", Film));
        Assert.Equal(0, _server.Answered("classification", Episode));
        Assert.Equal(1, _server.Answered("classification", Camera));
        Assert.Equal(1, _server.Answered("classification", Recorder));
        Assert.Equal(1, _server.Answered("classification", Nothing));
        bool AskedKind(string name) => _server.Log.Any(r => r.Name == name && r.Fields?.Contains("video_kind") == true);
        Assert.False(AskedKind(Camera));
        Assert.False(AskedKind(Recorder));
        Assert.True(AskedKind(Nothing));

        var camera = await OriginAsync(source, Camera);
        Assert.Equal(("own_recording", "metadata", "certain", "action_cam"), (Str(camera, "kind"), Str(camera, "source"), Str(camera, "certainty"), Str(camera, "device")));
        var recorder = await OriginAsync(source, Recorder);
        Assert.Equal(("screen_recording", "metadata", "certain"), (Str(recorder, "kind"), Str(recorder, "source"), Str(recorder, "certainty")));
        var film = await OriginAsync(source, Film);
        Assert.Equal(("movie", "name", "certain", "Some Invented Film"), (Str(film, "kind"), Str(film, "source"), Str(film, "certainty"), Str(film, "title")));
        Assert.Equal(2009, film.GetProperty("year").GetInt32());
        var episode = await OriginAsync(source, Episode);
        Assert.Equal(("series_episode", "Example Show"), (Str(episode, "kind"), Str(episode, "title")));
        Assert.Equal((2, 5), (episode.GetProperty("season").GetInt32(), episode.GetProperty("episode").GetInt32()));
        // Nothing in the file: the model's answer, as likely.
        var nothing = await OriginAsync(source, Nothing);
        Assert.Equal(("own_recording", "model", "likely"), (Str(nothing, "kind"), Str(nothing, "source"), Str(nothing, "certainty")));

        // show lists every fact with source and certainty; files carries the origin of each video.
        var shown = await OkAsync("show", source.ToString(), $"Videos/{Film}");
        var facts = shown.GetProperty("file").GetProperty("facts").EnumerateArray().ToList();
        Assert.Contains(facts, f => Str(f, "fact") == "audio_languages" && Str(f, "value") == "de,en" && Str(f, "source") == "metadata");
        Assert.Contains(facts, f => Str(f, "fact") == "release_year" && Str(f, "value") == "2009" && Str(f, "certainty") == "certain");
        var files = (await OkAsync("files", source.ToString())).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(5, files.Count(f => f.GetProperty("origin").ValueKind == JsonValueKind.Object));

        // The backfill: no call at all, the same kinds, the model's kind kept where the file leaves it open.
        var before = _server.Log.Count;
        var refresh = await OkAsync("facts", "refresh", source.ToString());
        Assert.Equal(before, _server.Log.Count);
        Assert.Equal((5, 5, 0), (refresh.GetProperty("videos").GetInt32(), refresh.GetProperty("read").GetInt32(), refresh.GetProperty("missing").GetInt32()));
        var kinds = refresh.GetProperty("kinds");
        Assert.Equal((2, 1, 1, 1), (kinds.GetProperty("own_recording").GetInt32(), kinds.GetProperty("screen_recording").GetInt32(),
            kinds.GetProperty("movie").GetInt32(), kinds.GetProperty("series_episode").GetInt32()));
        Assert.Equal(1, refresh.GetProperty("model_kinds_kept").GetInt32());
        Assert.Equal("model", Str(await OriginAsync(source, Nothing), "source"));
    }

    /// <summary>
    /// Done when: the kinds of the videos stand in <c>findings candidates</c>, per finding and part, and in the description
    /// of the library folders (top level and <c>library folder</c>), read from the facts of both scans without a model.
    /// </summary>
    [Fact]
    public async Task Candidates_AndLibraryFolders_ShowTheKindsOfTheirVideos()
    {
        var (source, library) = await PrepareAsync();

        var candidates = await OkAsync("findings", "candidates", source.ToString());
        var item = candidates.GetProperty("items").EnumerateArray().Single(i => Str(i, "kind") == "not_backed_up");
        var origins = item.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("origin")).Where(o => o.ValueKind == JsonValueKind.Object).ToList();
        Assert.Equal(5, origins.Sum(o => o.GetProperty("videos").GetInt32()));
        var all = origins.SelectMany(o => o.GetProperty("kinds").EnumerateObject()).GroupBy(k => k.Name).ToDictionary(g => g.Key, g => g.Sum(k => k.Value.GetInt32()));
        Assert.Equal(1, all["movie"]);
        Assert.Equal(1, all["series_episode"]);
        Assert.Equal(1, all["screen_recording"]);
        Assert.Contains(origins, o => o.GetProperty("works").EnumerateArray().Any(w => w.GetString() == "Some Invented Film (2009)"));
        Assert.Contains(origins, o => o.GetProperty("works").EnumerateArray().Any(w => w.GetString() == "Example Show S02"));

        var top = candidates.GetProperty("libraries").EnumerateArray().Single().GetProperty("top_level").EnumerateArray().ToDictionary(t => Str(t, "name")!);
        Assert.True(top["Filme"].GetProperty("origin").ValueKind == JsonValueKind.Object, candidates.GetProperty("libraries").ToString());
        Assert.Equal("movie", top["Filme"].GetProperty("origin").GetProperty("kinds").EnumerateObject().Single().Name);
        var series = top["Serien"].GetProperty("origin");
        Assert.Equal(2, series.GetProperty("kinds").GetProperty("series_episode").GetInt32());
        Assert.Equal(["Example Show S01"], series.GetProperty("works").EnumerateArray().Select(w => w.GetString()));
        Assert.Equal(1, top["Clips"].GetProperty("origin").GetProperty("devices").GetProperty("action_cam").GetInt32());

        var folder = (await OkAsync("library", "folder", library.ToString(), "Filme")).GetProperty("folder").GetProperty("origin");
        Assert.Equal(["Another Invented Film (2011)"], folder.GetProperty("works").EnumerateArray().Select(w => w.GetString()));
        Assert.Contains("1 movie", Str(folder, "text"));
    }

    /// <summary>
    /// Done when: the free sentences of a person about a library, from its hints file next to the database, stand in the
    /// head of <c>findings candidates</c>; comment lines and empty lines are left out. Without a file there is no hints entry.
    /// </summary>
    [Fact]
    public async Task Candidates_ShowTheHintsOfALibraryInTheHead()
    {
        var (source, _) = await PrepareAsync();
        var head = (await OkAsync("findings", "candidates", source.ToString())).GetProperty("libraries").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, head.GetProperty("hints").ValueKind);

        var hints = Path.Combine(Path.GetDirectoryName(_dbPath)!, "library-hints", "media.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(hints)!);
        File.WriteAllText(hints, "# what goes where\n\nFilms go to Filme, one folder per film with its year.\n  Own clips go to Clips by year.  \n");

        head = (await OkAsync("findings", "candidates", source.ToString())).GetProperty("libraries").EnumerateArray().Single();
        var shown = head.GetProperty("hints");
        Assert.Equal(hints, Str(shown, "file"));
        Assert.Equal(["Films go to Filme, one folder per film with its year.", "Own clips go to Clips by year."],
            shown.GetProperty("lines").EnumerateArray().Select(l => l.GetString()));
        Assert.False(shown.GetProperty("truncated").GetBoolean());
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>Source and library scanned, compared, bundled, and the video facts of both read without a model.</summary>
    private async Task<(long Source, long Library)> PrepareAsync()
    {
        var source = await ScanAsync(_source);
        var library = await ScanAsync(_library, "--role", "library");
        await OkAsync("compare", source.ToString(), "--against", library.ToString());
        await OkAsync("bundles", "build", source.ToString());
        await OkAsync("facts", "refresh", source.ToString());
        await OkAsync("facts", "refresh", library.ToString());
        Assert.DoesNotContain(_server.Log, r => r.Kind == "classification");
        return (source, library);
    }

    private async Task<JsonElement> OriginAsync(long job, string name)
    {
        var origin = (await OkAsync("show", job.ToString(), $"Videos/{name}")).GetProperty("file").GetProperty("origin");
        Assert.Equal(JsonValueKind.Object, origin.ValueKind);
        return origin;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr)
    {
        public JsonElement Json()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.Clone();
        }
    }

    private async Task<Run> SfoAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost
        {
            Out = stdout,
            Error = stderr,
            ConfigureServices = services =>
                services.AddHttpClient<IOllamaService, OllamaService>().ConfigurePrimaryHttpMessageHandler(() => _server)
        };
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], host, CancellationToken.None);
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<JsonElement> OkAsync(params string[] args)
    {
        var run = await SfoAsync([.. args, "--json"]);
        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        return run.Json().GetProperty("result");
    }

    private async Task<long> ScanAsync(string root, params string[] extra) =>
        (await OkAsync(["scan", root, .. extra])).GetProperty("job").GetProperty("id").GetInt64();

    /// <summary>Settings next to the redirected database: the fake model and short pauses.</summary>
    private void WriteSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dbPath)!, "settings.json"), $$"""
            {
              "Ollama": { "BaseUrl": "http://model-server.test:11999", "Model": "{{FakeModelServer.ModelName}}",
                          "TimeoutSeconds": 5, "MaxRetries": 2, "MaxParallelRequests": 2 },
              "ModelServer": { "RetryDelaySeconds": 0.002, "MaxRetryAfterSeconds": 0.01, "OutageAfterFailures": 2,
                               "ProbeIntervalSeconds": 0.005, "ProbeTimeoutSeconds": 0.2,
                               "MaxWaitMinutes": 0.5, "WarmupTimeoutSeconds": 1 }
            }
            """);
    }
}
