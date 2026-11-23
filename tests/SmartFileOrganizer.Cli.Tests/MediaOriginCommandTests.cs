using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartFileOrganizer.Application.Tests;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// Origin of images and kind of audio files as facts (https://github.com/crack666/smart_file_organizer/issues/103, the
/// part after the videos), through the CLI and without any model: <c>sfo facts refresh</c> reads them, and
/// <c>findings candidates</c> and <c>library folder</c> show them next to the videos. Files are built byte by byte by
/// <see cref="GeneratedFiles"/>; names follow the schemes of messengers and platforms, titles are invented.
/// </summary>
public class MediaOriginCommandTests : IDisposable
{
    private const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _source;
    private readonly string _library;

    public MediaOriginCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_mediaorigin_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _source = Path.Combine(_dir, "stick");
        _library = Path.Combine(_dir, "media");

        var png = Convert.FromBase64String(Png1x1);
        Write(_source, "Handy/IMG-20200101-WA0001.png", png);
        Write(_source, "Handy/FB_IMG_1588012345678.png", [.. png, 1]);
        Write(_source, "Handy/IMG_0001.jpg", GeneratedFiles.JpegWithExif(new DateTime(2020, 1, 1, 10, 0, 0), "Example", "X1", withGps: false));
        Write(_source, "Audio/Hörbücher/Kapitel 01.mp3", GeneratedFiles.Mp3WithTags("Kapitel 1", "Some Author", "Some Invented Book"));
        Write(_source, "Audio/track01.mp3", GeneratedFiles.Mp3WithTags("Morning Song", "Test Band", "First Album", frames: 210));

        Write(_library, "Hörbücher/Some Author/01.mp3", GeneratedFiles.Mp3WithTags("Teil 1", "Some Author", "Another Invented Book", frames: 220));
        Write(_library, "Fotos/2019/IMG_0100.jpg", GeneratedFiles.JpegWithExif(new DateTime(2019, 5, 1, 10, 0, 0), "Example", "X2", withGps: false));

        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dbPath)!, "settings.json"),
            """{ "Ollama": { "BaseUrl": "http://127.0.0.1:9", "TimeoutSeconds": 1, "MaxRetries": 0 } }""");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Done when: <c>facts refresh</c> reads the origin of each image and the kind of each audio file with a certainty, a
    /// messenger picture as <c>received</c>, a platform download as <c>download</c>, a book in an audiobook folder as
    /// <c>audiobook</c>; the groups of <c>findings candidates</c> count them, and so do the library folders.
    /// </summary>
    [Fact]
    public async Task Refresh_ReadsImageOriginAndAudioKind_AndCandidatesAndFoldersCountThem()
    {
        var source = await ScanAsync(_source);
        var library = await ScanAsync(_library, "--role", "library");
        await OkAsync("compare", source.ToString(), "--against", library.ToString());
        await OkAsync("bundles", "build", source.ToString());

        var refresh = await OkAsync("facts", "refresh", source.ToString());
        var images = refresh.GetProperty("images");
        Assert.Equal((3, 3, 0), (images.GetProperty("files").GetInt32(), images.GetProperty("read").GetInt32(), images.GetProperty("missing").GetInt32()));
        Assert.Equal(1, images.GetProperty("kinds").GetProperty("received").GetInt32());
        Assert.Equal(1, images.GetProperty("kinds").GetProperty("download").GetInt32());
        Assert.Equal(1, images.GetProperty("kinds").GetProperty("camera").GetInt32());
        Assert.Equal(1, images.GetProperty("certainty").GetProperty("received").GetProperty("certain").GetInt32());
        Assert.Equal(1, images.GetProperty("certainty").GetProperty("download").GetProperty("likely").GetInt32());
        var audio = refresh.GetProperty("audio");
        Assert.Equal(1, audio.GetProperty("kinds").GetProperty("audiobook").GetInt32());
        Assert.Equal(1, audio.GetProperty("kinds").GetProperty("music").GetInt32());
        await OkAsync("facts", "refresh", library.ToString());

        // show lists the fact with source and certainty.
        var facts = (await OkAsync("show", source.ToString(), "Handy/IMG-20200101-WA0001.png")).GetProperty("file").GetProperty("facts").EnumerateArray();
        Assert.Contains(facts, f => Str(f, "fact") == "image_origin" && Str(f, "value") == "received" && Str(f, "source") == "rule" && Str(f, "certainty") == "certain");

        // The groups of the findings count images per origin and audio files per kind.
        var candidates = await OkAsync("findings", "candidates", source.ToString());
        var origins = candidates.GetProperty("items").EnumerateArray()
            .SelectMany(i => i.GetProperty("groups").EnumerateArray()).Select(g => g.GetProperty("origin"))
            .Where(o => o.ValueKind == JsonValueKind.Object).ToList();
        int Sum(string part, string code) => origins.Sum(o => o.TryGetProperty(part, out var p) && p.TryGetProperty(code, out var n) ? n.GetInt32() : 0);
        Assert.True(Sum("images", "received") == 1, candidates.ToString());
        Assert.Equal(1, Sum("images", "download"));
        Assert.Equal(1, Sum("audio", "audiobook"));
        Assert.Contains(origins, o => Str(o, "text")!.Contains("image(s): ", StringComparison.Ordinal));

        // The library folders say what they hold.
        var top = candidates.GetProperty("libraries").EnumerateArray().Single().GetProperty("top_level").EnumerateArray().ToDictionary(t => Str(t, "name")!);
        Assert.Equal(1, top["Hörbücher"].GetProperty("origin").GetProperty("audio").GetProperty("audiobook").GetInt32());
        Assert.Equal(1, top["Fotos"].GetProperty("origin").GetProperty("images").GetProperty("camera").GetInt32());
        Assert.Equal(0, top["Fotos"].GetProperty("origin").GetProperty("videos").GetInt32());
        var folder = (await OkAsync("library", "folder", library.ToString(), "Hörbücher")).GetProperty("folder").GetProperty("origin");
        Assert.Equal("1 audio file(s): 1 audiobook", Str(folder, "text"));

        // The text form of the refresh names them too.
        var text = await SfoAsync("facts", "refresh", source.ToString());
        Assert.Contains("3 of 3 image(s) read, 0 missing - camera 1, download 1, received 1", text.Stdout);
        Assert.Contains("audio file(s) read", text.Stdout);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr);

    private async Task<Run> SfoAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost { Out = stdout, Error = stderr };
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], host, CancellationToken.None);
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<JsonElement> OkAsync(params string[] args)
    {
        var run = await SfoAsync([.. args, "--json"]);
        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
        using var doc = JsonDocument.Parse(run.Stdout);
        return doc.RootElement.Clone().GetProperty("result");
    }

    private async Task<long> ScanAsync(string root, params string[] extra) =>
        (await OkAsync(["scan", root, .. extra])).GetProperty("job").GetProperty("id").GetInt64();
}
