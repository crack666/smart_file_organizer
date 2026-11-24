using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Cli;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Desktop;
using SmartFileOrganizer.Desktop.Services;
using SmartFileOrganizer.Desktop.ViewModels;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Hosting.Tests;
using SmartFileOrganizer.Infrastructure.Content;
using SmartFileOrganizer.Application.Tests;
using SmartFileOrganizer.Tests.Support;

namespace SmartFileOrganizer.Desktop.Tests;

/// <summary>
/// A generated source and library in a temp folder this class makes, and a plan for them made the way a steering agent
/// makes it: through the CLI, in-process (<c>sfo eval --stub</c>, <c>compare</c>, <c>bundles build</c>, <c>decide</c>,
/// <c>findings accept</c>, <c>plan create</c>, <c>plan approve --all --by agent</c>). No model runs; every name and
/// every file is invented. The GUI side gets the services of <see cref="ServiceConfigurator"/> on the same database.
/// </summary>
public sealed class PlanTestbed : IDisposable
{
    private readonly bool _keep;
    private readonly List<TruthDirectoryEntry> _dirs = [new(".", "mixed")];
    private readonly List<TruthFileEntry> _files = [];

    /// <param name="root">A fixed folder (screenshots), else a new one in the temp folder.</param>
    /// <param name="withMedia">Also a video, a Word 97-2003 document, a .jfif photo and a file of an unknown format in
    /// Downloads (https://github.com/crack666/smart_file_organizer/issues/109). The video is made by <c>ffmpeg</c> when it
    /// is on the PATH, else it is a bare MP4 header that no tool can decode.</param>
    /// <param name="withCopies">Also a folder with the same large photo twice: a duplicate among the findings
    /// (https://github.com/crack666/smart_file_organizer/issues/27).</param>
    public PlanTestbed(string? root = null, bool withMedia = false, bool withCopies = false)
    {
        _keep = root is not null;
        Root = root ?? Path.Combine(Path.GetTempPath(), $"sfo_gui_{Guid.NewGuid():N}");
        if (Directory.Exists(Root)) FileSystemLinks.DeleteTree(Root);
        DatabasePath = Path.Combine(Root, "state", "data.db");
        Corpus = Path.Combine(Root, "quelle");
        Tree = Path.Combine(Corpus, "tree");
        Library = Path.Combine(Root, "bibliothek");
        Profile = Path.Combine(Root, "profile");
        Directory.CreateDirectory(Profile);

        // A whole holiday folder: one action for the folder.
        Folder("Urlaub Kroatien 2021", "uniform");
        for (var i = 0; i < 3; i++)
            Photo($"Urlaub Kroatien 2021/IMG_20210703_1{i}0000.jpg", i);

        // Downloads: one action per worth-keeping file; the person will call some of it clutter.
        Folder("Downloads", "mixed");
        Photo("Downloads/IMG_20230812_143012.jpg", 3);
        Photo("Downloads/IMG_20230812_143512.jpg", 4);
        Write("Downloads/Rechnung_Strom.pdf", "invoice", "%PDF-1.4 power");
        Write("Downloads/Einkaufsliste.txt", "document",
            "Einkaufsliste\n- Milch\n- Brot\n- Kaffee\n- Batterien AA\n\nfür Samstag, invented test content\n");

        if (withMedia)
        {
            WriteBytes("Downloads/Mietvertrag_alt.doc", "document", GeneratedFiles.LegacyDoc(
                "Mietvertrag\rzwischen Vermieterin Muster und Mieter Beispiel\rKaution: 900 € – fällig zum Einzug\r",
                "Übergabe der Schlüssel am 1. März\rinvented test content\r"));
            WriteBytes("Downloads/Postkarte.jfif", "photo", PhotoBytes(2));
            // A legacy Excel file: a compound file without a Word stream, which the preview does not read.
            var xls = GeneratedFiles.LegacyDoc("Tabelle\r", "Zahlen\r");
            "X"u8.CopyTo(xls.AsSpan(xls.AsSpan().IndexOf("W\0o\0r\0d\0D\0o\0c\0"u8)));
            WriteBytes("Downloads/Tabelle_alt.xls", "document", xls);
            Video("Downloads/Ausflug_2023.mp4");
        }

        if (withCopies)
        {
            // Large enough for a finding of its own (Findings:DuplicateMinBytes); what follows the picture is ignored by a decoder.
            Folder("Alte Kopien", "mixed");
            var large = PhotoBytes(1).Concat(new byte[1_300_000]).ToArray();
            WriteBytes("Alte Kopien/Panorama.jpg", "photo", large);
            WriteBytes("Alte Kopien/Panorama (1).jpg", "photo", large);
        }

        // Sensitive: decided one by one.
        Folder("Privat", "mixed");
        Write("Privat/passwoerter.txt", "document", "mail: dummy-not-real", ["credentials"]);

        var truth = new EvalTruthDocument(EvalTruth.SchemaVersion, new EvalCorpusInfo("gui-plan-test", 1, _files.Count), "tree", _dirs, _files);
        File.WriteAllText(Path.Combine(Corpus, EvalTruth.FileName), JsonSerializer.Serialize(truth, EvalJson.Options));

        Directory.CreateDirectory(Path.Combine(Library, "Fotos", "2019-06 Italien"));
        File.WriteAllText(Path.Combine(Library, "Fotos", "2019-06 Italien", "IMG_20190612_100000.jpg"), "italy");
    }

    public string Root { get; }
    public string DatabasePath { get; }
    public string Corpus { get; }
    public string Tree { get; }
    public string Library { get; }

    /// <summary>Stands for the user profile: the temp folder lies in the real one on Windows, where no target may lie.</summary>
    public string Profile { get; }

    public long JobId { get; private set; }
    public long LibraryJobId { get; private set; }
    public long PlanId { get; private set; }

    public RecordingShell Shell { get; } = new();

    /// <summary>The cache of the preview in this test folder, never the cache of the user.</summary>
    public string PreviewCache => Path.Combine(Root, "preview-cache");

    /// <summary>The GUI acts as if <c>ffmpeg</c> were not installed.</summary>
    public bool WithoutFfmpeg { get; set; }

    /// <summary>True when the video of <c>withMedia</c> was made by <c>ffmpeg</c> (a real, decodable video).</summary>
    public bool HasRealVideo { get; private set; }

    /// <summary>Called at the named points of a run (<see cref="PlanExecutionHooks"/>), for example to stop it midway.</summary>
    public Action<string, string>? Hook { get; set; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (_keep) return;
        try { FileSystemLinks.DeleteTree(Root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Scans and analyses the source with the stand-in model (<c>sfo eval --stub</c>): an analysed job, no plan.</summary>
    public async Task<long> AnalyzeAsync()
    {
        JobId = (await OkAsync("eval", Corpus, "--stub", Path.Combine(Corpus, EvalTruth.FileName))).GetProperty("job_id").GetInt64();
        return JobId;
    }

    /// <summary>
    /// Gives the analysed job what a stand-in model never answers: a shaky result (the shopping list, 40 %), a temporary
    /// file (the second photo) and a finance marker (the invoice).
    /// </summary>
    public Task MarkExamplesAsync() => SqlAsync($"""
        UPDATE ai_results SET confidence = 0.4
         WHERE file_node_id = (SELECT id FROM file_nodes WHERE job_id = {JobId} AND name = 'Einkaufsliste.txt');
        UPDATE ai_results SET keep_hint = 'temporary'
         WHERE file_node_id = (SELECT id FROM file_nodes WHERE job_id = {JobId} AND name = 'IMG_20230812_143512.jpg');
        UPDATE ai_results SET sensitive_markers = 'finance'
         WHERE file_node_id = (SELECT id FROM file_nodes WHERE job_id = {JobId} AND name = 'Rechnung_Strom.pdf');
        """);

    public async Task SqlAsync(string sql)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DatabasePath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DatabasePath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>The analysed job with everything its findings need (library, comparison, bundles, targets), nothing decided.</summary>
    public async Task PrepareFindingsAsync()
    {
        JobId = (await OkAsync("eval", Corpus, "--stub", Path.Combine(Corpus, EvalTruth.FileName))).GetProperty("job_id").GetInt64();
        LibraryJobId = (await OkAsync("scan", Library, "--role", "library")).GetProperty("job").GetProperty("id").GetInt64();
        await OkAsync("compare", JobId.ToString(), "--against", LibraryJobId.ToString());
        await OkAsync("bundles", "build", JobId.ToString());

        var targets = Path.Combine(Root, "targets.json");
        File.WriteAllText(targets, """
            { "schema_version": 1, "kind": "sfo_target_structure", "root": null, "source_job": null,
              "created_at": "2026-11-14T10:00:00Z", "origin": "rule", "model": null,
              "targets": [
                { "id": "photos", "path": "Fotos", "pattern": null, "holds": ["image"], "reason": null, "examples": [] },
                { "id": "documents", "path": "Dokumente", "pattern": null, "holds": ["document"], "reason": null, "examples": [] } ] }
            """);
        await OkAsync("decide", JobId.ToString(), "--targets", targets);
    }

    /// <summary>Scans, compares, decides and accepts every finding, then lets an agent create and confirm the plan.</summary>
    public async Task<long> CreatePlanAsync(bool agentApproves = true)
    {
        await PrepareFindingsAsync();
        var findings = await OkAsync("findings", JobId.ToString(), "--limit", "200");
        var keys = findings.GetProperty("items").EnumerateArray().Select(f => f.GetProperty("key").GetString()!).ToList();
        Assert.NotEmpty(keys);
        await OkAsync(["findings", "accept", JobId.ToString(), .. keys]);

        PlanId = (await OkAsync("plan", "create", JobId.ToString())).GetProperty("plan_id").GetInt64();
        if (agentApproves)
            await OkAsync("plan", "approve", PlanId.ToString(), "--all", "--by", "agent");
        return PlanId;
    }

    /// <summary>The GUI's composition on this database, with stand-ins for the model server and the shell.</summary>
    public IServiceProvider GuiServices() => ServiceConfigurator.Build(
        o => o.DatabasePath = DatabasePath,
        s =>
        {
            s.AddSingleton<IProtectedPlaces>(new FixedPlaces(Profile));
            s.AddSingleton<IOllamaService>(new FakeOllamaService());
            s.AddSingleton<IShellLauncher>(Shell);
            s.AddSingleton(new PlanExecutionHooks { At = (point, path) => Hook?.Invoke(point, path) });
            var frames = WithoutFfmpeg ? new VideoFrameExtractor(() => null) : new VideoFrameExtractor();
            s.AddSingleton(sp => new FilePreviewService(sp.GetRequiredService<FileContentExtractor>(), frames, PreviewCache));
        });

    public async Task<PlanViewModel> OpenPlanAsync()
    {
        var vm = GuiServices().GetRequiredService<PlanViewModel>();
        await vm.LoadAsync(JobId, PlanId);
        return vm;
    }

    public async Task<JsonElement> OkAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost
        {
            Out = stdout,
            Error = stderr,
            ConfigureServices = s => s.AddSingleton<IProtectedPlaces>(new FixedPlaces(Profile))
        };
        var exit = await SfoApp.RunAsync([.. args, "--json", "--db", DatabasePath], host);
        Assert.True(exit == ExitCodes.Ok, string.Join(' ', args) + ": " + stdout + stderr);
        using var doc = JsonDocument.Parse(stdout.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    private void Folder(string path, string verdict) => _dirs.Add(new TruthDirectoryEntry(path, verdict));

    private void Write(string relative, string category, string content, IReadOnlyList<string>? sensitive = null)
    {
        var path = Path.Combine(Tree, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _files.Add(new TruthFileEntry(relative, category, sensitive ?? []));
    }

    private void WriteBytes(string relative, string? category, byte[] content)
    {
        var path = Path.Combine(Tree, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        _files.Add(new TruthFileEntry(relative, category, []));
    }

    /// <summary>A 12-second test video with moving colour bars and a counter, made by ffmpeg; without it an MP4 header only.</summary>
    private void Video(string relative)
    {
        var path = Path.Combine(Tree, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (new VideoFrameExtractor().IsAvailable)
        {
            var psi = new ProcessStartInfo(VideoFrameExtractor.ToolName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=480x270:rate=10:duration=12",
                         "-c:v", "mpeg4", "-pix_fmt", "yuv420p", path }) // mpeg4 is built into every ffmpeg; libx264 is not

                psi.ArgumentList.Add(arg);
            using var make = Process.Start(psi)!;
            make.StandardError.ReadToEnd();
            make.WaitForExit();
            HasRealVideo = make.ExitCode == 0 && File.Exists(path);
        }
        if (!HasRealVideo)
            File.WriteAllBytes(path, GeneratedFiles.Mp4Header(new DateTime(2023, 8, 12, 14, 0, 0, DateTimeKind.Utc), 12, 480, 270));
        _files.Add(new TruthFileEntry(relative, "video", []));
    }

    /// <summary>A real, small JPEG, so the preview has a picture to show.</summary>
    private void Photo(string relative, int seed)
    {
        var path = Path.Combine(Tree, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, PhotoBytes(seed));
        _files.Add(new TruthFileEntry(relative, "photo", []));
    }

    private static byte[] PhotoBytes(int seed)
    {
        SKColor[] sky = [new(0x4A, 0x90, 0xD9), new(0xF2, 0x99, 0x4A), new(0x6F, 0xCF, 0x97), new(0x9B, 0x51, 0xE0), new(0xEB, 0x57, 0x57)];
        using var bitmap = new SKBitmap(320, 220);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 220),
                [sky[seed % sky.Length], new SKColor(0x1F, 0x3B, 0x57)], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, 320, 220, paint);
            using var sun = new SKPaint { Color = new SKColor(0xFF, 0xE0, 0x82), IsAntialias = true };
            canvas.DrawCircle(60 + 40 * seed, 70, 28, sun);
            using var hill = new SKPaint { Color = new SKColor(0x2E, 0x5E, 0x3A), IsAntialias = true };
            canvas.DrawOval(new SKRect(-40, 150, 360, 300), hill);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private sealed class FixedPlaces(params string[] places) : IProtectedPlaces
    {
        public IReadOnlyList<string> Get() => places;
    }
}

/// <summary>Records what the GUI hands to the shell instead of starting a program.</summary>
public sealed class RecordingShell : IShellLauncher
{
    public List<string> Opened { get; } = [];
    public List<string> Revealed { get; } = [];
    public void Open(string path) => Opened.Add(path);
    public void Reveal(string path) => Revealed.Add(path);
}
