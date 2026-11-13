using System.Text.Json;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Tests.Support;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>sfo status</c> and <c>sfo dirs</c> after a scan of a tree with links and unreadable folders
/// (https://github.com/crack666/smart_file_organizer/issues/57): read errors are summarised by cause, links
/// are listed by kind with their targets, and a link stands in the directory list with its mark.
/// </summary>
public class LinkAndErrorStatusTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _root;
    private readonly string _outside;
    private readonly List<string> _locked = [];

    public LinkAndErrorStatusTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_cli_links_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _root = Path.Combine(_dir, "profile");
        _outside = Path.Combine(_dir, "ProgramData");
        Directory.CreateDirectory(Path.Combine(_root, "Documents"));
        Directory.CreateDirectory(Path.Combine(_outside, "Vendor"));
        File.WriteAllText(Path.Combine(_root, "Documents", "letter.txt"), "Dear landlord");
        File.WriteAllText(Path.Combine(_outside, "Vendor", "cache.bin"), "program data");
    }

    public void Dispose()
    {
        foreach (var folder in _locked)
            FileSystemLinks.MakeReadable(folder);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        FileSystemLinks.DeleteTree(_dir);
    }

    private async Task<JsonElement> OkAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath, "--json"], new CliHost { Out = stdout, Error = stderr });
        Assert.True(exit == ExitCodes.Ok, stdout.ToString() + stderr);
        using var doc = JsonDocument.Parse(stdout.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    private async Task<string> TextAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], new CliHost { Out = stdout, Error = new StringWriter() });
        Assert.Equal(ExitCodes.Ok, exit);
        return stdout.ToString();
    }

    private void Lock(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "private.txt"), "x");
        FileSystemLinks.MakeUnreadable(folder);
        _locked.Add(folder);
    }

    [LinkFact(LinkNeed.UnreadableDirectory)]
    public async Task Status_SummarisesReadErrorsByCause()
    {
        Lock("Locked A");
        Lock("Locked B");
        Lock("Locked C");

        var scan = await OkAsync("scan", _root);
        var jobId = scan.GetProperty("job").GetProperty("id").GetInt64().ToString();
        var status = await OkAsync("status", jobId);

        Assert.Equal(3, status.GetProperty("phases").GetProperty("scan").GetProperty("errors").GetInt64());
        var group = Assert.Single(status.GetProperty("scan_errors").EnumerateArray());
        Assert.Equal("access_denied", group.GetProperty("cause").GetString());
        Assert.Equal(3, group.GetProperty("directories").GetInt64());
        Assert.Equal(0, group.GetProperty("files").GetInt64());
        Assert.Equal(3, group.GetProperty("examples").GetArrayLength());

        // Text: one line per cause instead of one per folder.
        var text = await TextAsync("status", jobId);
        Assert.Contains("scan errors     3 access_denied", text);

        // A folder that could not be read is an error with its cause, never an empty folder.
        var dirs = await OkAsync("dirs", jobId);
        var locked = dirs.GetProperty("items").EnumerateArray().Where(d => d.GetProperty("path").GetString()!.Contains("Locked")).ToList();
        Assert.Equal(3, locked.Count);
        Assert.All(locked, d =>
        {
            Assert.Equal("error", d.GetProperty("status").GetString());
            Assert.Equal("access_denied", d.GetProperty("error_cause").GetString());
        });
    }

    [LinkFact(LinkNeed.DirectoryLink)]
    public async Task StatusAndDirs_ShowTheLink_WithKindAndTarget()
    {
        FileSystemLinks.DirectoryLink(Path.Combine(_root, "All Users"), _outside);

        var scan = await OkAsync("scan", _root);
        var job = scan.GetProperty("job");
        Assert.Equal(1, job.GetProperty("files_processed").GetInt64());
        var jobId = job.GetProperty("id").GetInt64().ToString();

        var status = await OkAsync("status", jobId);
        var scanPhase = status.GetProperty("phases").GetProperty("scan");
        Assert.Equal(scanPhase.GetProperty("directories_total").GetInt64(), scanPhase.GetProperty("directories_processed").GetInt64());
        var kind = OperatingSystem.IsWindows() ? "junction" : "symlink";
        var group = Assert.Single(status.GetProperty("links").EnumerateArray());
        Assert.Equal(kind, group.GetProperty("kind").GetString());
        Assert.Equal(1, group.GetProperty("not_followed").GetInt64());
        var example = Assert.Single(group.GetProperty("examples").EnumerateArray());
        Assert.EndsWith("All Users", example.GetProperty("path").GetString());
        Assert.Equal(_outside, Path.TrimEndingDirectorySeparator(example.GetProperty("target").GetString()!), ignoreCase: OperatingSystem.IsWindows());

        var dirs = await OkAsync("dirs", jobId);
        var link = dirs.GetProperty("items").EnumerateArray().Single(d => d.GetProperty("path").GetString()!.EndsWith("All Users"));
        Assert.Equal("link", link.GetProperty("status").GetString());
        Assert.Equal(kind, link.GetProperty("link_kind").GetString());
        Assert.Equal(0, link.GetProperty("files").GetInt64());
        Assert.StartsWith("Link: ", link.GetProperty("scan_reason").GetString());
    }
}
