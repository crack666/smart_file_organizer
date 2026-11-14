using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Duplicates;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure;
using SmartFileOrganizer.Infrastructure.Content;
using SmartFileOrganizer.Infrastructure.Persistence;
using SmartFileOrganizer.Scanning.Engine;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Duplicates over a whole holding (https://github.com/crack666/smart_file_organizer/issues/87): generated folders in a
/// temp directory, scanned with the real scan engine into a temp database, read through a hasher that counts.
/// </summary>
public class DuplicateServiceTests : IDisposable
{
    private const int Big = 4 * 1024 * 1024;

    private readonly string _dir;
    private readonly DatabaseContext _db;
    private readonly ScanJobRepository _jobRepo;
    private readonly CountingHasher _hasher = new(new ContentHasher());

    public DuplicateServiceTests()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_dup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _db = new DatabaseContext(Path.Combine(_dir, "data.db"), NullLogger<DatabaseContext>.Instance);
        _db.InitializeAsync().GetAwaiter().GetResult();
        _jobRepo = new ScanJobRepository(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ fixture

    private static byte[] Bytes(int seed, int length)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private string Write(string relative, byte[] content)
    {
        var path = Path.GetFullPath(Path.Combine(_dir, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private DuplicateService CreateService(PlanOptions? plan = null) =>
        new(_jobRepo, new DuplicateRepository(_db), new ContentComparisonRepository(_db), _hasher, new FileSystemAccessor(),
            plan ?? new PlanOptions(), NullLogger<DuplicateService>.Instance);

    private async Task<ScanJob> ScanAsync(string relativeRoot, ScanRole role)
    {
        var root = Path.GetFullPath(Path.Combine(_dir, relativeRoot));
        Directory.CreateDirectory(root);
        var engine = new ScanEngine(new FileSystemAccessor(),
            new FileRepository(_db), new DirectoryRepository(_db), _jobRepo, NullLogger<ScanEngine>.Instance);
        var service = new ScanJobService(_jobRepo, engine, NullLogger<ScanJobService>.Instance);
        var job = await service.CreateJobAsync(root, role);
        return await service.RunAsync(job.Id);
    }

    // ------------------------------------------------------------------ stages

    [Fact]
    public async Task OnlyCandidatesAreRead_AndTheSameSizeOtherFileDropsOutInStageTwo()
    {
        var content = Bytes(1, Big);
        var copyA = Write("disk/videos/film.mp4", content);
        var copyB = Write("disk/backup/film-kopie.mp4", content);
        var other = Bytes(2, Big); // same size, other content
        var sameSize = Write("disk/videos/anderer-film.mp4", other);
        for (var i = 0; i < 40; i++)
            Write($"disk/einzeln/datei-{i:00}.bin", Bytes(100 + i, 20_000 + i * 37)); // every size once
        var job = await ScanAsync("disk", ScanRole.Source);

        var report = await CreateService().ReportAsync([job.Id]);

        // Stage 1 lets through the three files of one size; nothing else is opened.
        Assert.Equal(3, report.Stages.SizeCandidates);
        Assert.Equal(new[] { copyA, copyB, sameSize }.Order(), _hasher.Paths.Distinct().Order());
        // Stage 2 drops the other file of that size; no full checksum without --verify.
        Assert.Equal(2, report.Stages.SampleGroups);
        Assert.Equal(0, report.Stages.FullComputed);
        var group = Assert.Single(report.Groups);
        Assert.Equal(DuplicateScope.Files, group.Scope);
        Assert.Equal(DuplicateCertainty.Likely, group.Certainty);
        Assert.Equal(DuplicateAdvice.VerifyFirst, group.Advice);
        Assert.Equal(new[] { copyB, copyA }, group.Files.Select(f => f.FullPath));
        Assert.Equal((long)Big, group.WastedBytes);
        // A few hundred KiB of more than 12 MiB.
        Assert.Equal(43, report.TotalFiles);
        Assert.True(report.Stages.BytesRead * 20 < report.TotalBytes,
            $"{report.Stages.BytesRead} bytes read of {report.TotalBytes}");
        Assert.Equal(3, report.Stages.FilesRead);
    }

    [Fact]
    public async Task ADifferenceNoSampleHits_IsLikely_AndVerifySeparatesThem()
    {
        const int size = 1024 * 1024;
        var content = Bytes(3, size);
        var changed = (byte[])content.Clone();
        var offset = UnsampledOffset(size);
        changed[offset] ^= 0xFF;
        Write("disk/a/aufnahme.wav", content);
        Write("disk/b/aufnahme.wav", changed);
        var job = await ScanAsync("disk", ScanRole.Source);

        var likely = await CreateService().ReportAsync([job.Id]);
        var group = Assert.Single(likely.Groups);
        Assert.Equal(DuplicateCertainty.Likely, group.Certainty);
        Assert.Null(group.Hash);

        var verified = await CreateService().ReportAsync([job.Id], new DuplicateOptions(Verify: true));
        Assert.Empty(verified.Groups);
        Assert.True(verified.Verified);
        Assert.Equal(2, verified.Stages.FullComputed);
        Assert.Equal(0, verified.Stages.SampleComputed); // the samples came from the first run
    }

    [Fact]
    public async Task ASecondRunWithoutChanges_ReadsNoFile()
    {
        var content = Bytes(4, 600_000);
        Write("disk/x/a.dat", content);
        Write("disk/y/b.dat", content);
        Write("disk/z/klein-1.txt", Bytes(5, 900)); // small files of one size: read whole, so certain at once
        Write("disk/w/klein-2.txt", Bytes(5, 900));
        var job = await ScanAsync("disk", ScanRole.Source);

        var first = await CreateService().ReportAsync([job.Id], new DuplicateOptions(Verify: true));
        Assert.True(first.Stages.BytesRead > 0);
        var callsAfterFirst = _hasher.Calls;

        var second = await CreateService().ReportAsync([job.Id], new DuplicateOptions(Verify: true));
        Assert.Equal(callsAfterFirst, _hasher.Calls);
        Assert.Equal(0, second.Stages.FilesRead);
        Assert.Equal(0L, second.Stages.BytesRead);
        Assert.Equal(4, second.Stages.Reused);
        Assert.Equal(first.Groups.Select(g => (g.Certainty, g.WastedBytes)), second.Groups.Select(g => (g.Certainty, g.WastedBytes)));
        Assert.All(second.Groups, g => Assert.Equal(DuplicateCertainty.Certain, g.Certainty));

        // Without --verify the small files are certain too: their sample is their full checksum.
        var plain = await CreateService().ReportAsync([job.Id]);
        Assert.Contains(plain.Groups, g => g.Size == 900 && g.Certainty == DuplicateCertainty.Certain && g.Hash is not null);
    }

    // ------------------------------------------------------------------ across jobs

    [Fact]
    public async Task AFileInTwoLibraryRoots_AndAFileOnTwoSources_AreGroups()
    {
        var inLibraries = Bytes(6, 300_000);
        var onSources = Bytes(7, 310_000);
        var everywhere = Bytes(8, 320_000);
        Write("ablage-fotos/2019/urlaub.jpg", inLibraries);
        Write("ablage-videos/sicherung/urlaub.jpg", inLibraries);
        Write("stick/dokumente/brief.pdf", onSources);
        Write("platte/alt/brief.pdf", onSources);
        Write("stick/musik/lied.flac", everywhere);
        Write("ablage-fotos/musik/lied.flac", everywhere);
        var photos = await ScanAsync("ablage-fotos", ScanRole.Library);
        var videos = await ScanAsync("ablage-videos", ScanRole.Library);
        var stick = await ScanAsync("stick", ScanRole.Source);
        var disk = await ScanAsync("platte", ScanRole.Source);
        var managed = new PlanOptions { ManagedAreas = [Path.Combine(_dir, "ablage-videos")] };

        var report = await CreateService(managed).ReportAsync([stick.Id, disk.Id, photos.Id, videos.Id], new DuplicateOptions(Verify: true));

        Assert.Equal(3, report.Groups.Count);
        Assert.All(report.Groups, g => Assert.Equal(DuplicateCertainty.Certain, g.Certainty));
        Assert.All(report.Groups, g => Assert.True(g.AcrossJobs));

        var library = report.Groups.Single(g => g.Size == 300_000);
        Assert.Equal(new[] { photos.Id, videos.Id }.Order(), library.JobIds);
        Assert.Equal(DuplicateAdvice.LibraryOnly, library.Advice);
        Assert.True(library.InLibrary);
        Assert.Equal(new[] { false, true }, library.Files.Select(f => f.Managed));

        var sources = report.Groups.Single(g => g.Size == 310_000);
        Assert.Equal(new[] { stick.Id, disk.Id }.Order(), sources.JobIds);
        Assert.False(sources.InLibrary);
        Assert.Equal(DuplicateAdvice.NoLibraryCopy, sources.Advice);

        var both = report.Groups.Single(g => g.Size == 320_000);
        Assert.True(both.InLibrary);
        Assert.Equal(DuplicateAdvice.SourceCopiesHeld, both.Advice);
        Assert.Contains(both.Files, f => f.Role == ScanRole.Source);
    }

    [Fact]
    public async Task AJobRootInsideAnother_DoesNotMakeAFileItsOwnCopy()
    {
        var content = Bytes(9, 200_000);
        Write("nas/fotos/bild.jpg", content);
        var outer = await ScanAsync("nas", ScanRole.Library);
        var inner = await ScanAsync("nas/fotos", ScanRole.Library);

        var report = await CreateService().ReportAsync([outer.Id, inner.Id]);

        Assert.Empty(report.Groups);
        Assert.Equal(0, report.Stages.FilesRead);
    }

    // ------------------------------------------------------------------ folders

    [Fact]
    public async Task ACopiedFolder_IsOneGroup_InsteadOfItsFiles()
    {
        var files = Enumerable.Range(0, 4).Select(i => Bytes(20 + i, 150_000 + i)).ToList();
        Write("disk/Projekt/plan.dwg", files[0]);
        Write("disk/Projekt/teile/a.step", files[1]);
        Write("disk/Projekt/teile/b.step", files[2]);
        Write("disk/Projekt/notizen.txt", Bytes(30, 1234)); // only here: three of four files still have a copy
        Write("disk/Sicherung 2020/Projekt Kopie/plan.dwg", files[0]);
        Write("disk/Sicherung 2020/Projekt Kopie/teile/a.step", files[1]);
        Write("disk/Sicherung 2020/Projekt Kopie/teile/b.step", files[2]);
        Write("disk/Sicherung 2020/andere.bin", Bytes(31, 5555));
        // A loose copy beside many other files stays a file group.
        Write("disk/Downloads/foto.jpg", files[3]);
        Write("disk/Bilder/foto.jpg", files[3]);
        for (var i = 0; i < 6; i++)
            Write($"disk/Downloads/x{i}.bin", Bytes(40 + i, 3000 + i));
        var job = await ScanAsync("disk", ScanRole.Source);

        var report = await CreateService().ReportAsync([job.Id], new DuplicateOptions(Verify: true));

        Assert.Equal(2, report.Groups.Count);
        var folder = report.Groups[0];
        Assert.Equal(DuplicateScope.Folders, folder.Scope);
        Assert.Equal(DuplicateCertainty.Certain, folder.Certainty);
        Assert.Equal(
            new[] { Path.Combine(_dir, "disk", "Projekt"), Path.Combine(_dir, "disk", "Sicherung 2020", "Projekt Kopie") }.Order(StringComparer.Ordinal),
            folder.Folders.Select(f => f.Path));
        Assert.Equal(2, folder.Copies);
        Assert.Equal(150_000L + 150_001 + 150_002, folder.WastedBytes);
        Assert.Equal(6, folder.Files.Count);
        Assert.Equal(new[] { 4, 3 }, folder.Folders.Select(f => f.Files));
        Assert.Equal(new[] { 3, 3 }, folder.Folders.Select(f => f.MatchedFiles));

        var loose = report.Groups[1];
        Assert.Equal(DuplicateScope.Files, loose.Scope);
        Assert.Equal(2, loose.Files.Count);
        Assert.Contains(loose.Files, f => f.FullPath.EndsWith(Path.Combine("Downloads", "foto.jpg"), StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the comparison is unchanged

    [Fact]
    public async Task TheComparison_ReusesTheChecksumsOfTheReport()
    {
        var content = Bytes(50, 700_000);
        Write("stick/a.mov", content);
        Write("ablage/a.mov", content);
        var stick = await ScanAsync("stick", ScanRole.Source);
        var library = await ScanAsync("ablage", ScanRole.Library);
        await CreateService().ReportAsync([stick.Id, library.Id], new DuplicateOptions(Verify: true));
        var calls = _hasher.Calls;

        var comparison = await new ContentComparisonService(_jobRepo, new ContentComparisonRepository(_db), _hasher,
            new FileSystemAccessor(), NullLogger<ContentComparisonService>.Instance).CompareAsync(stick.Id, library.Id);

        Assert.Equal(ContentState.BackedUp, Assert.Single(comparison.Files).State);
        Assert.Equal(calls, _hasher.Calls);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>An offset in the middle of a file of <paramref name="size"/> bytes that no sample range covers.</summary>
    private static int UnsampledOffset(long size)
    {
        var ranges = ContentHasher.SampleRanges(size);
        for (long at = size / 2; at < size; at++)
        {
            if (!ranges.Any(r => at >= r.Offset && at < r.Offset + r.Length))
                return (int)at;
        }
        throw new InvalidOperationException("Every byte is sampled.");
    }

    /// <summary>The real hasher, counting what it is asked to read.</summary>
    private sealed class CountingHasher(IContentHasher inner) : IContentHasher
    {
        public int Calls { get; private set; }
        public List<string> Paths { get; } = [];

        public FileStat Stat(string path) => inner.Stat(path);

        public Task<SampleResult> ComputeSampleAsync(string path, long expectedSize, CancellationToken ct)
        {
            Calls++;
            Paths.Add(path);
            return inner.ComputeSampleAsync(path, expectedSize, ct);
        }

        public Task<string> ComputeFullAsync(string path, long expectedSize, CancellationToken ct)
        {
            Calls++;
            Paths.Add(path);
            return inner.ComputeFullAsync(path, expectedSize, ct);
        }
    }
}
