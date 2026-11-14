using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
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
/// Comparison of a generated source folder with a generated library folder, both in a temp
/// directory, scanned with the real scan engine into a temp database.
/// https://github.com/crack666/smart_file_organizer/issues/24
/// </summary>
public class ContentComparisonTests : IDisposable
{
    private const int Big = 300 * 1024; // larger than head + tail, so the full checksum decides

    private readonly string _dir;
    private readonly string _source;
    private readonly string _library;
    private readonly DatabaseContext _db;
    private readonly ScanJobRepository _jobRepo;
    private readonly CountingHasher _hasher = new(new ContentHasher());
    private IFileSystemAccessor _fs = new FileSystemAccessor();

    public ContentComparisonTests()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_cmp_{Guid.NewGuid():N}");
        _source = Path.Combine(_dir, "stick");
        _library = Path.Combine(_dir, "ablage");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_library);

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

    private static string Write(string root, string relative, byte[] content)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative)); // normalises separators
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Write(string root, string relative, string text) =>
        Write(root, relative, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Source and library covering every state. Returns the paths by short name.
    /// </summary>
    private Dictionary<string, string> CreateStandardFixture()
    {
        var photo = Bytes(1, Big);
        var almost = Bytes(2, Big);
        var almostLibrary = (byte[])almost.Clone();
        almostLibrary[Big / 2] ^= 0xFF; // same size, same head and tail, different middle
        var copy = Bytes(3, 4000);

        var p = new Dictionary<string, string>
        {
            // backed_up, renamed and in another folder in the library
            ["photo"] = Write(_source, "DCIM/IMG_0001.jpg", photo),
            ["photoCopy"] = Write(_source, "Backup/IMG_0001 (1).jpg", photo),
            ["small"] = Write(_source, "notes/einkauf.txt", "Milch, Brot, Butter"),
            // new although head and tail agree with a library file
            ["almost"] = Write(_source, "DCIM/IMG_0002.jpg", almost),
            // differs: same name in a folder of the same name, other content (other size / same size)
            ["letter"] = Write(_source, "Briefe/vermieter.txt", "Version zwei, laenger"),
            ["sameSize"] = Write(_source, "Briefe/kuendigung.txt", "AAAA-1111"),
            // new, and a duplicate group within the source
            ["dupA"] = Write(_source, "Kopien/a.dat", copy),
            ["dupB"] = Write(_source, "Kopien/tief/b.dat", copy),
            ["unique"] = Write(_source, "unique.bin", Bytes(4, 5000))
        };

        Write(_library, "Fotos/2015 Skifahrt/skifahrt-01.jpg", photo);
        Write(_library, "Fotos/2015 Skifahrt/skifahrt-01-kopie.jpg", photo);
        Write(_library, "Dokumente/einkaufsliste.txt", "Milch, Brot, Butter");
        Write(_library, "Fotos/anderes.jpg", almostLibrary);
        Write(_library, "Archiv/Briefe/vermieter.txt", "Version eins");
        Write(_library, "Archiv/Briefe/kuendigung.txt", "BBBB-2222");
        return p;
    }

    private ContentComparisonService CreateService() =>
        new(_jobRepo, new ContentComparisonRepository(_db), _hasher, new FileSystemAccessor(),
            NullLogger<ContentComparisonService>.Instance);

    private async Task<ScanJob> ScanAsync(string root, ScanRole role)
    {
        var engine = new ScanEngine(_fs,
            new FileRepository(_db), new DirectoryRepository(_db), _jobRepo, NullLogger<ScanEngine>.Instance);
        var service = new ScanJobService(_jobRepo, engine, NullLogger<ScanJobService>.Instance);
        var job = await service.CreateJobAsync(root, role);
        return await service.RunAsync(job.Id);
    }

    private async Task<(ScanJob Source, ScanJob Library)> ScanBothAsync() =>
        (await ScanAsync(_source, ScanRole.Source), await ScanAsync(_library, ScanRole.Library));

    private static FileComparison Result(ComparisonReport report, string path) =>
        report.Files.Single(f => f.FullPath == path);

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Compare_SetsAllStates_AndFindsDuplicateGroup_EvenForRenamedFiles()
    {
        var p = CreateStandardFixture();
        var (source, library) = await ScanBothAsync();

        var report = await CreateService().CompareAsync(source.Id, library.Id);

        Assert.True(report.Header.Complete, string.Join("; ", report.Errors.Select(e => e.Message)));
        Assert.Empty(report.Errors);

        var photo = Result(report, p["photo"]);
        Assert.Equal(ContentState.BackedUp, photo.State);
        Assert.Equal(2, photo.MatchCount);
        // Evidence for a later deletion step: the shared full checksum and when it was established.
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p["photo"]))), photo.MatchHash);
        Assert.NotNull(photo.MatchVerifiedAt);
        Assert.True(photo.MatchVerifiedAt <= DateTime.UtcNow);
        Assert.Null(Result(report, p["letter"]).MatchHash); // differs carries no checksum evidence
        Assert.Equal(Path.Combine(_library, "Fotos", "2015 Skifahrt", "skifahrt-01-kopie.jpg"), photo.MatchPath);
        Assert.Equal(ContentState.BackedUp, Result(report, p["photoCopy"]).State);
        Assert.Equal(ContentState.BackedUp, Result(report, p["small"]).State);
        Assert.Equal(Path.Combine(_library, "Dokumente", "einkaufsliste.txt"), Result(report, p["small"]).MatchPath);

        Assert.Equal(ContentState.New, Result(report, p["almost"]).State);
        Assert.Equal(ContentState.New, Result(report, p["dupA"]).State);
        Assert.Equal(ContentState.New, Result(report, p["unique"]).State);

        var letter = Result(report, p["letter"]);
        Assert.Equal(ContentState.Differs, letter.State);
        Assert.Equal(Path.Combine(_library, "Archiv", "Briefe", "vermieter.txt"), letter.MatchPath);
        Assert.Equal(ContentState.Differs, Result(report, p["sameSize"]).State);

        Assert.Equal(2, report.DuplicateGroups.Count);
        var dup = Assert.Single(report.DuplicateGroups, g => g.Paths.Contains(p["dupA"]));
        Assert.Equal([p["dupA"], p["dupB"]], dup.Paths);
        Assert.Equal(dup.Group, Result(report, p["dupB"]).DuplicateGroup);
        Assert.Contains(report.DuplicateGroups, g => g.Paths.SequenceEqual(new[] { p["photoCopy"], p["photo"] }.Order(StringComparer.Ordinal)));
        Assert.Null(Result(report, p["unique"]).DuplicateGroup);

        // Stored and readable again.
        var stored = await CreateService().GetStoredAsync(source.Id, library.Id);
        Assert.NotNull(stored);
        Assert.Equal(report.Files.Select(f => (f.FullPath, f.State, f.MatchPath, f.DuplicateGroup)),
            stored!.Files.Select(f => (f.FullPath, f.State, f.MatchPath, f.DuplicateGroup)));
        Assert.Equal(photo.MatchHash, Result(stored, p["photo"]).MatchHash);
        Assert.NotNull(Result(stored, p["photo"]).MatchVerifiedAt);
    }

    [Fact]
    public async Task Compare_SummarisesDirectories_IncludingSubdirectories()
    {
        var p = CreateStandardFixture();
        var (source, library) = await ScanBothAsync();

        var report = await CreateService().CompareAsync(source.Id, library.Id);

        var root = Assert.Single(report.Directories, d => d.Path == _source);
        Assert.Equal(9, root.Files);
        Assert.Equal(3, root.BackedUp);
        Assert.Equal(6, root.NotBackedUp);

        var backup = Assert.Single(report.Directories, d => d.Path == Path.GetDirectoryName(p["photoCopy"]));
        Assert.Equal(0, backup.NotBackedUp);
        Assert.Equal(1.0, backup.BackedUpShare);

        var copies = Assert.Single(report.Directories, d => d.Path == Path.GetDirectoryName(p["dupA"]));
        Assert.Equal(2, copies.Files); // includes the subdirectory
        Assert.Equal(2, copies.New);
    }

    [Fact]
    public async Task SecondRun_WithoutChanges_ComputesNoChecksum_AndChangedFileIsRecomputed()
    {
        var p = CreateStandardFixture();
        var (source, library) = await ScanBothAsync();
        var service = CreateService();

        var first = await service.CompareAsync(source.Id, library.Id);
        Assert.True(first.Hashing.HeadTailComputed > 0);
        Assert.True(first.Hashing.FullComputed > 0);
        var callsAfterFirst = _hasher.Calls;

        var second = await service.CompareAsync(source.Id, library.Id);

        Assert.Equal(0, second.Hashing.HeadTailComputed);
        Assert.Equal(0, second.Hashing.FullComputed);
        Assert.True(second.Hashing.Reused > 0);
        Assert.Equal(callsAfterFirst, _hasher.Calls);
        Assert.Equal(first.Files.Select(f => (f.FullPath, f.State)), second.Files.Select(f => (f.FullPath, f.State)));

        // Same size, new content and a new modification time: only this file is read again.
        var inLibrary = Path.Combine(_library, "Archiv", "Briefe", "kuendigung.txt");
        File.WriteAllText(inLibrary, "AAAA-1111");
        File.SetLastWriteTimeUtc(inLibrary, DateTime.UtcNow.AddMinutes(5));

        var third = await service.CompareAsync(source.Id, library.Id);

        Assert.Equal(1, third.Hashing.HeadTailComputed);
        Assert.Equal(ContentState.BackedUp, Result(third, p["sameSize"]).State);
    }

    [Fact]
    public async Task LibraryCandidateUnreadable_IsError_NotNew_AndRunIsIncomplete()
    {
        var p = CreateStandardFixture();
        var (source, library) = await ScanBothAsync();
        // Same size as the source letter, but gone (as if the network path dropped) after the scan.
        var vanished = Path.Combine(_library, "Archiv", "Briefe", "kuendigung.txt");
        File.Delete(vanished);

        var report = await CreateService().CompareAsync(source.Id, library.Id);

        var file = Result(report, p["sameSize"]);
        Assert.Equal(ContentState.Error, file.State);
        Assert.Contains(vanished, file.Error);
        Assert.False(report.Header.Complete);
        Assert.Contains(report.Errors, e => e.Role == ScanRole.Library && e.Path == vanished);
        Assert.Contains(report.Errors, e => e.Role == ScanRole.Source && e.Path == p["sameSize"]);
        // Files of other sizes are not affected.
        Assert.Equal(ContentState.New, Result(report, p["unique"]).State);
    }

    [Fact]
    public async Task SourceFileGoneOrChanged_IsError()
    {
        var p = CreateStandardFixture();
        var (source, library) = await ScanBothAsync();
        File.Delete(p["unique"]);
        File.AppendAllText(p["small"], " und Eier");

        var report = await CreateService().CompareAsync(source.Id, library.Id);

        Assert.Equal(ContentState.Error, Result(report, p["unique"]).State);
        var changed = Result(report, p["small"]);
        Assert.Equal(ContentState.Error, changed.State);
        Assert.Contains("scan the source again", changed.Error);
        Assert.False(report.Header.Complete);
    }

    [Fact]
    public async Task UnreadableDirectory_IsRecordedByTheScan_AndReportedByTheComparison()
    {
        CreateStandardFixture();
        var locked = Path.Combine(_library, "Fotos");
        _fs = new FailingFileSystemAccessor(locked);
        var (source, library) = await ScanBothAsync();

        Assert.Equal(JobStatus.Completed, library.Status);
        Assert.Equal(1, library.ErrorCount);

        var report = await CreateService().CompareAsync(source.Id, library.Id);

        var error = Assert.Single(report.Errors, e => e.Kind == ComparisonIssueKind.Scan);
        Assert.Equal(ScanRole.Library, error.Role);
        Assert.Equal(locked, error.Path);
        Assert.False(report.Header.Complete);
    }

    [Fact]
    public async Task WithoutLibrary_OnlyDuplicatesAreDetermined()
    {
        var p = CreateStandardFixture();
        var source = await ScanAsync(_source, ScanRole.Source);

        var report = await CreateService().CompareAsync(source.Id, null);

        Assert.True(report.Header.Complete);
        Assert.All(report.Files, f => Assert.Null(f.State));
        Assert.Equal(2, report.DuplicateGroups.Count);
        Assert.Empty(report.Directories);
        Assert.DoesNotContain(_hasher.Paths, path => path.StartsWith(_library, StringComparison.Ordinal));
        Assert.DoesNotContain(p["unique"], _hasher.Paths); // a unique size is never read
    }

    [Fact]
    public async Task EmptyLibrary_MakesEverythingNew_WithoutReadingAnything()
    {
        Write(_source, "a.txt", "eins");
        Write(_source, "b.txt", "zweiundzwanzig");
        var (source, library) = await ScanBothAsync();

        var report = await CreateService().CompareAsync(source.Id, library.Id);

        Assert.True(report.Header.Complete);
        Assert.All(report.Files, f => Assert.Equal(ContentState.New, f.State));
        Assert.Equal(0, _hasher.Calls);
    }

    [Fact]
    public async Task SourceInsideLibrary_FileDoesNotBackUpItself()
    {
        var inner = Path.Combine(_library, "Eingang");
        var path = Write(inner, "scan.pdf", Bytes(9, 2000));
        var source = await ScanAsync(inner, ScanRole.Source);
        var library = await ScanAsync(_library, ScanRole.Library);

        var report = await CreateService().CompareAsync(source.Id, library.Id);

        Assert.Equal(ContentState.New, Result(report, path).State);
    }

    [Fact]
    public async Task WrongRoleOrUnfinishedScan_IsRefused()
    {
        CreateStandardFixture();
        var (source, library) = await ScanBothAsync();
        var service = CreateService();

        var swapped = await Assert.ThrowsAsync<ComparisonException>(() => service.CompareAsync(library.Id, source.Id));
        Assert.Equal(ComparisonFailure.WrongRole, swapped.Failure);

        var unfinished = await new ScanJobService(_jobRepo, null!, NullLogger<ScanJobService>.Instance)
            .CreateJobAsync(_library, ScanRole.Library);
        var notDone = await Assert.ThrowsAsync<ComparisonException>(() => service.CompareAsync(source.Id, unfinished.Id));
        Assert.Equal(ComparisonFailure.ScanNotCompleted, notDone.Failure);

        var missing = await Assert.ThrowsAsync<ComparisonException>(() => service.CompareAsync(source.Id, 999));
        Assert.Equal(ComparisonFailure.JobNotFound, missing.Failure);
    }

    [Fact]
    public async Task Role_IsStoredAsFixedString_AndReadBack()
    {
        var job = await new ScanJobService(_jobRepo, null!, NullLogger<ScanJobService>.Instance)
            .CreateJobAsync(_library, ScanRole.Library);

        await using var conn = _db.CreateConnection();
        Assert.Equal("library", await conn.ExecuteScalarAsync<string>("SELECT role FROM scan_jobs WHERE id = @id", new { id = job.Id }));
        Assert.Equal(ScanRole.Library, (await _jobRepo.GetByIdAsync(job.Id))!.Role);
    }

    [Fact]
    public async Task DeletingAJob_RemovesItsComparisons()
    {
        CreateStandardFixture();
        var (source, library) = await ScanBothAsync();
        await CreateService().CompareAsync(source.Id, library.Id);

        await _jobRepo.DeleteAsync(library.Id);

        Assert.Null(await CreateService().GetStoredAsync(source.Id, library.Id));
        await using var conn = _db.CreateConnection();
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM comparison_files"));
    }

    // ------------------------------------------------------------------ fakes

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

    /// <summary>The real file system, except that one directory cannot be listed.</summary>
    private sealed class FailingFileSystemAccessor(string lockedDirectory) : IFileSystemAccessor
    {
        private readonly FileSystemAccessor _inner = new();

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public bool FileExists(string path) => _inner.FileExists(path);
        public IEnumerable<string> EnumerateDirectories(string path) => _inner.EnumerateDirectories(path);
        public IEnumerable<string> EnumerateFiles(string path) => _inner.EnumerateFiles(path);

        public IReadOnlyList<string> ListFiles(string path) =>
            path == lockedDirectory ? throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.") : _inner.ListFiles(path);

        public IReadOnlyList<string> ListDirectories(string path) => _inner.ListDirectories(path);
        public long GetFileSize(string path) => _inner.GetFileSize(path);
        public DateTime GetLastWriteTime(string path) => _inner.GetLastWriteTime(path);
        public string GetFileName(string path) => _inner.GetFileName(path);
        public string GetDirectoryName(string path) => _inner.GetDirectoryName(path);
        public string GetExtension(string path) => _inner.GetExtension(path);
        public string GetRelativePath(string relativeTo, string path) => _inner.GetRelativePath(relativeTo, path);
        public byte[] ReadPrefix(string path, int maxBytes) => _inner.ReadPrefix(path, maxBytes);
    }
}
