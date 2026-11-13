using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure;
using SmartFileOrganizer.Infrastructure.Persistence;
using SmartFileOrganizer.Scanning.Engine;
using SmartFileOrganizer.Scanning.Rules;
using SmartFileOrganizer.Tests.Support;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// The scan does not follow links and reports what it cannot read
/// (https://github.com/crack666/smart_file_organizer/issues/57). The tree imitates an old Windows profile:
/// <c>All Users</c> points to a program-data folder outside the profile, <c>Eigene Dateien</c> to a folder inside
/// it, <c>Anwendungsdaten</c> back to its own parent (a loop). All of it lies in a temp folder of the test.
/// </summary>
public class LinkScanTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root;
    private readonly string _outside;
    private readonly DatabaseContext _db;

    public LinkScanTests()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_linkscan_{Guid.NewGuid():N}");
        _root = Path.Combine(_dir, "Users", "Alice");
        _outside = Path.Combine(_dir, "ProgramData");
        Directory.CreateDirectory(Path.Combine(_root, "Documents"));
        Directory.CreateDirectory(Path.Combine(_root, "Pictures"));
        File.WriteAllText(Path.Combine(_root, "Documents", "letter.txt"), "Dear landlord");
        File.WriteAllBytes(Path.Combine(_root, "Pictures", "IMG_0001.jpg"), new byte[64]);

        // Plenty of content outside the profile: none of it may be counted.
        for (var i = 0; i < 5; i++)
        {
            var folder = Path.Combine(_outside, "Microsoft", $"Package {i}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "cache.bin"), "program data");
        }

        _db = new DatabaseContext(Path.Combine(_dir, "data.db"), NullLogger<DatabaseContext>.Instance);
        _db.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        FileSystemLinks.DeleteTree(_dir);
    }

    private void OldProfileLinks()
    {
        FileSystemLinks.DirectoryLink(Path.Combine(_root, "All Users"), _outside);
        FileSystemLinks.DirectoryLink(Path.Combine(_root, "Default User"), Path.Combine(_outside, "Microsoft"));
        FileSystemLinks.DirectoryLink(Path.Combine(_root, "Eigene Dateien"), Path.Combine(_root, "Documents"));
        FileSystemLinks.DirectoryLink(Path.Combine(_root, "Documents", "Anwendungsdaten"), Path.Combine(_root, "Documents"));
        FileSystemLinks.DirectoryLink(Path.Combine(_root, "Documents and Settings"), _root);
    }

    private async Task<(ScanJob Job, IReadOnlyList<DirectoryNode> Dirs)> ScanAsync(ScanRuleSet? rules = null, IFileSystemAccessor? fs = null)
    {
        var jobs = new ScanJobRepository(_db);
        var job = await jobs.CreateAsync(new ScanJob
        {
            RootPath = _root,
            Status = JobStatus.Created,
            CreatedAt = DateTime.UtcNow,
            Rules = rules == null ? null : ScanRulesFile.ToProfile(rules, "test")
        });
        var dirs = new DirectoryRepository(_db);
        var engine = new ScanEngine(fs ?? new FileSystemAccessor(), new FileRepository(_db), dirs, jobs, NullLogger<ScanEngine>.Instance);
        await engine.RunAsync(job, new Progress<ScanProgress>(), CancellationToken.None);
        return ((await jobs.GetByIdAsync(job.Id))!, await dirs.GetAllForJobOrderedByDepthAsync(job.Id));
    }

    private async Task<List<string>> FilePathsAsync(long jobId)
    {
        await using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<string>("SELECT full_path FROM file_nodes WHERE job_id = @jobId", new { jobId })).ToList();
    }

    [Fact]
    public async Task Counters_FoundAndProcessedFolders_Agree()
    {
        var (job, dirs) = await ScanAsync();

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(3, dirs.Count);   // root, Documents, Pictures
        Assert.Equal(dirs.Count, job.ProcessedDirectories);
        Assert.Equal(job.ProcessedDirectories, job.TotalDirectories);
        Assert.Equal(job.ProcessedFiles, job.TotalFiles);
    }

    [LinkFact(LinkNeed.DirectoryLink)]
    public async Task OldProfile_LinksAreRecorded_NotFollowed_AndTheirContentIsNotCounted()
    {
        OldProfileLinks();

        var (job, dirs) = await ScanAsync();

        Assert.Equal(JobStatus.Completed, job.Status);
        // Only the two real files of the profile; nothing from ProgramData, nothing twice through Eigene Dateien.
        var files = await FilePathsAsync(job.Id);
        Assert.Equal(2, files.Count);
        Assert.Equal(2, job.TotalFiles);
        Assert.DoesNotContain(files, f => f.Contains("All Users") || f.Contains("Eigene Dateien") || f.Contains("Anwendungsdaten"));

        var links = dirs.Where(d => d.DirStatus == DirectoryStatus.Link).ToDictionary(d => d.Name);
        Assert.Equal(["All Users", "Anwendungsdaten", "Default User", "Documents and Settings", "Eigene Dateien"], links.Keys.Order());
        var allUsers = links["All Users"];
        Assert.Equal(OperatingSystem.IsWindows() ? "junction" : "symlink", allUsers.LinkKind);
        Assert.Equal(_outside, Path.TrimEndingDirectorySeparator(allUsers.LinkTarget!), ignoreCase: OperatingSystem.IsWindows());
        Assert.StartsWith("Link: ", allUsers.DirReason);
        Assert.Contains("not followed", allUsers.DirReason);

        // Nothing below a link was entered, and the scan ended far from the safety limit.
        Assert.DoesNotContain(dirs, d => d.ParentPath.Contains("All Users") || d.ParentPath.Contains("Documents and Settings"));
        Assert.True(dirs.Max(d => d.Depth) < 3, $"Deepest folder at depth {dirs.Max(d => d.Depth)}.");
        Assert.DoesNotContain(dirs, d => d.DirReason?.Contains("safety limit") == true);

        // Every row counts as processed, links included; found and processed agree; no errors.
        Assert.Equal(dirs.Count, job.ProcessedDirectories);
        Assert.Equal(job.ProcessedDirectories, job.TotalDirectories);
        Assert.Equal(0, job.ErrorCount);
    }

    [LinkFact(LinkNeed.DirectoryLink)]
    public async Task FollowLinkRule_EntersTheNamedLink_ButNeverALoop()
    {
        OldProfileLinks();
        var rules = ScanRulesFile.Default with
        {
            Name = "follow-test",
            Rules =
            [
                .. ScanRulesFile.Default.Rules,
                new ScanRule("All Users", ScanRuleAction.FollowLink),
                new ScanRule("Documents and Settings", ScanRuleAction.FollowLink),
                new ScanRule("Anwendungsdaten", ScanRuleAction.FollowLink)
            ]
        };

        var (job, dirs) = await ScanAsync(rules);

        var files = await FilePathsAsync(job.Id);
        // The five program files, recorded under the link path, plus the two real ones.
        Assert.Equal(7, files.Count);
        Assert.Equal(5, files.Count(f => f.Contains("All Users")));

        var allUsers = dirs.Single(d => d.Name == "All Users");
        Assert.Equal(DirectoryStatus.Scanned, allUsers.DirStatus);
        Assert.NotNull(allUsers.LinkKind);
        Assert.Contains("followed link", allUsers.DirReason);

        // Allowed by a rule, refused because the target holds the root or lies inside it.
        var loop = dirs.Single(d => d.Name == "Documents and Settings");
        Assert.Equal(DirectoryStatus.Link, loop.DirStatus);
        Assert.Contains("inside the scan root", loop.DirReason);   // the root itself counts as inside
        var parent = dirs.Single(d => d.Name == "Anwendungsdaten");
        Assert.Equal(DirectoryStatus.Link, parent.DirStatus);

        // Default User is no rule's business: not followed; its target was not entered twice either.
        Assert.Equal(DirectoryStatus.Link, dirs.Single(d => d.Name == "Default User").DirStatus);
        Assert.True(dirs.Max(d => d.Depth) < 6);
    }

    [LinkFact(LinkNeed.FileSymlink)]
    public async Task FileThatIsALink_IsRecordedAsLink_NotCountedAsContent()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "Documents", "settings.lnk.dat"), Path.Combine(_outside, "Microsoft", "Package 0", "cache.bin"));

        var (job, dirs) = await ScanAsync();

        var files = await FilePathsAsync(job.Id);
        Assert.DoesNotContain(files, f => f.EndsWith("settings.lnk.dat"));
        Assert.Equal(1, dirs.Single(d => d.Name == "Documents").DirectFileCount);

        await using var conn = _db.CreateConnection();
        var link = await conn.QuerySingleAsync<(string Kind, string Target)>(
            "SELECT link_kind, link_target FROM file_links WHERE job_id = @id", new { id = job.Id });
        Assert.Equal("symlink", link.Kind);
        Assert.EndsWith("cache.bin", link.Target);
    }

    [LinkFact(LinkNeed.UnreadableDirectory)]
    public async Task UnreadableFolder_IsAnErrorWithItsCause_NeverEmpty()
    {
        var locked = Path.Combine(_root, "Locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "diary.txt"), "private");
        FileSystemLinks.MakeUnreadable(locked);
        try
        {
            var (job, dirs) = await ScanAsync();

            var node = dirs.Single(d => d.Name == "Locked");
            Assert.Equal(DirectoryStatus.Error, node.DirStatus);
            Assert.Equal("access_denied", node.ScanErrorCause);
            Assert.Equal(1, job.ErrorCount);
            Assert.Equal(job.ProcessedDirectories, job.TotalDirectories);
        }
        finally
        {
            FileSystemLinks.MakeReadable(locked);
        }
    }

    [Fact]
    public async Task UnreadableFileSize_IsReportedOnTheFile_NotStoredAsAnEmptyFile()
    {
        var fs = new VanishingFileSystem(new FileSystemAccessor(), "letter.txt");

        var (job, _) = await ScanAsync(fs: fs);

        await using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleAsync<(string? Cause, string? Error)>(
            "SELECT scan_error_cause, scan_error FROM file_nodes WHERE job_id = @id AND name = 'letter.txt'", new { id = job.Id });
        Assert.Equal("not_found", row.Cause);
        Assert.NotNull(row.Error);
        Assert.Equal(1, job.ErrorCount);

        var stats = await new JobStatisticsRepository(_db).GetAsync(job.Id, 10);
        var group = Assert.Single(stats.ScanErrors);
        Assert.Equal("not_found", group.Cause);
        Assert.Equal(1, group.Files);
    }

    /// <summary>The named file disappears between listing and reading its size.</summary>
    private sealed class VanishingFileSystem(IFileSystemAccessor inner, string name) : IFileSystemAccessor
    {
        public long GetFileSize(string path) =>
            Path.GetFileName(path) == name ? throw new FileNotFoundException("Gone while scanning.", path) : inner.GetFileSize(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public IEnumerable<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);
        public IEnumerable<string> EnumerateFiles(string path) => inner.EnumerateFiles(path);
        public IReadOnlyList<string> ListFiles(string path) => inner.ListFiles(path);
        public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);
        public DateTime GetLastWriteTime(string path) => inner.GetLastWriteTime(path);
        public string GetFileName(string path) => inner.GetFileName(path);
        public string GetDirectoryName(string path) => inner.GetDirectoryName(path);
        public string GetExtension(string path) => inner.GetExtension(path);
        public string GetRelativePath(string relativeTo, string path) => inner.GetRelativePath(relativeTo, path);
        public byte[] ReadPrefix(string path, int maxBytes) => inner.ReadPrefix(path, maxBytes);
        public FileSystemLink? GetLink(string path) => inner.GetLink(path);
    }
}
