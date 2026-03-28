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
/// Integration tests for the full scan → SQLite → query pipeline.
/// Uses a real SQLite file on disk (temp path) and a FakeFileSystem to control
/// what "files" exist, so no real disk I/O is needed.
/// </summary>
public class ScanPipelineIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseContext _db;
    private readonly ScanJobRepository _jobRepo;
    private readonly FileRepository _fileRepo;
    private readonly DirectoryRepository _dirRepo;

    public ScanPipelineIntegrationTests()
    {
        // Enable Dapper snake_case mapping (same as production)
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        _dbPath = Path.Combine(Path.GetTempPath(), $"sfo_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        _db.InitializeAsync().GetAwaiter().GetResult();

        _jobRepo = new ScanJobRepository(_db);
        _fileRepo = new FileRepository(_db);
        _dirRepo = new DirectoryRepository(_db);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<ScanJob> CreateJobAsync(string root)
    {
        return await _jobRepo.CreateAsync(new ScanJob
        {
            RootPath = root,
            Status = JobStatus.Created,
            CreatedAt = DateTime.UtcNow
        });
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task ScanJob_CreateAndRead_RoundTrips()
    {
        var job = await CreateJobAsync("/TestRoot");

        Assert.True(job.Id > 0);

        var loaded = await _jobRepo.GetByIdAsync(job.Id);
        Assert.NotNull(loaded);
        Assert.Equal("/TestRoot", loaded!.RootPath);
        Assert.Equal(JobStatus.Created, loaded.Status);
    }

    [Fact]
    public async Task FileNodes_InsertAndQueryByDirectory_ReturnsCorrectFiles()
    {
        var root = "/TestRoot";
        var subDir = "/TestRoot/Photos";
        var job = await CreateJobAsync(root);

        var files = new List<FileNode>
        {
            MakeFile(job.Id, root, "/TestRoot/readme.txt", FileType.Document),
            MakeFile(job.Id, subDir, "/TestRoot/Photos/img001.jpg", FileType.Image),
            MakeFile(job.Id, subDir, "/TestRoot/Photos/img002.jpg", FileType.Image),
        };
        await _fileRepo.InsertBatchAsync(files);

        // Query root directory
        var rootFiles = await _fileRepo.GetByDirectoryAsync(job.Id, root);
        Assert.Single(rootFiles);
        Assert.Equal("readme.txt", rootFiles[0].Name);

        // Query sub-directory
        var subFiles = await _fileRepo.GetByDirectoryAsync(job.Id, subDir);
        Assert.Equal(2, subFiles.Count);
        Assert.All(subFiles, f => Assert.Equal(subDir, f.ParentPath));
    }

    [Fact]
    public async Task FileNodes_QueryByDirectory_WrongJobId_ReturnsEmpty()
    {
        var root = "/TestRoot";
        var job = await CreateJobAsync(root);

        await _fileRepo.InsertBatchAsync([MakeFile(job.Id, root, "/TestRoot/file.txt", FileType.Document)]);

        // Different job id
        var result = await _fileRepo.GetByDirectoryAsync(job.Id + 99, root);
        Assert.Empty(result);
    }

    [Fact]
    public async Task FileNodes_PathMatchIsExact_SubfolderDoesNotLeakToParent()
    {
        var root = "/TestRoot";
        var sub = "/TestRoot/Sub";
        var job = await CreateJobAsync(root);

        // Only add file in sub — should NOT appear when querying root
        await _fileRepo.InsertBatchAsync([MakeFile(job.Id, sub, "/TestRoot/Sub/deep.txt", FileType.Document)]);

        var rootFiles = await _fileRepo.GetByDirectoryAsync(job.Id, root);
        Assert.Empty(rootFiles);

        var subFiles = await _fileRepo.GetByDirectoryAsync(job.Id, sub);
        Assert.Single(subFiles);
    }

    [Fact]
    public async Task ScanEngine_WithFakeFs_PopulatesDbCorrectly()
    {
        var root = "/Scan";
        var job = await CreateJobAsync(root);

        var fakeFs = new FakeFileSystemAccessor(new Dictionary<string, string[]>
        {
            ["/Scan"]           = ["/Scan/doc.pdf", "/Scan/photo.jpg"],
            ["/Scan/Videos"]    = ["/Scan/Videos/clip.mp4"],
            ["/Scan/Videos/Sub"] = ["/Scan/Videos/Sub/nested.txt"],
        });

        var heuristics = new HeuristicsEngine(new HeuristicsOptions());
        var engine = new ScanEngine(fakeFs, heuristics, _fileRepo, _dirRepo, _jobRepo, NullLogger<ScanEngine>.Instance);

        var progress = new List<ScanProgress>();
        await engine.RunAsync(job, new Progress<ScanProgress>(p => progress.Add(p)), CancellationToken.None);

        // Job should be Completed
        var finishedJob = await _jobRepo.GetByIdAsync(job.Id);
        Assert.Equal(JobStatus.Completed, finishedJob!.Status);
        Assert.Equal(4, finishedJob.ProcessedFiles); // doc.pdf, photo.jpg, clip.mp4, nested.txt

        // Files in root directory
        var rootFiles = await _fileRepo.GetByDirectoryAsync(job.Id, "/Scan");
        Assert.Equal(2, rootFiles.Count);
        Assert.Contains(rootFiles, f => f.Name == "doc.pdf");
        Assert.Contains(rootFiles, f => f.Name == "photo.jpg");

        // Files in Videos subdir
        var videoFiles = await _fileRepo.GetByDirectoryAsync(job.Id, "/Scan/Videos");
        Assert.Single(videoFiles);
        Assert.Equal("clip.mp4", videoFiles[0].Name);

        // FileType detection
        Assert.Equal(FileType.Document, rootFiles.First(f => f.Name == "doc.pdf").FileType);
        Assert.Equal(FileType.Image, rootFiles.First(f => f.Name == "photo.jpg").FileType);
        Assert.Equal(FileType.Video, videoFiles[0].FileType);

        // Directories in DB
        var rootDirs = await _dirRepo.GetRootsAsync(job.Id);
        Assert.Single(rootDirs);
        Assert.Equal("/Scan", rootDirs[0].FullPath);

        var children = await _dirRepo.GetChildrenAsync(job.Id, "/Scan");
        Assert.Single(children);
        Assert.Equal("/Scan/Videos", children[0].FullPath);
    }

    [Fact]
    public async Task ScanEngine_Resume_SkipsAlreadyScannedDirs()
    {
        var root = "/Scan";
        var job = await CreateJobAsync(root);

        var fakeFs = new FakeFileSystemAccessor(new Dictionary<string, string[]>
        {
            ["/Scan"] = ["/Scan/file.txt"],
        });

        var heuristics = new HeuristicsEngine(new HeuristicsOptions());
        var engine = new ScanEngine(fakeFs, heuristics, _fileRepo, _dirRepo, _jobRepo, NullLogger<ScanEngine>.Instance);

        // Run scan once
        await engine.RunAsync(job, new Progress<ScanProgress>(_ => { }), CancellationToken.None);
        var count1 = await _fileRepo.CountByJobAsync(job.Id);
        Assert.Equal(1, count1); // sanity check: exactly one file

        // Re-running should skip already-scanned directory and not duplicate rows
        job.Status = JobStatus.Created;
        await _jobRepo.UpdateAsync(job);
        await engine.RunAsync(job, new Progress<ScanProgress>(_ => { }), CancellationToken.None);
        var count2 = await _fileRepo.CountByJobAsync(job.Id);

        Assert.Equal(count1, count2); // No duplicates
    }

    // ------------------------------------------------------------------ helpers

    private static FileNode MakeFile(long jobId, string parentPath, string fullPath, FileType type)
    {
        var lastSlash = fullPath.LastIndexOf('/');
        var name = lastSlash >= 0 ? fullPath[(lastSlash + 1)..] : fullPath;
        var dotIdx = name.LastIndexOf('.');
        var ext = dotIdx >= 0 ? name[dotIdx..].ToLowerInvariant() : string.Empty;
        return new FileNode
        {
            JobId = jobId,
            FullPath = fullPath,
            Name = name,
            ParentPath = parentPath,
            RootPath = parentPath,
            RelativePath = name,
            RelativeDir = ".",
            Depth = 1,
            Size = 1024,
            LastWriteTime = DateTime.UtcNow,
            Extension = ext,
            FileType = type,
            Status = FileNodeStatus.Discovered,
            ScannedAt = DateTime.UtcNow
        };
    }
}

/// <summary>
/// Fake file system: maps directory paths to the list of file paths they contain.
/// Subdirectories are inferred from the keys.
/// Uses forward-slash paths that work on both Windows and Linux.
/// </summary>
internal class FakeFileSystemAccessor : IFileSystemAccessor
{
    // dirPath → list of file paths
    private readonly Dictionary<string, string[]> _dirFiles;

    // dirPath → list of immediate subdirectory paths (built by explicit parent→child edges)
    private readonly Dictionary<string, List<string>> _dirChildren;

    public FakeFileSystemAccessor(Dictionary<string, string[]> dirFiles)
    {
        _dirFiles = dirFiles;
        _dirChildren = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // Build parent→children index by splitting the path manually on '/'
        foreach (var dir in dirFiles.Keys)
        {
            var parent = GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent) && dirFiles.ContainsKey(parent))
            {
                if (!_dirChildren.ContainsKey(parent))
                    _dirChildren[parent] = [];
                _dirChildren[parent].Add(dir);
            }
        }
    }

    public bool DirectoryExists(string path) => _dirFiles.ContainsKey(path);
    public bool FileExists(string path) => _dirFiles.Values.Any(arr => arr.Contains(path));

    public IEnumerable<string> EnumerateFiles(string path)
        => _dirFiles.TryGetValue(path, out var files) ? files : [];

    public IEnumerable<string> EnumerateDirectories(string path)
        => _dirChildren.TryGetValue(path, out var children) ? children : [];

    public long GetFileSize(string path) => 1024;
    public DateTime GetLastWriteTime(string path) => DateTime.UtcNow;

    // Use forward-slash splitting so these work on both Windows and Linux
    public string GetFileName(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    public string GetDirectoryName(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx > 0 ? path[..idx] : string.Empty;
    }

    public string GetExtension(string path)
    {
        var name = GetFileName(path);
        var dotIdx = name.LastIndexOf('.');
        return dotIdx >= 0 ? name[dotIdx..].ToLowerInvariant() : string.Empty;
    }

    public string GetRelativePath(string relativeTo, string path)
    {
        if (path.StartsWith(relativeTo + "/", StringComparison.Ordinal))
            return path[(relativeTo.Length + 1)..];
        return path;
    }
}
