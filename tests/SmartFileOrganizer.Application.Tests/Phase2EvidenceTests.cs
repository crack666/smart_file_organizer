using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Content;
using SmartFileOrganizer.Infrastructure.Ollama;
using SmartFileOrganizer.Infrastructure.Persistence;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Phase 2 end to end with generated files, a temp database and a stand-in model: every file type
/// gets a stored result (rule or model), facts are stored with their source, and the evidence notes
/// travel into <c>ai_results.evidence</c>.
/// </summary>
public class Phase2EvidenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly DatabaseContext _db;
    private readonly FileRepository _fileRepo;
    private readonly FileFactRepository _factRepo;
    private readonly ClassificationRepository _classRepo;
    private readonly AnsweringOllama _ollama = new();

    public Phase2EvidenceTests()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_phase2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "test.db");
        _db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        _db.InitializeAsync().GetAwaiter().GetResult();
        _fileRepo = new FileRepository(_db);
        _factRepo = new FileFactRepository(_db);
        _classRepo = new ClassificationRepository(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private ClassificationService Service() => new(
        _fileRepo, _classRepo, _factRepo,
        new AiClassificationInputPreparer(new FileContentExtractor(), new OllamaOptions(), new VideoFrameExtractor(() => null)),
        _ollama,
        new DirectorySamplingService(_fileRepo, new DirectoryVerdictRepository(_db), new DirectorySampleRepository(_db),
            new DirectoryClassificationRepository(_db), new DirectoryAnalysisOptions(), NullLogger<DirectorySamplingService>.Instance),
        () => 2, NullLogger<ClassificationService>.Instance);

    private async Task<long> InsertAsync(params (string Name, FileType Type, SensitiveMarker Markers)[] files)
    {
        var job = await new ScanJobRepository(_db).CreateAsync(new ScanJob { RootPath = _dir, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow });
        await _fileRepo.InsertBatchAsync(files.Select(f =>
        {
            var path = Path.Combine(_dir, f.Name);
            return new FileNode
            {
                JobId = job.Id,
                FullPath = path,
                Name = f.Name,
                ParentPath = _dir,
                RootPath = _dir,
                RelativePath = f.Name,
                Extension = Path.GetExtension(f.Name).ToLowerInvariant(),
                FileType = f.Type,
                Size = new FileInfo(path).Length,
                LastWriteTime = DateTime.UtcNow,
                ScannedAt = DateTime.UtcNow,
                SensitiveMarkers = f.Markers,
                SensitiveRule = f.Markers == SensitiveMarker.None ? null : "test"
            };
        }).ToList());
        return job.Id;
    }

    [Fact]
    public async Task EveryFileType_GetsAResult_RulesWithoutModelCall()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "song.mp3"), GeneratedFiles.Mp3WithTags("Song", "Band", "Album"));
        GeneratedFiles.Zip(Path.Combine(_dir, "photos.zip"), ("a.jpg", 10), ("b.jpg", 10));
        await File.WriteAllBytesAsync(Path.Combine(_dir, "setup.exe"), [0x4D, 0x5A, 0x90, 0x00]);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "font.ttf"), [0x00, 0x01, 0x00, 0x00]);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "blob.xyz"), [0x00, 0x13, 0x37, 0x00]);
        await File.WriteAllTextAsync(Path.Combine(_dir, "main.py"), "print(1)");
        var jobId = await InsertAsync(
            ("song.mp3", FileType.Audio, SensitiveMarker.None),
            ("photos.zip", FileType.Archive, SensitiveMarker.None),
            ("setup.exe", FileType.Executable, SensitiveMarker.None),
            ("font.ttf", FileType.Font, SensitiveMarker.None),
            ("blob.xyz", FileType.Other, SensitiveMarker.None),
            ("main.py", FileType.Code, SensitiveMarker.None));

        var run = await Service().RunAsync(jobId);

        Assert.Equal(6, run.TotalFiles);
        Assert.Equal(6, run.ProcessedFiles);
        Assert.Equal(0, run.ErrorCount);
        Assert.Equal(["main.py", "photos.zip", "song.mp3"], _ollama.Names.OrderBy(n => n));

        await using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<(string Name, int Status, string? Category, long IsAi, string? Model, string? Evidence)>(
            """
            SELECT f.name, f.status, r.category, r.is_ai_result, r.model_used, r.evidence
            FROM file_nodes f LEFT JOIN ai_results r ON r.file_node_id = f.id
            WHERE f.job_id = @jobId
            """, new { jobId })).ToDictionary(r => r.Name);

        Assert.All(rows.Values, r => Assert.Equal((int)FileNodeStatus.AiAnalyzed, r.Status));
        Assert.All(rows.Values, r => Assert.NotNull(r.Category));
        Assert.Equal(0, rows["setup.exe"].IsAi);
        Assert.Equal("rule:program", rows["setup.exe"].Model);
        Assert.Equal(0, rows["blob.xyz"].IsAi);
        Assert.Equal(1, rows["song.mp3"].IsAi);
        Assert.Contains("\"missing\"", rows["song.mp3"].Evidence);
        Assert.Contains("not transcribed", rows["song.mp3"].Evidence);
    }

    [Fact]
    public async Task Facts_AreStoredWithCodesAndSource()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "song.mp3"), GeneratedFiles.Mp3WithTags("Song", "Band", "Album"));
        var jobId = await InsertAsync(("song.mp3", FileType.Audio, SensitiveMarker.None));

        await Service().RunAsync(jobId);

        await using var conn = _db.CreateConnection();
        var stored = (await conn.QueryAsync<(string Fact, string Value, string Source)>(
            "SELECT fact, value, source FROM file_facts ORDER BY fact")).ToList();
        Assert.Contains(("artist", "Band", "metadata"), stored);
        Assert.Contains(("audio_kind", "music", "rule"), stored);
        Assert.Contains(("detected_format", "mp3", "metadata"), stored);

        var fileId = await conn.ExecuteScalarAsync<long>("SELECT id FROM file_nodes WHERE name = 'song.mp3'");
        var read = await _factRepo.GetByFileNodeIdAsync(fileId);
        Assert.Contains(new FileFact(FileFactKey.Title, "Song", FactSource.Metadata), read);
    }

    [Fact]
    public async Task SensitiveFile_KeepsOnlyFactsWithoutFreeText()
    {
        GeneratedFiles.Docx(Path.Combine(_dir, "steuer.docx"), "Tax return 2015", "Some Person", new DateTime(2016, 3, 1), "Income");
        var jobId = await InsertAsync(("steuer.docx", FileType.Document, SensitiveMarker.Finance));

        await Service().RunAsync(jobId);

        await using var conn = _db.CreateConnection();
        var facts = (await conn.QueryAsync<string>("SELECT fact FROM file_facts")).ToList();
        Assert.Contains("page_count", facts);
        Assert.Contains("created_at", facts);
        Assert.DoesNotContain("title", facts);
        Assert.DoesNotContain("author", facts);
    }

    [Fact]
    public async Task RerunAfterNewEvidence_ReplacesFacts()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "song.mp3"), GeneratedFiles.Mp3WithTags("Song", "Band", "Album"));
        var jobId = await InsertAsync(("song.mp3", FileType.Audio, SensitiveMarker.None));
        await Service().RunAsync(jobId);
        await using var conn = _db.CreateConnection();
        var fileId = await conn.ExecuteScalarAsync<long>("SELECT id FROM file_nodes WHERE name = 'song.mp3'");

        await _factRepo.ReplaceAsync(fileId, [new FileFact(FileFactKey.AudioKind, "speech", FactSource.Rule)]);

        var read = await _factRepo.GetByFileNodeIdAsync(fileId);
        Assert.Equal([new FileFact(FileFactKey.AudioKind, "speech", FactSource.Rule)], read);
    }

    /// <summary>Answers every classification with a fixed result and records which files it saw.</summary>
    private sealed class AnsweringOllama : IOllamaService
    {
        private readonly ConcurrentBag<string> _names = [];
        public IReadOnlyList<string> Names => _names.ToList();

        public Task<FileClassification> ClassifyAsync(OllamaClassificationInput input, CancellationToken ct = default)
        {
            Assert.Null(input.RuleResult);
            _names.Add(input.File.Name);
            return Task.FromResult(new FileClassification
            {
                Category = FileCategory.Document,
                Importance = Importance.Medium,
                Confidence = 0.8,
                Summary = "scripted",
                ModelUsed = "scripted",
                AnalyzedAt = DateTime.UtcNow,
                IsAiResult = true
            });
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OllamaLocalModel>>([]);
        public Task WarmModelAsync(string? model = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DirectoryPreAssessmentOutcome> PreAssessDirectoryAsync(DirectoryPreAssessmentInput input, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DirectoryClassificationResult> SummarizeDirectoryAsync(DirectorySummaryInput input, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
