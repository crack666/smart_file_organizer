using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure;
using SmartFileOrganizer.Infrastructure.Content;
using SmartFileOrganizer.Infrastructure.Persistence;
using SmartFileOrganizer.Scanning.Engine;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// A library with several roots (https://github.com/crack666/smart_file_organizer/issues/74): a source compared with two
/// library jobs at once, in temp directories with the real scan engine and a temp database. Names are invented.
/// </summary>
public class MultiLibraryComparisonTests : IDisposable
{
    private readonly string _dir;
    private readonly string _source;
    private readonly string _photos;
    private readonly string _videos;
    private readonly DatabaseContext _db;
    private readonly ScanJobRepository _jobs;
    private readonly ContentComparisonRepository _repo;

    public MultiLibraryComparisonTests()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_multi_{Guid.NewGuid():N}");
        _source = Path.Combine(_dir, "stick");
        _photos = Path.Combine(_dir, "ablage-bilder");
        _videos = Path.Combine(_dir, "ablage-videos");

        Write(_source, "DCIM/IMG_0001.jpg", "photo one");
        Write(_source, "Filme/urlaub.mp4", "a holiday video, only on the second share");
        Write(_source, "Filme/neu.mp4", "a video no share holds");
        Write(_photos, "Fotos/2019/IMG_0001.jpg", "photo one");
        Write(_videos, "Videos/2019/urlaub.mp4", "a holiday video, only on the second share");

        _db = new DatabaseContext(Path.Combine(_dir, "data.db"), NullLogger<DatabaseContext>.Instance);
        _db.InitializeAsync().GetAwaiter().GetResult();
        _jobs = new ScanJobRepository(_db);
        _repo = new ContentComparisonRepository(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Write(string root, string relative, string content)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private ContentComparisonService Service() =>
        new(_jobs, _repo, new ContentHasher(), new FileSystemAccessor(), NullLogger<ContentComparisonService>.Instance);

    private async Task<ScanJob> ScanAsync(string root, ScanRole role)
    {
        var engine = new ScanEngine(new FileSystemAccessor(), new FileRepository(_db), new DirectoryRepository(_db), _jobs,
            NullLogger<ScanEngine>.Instance);
        var service = new ScanJobService(_jobs, engine, NullLogger<ScanJobService>.Instance);
        var job = await service.CreateJobAsync(root, role);
        return await service.RunAsync(job.Id);
    }

    private string Video => Path.GetFullPath(Path.Combine(_source, "Filme", "urlaub.mp4"));

    /// <summary>Fertig-wenn: a file only in the second of two libraries is backed_up and names it; without it, new.</summary>
    [Fact]
    public async Task FileOnlyInSecondLibrary_IsBackedUp_AndNamesIt_ButNewWithoutIt()
    {
        var source = await ScanAsync(_source, ScanRole.Source);
        var photos = await ScanAsync(_photos, ScanRole.Library);
        var videos = await ScanAsync(_videos, ScanRole.Library);

        var both = await Service().CompareWithLibrariesAsync(source.Id, [photos.Id, videos.Id]);
        var video = both.Files.Single(f => f.FullPath == Video);
        Assert.Equal(ContentState.BackedUp, video.State);
        Assert.Equal(videos.Id, video.MatchJobId);
        Assert.StartsWith(_videos, video.MatchPath);
        Assert.Equal(photos.Id, both.Files.Single(f => f.FullPath.EndsWith("IMG_0001.jpg", StringComparison.Ordinal)).MatchJobId);
        Assert.Equal(ContentState.New, both.Files.Single(f => f.FullPath.EndsWith("neu.mp4", StringComparison.Ordinal)).State);
        Assert.Equal([photos.Id, videos.Id], both.Libraries!.Select(l => l.Library.Id));
        Assert.Equal(1, both.Libraries![1].BackedUpFiles);
        Assert.Equal(2, both.Count(ContentState.BackedUp));

        // Later steps read the run as one set: the newest compare named both libraries.
        var (set, missing) = await LibraryComparisonSet.LoadAsync(_repo, source.Id, null);
        Assert.Empty(missing);
        Assert.Equal([photos.Id, videos.Id], set!.LibraryJobIds);
        Assert.Equal(ContentState.BackedUp, set.Files.Single(f => f.FullPath == Video).State);
        Assert.Equal(videos.Id, set.Files.Single(f => f.FullPath == Video).MatchJobId);

        // Without the second library the video is new.
        var first = await Service().CompareWithLibrariesAsync(source.Id, [photos.Id]);
        Assert.Equal(ContentState.New, first.Files.Single(f => f.FullPath == Video).State);
        var (latest, _) = await LibraryComparisonSet.LoadAsync(_repo, source.Id, null);
        Assert.Equal([photos.Id], latest!.LibraryJobIds);
        Assert.Equal(ContentState.New, latest.Files.Single(f => f.FullPath == Video).State);

        // Both comparisons stay stored: named again, the set holds the video again.
        var (named, _) = await LibraryComparisonSet.LoadAsync(_repo, source.Id, [photos.Id, videos.Id]);
        Assert.Equal(ContentState.BackedUp, named!.Files.Single(f => f.FullPath == Video).State);
    }

    [Fact]
    public async Task ComparisonStoredBeforeRuns_StaysReadable_AsRunWithOneLibrary()
    {
        var source = await ScanAsync(_source, ScanRole.Source);
        var videos = await ScanAsync(_videos, ScanRole.Library);
        await Service().CompareAsync(source.Id, videos.Id);

        // As a database written before migration 19 left it: no run id.
        await using (var conn = _db.CreateConnection())
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("UPDATE comparisons SET run_id = NULL");
        }

        var (set, _) = await LibraryComparisonSet.LoadAsync(_repo, source.Id, null);
        Assert.Equal([videos.Id], set!.LibraryJobIds);
        Assert.Equal(ContentState.BackedUp, set.Files.Single(f => f.FullPath == Video).State);
        Assert.Equal(videos.Id, set.Files.Single(f => f.FullPath == Video).MatchJobId);
    }

    [Fact]
    public void Merge_PrefersBackedUp_ThenError_ThenDiffers()
    {
        FileComparison Row(long id, ContentState state, long? match = null) =>
            new(id, $"/s/{id}", 10, state, match, match is null ? null : $"/m/{match}", match is null ? 0 : 1, null, null);
        var headers = new[]
        {
            new ComparisonHeader(1, 7, 8, DateTime.UtcNow, true, 0, 1),
            new ComparisonHeader(2, 7, 9, DateTime.UtcNow, true, 0, 1)
        };
        var set = LibraryComparisonSet.From(headers,
        [
            [Row(1, ContentState.New), Row(2, ContentState.Error), Row(3, ContentState.Differs, 31), Row(4, ContentState.New)],
            [Row(1, ContentState.BackedUp, 11), Row(2, ContentState.New), Row(3, ContentState.New), Row(4, ContentState.Differs, 41)]
        ]);

        Assert.Equal((ContentState?)ContentState.BackedUp, set.Rows[1].State);
        Assert.Equal(9, set.Rows[1].MatchJobId);
        Assert.Equal((ContentState?)ContentState.Error, set.Rows[2].State); // unknown whether the first holds it
        Assert.Equal((ContentState?)ContentState.Differs, set.Rows[3].State);
        Assert.Equal(8, set.Rows[3].MatchJobId);
        Assert.Equal(9, set.Rows[4].MatchJobId);
        Assert.Equal("library jobs 8 and 9", set.Label);
    }

    [Fact]
    public void FreeSpaceProbe_AnswersForFolders_AndUnknownInsteadOfFailing()
    {
        var probe = new FreeSpaceProbe();
        Assert.True(probe.AvailableBytes(_dir) > 0);
        // A folder a proposal would create: answered for its nearest existing parent.
        Assert.True(probe.AvailableBytes(Path.Combine(_dir, "noch", "nicht", "da")) > 0);
        Assert.Null(probe.AvailableBytes(""));
        Assert.Null(probe.AvailableBytes("\0invalid"));
    }
}
