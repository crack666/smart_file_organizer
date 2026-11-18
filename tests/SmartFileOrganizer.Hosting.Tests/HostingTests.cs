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

    private ServiceProvider BuildProvider(IOllamaService? ollama = null, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSmartFileOrganizerCore(o =>
        {
            o.DatabasePath = _dbPath;
            o.ReloadSettingsOnChange = false;
        });
        if (ollama != null)
            services.AddSingleton(ollama);
        configure?.Invoke(services);

        // Default scan rules on purpose: the temp directory lives under "AppData" on Windows,
        // and folders above the scan root must not cause a skip.
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

    /// <summary>
    /// Sums over a subtree come from one service of the shared core, for the command line and the desktop app alike;
    /// the columns of <c>directory_nodes</c> hold the folder's own files only and no subtree count
    /// (https://github.com/crack666/smart_file_organizer/issues/111, from #6).
    /// </summary>
    [Fact]
    public async Task SubtreeSizesAndCounts_ComeFromTheReadServiceOfTheCore_NotFromStoredColumns()
    {
        var root = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        await File.WriteAllBytesAsync(Path.Combine(root, "top.bin"), new byte[10]);
        await File.WriteAllBytesAsync(Path.Combine(root, "a", "one.bin"), new byte[100]);
        await File.WriteAllBytesAsync(Path.Combine(root, "a", "b", "two.bin"), new byte[1000]);
        await File.WriteAllBytesAsync(Path.Combine(root, "a", "b", "three.bin"), new byte[2000]);

        await using var provider = BuildProvider();
        await provider.InitializeSmartFileOrganizerDatabaseAsync();
        var scans = provider.GetRequiredService<ScanJobService>();
        var job = await scans.CreateJobAsync(root);
        var scanDone = new TaskCompletionSource<ScanJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        scans.JobStateChanged += (_, j) => { if (j.Id == job.Id && j.Status == JobStatus.Completed) scanDone.TrySetResult(j); };
        await scans.StartAsync(job.Id);
        await scanDone.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var page = await provider.GetRequiredService<JobReadService>().ListDirectoriesAsync(new DirectoryListQuery { JobId = job.Id });
        var byPath = page.Items.ToDictionary(d => d.FullPath);
        (long, long, long) Sums(string path) => (byPath[path].SizeBytes, byPath[path].Files, byPath[path].DirectFiles);
        Assert.Equal((3110L, 4L, 1L), Sums(root));
        Assert.Equal((3100L, 3L, 1L), Sums(Path.Combine(root, "a")));
        Assert.Equal((3000L, 2L, 2L), Sums(Path.Combine(root, "a", "b")));

        var stored = await provider.GetRequiredService<IDirectoryRepository>().GetByPathAsync(job.Id, Path.Combine(root, "a"));
        Assert.Equal(100, stored!.TotalSize);
        Assert.Equal(0, stored.RecursiveFileCount);
    }

    /// <summary>
    /// What the desktop app shows comes from the states of the coordinator. Pausing and resuming while the run
    /// waits for the model server shows waiting again, not running, and the server coming back does not turn a
    /// paused run into a running one (https://github.com/crack666/smart_file_organizer/issues/111).
    /// </summary>
    [Fact]
    public async Task PauseAndResume_WhileWaitingForTheModelServer_ShowWaiting_NotRunning()
    {
        var root = Path.Combine(_dir, "data");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "Shopping list: milk, bread");
        await File.WriteAllTextAsync(Path.Combine(root, "letter.txt"), "Dear landlord");

        var ollama = new AwayOllamaService();
        await using var provider = BuildProvider(ollama, services => services.AddSingleton(new ModelServerOptions
        {
            OutageAfterFailures = 1, RetryDelaySeconds = 0.001, ProbeIntervalSeconds = 0.02, MaxWaitMinutes = 5, WarmupTimeoutSeconds = 5
        }));
        await provider.InitializeSmartFileOrganizerDatabaseAsync();
        var scans = provider.GetRequiredService<ScanJobService>();
        var job = await scans.CreateJobAsync(root);
        var scanDone = new TaskCompletionSource<ScanJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        scans.JobStateChanged += (_, j) => { if (j.Id == job.Id && j.Status == JobStatus.Completed) scanDone.TrySetResult(j); };
        await scans.StartAsync(job.Id);
        await scanDone.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var ai = provider.GetRequiredService<AiClassificationCoordinator>();
        var states = new List<AiProcessingStateChanged>();
        ai.StateChanged += (_, e) => { lock (states) states.Add(e); };
        AiProcessingStateChanged Last() { lock (states) return states[^1]; }
        async Task UntilAsync(Func<bool> reached, string what)
        {
            for (var i = 0; i < 1500 && !reached(); i++)
                await Task.Delay(20);
            Assert.True(reached(), $"{what}; last state: {Last().State} '{Last().StatusText}'");
        }

        ollama.Away = true;
        await ai.StartOrResumeAsync(job.Id);
        await UntilAsync(() => Last().State == AiProcessingState.WaitingForModelServer, "the run never waited for the server");

        ai.Pause();
        Assert.Equal(AiProcessingState.Paused, Last().State);
        await ai.StartOrResumeAsync(job.Id);
        Assert.Equal(AiProcessingState.WaitingForModelServer, Last().State);
        Assert.StartsWith("Waiting for the model server", Last().StatusText);

        // Paused again; the server comes back meanwhile. The display stays paused.
        ai.Pause();
        var probesBefore = ollama.Probes;
        ollama.Away = false;
        await UntilAsync(() => ollama.Probes > probesBefore, "the gate never probed the server again");
        await Task.Delay(200);
        Assert.Equal(AiProcessingState.Paused, Last().State);

        await ai.StartOrResumeAsync(job.Id);
        Assert.Equal(AiProcessingState.Running, Last().State);
        await UntilAsync(() => Last().State is AiProcessingState.Completed or AiProcessingState.Failed or AiProcessingState.Unavailable,
            "the run never ended");
        Assert.True(Last().State == AiProcessingState.Completed, $"{Last().State}: {Last().StatusText} {Last().ErrorMessage}");
    }

    /// <summary>The fake behind a server that can be away: calls then fail as unreachable and the probe says no.</summary>
    private sealed class AwayOllamaService : IOllamaService
    {
        private readonly FakeOllamaService _inner = new();
        private int _probes;

        public volatile bool Away;
        public int Probes => Volatile.Read(ref _probes);

        /// <summary>True for the check at the start of a run; afterwards the probe of the gate, which sees the outage.</summary>
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
            Task.FromResult(Interlocked.Increment(ref _probes) == 1 || !Away);

        public Task<IReadOnlyList<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default) => _inner.GetLocalModelsAsync(ct);
        public Task WarmModelAsync(string? model = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task<FileClassification> ClassifyAsync(OllamaClassificationInput input, CancellationToken ct = default) =>
            Away ? throw Unreachable() : _inner.ClassifyAsync(input, ct);

        public Task<DirectoryPreAssessmentOutcome> PreAssessDirectoryAsync(DirectoryPreAssessmentInput input, CancellationToken ct = default) =>
            Away ? throw Unreachable() : _inner.PreAssessDirectoryAsync(input, ct);

        public Task<DirectoryClassificationResult> SummarizeDirectoryAsync(DirectorySummaryInput input, CancellationToken ct = default) =>
            Away ? throw Unreachable() : _inner.SummarizeDirectoryAsync(input, ct);

        private static ModelServerUnavailableException Unreachable() => new(ModelServerFailure.ConnectionFailed, "connection refused");
    }

    /// <summary>
    /// The guard of the test projects: the model server address falls back to SFO_OLLAMA_URL (a dead port here), not
    /// to the shipped default, and an address in settings.json still wins, so tests with a stand-in keep theirs.
    /// </summary>
    [Fact]
    public async Task ModelServerAddress_DefaultsToTheEnvironment_SettingsStillWin()
    {
        Assert.Equal(NoRealModelServer.DeadAddress, Environment.GetEnvironmentVariable(SmartFileOrganizerHostOptions.OllamaUrlVariable));
        await using (var provider = BuildProvider())
            Assert.Equal(NoRealModelServer.DeadAddress, provider.GetRequiredService<SmartFileOrganizer.Infrastructure.Ollama.OllamaOptions>().BaseUrl);

        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dbPath)!, "settings.json"),
            """{ "Ollama": { "BaseUrl": "http://model-server.test:11999" } }""");
        await using (var provider = BuildProvider())
            Assert.Equal("http://model-server.test:11999", provider.GetRequiredService<SmartFileOrganizer.Infrastructure.Ollama.OllamaOptions>().BaseUrl);
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

        // Phase 1 reached every directory; docs and photos hold one file each, so their verdict follows the file
        // (rule single-file) and only the root asks the model
        Assert.Single(ollama.PreAssessedDirectories);
        var dirResults = provider.GetRequiredService<IDirectoryClassificationRepository>();
        Assert.NotNull(await dirResults.GetByDirectoryPathAsync(job.Id, root));

        AssertNoAvaloniaReachable();
    }

    [Fact]
    public async Task AwaitedRuns_ReportPhaseCounters_ThroughJobStatusService()
    {
        var root = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "alpha");
        await File.WriteAllBytesAsync(Path.Combine(root, "sub", "b.png"), [0x89, 0x50, 0x4E, 0x47, 1]);
        await File.WriteAllBytesAsync(Path.Combine(root, "sub", "c.bin"), [0x00, 0x01, 0x00, 0xFE]); // decided by rule

        var ollama = new FakeOllamaService();
        await using var provider = BuildProvider(ollama);
        await provider.InitializeSmartFileOrganizerDatabaseAsync();

        var scans = provider.GetRequiredService<ScanJobService>();
        var job = await scans.CreateJobAsync(root);
        var scanned = await scans.RunAsync(job.Id);
        Assert.Equal(JobStatus.Completed, scanned.Status);

        var status = provider.GetRequiredService<JobStatusService>();
        var before = await status.GetAsync(job.Id);
        Assert.NotNull(before);
        Assert.Equal(3, before!.Statistics.FilesTotal);
        Assert.Equal(3, before.Statistics.FilesAiEligible);
        Assert.Equal(3, before.Statistics.FilesAiPending);
        Assert.Equal(2, before.Statistics.DirectoriesTotal);
        Assert.Equal(0, before.Statistics.DirectoriesPreAssessed);

        // Only phase 2, awaited, with phase-tagged progress
        var ai = provider.GetRequiredService<AiClassificationCoordinator>();
        var reported = new List<AiPhaseProgress>();
        var run = await ai.RunAsync(job.Id, AiPhases.Classification, new SyncProgress<AiPhaseProgress>(reported.Add));
        Assert.True(run.OllamaAvailable);
        Assert.Equal(AiPhases.Classification, Assert.Single(run.Phases).Phase);
        Assert.NotEmpty(reported);
        Assert.All(reported, p => Assert.Equal(AiPhases.Classification, p.Phase));
        Assert.Empty(ollama.PreAssessedDirectories);

        var after = (await status.GetAsync(job.Id))!.Statistics;
        Assert.Equal(3, after.FilesAiAnalyzed);
        Assert.Equal(0, after.FilesAiPending);
        Assert.Equal(0, after.DirectoriesSummarized);
        Assert.Empty(after.RecentErrors);

        Assert.Null(await status.GetAsync(job.Id + 1000));
    }

    [Fact]
    public async Task AwaitedScan_Cancelled_IsStoredAsPaused_AndResumes()
    {
        var root = Path.Combine(_dir, "data");
        for (var i = 0; i < 5; i++)
        {
            Directory.CreateDirectory(Path.Combine(root, $"d{i}"));
            await File.WriteAllTextAsync(Path.Combine(root, $"d{i}", "a.txt"), "alpha");
        }

        await using var provider = BuildProvider();
        await provider.InitializeSmartFileOrganizerDatabaseAsync();
        var scans = provider.GetRequiredService<ScanJobService>();
        var job = await scans.CreateJobAsync(root);

        // Cancel at the first progress report, i.e. after the scan has started.
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scans.RunAsync(job.Id, new SyncProgress<ScanProgress>(_ => cts.Cancel()), cts.Token));
        Assert.Equal(JobStatus.Paused, (await scans.GetJobAsync(job.Id))!.Status);

        var resumed = await scans.RunAsync(job.Id);
        Assert.Equal(JobStatus.Completed, resumed.Status);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
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
