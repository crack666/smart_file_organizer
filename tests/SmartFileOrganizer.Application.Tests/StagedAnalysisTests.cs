using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure;
using SmartFileOrganizer.Infrastructure.Persistence;
using SmartFileOrganizer.Scanning.Engine;
using SmartFileOrganizer.Scanning.Rules;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Staged analysis (https://github.com/crack666/smart_file_organizer/issues/8) end to end: directories are
/// generated on disk, scanned with the real scan engine into a temp SQLite file, then run through all three
/// phases with a scripted Ollama stand-in that counts calls.
/// </summary>
public class StagedAnalysisTests : IDisposable
{
    private readonly string _dir;
    private readonly DatabaseContext _db;
    private readonly ScanJobRepository _jobRepo;
    private readonly FileRepository _fileRepo;
    private readonly DirectoryRepository _dirRepo;
    private readonly DirectoryClassificationRepository _dirClassRepo;
    private readonly DirectoryVerdictRepository _verdictRepo;
    private readonly DirectorySampleRepository _sampleRepo;
    private readonly ClassificationRepository _classRepo;
    private readonly ScriptedOllamaService _ollama = new();
    private readonly DirectoryAnalysisOptions _options = new();

    public StagedAnalysisTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_staged_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _db = new DatabaseContext(Path.Combine(_dir, "data.db"), NullLogger<DatabaseContext>.Instance);
        _db.InitializeAsync().GetAwaiter().GetResult();

        _jobRepo = new ScanJobRepository(_db);
        _fileRepo = new FileRepository(_db);
        _dirRepo = new DirectoryRepository(_db);
        _dirClassRepo = new DirectoryClassificationRepository(_db);
        _verdictRepo = new DirectoryVerdictRepository(_db);
        _sampleRepo = new DirectorySampleRepository(_db);
        _classRepo = new ClassificationRepository(_db);

        // Default Phase 2 answer: a photo, sure, with a target.
        _ollama.Classify = _ => Photo();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ helpers

    private static FileClassification Photo(double confidence = 0.9) => new()
    {
        Category = FileCategory.Photo,
        Importance = Importance.High,
        Confidence = confidence,
        Summary = "a holiday photo",
        SuggestedTarget = "Fotos/Urlaub"
    };

    private string MakeTree(string name) => Directory.CreateDirectory(Path.Combine(_dir, "tree", name)).FullName;

    private static void WriteFile(string path, int bytes = 16, DateTime? modifiedUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (modifiedUtc is { } t) File.SetLastWriteTimeUtc(path, t);
    }

    private static string WritePhotos(string dir, int count)
    {
        var start = new DateTime(2019, 7, 1, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= count; i++)
            // Size grows with the number, the date does not: smallest and oldest are different files.
            WriteFile(Path.Combine(dir, $"IMG_{i:0000}.JPG"), 100 + i, start.AddMinutes(i * 37 % (count + 1) * 7));
        return dir;
    }

    private async Task<long> ScanAsync(string root)
    {
        // Temp paths can contain "AppData" or user-data names; only the rules under test are active here.
        var rules = new ScanRuleSet
        {
            Name = "test",
            Rules = [new ScanRule("$Recycle.Bin", ScanRuleAction.Skip)]
        };
        var job = await _jobRepo.CreateAsync(new ScanJob
        {
            RootPath = root, Status = JobStatus.Created, CreatedAt = DateTime.UtcNow,
            Rules = ScanRulesFile.ToProfile(rules, "test")
        });
        var engine = new ScanEngine(new FileSystemAccessor(), _fileRepo, _dirRepo, _jobRepo, NullLogger<ScanEngine>.Instance);
        await engine.RunAsync(job, new Progress<ScanProgress>(_ => { }), CancellationToken.None);
        return job.Id;
    }

    private DirectorySamplingService Sampling() => new(
        _fileRepo, _verdictRepo, _sampleRepo, _dirClassRepo, _options, NullLogger<DirectorySamplingService>.Instance);

    private ClassificationService Classification() => new(
        _fileRepo, _classRepo, new FileFactRepository(_db), new PlainInputPreparer(), _ollama, Sampling(), () => 4, NullLogger<ClassificationService>.Instance);

    private Task<AiClassificationRunResult> Phase1Async(long jobId) => new DirectoryPreAssessmentService(
        _dirRepo, _fileRepo, _dirClassRepo, _verdictRepo, _ollama, () => 4, _options,
        NullLogger<DirectoryPreAssessmentService>.Instance).RunAsync(jobId);

    private Task<AiClassificationRunResult> Phase3Async(long jobId) => new DirectorySummaryService(
        _dirRepo, _fileRepo, _dirClassRepo, _classRepo, _verdictRepo, _sampleRepo, _ollama, () => 4,
        NullLogger<DirectorySummaryService>.Instance).RunAsync(jobId);

    private async Task<long> RunAllAsync(string root, Func<DirectoryPreAssessmentInput, DirectoryVerdictKind?> verdict)
    {
        _ollama.Answer = verdict;
        var jobId = await ScanAsync(root);
        await Phase1Async(jobId);
        await Classification().RunAsync(jobId);
        await Phase3Async(jobId);
        return jobId;
    }

    private async Task<long> DirIdAsync(long jobId, string path) =>
        (await _dirRepo.GetByPathAsync(jobId, path))?.Id ?? throw new Xunit.Sdk.XunitException($"no directory {path}");

    /// <summary>Phase 2 input without reading files: the tests script the answer, not the evidence.</summary>
    private sealed class PlainInputPreparer : IClassificationInputPreparer
    {
        public Task<OllamaClassificationInput> PrepareAsync(FileNode file, CancellationToken ct = default) =>
            PrepareAsync(file, EvidenceDepth.Standard, ct);

        public Task<OllamaClassificationInput> PrepareAsync(FileNode file, EvidenceDepth depth, CancellationToken ct = default) =>
            Task.FromResult(new OllamaClassificationInput { File = file, Depth = depth });
    }

    // ------------------------------------------------------------------ Fertig, wenn

    [Fact]
    public async Task UniformDirectoryOf500_CostsAtMostTheSampleSize_AndEveryFileCarriesAResult()
    {
        var trip = WritePhotos(MakeTree("Urlaub"), 500);

        var jobId = await RunAllAsync(trip, _ => DirectoryVerdictKind.Uniform);

        Assert.True(_ollama.ClassifyInputs.Count <= _options.MaxSamplesPerDirectory,
            $"{_ollama.ClassifyInputs.Count} model calls for a uniform directory");
        Assert.Equal(_options.MaxSamplesPerDirectory, _ollama.ClassifyInputs.Count);

        var files = await _fileRepo.GetByDirectoryAsync(jobId, trip);
        Assert.Equal(500, files.Count);
        Assert.All(files, f => Assert.NotNull(f.Classification));

        var measured = files.Where(f => f.Status == FileNodeStatus.AiAnalyzed).ToList();
        var inherited = files.Where(f => f.Status == FileNodeStatus.Inherited).ToList();
        Assert.Equal(15, measured.Count);
        Assert.Equal(485, inherited.Count);

        var dirId = await DirIdAsync(jobId, trip);
        Assert.All(measured, f => Assert.Equal(ResultOrigin.Model, f.Classification!.Origin));
        // Members agree with the confirmed sample of their directory: two signals; inherited ones were not looked at.
        Assert.All(measured, f => Assert.Equal(ResultCertainty.Certain, f.Classification!.Certainty));
        Assert.All(inherited, f => Assert.Equal(ResultCertainty.Likely, f.Classification!.Certainty));
        Assert.All(inherited, f =>
        {
            var c = f.Classification!;
            Assert.Equal(ResultOrigin.Inherited, c.Origin);
            Assert.False(c.IsAiResult);
            Assert.Null(c.ModelUsed);
            Assert.Equal(dirId, c.InheritedFromDirectoryNodeId);
            Assert.Equal(FileCategory.Photo, c.Category);
            Assert.Equal("Fotos/Urlaub", c.SuggestedTarget);
            // Never as sure as a measured result.
            Assert.True(c.Confidence <= _options.InheritedConfidenceCap);
            Assert.True(c.Confidence < measured.Min(m => m.Classification!.Confidence));
            Assert.StartsWith("Not looked at", c.Summary);
        });

        var sample = (await _sampleRepo.GetByNodeIdAsync(dirId))!;
        Assert.Equal(SampleState.Confirmed, sample.State);
        Assert.Equal(FileCategory.Photo, sample.MajorityCategory);
        Assert.Equal(485, sample.InheritedCount);
        Assert.Equal(0, sample.DeviationShare);
        Assert.Equal(500, sample.EligibleCount);
        Assert.Equal(measured.Select(f => f.Id).Order(), sample.Members.Select(m => m.FileNodeId).Order());
        Assert.Contains(sample.Members, m => m.Role == SampleRole.Size);
        Assert.Contains(sample.Members, m => m.Role == SampleRole.Date);
        Assert.Contains(sample.Members, m => m.Role == SampleRole.Stratum);

        // Stored as English codes.
        await using var conn = _db.CreateConnection();
        Assert.Equal(485, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM ai_results WHERE origin = 'inherited'"));
        Assert.Equal("confirmed", await conn.ExecuteScalarAsync<string>("SELECT state FROM directory_samples"));

        // Phase 3: the verdict already says what it is; no model call.
        Assert.Empty(_ollama.SummaryInputs);
        var summary = await _dirClassRepo.GetByNodeAndPhaseAsync(dirId, "summary");
        Assert.NotNull(summary);
        Assert.Null(summary!.ModelUsed);
        Assert.Contains("485 file(s) inherited", summary.Summary);
    }

    [Fact]
    public async Task DeviatingSample_RaisesToMixed_ThenEveryFileIsJudgedOnItsOwn()
    {
        var trip = WritePhotos(MakeTree("Handy"), 120);
        // Every third file is a screenshot: far more than one outlier in fifteen.
        _ollama.Classify = input => int.Parse(input.File.Name[4..8]) % 3 == 0
            ? new FileClassification { Category = FileCategory.Screenshot, Importance = Importance.Low, Confidence = 0.9 }
            : Photo();

        var jobId = await RunAllAsync(trip, _ => DirectoryVerdictKind.Uniform);
        var dirId = await DirIdAsync(jobId, trip);

        var verdict = (await _verdictRepo.GetByNodeIdAsync(dirId))!;
        Assert.Equal(DirectoryVerdictKind.Mixed, verdict.Verdict);
        Assert.Equal(DirectoryVerdictKind.Uniform, verdict.EscalatedFrom);
        Assert.Contains("Off:", verdict.EscalationReason);

        var sample = (await _sampleRepo.GetByNodeIdAsync(dirId))!;
        Assert.Equal(SampleState.Escalated, sample.State);
        Assert.True(sample.DeviationShare > _options.MaxDeviationShare);
        Assert.Contains(sample.Members, m => m.Deviates == true);
        Assert.Contains(sample.Members, m => m.Deviates == false);

        // Every file judged by the model exactly once, none inherited.
        Assert.Equal(120, _ollama.ClassifyInputs.Count);
        Assert.Equal(120, _ollama.ClassifiedNames.Distinct().Count());
        var files = await _fileRepo.GetByDirectoryAsync(jobId, trip);
        Assert.All(files, f =>
        {
            Assert.Equal(FileNodeStatus.AiAnalyzed, f.Status);
            Assert.Equal(ResultOrigin.Model, f.Classification!.Origin);
        });
        Assert.All(_ollama.ClassifyInputs, i => Assert.True(i.AskKeepHint));

        // Mixed now: the model summarizes it.
        Assert.Single(_ollama.SummaryInputs, s => s.Directory.Id == dirId);
    }

    [Fact]
    public async Task TwoRunsOverTheSameDirectory_DrawTheSameSample()
    {
        var trip = WritePhotos(MakeTree("Wiederholt"), 200);
        _ollama.Answer = _ => DirectoryVerdictKind.Uniform;
        var jobId = await ScanAsync(trip);
        await Phase1Async(jobId);
        var dirId = await DirIdAsync(jobId, trip);

        await Sampling().PlanAsync(jobId);
        var first = (await _sampleRepo.GetByNodeIdAsync(dirId))!;
        Assert.Equal(185, (await _fileRepo.GetByDirectoryAsync(jobId, trip)).Count(f => f.Status == FileNodeStatus.Deferred));

        await Sampling().ForgetAsync(dirId);
        Assert.DoesNotContain(await _fileRepo.GetByDirectoryAsync(jobId, trip), f => f.Status == FileNodeStatus.Deferred);
        await Sampling().PlanAsync(jobId);
        var second = (await _sampleRepo.GetByNodeIdAsync(dirId))!;

        Assert.Equal(first.Seed, second.Seed);
        Assert.Equal(first.Members.Select(m => (m.FileNodeId, m.Role)), second.Members.Select(m => (m.FileNodeId, m.Role)));

        // Independent of listing order.
        var files = (await _fileRepo.GetByDirectoryAsync(jobId, trip)).ToList();
        var forward = DirectorySampler.Draw(files, first.Seed, 15, new HashSet<string>());
        files.Reverse();
        var backward = DirectorySampler.Draw(files, first.Seed, 15, new HashSet<string>());
        Assert.Equal(forward.Members.Select(m => m.FileNodeId).Order(), backward.Members.Select(m => m.FileNodeId).Order());
    }

    // ------------------------------------------------------------------ the other ways

    [Fact]
    public async Task OneOutlierInFifteen_IsTolerated_AndKeepsItsOwnResult()
    {
        var trip = WritePhotos(MakeTree("Ausreisser"), 100);
        _ollama.Answer = _ => DirectoryVerdictKind.Uniform;
        var jobId = await ScanAsync(trip);
        await Phase1Async(jobId);
        await Sampling().PlanAsync(jobId);
        var dirId = await DirIdAsync(jobId, trip);
        var odd = (await _sampleRepo.GetByNodeIdAsync(dirId))!.Members[3].Name;

        _ollama.Classify = input => input.File.Name == odd
            ? new FileClassification { Category = FileCategory.Screenshot, Importance = Importance.Low, Confidence = 0.8 }
            : Photo();
        await Classification().RunAsync(jobId);

        var sample = (await _sampleRepo.GetByNodeIdAsync(dirId))!;
        Assert.Equal(SampleState.Confirmed, sample.State);
        Assert.Equal(1, sample.DeviatingCount);
        Assert.Equal(85, sample.InheritedCount);
        var files = await _fileRepo.GetByDirectoryAsync(jobId, trip);
        Assert.Equal(FileCategory.Screenshot, files.Single(f => f.Name == odd).Classification!.Category);
        Assert.Equal(DirectoryVerdictKind.Uniform, (await _verdictRepo.GetByNodeIdAsync(dirId))!.Verdict);
    }

    [Fact]
    public async Task SensitiveFileInUniformDirectory_IsAlwaysJudged_AndNeverInherits()
    {
        var trip = WritePhotos(MakeTree("Scans"), 60);
        WriteFile(Path.Combine(trip, "reisepass_scan.jpg"), 70);

        var jobId = await RunAllAsync(trip, _ => DirectoryVerdictKind.Uniform);
        var dirId = await DirIdAsync(jobId, trip);

        var sample = (await _sampleRepo.GetByNodeIdAsync(dirId))!;
        var sensitive = Assert.Single(sample.Members, m => m.Role == SampleRole.Sensitive);
        Assert.Equal("reisepass_scan.jpg", sensitive.Name);
        Assert.Equal(16, sample.Members.Count); // on top of the sample size
        Assert.Contains("reisepass_scan.jpg", _ollama.ClassifiedNames);

        var file = (await _fileRepo.GetByDirectoryAsync(jobId, trip)).Single(f => f.Name == "reisepass_scan.jpg");
        Assert.Equal(FileNodeStatus.AiAnalyzed, file.Status);
        Assert.Equal(ResultOrigin.Model, file.Classification!.Origin);
    }

    [Fact]
    public async Task MixedDirectory_JudgesEveryFile_WithKeepHint_AndSensitiveIsNeverTemporary()
    {
        var downloads = MakeTree("Downloads");
        foreach (var name in new[] { "rechnung.pdf", "foto.jpg", "clip.mp4", "notiz.txt", "passwoerter.txt" })
            WriteFile(Path.Combine(downloads, name));
        _ollama.Classify = _ => new FileClassification
        {
            Category = FileCategory.Document, Importance = Importance.Low, Confidence = 0.7,
            KeepHint = KeepHint.Temporary, KeepReason = "looks like a download remainder"
        };

        var jobId = await RunAllAsync(downloads, _ => DirectoryVerdictKind.Mixed);

        Assert.Equal(5, _ollama.FirstLooks.Count);
        Assert.All(_ollama.ClassifyInputs, i => Assert.True(i.AskKeepHint));
        // The scripted verdict says "keep": every temporary file contradicts it and is looked at again.
        Assert.Equal(4, _ollama.SecondLooks.Count);
        Assert.DoesNotContain(_ollama.SecondLooks, i => i.File.Name == "passwoerter.txt");
        Assert.Empty(await _sampleRepo.GetForJobAsync(jobId));

        var files = await _fileRepo.GetByDirectoryAsync(jobId, downloads);
        Assert.All(files.Where(f => f.Name != "passwoerter.txt"), f => Assert.Equal(KeepHint.Temporary, f.Classification!.KeepHint));
        var secret = files.Single(f => f.Name == "passwoerter.txt").Classification!;
        Assert.Equal(KeepHint.WorthKeeping, secret.KeepHint);
        Assert.DoesNotContain("download", secret.KeepReason);

        await using var conn = _db.CreateConnection();
        Assert.Equal(4, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM ai_results WHERE keep_hint = 'temporary'"));

        Assert.Single(_ollama.SummaryInputs); // mixed: the model summarizes
    }

    [Fact]
    public async Task UnitAndSystem_AnalyseNoFile_AndNeedNoSummaryCall()
    {
        var root = MakeTree("Platte");
        WriteFile(Path.Combine(root, "Programm", "tool.exe"));
        WriteFile(Path.Combine(root, "Programm", "tool.dll"));
        WriteFile(Path.Combine(root, "Programm", "handbuch.pdf"));
        WriteFile(Path.Combine(root, "Programm", "logo.png"));
        WriteFile(Path.Combine(root, "Cache", "a.jpg"));
        WriteFile(Path.Combine(root, "Cache", "b.jpg"));

        var jobId = await RunAllAsync(root, input => input.Directory.Name == "Cache" ? DirectoryVerdictKind.System : DirectoryVerdictKind.Mixed);

        Assert.Empty(_ollama.ClassifyInputs);
        foreach (var sub in new[] { "Programm", "Cache" })
            Assert.All(await _fileRepo.GetByDirectoryAsync(jobId, Path.Combine(root, sub)), f => Assert.Equal(FileNodeStatus.Skipped, f.Status));
        Assert.Empty(_ollama.SummaryInputs);
        Assert.Null((await _dirClassRepo.GetByDirectoryPathAsync(jobId, Path.Combine(root, "Programm")))?.Error);
    }

    [Fact]
    public async Task EmptyVerdictFromTheModel_ForADirectoryWithFiles_StillJudgesThem()
    {
        var odd = MakeTree("Unklar");
        WriteFile(Path.Combine(odd, "a.pdf"));
        WriteFile(Path.Combine(odd, "b.jpg"));

        var jobId = await RunAllAsync(odd, _ => DirectoryVerdictKind.Empty);

        Assert.Equal(2, _ollama.FirstLooks.Count);
        Assert.All(await _fileRepo.GetByDirectoryAsync(jobId, odd), f => Assert.NotNull(f.Classification));
        // "Photo" for a PDF breaks the kind rule twice: the PDF goes to review, the image is fine.
        Assert.Equal("a.pdf", Assert.Single(_ollama.SecondLooks).File.Name);
    }

    [Fact]
    public async Task SmallUniformDirectory_IsJudgedCompletely_AndStillRaisedWhenItDisagrees()
    {
        var small = MakeTree("Klein");
        for (var i = 1; i <= 8; i++) WriteFile(Path.Combine(small, $"doc{i}.pdf"));
        _ollama.Classify = input => new FileClassification
        {
            Category = input.File.Name.CompareTo("doc5") < 0 ? FileCategory.Invoice : FileCategory.Letter,
            Importance = Importance.Medium,
            Confidence = 0.9
        };

        var jobId = await RunAllAsync(small, _ => DirectoryVerdictKind.Uniform);
        var dirId = await DirIdAsync(jobId, small);

        Assert.Equal(8, _ollama.ClassifyInputs.Count);
        Assert.Equal(DirectoryVerdictKind.Mixed, (await _verdictRepo.GetByNodeIdAsync(dirId))!.Verdict);
        Assert.Equal(SampleState.Escalated, (await _sampleRepo.GetByNodeIdAsync(dirId))!.State);
        Assert.Equal(8, _ollama.ClassifyInputs.Count); // nothing judged twice after the escalation
    }

    [Fact]
    public async Task CancelledRun_LeavesNoDirectoryHalfInherited_AndResumes()
    {
        var trip = WritePhotos(MakeTree("Abbruch"), 300);
        _ollama.Answer = _ => DirectoryVerdictKind.Uniform;
        var jobId = await ScanAsync(trip);
        await Phase1Async(jobId);
        var dirId = await DirIdAsync(jobId, trip);

        using var cts = new CancellationTokenSource();
        var calls = 0;
        _ollama.BeforeClassify = _ => { if (Interlocked.Increment(ref calls) == 6) cts.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Classification().RunAsync(jobId, ct: cts.Token));

        var afterCancel = await _fileRepo.GetByDirectoryAsync(jobId, trip);
        Assert.DoesNotContain(afterCancel, f => f.Status == FileNodeStatus.Inherited);
        Assert.Equal(285, afterCancel.Count(f => f.Status == FileNodeStatus.Deferred));
        Assert.Equal(SampleState.Open, (await _sampleRepo.GetByNodeIdAsync(dirId))!.State);
        await using (var conn = _db.CreateConnection())
            Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM ai_results WHERE origin = 'inherited'"));

        _ollama.BeforeClassify = null;
        var membersBefore = (await _sampleRepo.GetByNodeIdAsync(dirId))!.Members.Select(m => m.FileNodeId).Order().ToList();
        await Classification().RunAsync(jobId);

        var sample = (await _sampleRepo.GetByNodeIdAsync(dirId))!;
        Assert.Equal(SampleState.Confirmed, sample.State);
        Assert.Equal(membersBefore, sample.Members.Select(m => m.FileNodeId).Order());
        Assert.Equal(15, _ollama.ClassifyInputs.Count); // each member once over both runs
        Assert.All(await _fileRepo.GetByDirectoryAsync(jobId, trip), f => Assert.NotNull(f.Classification));
    }

    [Fact]
    public async Task FocusedRescanOfAUniformDirectory_DrawsAFreshSample_AndInherits()
    {
        var trip = WritePhotos(MakeTree("Nachgescannt"), 80);
        var jobId = await RunAllAsync(trip, _ => DirectoryVerdictKind.Uniform);
        _ollama.ClassifyInputs.Clear();

        var rescan = new FocusedRescanService(
            new ScopedRefreshService(_jobRepo, _fileRepo, _dirRepo, new FileSystemAccessor()),
            _fileRepo, _dirRepo, new UserOverrideRepository(_db),
            new DirectoryPreAssessmentService(_dirRepo, _fileRepo, _dirClassRepo, _verdictRepo, _ollama, () => 4, _options,
                NullLogger<DirectoryPreAssessmentService>.Instance),
            Classification(),
            new DirectorySummaryService(_dirRepo, _fileRepo, _dirClassRepo, _classRepo, _verdictRepo, _sampleRepo, _ollama, () => 4,
                NullLogger<DirectorySummaryService>.Instance),
            Sampling(), _verdictRepo, NullLogger<FocusedRescanService>.Instance);
        await rescan.RescanDirectoryAsync(jobId, trip);

        Assert.Equal(15, _ollama.ClassifyInputs.Count);
        var files = await _fileRepo.GetByDirectoryAsync(jobId, trip);
        Assert.Equal(65, files.Count(f => f.Status == FileNodeStatus.Inherited));
        Assert.All(files, f => Assert.NotNull(f.Classification));
        Assert.Equal(SampleState.Confirmed, (await _sampleRepo.GetByNodeIdAsync(await DirIdAsync(jobId, trip)))!.State);
    }

    // ------------------------------------------------------------------ prompt and parser

    [Fact]
    public void KeepHint_IsInTheSchemaOnlyWhenAsked_AndParsedFromTheAnswer()
    {
        var with = System.Text.Json.JsonSerializer.Serialize(Infrastructure.Ollama.OllamaService.BuildClassificationSchema(true));
        var without = System.Text.Json.JsonSerializer.Serialize(Infrastructure.Ollama.OllamaService.BuildClassificationSchema(false));
        Assert.Contains("\"keep_hint\"", with);
        Assert.Contains("worth_keeping", with);
        Assert.DoesNotContain("keep_hint", without);

        static string Answer(string inner) =>
            System.Text.Json.JsonSerializer.Serialize(new { message = new { content = inner } });

        var parsed = new FileClassification();
        Infrastructure.Ollama.OllamaService.ParseResponse(Answer(
            """{"category":"SoftwareInstaller","importance":"Low","confidence":0.8,"summary":"s","suggested_target":"","sensitive":[],"keep_hint":"temporary","keep_reason":"installer"}"""),
            parsed);
        Assert.Equal(KeepHint.Temporary, parsed.KeepHint);
        Assert.Equal("installer", parsed.KeepReason);

        var unknown = new FileClassification();
        Infrastructure.Ollama.OllamaService.ParseResponse(Answer("""{"category":"Photo","keep_hint":"maybe","keep_reason":"x"}"""), unknown);
        Assert.Null(unknown.KeepHint);
        Assert.Null(unknown.KeepReason);
    }

    // ------------------------------------------------------------------ evaluation rules

    private static (List<SampleMember> Members, Dictionary<long, FileClassification?> Results) Sample(
        params (FileCategory? Category, SampleRole Role, SensitiveMarker Markers)[] items)
    {
        var members = new List<SampleMember>();
        var results = new Dictionary<long, FileClassification?>();
        for (var i = 0; i < items.Length; i++)
        {
            members.Add(new SampleMember { FileNodeId = i + 1, Name = $"f{i}.jpg", Role = items[i].Role });
            results[i + 1] = items[i].Category is { } c
                ? new FileClassification { Category = c, Confidence = 0.95, Importance = Importance.High, SensitiveMarkers = items[i].Markers }
                : new FileClassification { Error = "timeout" };
        }
        return (members, results);
    }

    private static (FileCategory?, SampleRole, SensitiveMarker) M(FileCategory? c, SampleRole r = SampleRole.Stratum,
        SensitiveMarker s = SensitiveMarker.None) => (c, r, s);

    [Fact]
    public void Evaluate_TieWithoutMajority_Escalates()
    {
        var (members, results) = Sample(M(FileCategory.Photo), M(FileCategory.Photo), M(FileCategory.Invoice), M(FileCategory.Invoice));
        var e = DirectorySampler.Evaluate(members, results, new DirectoryAnalysisOptions { MaxDeviationShare = 0.9 });
        Assert.False(e.Confirmed);
        Assert.Contains("No clear majority", e.Reason);
    }

    [Fact]
    public void Evaluate_MajorityReviewNeeded_Escalates()
    {
        var (members, results) = Sample(M(FileCategory.ReviewNeeded), M(FileCategory.ReviewNeeded), M(FileCategory.ReviewNeeded));
        Assert.False(DirectorySampler.Evaluate(members, results, new DirectoryAnalysisOptions()).Confirmed);
    }

    [Fact]
    public void Evaluate_FailedMembersCountAsDeviating()
    {
        var items = Enumerable.Repeat(M(FileCategory.Photo), 13).Append(M(null)).Append(M(null)).ToArray();
        var (members, results) = Sample(items);
        var e = DirectorySampler.Evaluate(members, results, new DirectoryAnalysisOptions());
        Assert.False(e.Confirmed); // 2 of 15 = 13 % > 10 %
        Assert.Equal(2, e.Failed);
        Assert.All(members.Where(m => m.Category is null), m => Assert.Equal("timeout", m.Error));
    }

    [Fact]
    public void Evaluate_ModelFindsSensitiveContentInARandomMember_Escalates()
    {
        var items = Enumerable.Repeat(M(FileCategory.Photo), 14)
            .Append(M(FileCategory.Photo, SampleRole.Stratum, SensitiveMarker.Identity)).ToArray();
        var (members, results) = Sample(items);
        var e = DirectorySampler.Evaluate(members, results, new DirectoryAnalysisOptions());
        Assert.False(e.Confirmed);
        Assert.Contains("sensitive", e.Reason);

        // A member drawn because the rules marked it is expected to be sensitive.
        members[^1].Role = SampleRole.Sensitive;
        Assert.True(DirectorySampler.Evaluate(members, results, new DirectoryAnalysisOptions()).Confirmed);
    }

    /// <summary>
    /// The target a model suggests for a marked file is model text about that file's content. Unmarked files
    /// inherit the target of the sample, so a marked member must not take part in choosing it.
    /// </summary>
    [Fact]
    public void Evaluate_InheritedTarget_IsNotTakenFromAMarkedMember()
    {
        var (members, results) = Sample(
            M(FileCategory.Invoice, SampleRole.Sensitive, SensitiveMarker.Finance),
            M(FileCategory.Invoice, SampleRole.Sensitive, SensitiveMarker.Finance),
            M(FileCategory.Invoice));
        results[1]!.SuggestedTarget = "Finance/Account-DE00-FAKE";
        results[2]!.SuggestedTarget = "Finance/Account-DE00-FAKE";
        results[3]!.SuggestedTarget = "Documents/Invoices";

        var e = DirectorySampler.Evaluate(members, results, new DirectoryAnalysisOptions());

        Assert.True(e.Confirmed);
        Assert.Equal("Documents/Invoices", e.InheritedTarget);

        // Only marked members: no target to inherit.
        results[3]!.SuggestedTarget = null;
        Assert.Null(DirectorySampler.Evaluate(members, results, new DirectoryAnalysisOptions()).InheritedTarget);
    }

    [Fact]
    public void Evaluate_InheritedConfidenceIsCapped()
    {
        var (members, results) = Sample(M(FileCategory.Photo), M(FileCategory.Photo), M(FileCategory.Photo));
        var e = DirectorySampler.Evaluate(members, results, new DirectoryAnalysisOptions { InheritedConfidenceCap = 0.6 });
        Assert.True(e.Confirmed);
        Assert.Equal(0.6, e.InheritedConfidence);
    }

    [Fact]
    public void Draw_TooManyExtensionGroups_IsNotSampleable()
    {
        var files = Enumerable.Range(0, 40).Select(i => new FileNode
        {
            Id = i + 1, Name = $"f{i}.e{i % 20}", Extension = $".e{i % 20}", FileType = FileType.Document, Size = 10 + i
        }).ToList();
        var draw = DirectorySampler.Draw(files, "seed", 15, new HashSet<string>());
        Assert.NotNull(draw.NotSampleableReason);
    }

    [Fact]
    public void Draw_CoversEveryExtensionGroup_AndModelAnomalies()
    {
        var files = Enumerable.Range(0, 200).Select(i => new FileNode
        {
            Id = i + 1,
            Name = i < 180 ? $"IMG_{i:000}.jpg" : i < 195 ? $"VID_{i:000}.mp4" : $"note{i}.pdf",
            Extension = i < 180 ? ".jpg" : i < 195 ? ".mp4" : ".pdf",
            FileType = FileType.Image,
            Size = 1000 + i,
            LastWriteTime = new DateTime(2020, 1, 1).AddDays(i)
        }).ToList();
        var draw = DirectorySampler.Draw(files, "seed", 15, new HashSet<string>(["IMG_050.jpg"]));

        Assert.Null(draw.NotSampleableReason);
        Assert.Equal(15, draw.Members.Count);
        Assert.Equal(185, draw.DeferredCount);
        var names = draw.Members.Select(m => m.Name).ToList();
        Assert.Contains(names, n => n.EndsWith(".mp4"));
        Assert.Contains(names, n => n.EndsWith(".pdf"));
        Assert.Contains(draw.Members, m => m.Name == "IMG_050.jpg" && m.Role == SampleRole.Anomaly);
        Assert.True(names.Count(n => n.EndsWith(".jpg")) > names.Count(n => n.EndsWith(".mp4")));
    }
}
