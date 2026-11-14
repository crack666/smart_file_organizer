using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure;
using SmartFileOrganizer.Infrastructure.Evaluation;
using SmartFileOrganizer.Infrastructure.Persistence;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// The evaluation pieces without the CLI: corpus generator and its truth, truth validation, codes,
/// and test cases from corrections. Temp folders and a temp database; no model.
/// </summary>
public class EvaluationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sfo_eval_{Guid.NewGuid():N}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    // ── Corpus and truth ─────────────────────────────────────────────────

    [Fact]
    public void Generator_TruthListsExactlyWhatIsOnDisk()
    {
        var corpus = SmallCorpusGenerator.Generate(Path.Combine(_dir, "c"));
        var truth = EvalTruth.Load(corpus.TruthPath);

        var onDiskDirs = Directory.EnumerateDirectories(corpus.RootPath, "*", SearchOption.AllDirectories)
            .Select(d => EvalTruth.NormalizePath(Path.GetRelativePath(corpus.RootPath, d)))
            .Append(".")
            .Order(StringComparer.Ordinal).ToList();
        var onDiskFiles = Directory.EnumerateFiles(corpus.RootPath, "*", SearchOption.AllDirectories)
            .Select(f => EvalTruth.NormalizePath(Path.GetRelativePath(corpus.RootPath, f)))
            .Order(StringComparer.Ordinal).ToList();

        Assert.Equal(onDiskDirs, truth.Directories.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(onDiskFiles, truth.Files.Keys.Order(StringComparer.Ordinal));
        Assert.InRange(truth.Directories.Count, 30, 80);
        Assert.Equal(corpus.Directories, truth.Directories.Count);
        Assert.Equal(corpus.Files, truth.Files.Count);
        Assert.Equal("small", truth.Corpus.Name);
        Assert.Equal(SmallCorpusGenerator.DefaultSeed, truth.Corpus.Seed);
    }

    [Fact]
    public void Generator_CoversTheFolderKindsAndMarkers()
    {
        var truth = EvalTruth.Load(SmallCorpusGenerator.Generate(Path.Combine(_dir, "c")).TruthPath);

        // Every verdict and every marker is present at least once, so each can be measured.
        foreach (var verdict in Enum.GetValues<DirectoryVerdictKind>())
            Assert.Contains(truth.Directories.Values, d => d.Verdict == verdict);
        foreach (var marker in Enum.GetValues<SensitiveMarker>().Where(m => m != SensitiveMarker.None))
            Assert.Contains(truth.Files.Values, f => (f.Sensitive & marker) != 0);
        Assert.Contains(truth.Files.Values, f => f.Category == FileCategory.Photo);
        Assert.Contains(truth.Files.Values, f => f.Category == FileCategory.Invoice);
        Assert.Contains(truth.Files.Values, f => f.Category is null);
    }

    /// <summary>
    /// The counter-samples of version 2 (issue 50) only measure the model if no name rule marks them first:
    /// a bank statement and a payslip that must stay finance, a personal letter that must stay private_message.
    /// </summary>
    [Theory]
    [InlineData("Dokumente/Bank/Umsaetze_Girokonto_2024_05.pdf", SensitiveMarker.Finance, FileCategory.PersonalDocument)]
    [InlineData("Dokumente/Bank/Verdienstnachweis_2023_05.pdf", SensitiveMarker.Finance, FileCategory.PersonalDocument)]
    [InlineData("Desktop/Brief_an_Lisa.txt", SensitiveMarker.PrivateMessage, FileCategory.Letter)]
    public void Generator_CounterSamples_AreLeftToTheModel(string path, SensitiveMarker marker, FileCategory category)
    {
        var truth = EvalTruth.Load(SmallCorpusGenerator.Generate(Path.Combine(_dir, "c")).TruthPath);

        var file = truth.Files[path];
        Assert.Equal(marker, file.Sensitive);
        Assert.Equal(category, file.Category);
        Assert.Equal(SensitiveMarker.None, SensitiveFileRules.MatchName(Path.GetFileName(path), EvalTruth.ParentOf(path)).Markers);
    }

    [Fact]
    public void Generator_IsReproducible_ForTheSameSeed_AndDiffersForAnother()
    {
        var a = SmallCorpusGenerator.Generate(Path.Combine(_dir, "a"));
        var b = SmallCorpusGenerator.Generate(Path.Combine(_dir, "b"));
        var c = SmallCorpusGenerator.Generate(Path.Combine(_dir, "c"), seed: 7);

        Assert.Equal(Fingerprint(a.Directory), Fingerprint(b.Directory));
        Assert.NotEqual(Fingerprint(a.Directory), Fingerprint(c.Directory));
    }

    /// <summary>
    /// Issue 56: the library of the corpus lies next to the tree, every file it describes exists there, every expected
    /// place is a folder of it, and the traps are in: two occasions of one day, and occasions without a folder.
    /// </summary>
    [Fact]
    public void Generator_WritesALibraryWithKnownTargets_NextToTheTree()
    {
        var corpus = SmallCorpusGenerator.Generate(Path.Combine(_dir, "t"));
        var targets = EvalTargetTruth.LoadBeside(corpus.TruthPath);
        Assert.NotNull(targets);
        var library = Path.Combine(corpus.Directory, targets.Library);
        Assert.NotEqual(Path.GetFullPath(corpus.RootPath), Path.GetFullPath(library));
        Assert.False(Path.GetFullPath(library).StartsWith(Path.GetFullPath(corpus.RootPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal));

        Assert.All(targets.LibraryFiles, f => Assert.True(File.Exists(Path.Combine(library, f.Path))));
        Assert.Equal(Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories).Count(), targets.LibraryFiles.Count);
        var truth = EvalTruth.Load(corpus.TruthPath);
        foreach (var t in targets.Targets)
        {
            if (t.Unit == "folder")
            {
                Assert.True(Directory.Exists(Path.Combine(corpus.RootPath, t.Path)), t.Path);
            }
            else
            {
                Assert.True(truth.Files.ContainsKey(t.Path), t.Path);
                Assert.NotNull(truth.Files[t.Path].Description);
            }
            if (t.Destination is { } d) Assert.True(Directory.Exists(Path.Combine(library, d)), d);
        }
        Assert.True(targets.Targets.Count(t => t.Unit == "bundle" && t.Path.StartsWith("Import/IMG_20210206_", StringComparison.Ordinal)) == 2);
        Assert.Contains(targets.Targets, t => t.Destination is null);

        // Issue 85: closed units of the library with loose files of their time, and a library that is gone.
        foreach (var unit in new[] { "Filme/Sommer am See (2009)", "Serien/Beispielserie/Staffel 1", "Spiele/Beispielspiel/cache", "Programme/Bildbetrachter" })
            Assert.True(Directory.Exists(Path.Combine(library, unit)), unit);
        Assert.Equal(4, targets.Targets.Count(t => t.Destination is null && t.Note!.StartsWith("Trap (Issue 85)", StringComparison.Ordinal) && t.Outcome is null));
        Assert.Contains(targets.Targets, t => t.Outcome == EvalTargetTruth.LibraryUnreachable);
    }

    [Fact]
    public void Generator_RefusesAFolderThatIsNotEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "busy"));
        File.WriteAllText(Path.Combine(_dir, "busy", "keep.txt"), "x");
        Assert.Throws<IOException>(() => SmallCorpusGenerator.Generate(Path.Combine(_dir, "busy")));
    }

    [Fact]
    public void Generator_KeyDummy_HasAHeaderButNoValidKeyMaterial()
    {
        var corpus = SmallCorpusGenerator.Generate(Path.Combine(_dir, "c"));
        var key = File.ReadAllText(Path.Combine(corpus.RootPath, ".ssh", "id_ed25519"));

        Assert.StartsWith("-----BEGIN OPENSSH " + "PRIVATE KEY-----", key);
        // A real OpenSSH key body starts with the magic "openssh-key-v1"; the dummy is random filler.
        var body = string.Concat(key.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("-----", StringComparison.Ordinal)));
        Assert.False(Encoding.ASCII.GetString(Convert.FromBase64String(body)).StartsWith("openssh-key-v1", StringComparison.Ordinal));
    }

    [Fact]
    public void Truth_RejectsAmbiguousOrUnknownEntries()
    {
        var doc = new EvalTruthDocument(1, new EvalCorpusInfo("t", 1, 1), "tree",
            [
                new(".", "mixed"),
                new("A", "uniform"),
                new("A", "mixed"),
                new("B", "not-a-verdict"),
                new("C/D", "unit")
            ],
            [
                new("A/x.pdf", "invoice", []),
                new("A/x.pdf", "invoice", []),
                new("A/y.pdf", "no_such_category", []),
                new("A/z.pdf", null, ["no_such_marker"]),
                new("A/w.pdf", null, null!),
                new("Q/v.pdf", null, [])
            ]);

        var ex = Assert.Throws<EvalTruthException>(() => EvalTruth.FromDocument(doc));

        Assert.Contains(ex.Problems, p => p.Contains("directory 'A' is listed twice"));
        Assert.Contains(ex.Problems, p => p.Contains("unknown verdict 'not-a-verdict'"));
        Assert.Contains(ex.Problems, p => p.Contains("its parent 'C' is not listed"));
        Assert.Contains(ex.Problems, p => p.Contains("file 'A/x.pdf' is listed twice"));
        Assert.Contains(ex.Problems, p => p.Contains("unknown category 'no_such_category'"));
        Assert.Contains(ex.Problems, p => p.Contains("unknown sensitive marker 'no_such_marker'"));
        Assert.Contains(ex.Problems, p => p.Contains("'A/w.pdf': sensitive is missing"));
        Assert.Contains(ex.Problems, p => p.Contains("its folder 'Q' is not listed"));
    }

    // ── Codes ────────────────────────────────────────────────────────────

    [Fact]
    public void Codes_EnglishCodesOfIssue35_MapToTheEnums()
    {
        Assert.True(EvalCodes.TryParseVerdict("unit", out var unit));
        Assert.Equal(DirectoryVerdictKind.Unit, unit);
        Assert.True(EvalCodes.TryParseVerdict("uniform", out var uniform));
        Assert.Equal(DirectoryVerdictKind.Uniform, uniform);
        Assert.True(EvalCodes.TryParseVerdict("mixed", out var mixed));
        Assert.Equal(DirectoryVerdictKind.Mixed, mixed);
        Assert.True(EvalCodes.TryParseVerdict("empty", out var empty));
        Assert.Equal(DirectoryVerdictKind.Empty, empty);
        Assert.True(EvalCodes.TryParseVerdict("system", out var system));
        Assert.Equal(DirectoryVerdictKind.System, system);

        Assert.True(EvalCodes.TryParseMarker("credentials", out var c));
        Assert.Equal(SensitiveMarker.Credentials, c);
        Assert.True(EvalCodes.TryParseMarker("private_message", out var p));
        Assert.Equal(SensitiveMarker.PrivateMessage, p);
        Assert.True(EvalCodes.TryParseMarker("health", out var h));
        Assert.Equal(SensitiveMarker.Health, h);

        // The truth codes are the central codes, so the output shows exactly what the truth file says.
        Assert.Equal("mixed", EvalCodes.Verdict(DirectoryVerdictKind.Mixed));
        Assert.Equal("credentials,keys", EvalCodes.MarkerList(SensitiveMarker.Credentials | SensitiveMarker.Keys));
        Assert.False(EvalCodes.TryParseVerdict("chaos", out _));
        Assert.False(EvalCodes.TryParseVerdict("gemischt", out _));
    }

    [Fact]
    public void Codes_CategoryCodes_AreTheSnakeCaseNames_AndRoundTrip()
    {
        Assert.Equal("document_scan", EvalCodes.Category(FileCategory.DocumentScan));
        foreach (var category in Enum.GetValues<FileCategory>())
        {
            Assert.True(EvalCodes.TryParseCategory(EvalCodes.Category(category), out var back));
            Assert.Equal(category, back);
        }
    }

    // ── Cases from corrections ───────────────────────────────────────────

    private sealed record Store(DatabaseContext Db, FileRepository Files, ClassificationRepository Classifications,
        EvalCaseRepository Cases, ReviewService Review, EvalCaseService CaseService, long JobId);

    private sealed class FixedPreparer : IClassificationInputPreparer
    {
        public int Calls { get; private set; }

        public Task<OllamaClassificationInput> PrepareAsync(FileNode file, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new OllamaClassificationInput { File = file, ExtractedText = $"text of {file.Name}" });
        }
    }

    private async Task<Store> CreateStoreAsync(FixedPreparer preparer)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        Directory.CreateDirectory(_dir);
        var db = new DatabaseContext(Path.Combine(_dir, "cases.db"), NullLogger<DatabaseContext>.Instance);
        await db.InitializeAsync();
        var job = await new ScanJobRepository(db).CreateAsync(new ScanJob { RootPath = "/Scan", Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow });
        var files = new FileRepository(db);
        var cases = new EvalCaseRepository(db);
        var service = new EvalCaseService(cases, files, preparer, new FileSystemAccessor(), NullLogger<EvalCaseService>.Instance);
        return new Store(db, files, new ClassificationRepository(db), cases,
            new ReviewService(new UserOverrideRepository(db), files, service), service, job.Id);
    }

    private static async Task<long> AddClassifiedFileAsync(Store s, string name, FileCategory modelSaid, SensitiveMarker modelMarkers = SensitiveMarker.None)
    {
        await s.Files.InsertBatchAsync([new FileNode
        {
            JobId = s.JobId, FullPath = "/Scan/docs/" + name, Name = name, ParentPath = "/Scan/docs", RootPath = "/Scan",
            RelativePath = "docs/" + name, RelativeDir = "docs", Extension = Path.GetExtension(name), FileType = FileType.Document,
            LastWriteTime = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), ScannedAt = DateTime.UtcNow, Size = 10
        }]);
        var id = (await s.Files.GetByDirectoryAsync(s.JobId, "/Scan/docs")).Single(f => f.Name == name).Id;
        await s.Classifications.UpsertAsync(new FileClassification
        {
            FileNodeId = id, Category = modelSaid, ModelUsed = "model-a", AnalyzedAt = DateTime.UtcNow,
            IsAiResult = true, SensitiveMarkers = modelMarkers
        });
        return id;
    }

    [Fact]
    public async Task Correction_BecomesACase_WithTheEvidenceTheModelSaw()
    {
        var preparer = new FixedPreparer();
        var s = await CreateStoreAsync(preparer);
        var id = await AddClassifiedFileAsync(s, "strom.pdf", FileCategory.Document);

        await s.Review.ApplyOverrideAsync(id, FileCategory.Invoice, null, null);

        var stored = Assert.Single(await s.Cases.GetAllAsync());
        Assert.Equal(EvalCaseKind.FileCategory, stored.Kind);
        Assert.Equal(EvalCaseOrigin.ReviewOverride, stored.Origin);
        Assert.Equal("invoice", stored.Expected);
        Assert.Equal("document", stored.Observed);
        Assert.Equal("model-a", stored.ObservedModel);
        Assert.Equal("/Scan/docs/strom.pdf", stored.SubjectPath);
        Assert.Equal("docs/strom.pdf", stored.RelativePath);
        Assert.Contains("text of strom.pdf", stored.EvidenceJson);

        // A second correction of the same file updates the case instead of adding one.
        await s.Review.ApplyOverrideAsync(id, FileCategory.DocumentScan, null, null);
        Assert.Equal("document_scan", Assert.Single(await s.Cases.GetAllAsync()).Expected);
    }

    [Fact]
    public async Task AcceptingTheModelAnswer_IsNoCase()
    {
        var s = await CreateStoreAsync(new FixedPreparer());
        var id = await AddClassifiedFileAsync(s, "brief.txt", FileCategory.Letter);

        await s.Review.ApplyOverrideAsync(id, FileCategory.Letter, "Briefe", null);

        Assert.Empty(await s.Cases.GetAllAsync());
    }

    [Fact]
    public async Task SensitiveFile_CaseKeepsPathAndMarkersOnly()
    {
        var preparer = new FixedPreparer();
        var s = await CreateStoreAsync(preparer);
        var id = await AddClassifiedFileAsync(s, "auszug.pdf", FileCategory.Document, SensitiveMarker.Finance);

        await s.Review.ApplyOverrideAsync(id, FileCategory.Invoice, null, null);

        var stored = Assert.Single(await s.Cases.GetAllAsync());
        Assert.True(stored.IsSensitive);
        Assert.Equal(SensitiveMarker.Finance, stored.SensitiveMarkers);
        Assert.Null(stored.EvidenceJson);
        Assert.Equal(0, preparer.Calls);

        await using var conn = s.Db.CreateConnection();
        Assert.Null(await conn.ExecuteScalarAsync<string?>("SELECT evidence_json FROM eval_cases WHERE id = @id", new { id = stored.Id }));
    }

    [Fact]
    public async Task RunCases_ScoresEachCase_AgainstTheCurrentAnswers()
    {
        var s = await CreateStoreAsync(new FixedPreparer());
        var fixedId = await AddClassifiedFileAsync(s, "strom.pdf", FileCategory.Document);
        var stillWrongId = await AddClassifiedFileAsync(s, "vertrag.pdf", FileCategory.Letter);
        await s.Review.ApplyOverrideAsync(fixedId, FileCategory.Invoice, null, null);
        await s.Review.ApplyOverrideAsync(stillWrongId, FileCategory.PersonalDocument, null, null);

        // The "new model" answers invoice for strom.pdf and still letter for vertrag.pdf.
        var answers = EvalTruth.FromDocument(new EvalTruthDocument(1, new EvalCorpusInfo("answers", 1, 0), "tree",
            [new(".", "mixed"), new("docs", "mixed")],
            [new("docs/strom.pdf", "invoice", []), new("docs/vertrag.pdf", "letter", [])]));
        var report = await s.CaseService.RunCasesAsync(new ScriptedAnswerOllamaService(answers, "answers.json"), "stub:answers.json");

        var score = Assert.Single(report.Scores);
        Assert.Equal((2, 1, 1, 0), (score.Total, score.Hits, score.Wrong, score.Missing));
        var cases = report.Cases!.ToDictionary(c => Path.GetFileName(c.Path));
        Assert.Equal((EvalOutcome.Hit, "document", "invoice"), (cases["strom.pdf"].Outcome, cases["strom.pdf"].CorrectedFrom, cases["strom.pdf"].Actual));
        Assert.Equal((EvalOutcome.Wrong, "letter", "letter"), (cases["vertrag.pdf"].Outcome, cases["vertrag.pdf"].CorrectedFrom, cases["vertrag.pdf"].Actual));
        var failure = Assert.Single(report.Failures);
        Assert.Equal(("personal_document", "letter"), (failure.Expected, failure.Actual));
    }

    [Fact]
    public async Task RunCases_SensitiveCaseWithoutFile_IsMissing_NotGuessed()
    {
        var s = await CreateStoreAsync(new FixedPreparer());
        var id = await AddClassifiedFileAsync(s, "auszug.pdf", FileCategory.Document, SensitiveMarker.Finance);
        await s.Review.ApplyOverrideAsync(id, FileCategory.Invoice, null, null);

        var answers = EvalTruth.FromDocument(new EvalTruthDocument(1, new EvalCorpusInfo("answers", 1, 0), "tree",
            [new(".", "mixed"), new("docs", "mixed")], [new("docs/auszug.pdf", "invoice", ["finance"])]));
        var report = await s.CaseService.RunCasesAsync(new ScriptedAnswerOllamaService(answers, "answers.json"), "stub");

        // /Scan/docs/auszug.pdf does not exist and no evidence was stored.
        var result = Assert.Single(report.Cases!);
        Assert.Equal(EvalOutcome.Missing, result.Outcome);
        Assert.True(result.Sensitive);
    }

    // ── Report and comparison ────────────────────────────────────────────

    [Fact]
    public void Compare_NamesFixedAndNewFailures_AndWhatChanged()
    {
        EvalRun Run(long id, string model, params EvalFailure[] failures) => new()
        {
            Id = id, Mode = EvalRunMode.Corpus, Subject = "small v1 seed 36", Model = model, PromptFingerprint = "p1",
            SettingsJson = $$"""{"ollama.max_parallel_requests":"{{id}}"}""",
            ReportJson = System.Text.Json.JsonSerializer.Serialize(
                new EvalReport([new EvalKindScore(EvalKind.FileCategory, 3, 3 - failures.Length, failures.Length, 0, null)], failures, 0, []),
                EvalJson.Compact)
        };
        EvalFailure F(string path) => new(EvalKind.FileCategory, CorpusEvaluator.Key(EvalKind.FileCategory, path), path,
            EvalOutcome.Wrong, "photo", "screenshot", "model x", null);

        var comparison = EvalRunService.Compare(Run(1, "a", F("x.jpg"), F("y.jpg")), Run(2, "b", F("y.jpg"), F("z.jpg")));

        Assert.True(comparison.SameSubject);
        Assert.Equal("x.jpg", Assert.Single(comparison.Fixed).Path);
        Assert.Equal("z.jpg", Assert.Single(comparison.NewFailures).Path);
        Assert.Equal(1, comparison.StillFailing);
        Assert.Contains(comparison.Changes, c => c is { Name: "model", Before: "a", After: "b" });
        Assert.Contains(comparison.Changes, c => c is { Name: "ollama.max_parallel_requests", Before: "1", After: "2" });
        Assert.DoesNotContain(comparison.Changes, c => c.Name == "prompt_fingerprint");
    }

    private static string Fingerprint(string directory)
    {
        var sb = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            sb.Append(Path.GetRelativePath(directory, file)).Append('|')
              .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).Append('|')
              // Corpus files carry fixed dates; truth.json, findings.json and targets.json are written now.
              .Append(Path.GetFileName(file) is EvalTruth.FileName or EvalFindingTruth.FileName or EvalTargetTruth.FileName
                  ? 0 : File.GetLastWriteTimeUtc(file).Ticks).Append('\n');
        }
        return sb.ToString();
    }
}
