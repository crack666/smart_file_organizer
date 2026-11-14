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

    public PlanTestbed(string? root = null)
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
    public long PlanId { get; private set; }

    public RecordingShell Shell { get; } = new();

    /// <summary>Called at the named points of a run (<see cref="PlanExecutionHooks"/>), for example to stop it midway.</summary>
    public Action<string, string>? Hook { get; set; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (_keep) return;
        try { FileSystemLinks.DeleteTree(Root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Scans, compares, decides and accepts every finding, then lets an agent create and confirm the plan.</summary>
    public async Task<long> CreatePlanAsync(bool agentApproves = true)
    {
        JobId = (await OkAsync("eval", Corpus, "--stub", Path.Combine(Corpus, EvalTruth.FileName))).GetProperty("job_id").GetInt64();
        var library = (await OkAsync("scan", Library, "--role", "library")).GetProperty("job").GetProperty("id").GetInt64();
        await OkAsync("compare", JobId.ToString(), "--against", library.ToString());
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

    /// <summary>A real, small JPEG, so the preview has a picture to show.</summary>
    private void Photo(string relative, int seed)
    {
        var path = Path.Combine(Tree, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
        File.WriteAllBytes(path, data.ToArray());
        _files.Add(new TruthFileEntry(relative, "photo", []));
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
