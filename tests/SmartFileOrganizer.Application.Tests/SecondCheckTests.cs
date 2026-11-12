using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Ollama;
using SmartFileOrganizer.Infrastructure.Persistence;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Second check of uncertain results and retries of failed files
/// (https://github.com/crack666/smart_file_organizer/issues/23), with a temp database and a scripted model.
/// </summary>
public class SecondCheckTests : IDisposable
{
    private readonly string _dir;
    private readonly DatabaseContext _db;
    private readonly FileRepository _fileRepo;
    private readonly ClassificationRepository _classRepo;
    private readonly DirectoryRepository _dirRepo;
    private readonly DirectoryVerdictRepository _verdictRepo;
    private readonly UserOverrideRepository _overrideRepo;
    private readonly ScriptedOllamaService _ollama = new();
    private readonly SecondCheckOptions _options = new();
    private readonly FactPreparer _preparer = new();

    public SecondCheckTests()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_check_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _db = new DatabaseContext(Path.Combine(_dir, "data.db"), NullLogger<DatabaseContext>.Instance);
        _db.InitializeAsync().GetAwaiter().GetResult();
        _fileRepo = new FileRepository(_db);
        _classRepo = new ClassificationRepository(_db);
        _dirRepo = new DirectoryRepository(_db);
        _verdictRepo = new DirectoryVerdictRepository(_db);
        _overrideRepo = new UserOverrideRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ helpers

    private ClassificationService Service() => new(
        _fileRepo, _classRepo, new FileFactRepository(_db), _preparer, _ollama,
        new DirectorySamplingService(_fileRepo, _verdictRepo, new DirectorySampleRepository(_db),
            new DirectoryClassificationRepository(_db), new DirectoryAnalysisOptions(), NullLogger<DirectorySamplingService>.Instance),
        () => 2, NullLogger<ClassificationService>.Instance, _options, _overrideRepo);

    private static FileClassification Answer(FileCategory category, double confidence = 0.9, Importance importance = Importance.Medium) => new()
    {
        Category = category,
        Importance = importance,
        Confidence = confidence,
        Summary = $"looks like {category}",
        SuggestedTarget = $"Target/{category}"
    };

    private async Task<long> InsertAsync(params (string Name, FileType Type)[] files)
    {
        var job = await new ScanJobRepository(_db).CreateAsync(new ScanJob { RootPath = _dir, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow });
        await _fileRepo.InsertBatchAsync(files.Select(f => new FileNode
        {
            JobId = job.Id,
            FullPath = Path.Combine(_dir, f.Name),
            Name = f.Name,
            ParentPath = _dir,
            RootPath = _dir,
            RelativePath = f.Name,
            Extension = Path.GetExtension(f.Name).ToLowerInvariant(),
            FileType = f.Type,
            Size = 100,
            LastWriteTime = DateTime.UtcNow,
            ScannedAt = DateTime.UtcNow
        }).ToList());
        return job.Id;
    }

    private async Task SetVerdictAsync(long jobId, RetentionValue retention)
    {
        await _dirRepo.InsertBatchAsync([new DirectoryNode
        {
            JobId = jobId, FullPath = _dir, Name = Path.GetFileName(_dir), ParentPath = Path.GetDirectoryName(_dir)!,
            RootPath = _dir, RelativePath = string.Empty, DirStatus = DirectoryStatus.Scanned
        }]);
        var node = (await _dirRepo.GetByPathAsync(jobId, _dir))!;
        await _verdictRepo.UpsertAsync(new DirectoryVerdict
        {
            DirectoryNodeId = node.Id, Verdict = DirectoryVerdictKind.Gemischt, Source = VerdictSource.Modell,
            Purpose = "downloads of a year", Retention = retention, Reason = "scripted", JudgedAt = DateTime.UtcNow
        });
    }

    private async Task<FileNode> FileAsync(long jobId, string name) =>
        (await _fileRepo.GetByDirectoryAsync(jobId, _dir)).Single(f => f.Name == name);

    private async Task<IReadOnlyList<ResultCheck>> ChecksAsync(long jobId, string name) =>
        await _classRepo.GetChecksAsync((await FileAsync(jobId, name)).Id);

    /// <summary>Evidence without reading files: facts per file name, depth passed through.</summary>
    private sealed class FactPreparer : IClassificationInputPreparer
    {
        public Dictionary<string, IReadOnlyList<FileFact>> Facts { get; } = [];

        public Task<OllamaClassificationInput> PrepareAsync(FileNode file, CancellationToken ct = default) =>
            PrepareAsync(file, EvidenceDepth.Standard, ct);

        public Task<OllamaClassificationInput> PrepareAsync(FileNode file, EvidenceDepth depth, CancellationToken ct = default) =>
            Task.FromResult(new OllamaClassificationInput
            {
                File = file,
                Depth = depth,
                Facts = Facts.GetValueOrDefault(file.Name) ?? [],
                Contains = [depth == EvidenceDepth.Extended ? "extended evidence" : "standard evidence"]
            });
    }

    // ------------------------------------------------------------------ Fertig, wenn (1)

    [Fact]
    public async Task TwoAgreeingAnswers_GiveCertain_TwoDifferentGiveReview_WithBothAnswers()
    {
        var jobId = await InsertAsync(("agree.jpg", FileType.Image), ("differ.jpg", FileType.Image));
        _ollama.Classify = input => (input.File.Name, input.Depth) switch
        {
            // First look: unsure about both.
            (_, EvidenceDepth.Standard) => Answer(FileCategory.Photo, 0.5),
            ("agree.jpg", _) => Answer(FileCategory.Photo, 0.8),
            _ => Answer(FileCategory.DocumentScan, 0.8, Importance.High)
        };

        var run = await Service().RunAsync(jobId);

        Assert.Equal(2, _ollama.FirstLooks.Count);
        Assert.Equal(2, _ollama.SecondLooks.Count);
        Assert.Equal(2, run.SecondChecks);

        var agree = (await FileAsync(jobId, "agree.jpg")).Classification!;
        Assert.Equal(ResultCertainty.Certain, agree.Certainty);
        Assert.Equal(FileCategory.Photo, agree.Category);
        Assert.Equal(0.9, agree.Confidence, 3); // 1 - 0.5 * 0.2: higher than either answer
        var agreeCheck = Assert.Single(await ChecksAsync(jobId, "agree.jpg"));
        Assert.Equal(CheckOutcome.Agreed, agreeCheck.Outcome);
        Assert.Equal([CheckReason.LowConfidence], agreeCheck.Reasons);

        var differ = (await FileAsync(jobId, "differ.jpg")).Classification!;
        Assert.Equal(ResultCertainty.Review, differ.Certainty);
        Assert.Equal(FileCategory.ReviewNeeded, differ.Category);
        Assert.Equal(Importance.High, differ.Importance); // never lose the higher importance
        Assert.Null(differ.SuggestedTarget);
        Assert.Contains("Photo", differ.Summary);
        Assert.Contains("DocumentScan", differ.Summary);

        // Both answers kept as they came.
        var check = Assert.Single(await ChecksAsync(jobId, "differ.jpg"));
        Assert.Equal(CheckOutcome.Disagreed, check.Outcome);
        Assert.Equal(FileCategory.Photo, check.First.Category);
        Assert.Equal(0.5, check.First.Confidence);
        Assert.Equal("Target/Photo", check.First.SuggestedTarget);
        Assert.Equal(FileCategory.DocumentScan, check.Second!.Category);
        Assert.Equal("looks like DocumentScan", check.Second.Summary);
        Assert.Contains("extended evidence", check.Second.Evidence);

        // Stored as English codes.
        await using var conn = _db.CreateConnection();
        Assert.Equal(["certain", "review"], (await conn.QueryAsync<string>("SELECT certainty FROM ai_results ORDER BY certainty")).ToList());
        Assert.Equal(["agreed", "disagreed"], (await conn.QueryAsync<string>("SELECT outcome FROM result_checks ORDER BY outcome")).ToList());
    }

    [Fact]
    public async Task SecondCall_GetsMoreEvidenceAndTheDirectory_ButNeverTheFirstAnswer()
    {
        var jobId = await InsertAsync(("unsure.jpg", FileType.Image), ("neighbour.jpg", FileType.Image));
        await SetVerdictAsync(jobId, RetentionValue.Pruefen);
        _ollama.Classify = input => input.File.Name == "unsure.jpg" && input.Depth == EvidenceDepth.Standard
            ? Answer(FileCategory.Screenshot, 0.3)
            : Answer(FileCategory.Photo);

        await Service().RunAsync(jobId);

        var second = Assert.Single(_ollama.SecondLooks);
        Assert.Equal("unsure.jpg", second.File.Name);
        Assert.Null(_ollama.FirstLooks.Single(i => i.File.Name == "unsure.jpg").Context);
        var context = second.Context!;
        Assert.Equal("gemischt", context.Verdict);
        Assert.Equal("downloads of a year", context.Purpose);
        Assert.Equal("pruefen", context.Retention);
        Assert.Equal(2, context.FilesInDirectory);
        Assert.DoesNotContain(context.Neighbours, n => n.Name == "unsure.jpg");

        var prompt = OllamaService.BuildClassificationPrompt(second);
        Assert.Contains("downloads of a year", prompt);
        Assert.Contains("neighbour.jpg", prompt);
        Assert.DoesNotContain("Screenshot", prompt[prompt.IndexOf("Context:", StringComparison.Ordinal)..prompt.IndexOf("Classification guidance", StringComparison.Ordinal)]);
        Assert.DoesNotContain("looks like Screenshot", prompt);
    }

    [Fact]
    public async Task SureAnswerWithoutContradiction_IsNotCheckedAgain()
    {
        var jobId = await InsertAsync(("a.jpg", FileType.Image), ("b.pdf", FileType.Document));
        _ollama.Classify = input => input.File.Name == "a.jpg" ? Answer(FileCategory.Photo) : Answer(FileCategory.Invoice);

        await Service().RunAsync(jobId);

        Assert.Empty(_ollama.SecondLooks);
        Assert.Equal(ResultCertainty.Likely, (await FileAsync(jobId, "a.jpg")).Classification!.Certainty);
        await using var conn = _db.CreateConnection();
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM result_checks"));
    }

    [Fact]
    public async Task MissingConfidence_IsNotAssumed_AndLeadsToASecondCheck()
    {
        var parsed = new FileClassification();
        OllamaService.ParseResponse("""{"message":{"content":"{\"category\":\"Photo\",\"importance\":\"High\"}"}}""", parsed);
        Assert.Equal(FileCategory.Photo, parsed.Category);
        Assert.Equal(0, parsed.Confidence);
        Assert.True(parsed.ConfidenceMissing);

        var stated = new FileClassification();
        OllamaService.ParseResponse("""{"message":{"content":"{\"category\":\"Photo\",\"confidence\":0.42}"}}""", stated);
        Assert.False(stated.ConfidenceMissing);
        Assert.Equal(0.42, stated.Confidence);

        var jobId = await InsertAsync(("x.jpg", FileType.Image));
        _ollama.Classify = input =>
        {
            var a = Answer(FileCategory.Photo);
            if (input.Depth == EvidenceDepth.Standard) { a.Confidence = 0; a.ConfidenceMissing = true; }
            return a;
        };
        await Service().RunAsync(jobId);

        var check = Assert.Single(await ChecksAsync(jobId, "x.jpg"));
        Assert.Equal([CheckReason.MissingConfidence], check.Reasons);
        Assert.Null(check.First.Confidence);
        Assert.Equal(ResultCertainty.Certain, (await FileAsync(jobId, "x.jpg")).Classification!.Certainty);
    }

    [Fact]
    public async Task SecondCheckSwitchedOff_LeavesTheResultUncertain()
    {
        _options.Enabled = false;
        var jobId = await InsertAsync(("x.jpg", FileType.Image));
        _ollama.Classify = _ => Answer(FileCategory.Photo, 0.4);

        await Service().RunAsync(jobId);

        Assert.Empty(_ollama.SecondLooks);
        Assert.Equal(ResultCertainty.Uncertain, (await FileAsync(jobId, "x.jpg")).Classification!.Certainty);
        Assert.Equal(CheckOutcome.Skipped, Assert.Single(await ChecksAsync(jobId, "x.jpg")).Outcome);
    }

    [Fact]
    public async Task FailedSecondCall_KeepsTheFirstAnswer_AsUncertain()
    {
        var jobId = await InsertAsync(("x.jpg", FileType.Image));
        _ollama.Classify = input => input.Depth == EvidenceDepth.Standard
            ? Answer(FileCategory.Photo, 0.4)
            : new FileClassification { Error = "Ollama request timed out after 180s." };

        await Service().RunAsync(jobId);

        var file = await FileAsync(jobId, "x.jpg");
        Assert.Equal(FileNodeStatus.AiAnalyzed, file.Status);
        Assert.Equal(FileCategory.Photo, file.Classification!.Category);
        Assert.Null(file.Classification.Error);
        Assert.Equal(ResultCertainty.Uncertain, file.Classification.Certainty);
        var check = Assert.Single(await ChecksAsync(jobId, "x.jpg"));
        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Equal("Ollama request timed out after 180s.", check.Second!.Error);
    }

    // ------------------------------------------------------------------ rules and verdict

    [Fact]
    public async Task UnambiguousRule_BeatsTheModel_WithoutASecondCall_AndTheContradictionIsStored()
    {
        var jobId = await InsertAsync(("Screenshot_2021-03-04.png", FileType.Image), ("IMG_0001.jpg", FileType.Image));
        _preparer.Facts["Screenshot_2021-03-04.png"] = [new FileFact(FileFactKey.ImageOrigin, "screenshot", FactSource.Rule)];
        _preparer.Facts["IMG_0001.jpg"] =
        [
            new FileFact(FileFactKey.ImageOrigin, "camera", FactSource.Rule),
            new FileFact(FileFactKey.Device, "Phone X", FactSource.Metadata)
        ];
        _ollama.Classify = input => input.File.Name.StartsWith("Screenshot", StringComparison.Ordinal)
            ? Answer(FileCategory.Photo)
            : Answer(FileCategory.Photo);

        await Service().RunAsync(jobId);

        Assert.Empty(_ollama.SecondLooks);
        var shot = (await FileAsync(jobId, "Screenshot_2021-03-04.png")).Classification!;
        Assert.Equal(FileCategory.Screenshot, shot.Category);
        Assert.Equal(ResultCertainty.Likely, shot.Certainty);
        var check = Assert.Single(await ChecksAsync(jobId, "Screenshot_2021-03-04.png"));
        Assert.Equal(CheckOutcome.RuleWins, check.Outcome);
        Assert.Equal([CheckReason.RuleDisagrees], check.Reasons);
        Assert.Equal(FileCategory.Photo, check.First.Category);
        Assert.Contains("image_origin=screenshot", check.RuleNote);

        // Rule and model agree: certain without a second call.
        Assert.Equal(ResultCertainty.Certain, (await FileAsync(jobId, "IMG_0001.jpg")).Classification!.Certainty);
    }

    [Fact]
    public async Task CategoryThatTheFileCannotBe_IsCheckedAgain_AndTheFittingSecondAnswerStands()
    {
        var jobId = await InsertAsync(("holiday.jpg", FileType.Image), ("clip.mp4", FileType.Video));
        _preparer.Facts["holiday.jpg"] = [new FileFact(FileFactKey.DetectedFormat, "jpeg")];
        // HEIC and MOV also start with "ftyp": "mp4" alone does not make a video a non-image or the reverse.
        _preparer.Facts["clip.mp4"] = [new FileFact(FileFactKey.DetectedFormat, "mp4")];
        _ollama.Classify = input => (input.File.Name, input.Depth) switch
        {
            ("holiday.jpg", EvidenceDepth.Standard) => Answer(FileCategory.Video),
            ("holiday.jpg", _) => Answer(FileCategory.Photo),
            _ => Answer(FileCategory.Video)
        };

        await Service().RunAsync(jobId);

        Assert.Equal("holiday.jpg", Assert.Single(_ollama.SecondLooks).File.Name);
        var photo = (await FileAsync(jobId, "holiday.jpg")).Classification!;
        Assert.Equal(FileCategory.Photo, photo.Category);
        Assert.Equal(ResultCertainty.Likely, photo.Certainty);
        var check = Assert.Single(await ChecksAsync(jobId, "holiday.jpg"));
        Assert.Equal(CheckOutcome.Corrected, check.Outcome);
        Assert.Equal([CheckReason.KindMismatch], check.Reasons);
        Assert.Contains("an image file", check.RuleNote);
    }

    [Fact]
    public async Task ValuableFileInDisposableDirectory_IsCheckedAgain_AndConfirmedAsCertain()
    {
        var jobId = await InsertAsync(("contract.pdf", FileType.Document), ("setup-notes.txt", FileType.Code));
        await SetVerdictAsync(jobId, RetentionValue.Entbehrlich);
        _ollama.Classify = input => input.File.Name == "contract.pdf"
            ? Answer(FileCategory.PersonalDocument, 0.9, Importance.Critical)
            : Answer(FileCategory.Document, 0.9, Importance.Low);

        await Service().RunAsync(jobId);

        Assert.Equal("contract.pdf", Assert.Single(_ollama.SecondLooks).File.Name);
        var check = Assert.Single(await ChecksAsync(jobId, "contract.pdf"));
        Assert.Equal([CheckReason.VerdictDisagrees], check.Reasons);
        Assert.Equal(CheckOutcome.Agreed, check.Outcome);
        Assert.Equal(ResultCertainty.Certain, (await FileAsync(jobId, "contract.pdf")).Classification!.Certainty);
    }

    [Fact]
    public async Task CorrectedByAPerson_IsNeverCheckedAgain()
    {
        var jobId = await InsertAsync(("x.jpg", FileType.Image));
        var file = await FileAsync(jobId, "x.jpg");
        await _overrideRepo.UpsertAsync(new UserOverride { FileNodeId = file.Id, OverriddenCategory = FileCategory.Photo, OverriddenAt = DateTime.UtcNow });
        _ollama.Classify = _ => Answer(FileCategory.Screenshot, 0.3);

        await Service().RunAsync(jobId);

        Assert.Empty(_ollama.SecondLooks);
        Assert.Equal(CheckOutcome.Skipped, Assert.Single(await ChecksAsync(jobId, "x.jpg")).Outcome);
    }

    [Fact]
    public async Task SensitiveFile_NeverBecomesTrash_AfterASecondCheck()
    {
        var job = await new ScanJobRepository(_db).CreateAsync(new ScanJob { RootPath = _dir, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow });
        await _fileRepo.InsertBatchAsync([new FileNode
        {
            JobId = job.Id, FullPath = Path.Combine(_dir, "passwoerter.txt"), Name = "passwoerter.txt", ParentPath = _dir,
            RootPath = _dir, RelativePath = "passwoerter.txt", Extension = ".txt", FileType = FileType.Document, Size = 10,
            LastWriteTime = DateTime.UtcNow, ScannedAt = DateTime.UtcNow,
            SensitiveMarkers = SensitiveMarker.Zugangsdaten, SensitiveRule = "test"
        }]);
        _ollama.Classify = _ => new FileClassification
        {
            Category = FileCategory.TrashCandidate, Importance = Importance.Low, Confidence = 0.4,
            KeepHint = KeepHint.Temporary, KeepReason = "old download"
        };

        await Service().RunAsync(job.Id);

        Assert.Single(_ollama.SecondLooks);
        var c = (await FileAsync(job.Id, "passwoerter.txt")).Classification!;
        Assert.Equal(FileCategory.ReviewNeeded, c.Category);
        Assert.Equal(KeepHint.WorthKeeping, c.KeepHint);
        Assert.DoesNotContain("download", c.KeepReason);
    }

    // ------------------------------------------------------------------ stop between the calls

    [Fact]
    public async Task StopBetweenFirstAndSecondCall_StoresNothing_AndTheResumeFinishesTheFile()
    {
        var jobId = await InsertAsync(("x.jpg", FileType.Image));
        using var cts = new CancellationTokenSource();
        _ollama.BeforeClassify = input => { if (input.Depth == EvidenceDepth.Extended) cts.Cancel(); };
        _ollama.Classify = _ => Answer(FileCategory.Photo, 0.4);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().RunAsync(jobId, ct: cts.Token));

        var stopped = await FileAsync(jobId, "x.jpg");
        Assert.Null(stopped.Classification);
        Assert.Empty(await ChecksAsync(jobId, "x.jpg"));
        Assert.NotEqual(FileNodeStatus.AiAnalyzed, stopped.Status);

        _ollama.BeforeClassify = null;
        await Service().RunAsync(jobId);

        var done = await FileAsync(jobId, "x.jpg");
        Assert.Equal(FileNodeStatus.AiAnalyzed, done.Status);
        Assert.Equal(ResultCertainty.Certain, done.Classification!.Certainty);
        Assert.Single(await ChecksAsync(jobId, "x.jpg"));
    }

    // ------------------------------------------------------------------ Fertig, wenn (2)

    [Fact]
    public async Task RetryErrors_WorksOnlyOnFailedFiles_AndCountsTheAttempts()
    {
        var jobId = await InsertAsync(("ok.jpg", FileType.Image), ("broken.jpg", FileType.Image), ("flaky.jpg", FileType.Image));
        var failing = new HashSet<string> { "broken.jpg", "flaky.jpg" };
        _ollama.Classify = input => failing.Contains(input.File.Name)
            ? new FileClassification { Error = $"HTTP 500 for {input.File.Name}" }
            : Answer(FileCategory.Photo);

        var first = await Service().RunAsync(jobId);
        Assert.Equal(2, first.ErrorCount);
        Assert.Equal(FileNodeStatus.Error, (await FileAsync(jobId, "broken.jpg")).Status);

        // A run without the option leaves failed files alone.
        _ollama.ClassifyInputs.Clear();
        await Service().RunAsync(jobId);
        Assert.Empty(_ollama.ClassifyInputs);

        // flaky.jpg works now, broken.jpg still fails.
        failing.Remove("flaky.jpg");
        var retry = await Service().RunAsync(jobId, retryErrors: true);

        Assert.Equal(2, retry.RetriedFiles);
        Assert.Equal(["broken.jpg", "flaky.jpg"], _ollama.ClassifiedNames);
        Assert.Equal(1, retry.ErrorCount);

        var ok = (await FileAsync(jobId, "ok.jpg")).Classification!;
        Assert.Equal(1, ok.Attempts);

        var flaky = await FileAsync(jobId, "flaky.jpg");
        Assert.Equal(FileNodeStatus.AiAnalyzed, flaky.Status);
        Assert.Equal(2, flaky.Classification!.Attempts);
        Assert.Null(flaky.Classification.Error);
        Assert.Equal("HTTP 500 for flaky.jpg", flaky.Classification.PreviousError);

        var broken = await FileAsync(jobId, "broken.jpg");
        Assert.Equal(FileNodeStatus.Error, broken.Status);
        Assert.Equal(2, broken.Classification!.Attempts);
        Assert.Equal("HTTP 500 for broken.jpg", broken.Classification.Error);

        // Once more: only the file that still fails.
        _ollama.ClassifyInputs.Clear();
        await Service().RunAsync(jobId, retryErrors: true);
        Assert.Equal(["broken.jpg"], _ollama.ClassifiedNames);
        Assert.Equal(3, (await FileAsync(jobId, "broken.jpg")).Classification!.Attempts);
    }

    [Fact]
    public async Task RetryErrors_LeavesFilesCorrectedByAPersonAlone()
    {
        var jobId = await InsertAsync(("broken.jpg", FileType.Image));
        _ollama.Classify = _ => new FileClassification { Error = "HTTP 500" };
        await Service().RunAsync(jobId);
        var file = await FileAsync(jobId, "broken.jpg");
        await _overrideRepo.UpsertAsync(new UserOverride { FileNodeId = file.Id, OverriddenCategory = FileCategory.Photo, OverriddenAt = DateTime.UtcNow });
        _ollama.ClassifyInputs.Clear();

        var retry = await Service().RunAsync(jobId, retryErrors: true);

        Assert.Equal(0, retry.RetriedFiles);
        Assert.Empty(_ollama.ClassifyInputs);
    }

    // ------------------------------------------------------------------ pure rules

    [Theory]
    [InlineData(0.5, false, 0.8, false, 0.9)]
    [InlineData(0.9, false, 0.9, false, 0.99)]
    [InlineData(0, true, 0.8, false, 0.9)]   // missing counts as 0.5
    [InlineData(0, true, 0, true, 0.75)]
    public void AgreeingConfidences_Rise(double c1, bool missing1, double c2, bool missing2, double expected)
    {
        var a = new FileClassification { Confidence = c1, ConfidenceMissing = missing1 };
        var b = new FileClassification { Confidence = c2, ConfidenceMissing = missing2 };
        Assert.Equal(expected, ResultCheckRules.Combined(a, b), 3);
    }

    [Theory]
    [InlineData(FileType.Image, "jpeg", FileCategory.Photo, false)]
    [InlineData(FileType.Image, "mp4", FileCategory.Photo, false)]   // HEIC: ftyp box, extension decides
    [InlineData(FileType.Image, "jpeg", FileCategory.Video, true)]
    [InlineData(FileType.Document, "pdf", FileCategory.Photo, true)]
    [InlineData(FileType.Document, "pdf", FileCategory.DocumentScan, false)]
    [InlineData(FileType.Other, "wav", FileCategory.Audio, false)]
    [InlineData(FileType.Audio, null, FileCategory.Audio, false)]
    public void KindRule(FileType type, string? format, FileCategory category, bool mismatch)
    {
        var file = new FileNode { Name = "f", FileType = type };
        IReadOnlyList<FileFact> facts = format is null ? [] : [new FileFact(FileFactKey.DetectedFormat, format)];
        Assert.Equal(mismatch, ResultCheckRules.KindMismatch(file, facts, category) is not null);
    }

    [Fact]
    public void Verdict_Pruefen_NeverContradicts_Keep_RejectsTrash()
    {
        var trash = new FileClassification { Category = FileCategory.TrashCandidate };
        Assert.Null(ResultCheckRules.VerdictContradiction(trash, new DirectoryVerdict { Retention = RetentionValue.Pruefen }));
        Assert.NotNull(ResultCheckRules.VerdictContradiction(trash, new DirectoryVerdict { Retention = RetentionValue.Behalten }));
        Assert.Null(ResultCheckRules.VerdictContradiction(trash, new DirectoryVerdict { Retention = RetentionValue.Behalten, Error = "x" }));
    }
}
