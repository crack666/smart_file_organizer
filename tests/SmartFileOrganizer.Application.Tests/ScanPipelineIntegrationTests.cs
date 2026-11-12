using Dapper;
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

        var engine = new ScanEngine(fakeFs, _fileRepo, _dirRepo, _jobRepo, NullLogger<ScanEngine>.Instance);

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
    public async Task ScanEngine_RootUnderAppData_ScansPartialWords_SkipsWholeSegmentsWithReason()
    {
        var root = "/home/alice/AppData/Scan";
        var job = await CreateJobAsync(root);

        var fakeFs = new FakeFileSystemAccessor(new Dictionary<string, string[]>
        {
            [root]                   = ["/home/alice/AppData/Scan/a.txt"],
            [root + "/Combinations"] = ["/home/alice/AppData/Scan/Combinations/b.txt"],
            [root + "/bin"]          = ["/home/alice/AppData/Scan/bin/c.dll"],
        });

        var engine = new ScanEngine(fakeFs, _fileRepo, _dirRepo, _jobRepo, NullLogger<ScanEngine>.Instance);
        await engine.RunAsync(job, new Progress<ScanProgress>(_ => { }), CancellationToken.None);

        var finishedJob = await _jobRepo.GetByIdAsync(job.Id);
        Assert.Equal(2, finishedJob!.ProcessedFiles); // a.txt, b.txt

        var combinations = await _dirRepo.GetByPathAsync(job.Id, root + "/Combinations");
        Assert.Equal(DirectoryStatus.Scanned, combinations!.DirStatus);

        var bin = await _dirRepo.GetByPathAsync(job.Id, root + "/bin");
        Assert.Equal(DirectoryStatus.Skip, bin!.DirStatus);
        Assert.Equal("Skip: matches skip rule 'bin' of rules 'default': build output", bin.DirReason);
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

        var engine = new ScanEngine(fakeFs, _fileRepo, _dirRepo, _jobRepo, NullLogger<ScanEngine>.Instance);

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

    // ------------------------------------------------------------------ rules (#21)

    private ScanEngine NewEngine(IFileSystemAccessor fs) =>
        new(fs, _fileRepo, _dirRepo, _jobRepo, NullLogger<ScanEngine>.Instance);

    private ScanJobService NewJobService(IFileSystemAccessor fs) =>
        new(_jobRepo, NewEngine(fs), NullLogger<ScanJobService>.Instance);

    private static ScanRulesProfile Profile(string name, params ScanRule[] rules) =>
        ScanRulesFile.ToProfile(new ScanRuleSet { Name = name, Rules = rules }, $"{name}.json");

    /// <summary>A fake tree where every folder on the way is a key, so the fake file system links them.</summary>
    private static Dictionary<string, string[]> Tree(params (string Dir, string[] Files)[] dirs)
    {
        var tree = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (dir, files) in dirs)
        {
            for (var parent = dir; parent.Length > 0; parent = parent[..Math.Max(0, parent.LastIndexOf('/'))])
                tree.TryAdd(parent, []);
            tree[dir] = files;
        }
        return tree;
    }

    [Fact]
    public async Task ScanEngine_JobWithoutRules_RecordsTheShippedRules()
    {
        var job = await CreateJobAsync("/Scan");

        await NewEngine(new FakeFileSystemAccessor(Tree(("/Scan", ["/Scan/a.txt"])))).RunAsync(job, new Progress<ScanProgress>(_ => { }), CancellationToken.None);

        var loaded = await _jobRepo.GetByIdAsync(job.Id);
        Assert.Equal("default", loaded!.RulesName);
        Assert.Equal(ScanRulesFile.BuiltInSource, loaded.RulesSource);
        Assert.Equal(ScanRulesFile.Hash(ScanRulesFile.Default), loaded.RulesHash);
        Assert.Equal(ScanRulesFile.Default.Rules.Count, ScanRulesFile.FromProfile(loaded.Rules!).Rules.Count);
    }

    // Beyond shallow_from the content decides: a photo folder is entered, a program folder recorded shallow.
    [Fact]
    public async Task ScanEngine_BeyondShallowDepth_EntersPhotoFolder_RecordsProgramFolderShallow()
    {
        const string deep = "/Scan/1/2/3/4/5";
        var fs = new FakeFileSystemAccessor(Tree(
            (deep + "/Urlaubsfotos", [deep + "/Urlaubsfotos/strand.jpg"]),
            (deep + "/Urlaubsfotos/Tag2", [deep + "/Urlaubsfotos/Tag2/berg.jpg"]),
            (deep + "/Tool", [deep + "/Tool/tool.exe", deep + "/Tool/tool.dll"]),
            (deep + "/Tool/Plugins", [deep + "/Tool/Plugins/p.dll"])));
        var job = await CreateJobAsync("/Scan");

        await NewEngine(fs).RunAsync(job, new Progress<ScanProgress>(_ => { }), CancellationToken.None);

        var photos = await _dirRepo.GetByPathAsync(job.Id, deep + "/Urlaubsfotos");
        Assert.Equal(DirectoryStatus.Scanned, photos!.DirStatus);
        Assert.Contains("user file", photos.DirReason);
        Assert.Equal(DirectoryStatus.Scanned, (await _dirRepo.GetByPathAsync(job.Id, deep + "/Urlaubsfotos/Tag2"))!.DirStatus);

        var tool = await _dirRepo.GetByPathAsync(job.Id, deep + "/Tool");
        Assert.Equal(DirectoryStatus.Shallow, tool!.DirStatus);
        Assert.Contains("program folder", tool.DirReason);
        Assert.Equal(2, tool.DirectFileCount);
        Assert.Equal(1, tool.SubdirCount);   // counted, not entered
        Assert.Null(await _dirRepo.GetByPathAsync(job.Id, deep + "/Tool/Plugins"));
    }

    // The "done when" of #21: own rules scan a folder the default skips and record shallow one it scans.
    [Fact]
    public async Task ScanEngine_OwnRules_ScanSkippedFolder_AndRecordScannedFolderShallow()
    {
        var fs = new FakeFileSystemAccessor(Tree(
            ("/Scan/bin", ["/Scan/bin/export.csv"]),
            ("/Scan/Games", ["/Scan/Games/readme.txt"]),
            ("/Scan/Games/Quake", ["/Scan/Games/Quake/save.dat"])));

        var withDefault = await CreateJobAsync("/Scan");
        await NewEngine(fs).RunAsync(withDefault, new Progress<ScanProgress>(_ => { }), CancellationToken.None);
        Assert.Equal(DirectoryStatus.Skip, (await _dirRepo.GetByPathAsync(withDefault.Id, "/Scan/bin"))!.DirStatus);
        Assert.Equal(DirectoryStatus.Scanned, (await _dirRepo.GetByPathAsync(withDefault.Id, "/Scan/Games"))!.DirStatus);

        var own = Profile("laptop", new ScanRule("bin", ScanRuleAction.UserData, Why: "exports live here"),
                                    new ScanRule("Games", ScanRuleAction.Shallow, Why: "installed games"));
        var withOwn = await NewJobService(fs).CreateJobAsync("/Scan", own);
        await NewEngine(fs).RunAsync(withOwn, new Progress<ScanProgress>(_ => { }), CancellationToken.None);

        var bin = await _dirRepo.GetByPathAsync(withOwn.Id, "/Scan/bin");
        Assert.Equal(DirectoryStatus.Scanned, bin!.DirStatus);
        Assert.Contains("rules 'laptop'", bin.DirReason);
        Assert.Contains("exports live here", bin.DirReason);
        var games = await _dirRepo.GetByPathAsync(withOwn.Id, "/Scan/Games");
        Assert.Equal(DirectoryStatus.Shallow, games!.DirStatus);
        Assert.Equal(1, games.SubdirCount);
        Assert.Null(await _dirRepo.GetByPathAsync(withOwn.Id, "/Scan/Games/Quake"));
        Assert.Equal("laptop", (await _jobRepo.GetByIdAsync(withOwn.Id))!.RulesName);
    }

    [Fact]
    public async Task RunWithChangedRules_ReopensFinishedJob_EntersFormerSkipAndShallow_WithoutDuplicates()
    {
        var fs = new FakeFileSystemAccessor(Tree(
            ("/Scan/bin", ["/Scan/bin/x.txt"]),
            ("/Scan/Archive", ["/Scan/Archive/a.txt"]),
            ("/Scan/Archive/Old", ["/Scan/Archive/Old/o.txt"])));
        var service = NewJobService(fs);
        var narrow = Profile("narrow", new ScanRule("bin", ScanRuleAction.Skip), new ScanRule("Archive", ScanRuleAction.Shallow));
        var job = await service.CreateJobAsync("/Scan", narrow);

        var first = await service.RunAsync(job.Id);
        Assert.Equal(JobStatus.Completed, first.Status);
        Assert.Equal(1, first.ProcessedFiles);   // a.txt; bin skipped, Old not entered

        // Same rules: a finished job stays as it is.
        Assert.Equal(1, (await service.RunAsync(job.Id, narrow, null)).ProcessedFiles);

        var wide = Profile("wide");
        var second = await service.RunAsync(job.Id, wide, null);

        Assert.Equal(JobStatus.Completed, second.Status);
        Assert.Equal("wide", second.RulesName);
        Assert.Equal(3, await _fileRepo.CountByJobAsync(job.Id));
        Assert.Equal(3, second.ProcessedFiles);

        var bin = await _dirRepo.GetByPathAsync(job.Id, "/Scan/bin");
        Assert.Equal(DirectoryStatus.Scanned, bin!.DirStatus);
        Assert.Contains("re-evaluated on resume", bin.DirReason);
        Assert.Contains("before: Skip: matches skip rule 'bin' of rules 'narrow'", bin.DirReason);
        var archive = await _dirRepo.GetByPathAsync(job.Id, "/Scan/Archive");
        Assert.Equal(DirectoryStatus.Scanned, archive!.DirStatus);
        Assert.Equal(1, archive.DirectFileCount);
        Assert.Equal(DirectoryStatus.Scanned, (await _dirRepo.GetByPathAsync(job.Id, "/Scan/Archive/Old"))!.DirStatus);

        // Back to the narrow rules: nothing already collected is dropped.
        var third = await service.RunAsync(job.Id, narrow, null);
        Assert.Equal(JobStatus.Completed, third.Status);
        Assert.Equal(3, await _fileRepo.CountByJobAsync(job.Id));
        Assert.Equal(DirectoryStatus.Scanned, (await _dirRepo.GetByPathAsync(job.Id, "/Scan/bin"))!.DirStatus);
    }

    // A job paused before rules were stored still holds decisions of the old hard-coded rules,
    // for example a photo folder recorded shallow only because of its depth.
    [Fact]
    public async Task Resume_JobFromBeforeRules_ReevaluatesOldShallowDecision()
    {
        const string photos = "/Scan/1/2/3/4/5/Urlaubsfotos";
        var fs = new FakeFileSystemAccessor(Tree(
            (photos, [photos + "/strand.jpg"]),
            (photos + "/Tag2", [photos + "/Tag2/berg.jpg"])));
        var job = await CreateJobAsync("/Scan");
        await _dirRepo.InsertBatchAsync([new DirectoryNode
        {
            JobId = job.Id, FullPath = photos, Name = "Urlaubsfotos", ParentPath = "/Scan/1/2/3/4/5", RootPath = "/Scan",
            RelativePath = "1/2/3/4/5/Urlaubsfotos", Depth = 6, DirStatus = DirectoryStatus.Shallow,
            DirReason = "Shallow: depth 6 reaches shallow depth 6", DirectFileCount = 1, ScannedAt = DateTime.UtcNow
        }]);
        await _fileRepo.InsertBatchAsync([MakeFile(job.Id, photos, photos + "/strand.jpg", FileType.Image)]);
        job.Status = JobStatus.Paused;
        await _jobRepo.UpdateAsync(job);

        var resumed = await NewJobService(fs).RunAsync(job.Id);

        Assert.Equal("default", resumed.RulesName);
        var node = await _dirRepo.GetByPathAsync(job.Id, photos);
        Assert.Equal(DirectoryStatus.Scanned, node!.DirStatus);
        Assert.Contains("before: Shallow: depth 6 reaches shallow depth 6", node.DirReason);
        Assert.Equal(DirectoryStatus.Scanned, (await _dirRepo.GetByPathAsync(job.Id, photos + "/Tag2"))!.DirStatus);
        Assert.Equal(2, await _fileRepo.CountByJobAsync(job.Id));
    }

    // Skipped folders used to be counted as discovered but never as processed, so a finished scan
    // with two or more of them looked unfinished and was reported as paused.
    [Fact]
    public async Task FinishedScanWithSeveralSkippedFolders_StaysCompleted()
    {
        var fs = new FakeFileSystemAccessor(Tree(
            ("/Scan/bin", ["/Scan/bin/a.dll"]),
            ("/Scan/obj", ["/Scan/obj/b.obj"]),
            ("/Scan/node_modules", ["/Scan/node_modules/c.js"]),
            ("/Scan/docs", ["/Scan/docs/d.txt"])));
        var service = NewJobService(fs);
        var job = await service.CreateJobAsync("/Scan");

        await service.RunAsync(job.Id);

        var reloaded = await service.GetJobAsync(job.Id);
        Assert.Equal(JobStatus.Completed, reloaded!.Status);
        Assert.True(reloaded.ProcessedDirectories >= reloaded.TotalDirectories);
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

    // filePath → content; files without an entry read as empty
    private readonly Dictionary<string, byte[]> _contents;

    public FakeFileSystemAccessor(Dictionary<string, string[]> dirFiles, Dictionary<string, byte[]>? contents = null)
    {
        _dirFiles = dirFiles;
        _contents = contents ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);
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

    public long GetFileSize(string path) => _contents.TryGetValue(path, out var c) ? c.Length : 1024;

    public List<string> PrefixReads { get; } = [];

    public byte[] ReadPrefix(string path, int maxBytes)
    {
        PrefixReads.Add(path);
        return _contents.TryGetValue(path, out var c) ? c[..Math.Min(c.Length, maxBytes)] : [];
    }
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
