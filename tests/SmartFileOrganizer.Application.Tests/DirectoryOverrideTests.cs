using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Persistence;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Directory verdicts set by a person (https://github.com/crack666/smart_file_organizer/issues/14): stored beside the
/// verdict of rule or model, winning wherever it is read, and taken back without a trace.
/// </summary>
public sealed class DirectoryOverrideTests : IDisposable
{
    private readonly string _dir;
    private readonly DatabaseContext _db;
    private readonly DirectoryRepository _dirs;
    private readonly FileRepository _files;
    private readonly DirectoryVerdictRepository _verdicts;
    private readonly DirectorySampleRepository _samples;
    private readonly JobReadRepository _read;
    private readonly DirectoryReviewService _review;
    private long _jobId;

    public DirectoryOverrideTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_dir_override_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _db = new DatabaseContext(Path.Combine(_dir, "data.db"), NullLogger<DatabaseContext>.Instance);
        _db.InitializeAsync().GetAwaiter().GetResult();
        _dirs = new DirectoryRepository(_db);
        _files = new FileRepository(_db);
        _verdicts = new DirectoryVerdictRepository(_db);
        _samples = new DirectorySampleRepository(_db);
        _read = new JobReadRepository(_db);
        _review = new DirectoryReviewService(new DirectoryOverrideRepository(_db), _verdicts, _samples);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<DirectoryNode> FolderAsync(params string[] files)
    {
        _jobId = (await new ScanJobRepository(_db).CreateAsync(new ScanJob
        {
            RootPath = _dir, Status = JobStatus.Completed, CreatedAt = DateTime.UtcNow
        })).Id;
        await _dirs.InsertBatchAsync([new DirectoryNode
        {
            JobId = _jobId, FullPath = _dir, Name = "folder", ParentPath = Path.GetDirectoryName(_dir)!, RootPath = _dir,
            RelativePath = ".", DirStatus = DirectoryStatus.Scanned, ScannedAt = DateTime.UtcNow
        }]);
        await _files.InsertBatchAsync(files.Select(f => new FileNode
        {
            JobId = _jobId, FullPath = Path.Combine(_dir, f), Name = f, ParentPath = _dir, RootPath = _dir, RelativePath = f,
            RelativeDir = ".", Extension = ".txt", FileType = FileType.Document, Size = 10,
            LastWriteTime = DateTime.UtcNow, ScannedAt = DateTime.UtcNow
        }));
        return (await _dirs.GetByPathAsync(_jobId, _dir))!;
    }

    private Task StoreAsync(long nodeId, DirectoryVerdictKind? kind, string? error = null) => _verdicts.UpsertAsync(new DirectoryVerdict
    {
        DirectoryNodeId = nodeId, Verdict = kind, Source = VerdictSource.Model, Purpose = "letters", Retention = RetentionValue.Review,
        Reason = "all letters", ModelUsed = "m", JudgedAt = DateTime.UtcNow, Error = error
    });

    private async Task<IReadOnlyList<FileNodeStatus>> StatusesAsync() =>
        (await _files.GetByDirectoryAsync(_jobId, _dir)).OrderBy(f => f.Name).Select(f => f.Status).ToList();

    [Fact]
    public async Task Override_WinsInEveryReader_KeepsTheStoredVerdict_AndCanBeTakenBack()
    {
        var node = await FolderAsync("a.txt");
        await StoreAsync(node.Id, DirectoryVerdictKind.Uniform);

        await _review.ApplyOverrideAsync(node.Id, DirectoryVerdictKind.Mixed, RetentionValue.Keep, " grew over years ");

        var verdict = (await _verdicts.GetByDirectoryPathAsync(_jobId, _dir))!;
        Assert.Equal(DirectoryVerdictKind.Mixed, verdict.Verdict);
        Assert.Equal(RetentionValue.Keep, verdict.Retention);
        Assert.Equal(VerdictSource.Human, verdict.Source);
        Assert.Equal("grew over years", verdict.Reason);
        Assert.Equal("letters", verdict.Purpose);
        Assert.Null(verdict.ModelUsed);
        Assert.Equal(DirectoryVerdictKind.Uniform, verdict.Replaced!.Verdict);
        Assert.Equal("all letters", verdict.Replaced.Reason);

        var row = Assert.Single(await _read.GetDirectoriesAsync(_jobId));
        Assert.Equal(("mixed", "human", "keep", "grew over years"), (row.Verdict, row.VerdictSource, row.Retention, row.Reason));
        await foreach (var file in _read.StreamFilesAsync(new FileListFilter { JobId = _jobId }))
            Assert.Equal(("mixed", "keep"), (file.DirectoryVerdict, file.DirectoryRetention));

        // A new judgement of the model does not touch the correction.
        await StoreAsync(node.Id, DirectoryVerdictKind.Unit);
        Assert.Equal(DirectoryVerdictKind.Mixed, (await _verdicts.GetByNodeIdAsync(node.Id))!.Verdict);

        await _review.ResetAsync(node.Id);
        var back = Assert.Single(await _verdicts.GetForJobAsync(_jobId));
        Assert.Equal((DirectoryVerdictKind.Unit, VerdictSource.Model, "all letters"), (back.Verdict!.Value, back.Source, back.Reason));
        Assert.Null(back.Override);
    }

    [Fact]
    public async Task Override_WithoutRetention_KeepsTheStoredOne_AndASecondOneReplacesTheFirst()
    {
        var node = await FolderAsync();
        await StoreAsync(node.Id, DirectoryVerdictKind.Uniform);
        await _review.ApplyOverrideAsync(node.Id, DirectoryVerdictKind.Mixed, RetentionValue.Disposable, null);
        await _review.ApplyOverrideAsync(node.Id, DirectoryVerdictKind.Unit, null, null);

        var verdict = (await _verdicts.GetByNodeIdAsync(node.Id))!;
        Assert.Equal(DirectoryVerdictKind.Unit, verdict.Verdict);
        Assert.Equal(RetentionValue.Review, verdict.Retention);
        Assert.Equal(node.Id, verdict.UnitDirectoryNodeId);
    }

    [Fact]
    public async Task Override_OfAFolderWithoutVerdict_OrWithAnError_CountsAsJudged()
    {
        var node = await FolderAsync();
        await _review.ApplyOverrideAsync(node.Id, DirectoryVerdictKind.Mixed, null, null);
        var verdict = Assert.Single(await _verdicts.GetForJobAsync(_jobId));
        Assert.Equal(DirectoryVerdictKind.Mixed, verdict.Verdict);
        Assert.Null(verdict.Replaced);

        await StoreAsync(node.Id, null, "no valid verdict");
        verdict = (await _verdicts.GetByNodeIdAsync(node.Id))!;
        Assert.Null(verdict.Error);
        Assert.Equal("no valid verdict", verdict.Replaced!.Error);
        Assert.Null(Assert.Single(await _read.GetDirectoriesAsync(_jobId)).VerdictError);
    }

    [Fact]
    public async Task Files_FollowTheVerdict_ButKeepTheirResults()
    {
        var node = await FolderAsync("a.txt", "b.txt");
        await StoreAsync(node.Id, DirectoryVerdictKind.Mixed);
        var a = (await _files.GetByDirectoryAsync(_jobId, _dir)).Single(f => f.Name == "a.txt");
        await new ClassificationRepository(_db).UpsertAsync(new FileClassification
        {
            FileNodeId = a.Id, Category = FileCategory.Letter, AnalyzedAt = DateTime.UtcNow
        });
        await _files.UpdateStatusAsync(a.Id, FileNodeStatus.AiAnalyzed);

        await _review.ApplyOverrideAsync(node.Id, DirectoryVerdictKind.Unit, null, null);
        Assert.Equal([FileNodeStatus.AiAnalyzed, FileNodeStatus.Skipped], await StatusesAsync());
        Assert.Equal(0, await _files.ReleaseStaleSkippedAsync(_jobId));

        await _review.ResetAsync(node.Id);
        Assert.Equal([FileNodeStatus.AiAnalyzed, FileNodeStatus.Discovered], await StatusesAsync());
    }

    [Fact]
    public async Task OpenSample_IsDropped_WhenTheFolderIsNotUniformAnyMore()
    {
        var node = await FolderAsync("a.txt", "b.txt");
        await StoreAsync(node.Id, DirectoryVerdictKind.Uniform);
        var b = (await _files.GetByDirectoryAsync(_jobId, _dir)).Single(f => f.Name == "b.txt");
        await _samples.CreateAsync(new DirectorySample
        {
            DirectoryNodeId = node.Id, Seed = "s", State = SampleState.Open, EligibleCount = 2, Members = [], DrawnAt = DateTime.UtcNow
        }, [b.Id]);
        Assert.Equal([FileNodeStatus.Discovered, FileNodeStatus.Deferred], await StatusesAsync());

        await _review.ApplyOverrideAsync(node.Id, DirectoryVerdictKind.Mixed, null, null);

        Assert.Null(await _samples.GetByNodeIdAsync(node.Id));
        Assert.Equal([FileNodeStatus.Discovered, FileNodeStatus.Discovered], await StatusesAsync());
    }
}
