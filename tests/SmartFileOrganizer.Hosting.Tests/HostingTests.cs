using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Persistence.Migrations;
using SmartFileOrganizer.Scanning.Engine;

namespace SmartFileOrganizer.Hosting.Tests;

/// <summary>
/// The core composed through <see cref="ServiceCollectionExtensions.AddSmartFileOrganizerCore"/> alone:
/// no GUI project referenced, database and settings in a temp directory, Ollama replaced by a fake.
/// </summary>
public class HostingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public HostingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_hosting_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "state", "data.db");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ServiceProvider BuildProvider(FakeOllamaService? ollama = null)
    {
        var services = new ServiceCollection();
        services.AddSmartFileOrganizerCore(o =>
        {
            o.DatabasePath = _dbPath;
            o.ReloadSettingsOnChange = false;
        });
        if (ollama != null)
            services.AddSingleton<IOllamaService>(ollama);

        // The temp directory lives under "AppData" on Windows, which the default skip patterns
        // match anywhere in the path. Scan rules are not under test here.
        services.AddSingleton(new HeuristicsOptions { SkipPatterns = [] });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Provider_ResolvesAllCoreServices_WithoutAvalonia()
    {
        await using var provider = BuildProvider();
        var version = await provider.InitializeSmartFileOrganizerDatabaseAsync();

        Assert.Equal(SchemaMigrator.BuiltIn.Count, version);
        Assert.True(File.Exists(_dbPath));

        Assert.NotNull(provider.GetRequiredService<ScanJobService>());
        Assert.NotNull(provider.GetRequiredService<FileQueryService>());
        Assert.NotNull(provider.GetRequiredService<DirectoryQueryService>());
        Assert.NotNull(provider.GetRequiredService<AiClassificationCoordinator>());
        Assert.NotNull(provider.GetRequiredService<ReviewService>());
        Assert.NotNull(provider.GetRequiredService<ScanExportService>());
        Assert.NotNull(provider.GetRequiredService<FocusedRescanService>());
        Assert.NotNull(provider.GetRequiredService<IOllamaService>());
        Assert.NotNull(provider.GetRequiredService<IClassificationInputPreparer>());
        Assert.NotNull(provider.GetRequiredService<OllamaSettingsService>());

        var paths = provider.GetRequiredService<SmartFileOrganizerPaths>();
        Assert.Equal(Path.GetFullPath(_dbPath), paths.DatabasePath);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(paths.DatabasePath)!, "settings.json"), paths.SettingsPath);

        AssertNoAvaloniaReachable();
    }

    [Fact]
    public async Task ScanThenAnalysis_RunsThroughHostingOnly_WithFakeOllama()
    {
        var root = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        Directory.CreateDirectory(Path.Combine(root, "photos"));
        await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "Shopping list: milk, bread");
        await File.WriteAllBytesAsync(Path.Combine(root, "docs", "page.pdf"), MinimalPdf());
        await File.WriteAllBytesAsync(Path.Combine(root, "photos", "pic.png"), [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);

        var ollama = new FakeOllamaService();
        await using var provider = BuildProvider(ollama);
        await provider.InitializeSmartFileOrganizerDatabaseAsync();

        // Scan
        var scans = provider.GetRequiredService<ScanJobService>();
        var job = await scans.CreateJobAsync(root);
        var scanDone = new TaskCompletionSource<ScanJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        scans.JobStateChanged += (_, j) => { if (j.Id == job.Id) scanDone.TrySetResult(j); };
        await scans.StartAsync(job.Id);
        var scanned = await scanDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(JobStatus.Completed, scanned.Status);
        Assert.Equal(3, scanned.ProcessedFiles);

        // Analysis (all three phases)
        var ai = provider.GetRequiredService<AiClassificationCoordinator>();
        var aiDone = new TaskCompletionSource<AiProcessingStateChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
        ai.StateChanged += (_, e) =>
        {
            if (e.State is AiProcessingState.Completed or AiProcessingState.Failed
                or AiProcessingState.Cancelled or AiProcessingState.Unavailable)
                aiDone.TrySetResult(e);
        };
        await ai.StartOrResumeAsync(job.Id);
        var final = await aiDone.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(final.State == AiProcessingState.Completed, $"{final.State}: {final.StatusText} {final.ErrorMessage}");
        Assert.Equal("AI classification completed.", final.StatusText);

        // Every file analysed and persisted
        var files = provider.GetRequiredService<IFileRepository>();
        var classifications = provider.GetRequiredService<IClassificationRepository>();
        var all = new List<FileNode>();
        foreach (var dir in new[] { root, Path.Combine(root, "docs"), Path.Combine(root, "photos") })
            all.AddRange(await files.GetByDirectoryAsync(job.Id, dir));

        Assert.Equal(3, all.Count);
        foreach (var file in all)
        {
            Assert.Equal(FileNodeStatus.AiAnalyzed, file.Status);
            var c = await classifications.GetByFileNodeIdAsync(file.Id);
            Assert.NotNull(c);
            Assert.Equal("fake-model", c!.ModelUsed);
        }

        // Evidence went through the UI-free extractor: PDF as rendered page, PNG as bytes, text as text
        var pdfInput = ollama.FileInputs.Single(i => i.File.Name == "page.pdf");
        Assert.Single(pdfInput.Base64Images);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, Convert.FromBase64String(pdfInput.Base64Images[0])[..4]);
        Assert.Single(ollama.FileInputs.Single(i => i.File.Name == "pic.png").Base64Images);
        Assert.Contains("Shopping list", ollama.FileInputs.Single(i => i.File.Name == "notes.txt").ExtractedText);

        // Phase 1 and phase 3 reached every directory
        Assert.Equal(3, ollama.PreAssessedDirectories.Count);
        var dirResults = provider.GetRequiredService<IDirectoryClassificationRepository>();
        Assert.NotNull(await dirResults.GetByDirectoryPathAsync(job.Id, root));

        AssertNoAvaloniaReachable();
    }

    [Fact]
    public void Paths_DefaultToAppData_WhenNothingIsSet()
    {
        var paths = SmartFileOrganizerPaths.Resolve(new SmartFileOrganizerHostOptions(), _ => null);

        Assert.Equal(Path.Combine(SmartFileOrganizerPaths.DefaultDataDirectory, "data.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(SmartFileOrganizerPaths.DefaultDataDirectory, "settings.json"), paths.SettingsPath);
    }

    [Fact]
    public void Paths_EnvironmentDatabase_MovesSettingsAlongside()
    {
        var env = new Dictionary<string, string?> { ["SFO_DB"] = Path.Combine(_dir, "env.db") };

        var paths = SmartFileOrganizerPaths.Resolve(new SmartFileOrganizerHostOptions(), k => env.GetValueOrDefault(k));

        Assert.Equal(Path.Combine(_dir, "env.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(_dir, "settings.json"), paths.SettingsPath);
    }

    [Fact]
    public void Paths_OptionWinsOverEnvironment_AndSettingsCanBeSetSeparately()
    {
        var env = new Dictionary<string, string?>
        {
            ["SFO_DB"] = Path.Combine(_dir, "env.db"),
            ["SFO_SETTINGS"] = Path.Combine(_dir, "env-settings.json")
        };
        var options = new SmartFileOrganizerHostOptions { DatabasePath = Path.Combine(_dir, "opt.db") };

        var paths = SmartFileOrganizerPaths.Resolve(options, k => env.GetValueOrDefault(k));

        Assert.Equal(Path.Combine(_dir, "opt.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(_dir, "env-settings.json"), paths.SettingsPath);
    }

    /// <summary>No Avalonia assembly is loaded, and none is referenced by any SmartFileOrganizer assembly
    /// reachable from the hosting project.</summary>
    private static void AssertNoAvaloniaReachable()
    {
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
            a => a.GetName().Name!.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));

        var seen = new HashSet<string>();
        var queue = new Queue<Assembly>([typeof(ServiceCollectionExtensions).Assembly]);
        while (queue.Count > 0)
        {
            var asm = queue.Dequeue();
            if (!seen.Add(asm.GetName().Name!)) continue;
            foreach (var reference in asm.GetReferencedAssemblies())
            {
                Assert.False(reference.Name!.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase),
                    $"{asm.GetName().Name} references {reference.Name}");
                if (reference.Name.StartsWith("SmartFileOrganizer", StringComparison.Ordinal))
                    queue.Enqueue(Assembly.Load(reference));
            }
        }
        Assert.Contains("SmartFileOrganizer.Infrastructure", seen);
    }

    /// <summary>A one-page PDF with a filled rectangle and correct xref offsets.</summary>
    private static byte[] MinimalPdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Contents 4 0 R >>",
            "<< /Length 31 >>\nstream\n0 0 1 rg 20 20 100 100 re f\n\nendstream"
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
