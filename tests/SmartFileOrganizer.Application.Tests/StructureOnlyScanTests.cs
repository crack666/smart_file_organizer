using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure;
using SmartFileOrganizer.Infrastructure.Persistence;
using SmartFileOrganizer.Scanning.Engine;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// <c>sfo scan --structure-only</c> records the tree without opening a file
/// (https://github.com/crack666/smart_file_organizer/issues/29). Proven through the file system
/// interface: the only member that opens a file is <see cref="IFileSystemAccessor.ReadPrefix"/>.
/// </summary>
public class StructureOnlyScanTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root;
    private readonly DatabaseContext _db;

    public StructureOnlyScanTests()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_shape_{Guid.NewGuid():N}");
        _root = Path.Combine(_dir, "tree");
        Directory.CreateDirectory(Path.Combine(_root, "Documents"));
        Directory.CreateDirectory(Path.Combine(_root, "Photos", "Ski trip 2015"));
        // Small text files are what the sensitive-file header probe reads in a normal scan.
        File.WriteAllText(Path.Combine(_root, "Documents", "notes.txt"), "shopping list");
        File.WriteAllText(Path.Combine(_root, "Documents", "server.conf"), "port=1");
        File.WriteAllText(Path.Combine(_root, "Documents", "passwords.txt"), "not a real password");
        File.WriteAllBytes(Path.Combine(_root, "Photos", "Ski trip 2015", "IMG_0001.jpg"), new byte[64]);
        _db = new DatabaseContext(Path.Combine(_dir, "data.db"), NullLogger<DatabaseContext>.Instance);
        _db.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<(ScanJob Job, CountingFileSystem Fs)> ScanAsync(bool structureOnly)
    {
        var jobs = new ScanJobRepository(_db);
        var job = await jobs.CreateAsync(new ScanJob
        {
            RootPath = _root,
            Status = JobStatus.Created,
            CreatedAt = DateTime.UtcNow,
            StructureOnly = structureOnly
        });
        var fs = new CountingFileSystem(new FileSystemAccessor(), forbidOpening: structureOnly);
        var engine = new ScanEngine(fs, new FileRepository(_db), new DirectoryRepository(_db), jobs, NullLogger<ScanEngine>.Instance);
        await engine.RunAsync(job, new Progress<ScanProgress>(), CancellationToken.None);
        return ((await jobs.GetByIdAsync(job.Id))!, fs);
    }

    [Fact]
    public async Task StructureOnlyScan_OpensNoFile_AndStillRecordsTheTree()
    {
        var (job, fs) = await ScanAsync(structureOnly: true);

        Assert.Equal(0, fs.OpenedFiles);
        Assert.True(job.StructureOnly);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(4, job.ProcessedFiles);

        // Name rules still apply without opening the file.
        var files = await new FileRepository(_db).GetByDirectoryAsync(job.Id, Path.Combine(_root, "Documents"));
        Assert.Contains(files, f => f.Name == "passwords.txt" && f.SensitiveMarkers.HasFlag(SensitiveMarker.Credentials));
    }

    [Fact]
    public async Task NormalScan_ProbesSmallFiles_SoTheCounterIsMeaningful()
    {
        var (job, fs) = await ScanAsync(structureOnly: false);

        Assert.False(job.StructureOnly);
        Assert.True(fs.OpenedFiles > 0, "A normal scan reads the header of small text files.");
    }

    /// <summary>Passes everything through and counts the calls that open a file.</summary>
    private sealed class CountingFileSystem(IFileSystemAccessor inner, bool forbidOpening) : IFileSystemAccessor
    {
        public int OpenedFiles { get; private set; }

        public byte[] ReadPrefix(string path, int maxBytes)
        {
            OpenedFiles++;
            if (forbidOpening)
                throw new InvalidOperationException($"A structure-only scan opened {path}.");
            return inner.ReadPrefix(path, maxBytes);
        }

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public IEnumerable<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);
        public IEnumerable<string> EnumerateFiles(string path) => inner.EnumerateFiles(path);
        public long GetFileSize(string path) => inner.GetFileSize(path);
        public DateTime GetLastWriteTime(string path) => inner.GetLastWriteTime(path);
        public string GetFileName(string path) => inner.GetFileName(path);
        public string GetDirectoryName(string path) => inner.GetDirectoryName(path);
        public string GetExtension(string path) => inner.GetExtension(path);
        public string GetRelativePath(string relativeTo, string path) => inner.GetRelativePath(relativeTo, path);
    }
}
