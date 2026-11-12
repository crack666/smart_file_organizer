using System.Text.Json;
using SmartFileOrganizer.Cli.Contract;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>sfo bundles build|list|show</c>, <c>files --bundle</c> and the dump split of <c>summary</c>, run in-process
/// against a generated tree and a temp database (https://github.com/crack666/smart_file_organizer/issues/39).
/// </summary>
public class BundleCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _root;
    private readonly string _library;

    public BundleCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_bundles_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _root = Path.Combine(_dir, "data");
        _library = Path.Combine(_dir, "library");

        // A phone copy with a camera roll, app data, an empty LOST.DIR and a key file in Documents.
        Write("Handy alt/DCIM/Camera/IMG_20140503_100000.jpg", "cam1");
        Write("Handy alt/DCIM/Camera/IMG_20140503_101500.jpg", "cam2");
        Write("Handy alt/DCIM/Camera/IMG_20140503_103000.jpg", "cam3");
        Write("Handy alt/Android/data/com.example.app/files/cache.bin", "noise");
        Write("Handy alt/Documents/id_rsa", "not a real key");
        Directory.CreateDirectory(Path.Combine(_root, "Handy alt", "LOST.DIR"));
        Directory.CreateDirectory(Path.Combine(_root, "Handy alt", "Download"));

        // Loose photos without dates in their names, from three periods.
        DateTime[] starts =
        [
            new(2015, 2, 14, 9, 0, 0, DateTimeKind.Utc),
            new(2016, 7, 30, 15, 0, 0, DateTimeKind.Utc),
            new(2018, 12, 24, 17, 0, 0, DateTimeKind.Utc)
        ];
        for (var period = 0; period < 3; period++)
        {
            for (var i = 0; i < 10; i++)
            {
                var path = Write($"Unsortiert/p{period}_{i:D2}.jpg", $"photo {period} {i}");
                File.SetLastWriteTimeUtc(path, starts[period].AddMinutes(i * 5));
            }
        }

        Write("Skifahrt 2015/bericht.txt", "Ski");

        // The library already holds two of the loose photos.
        Directory.CreateDirectory(_library);
        File.Copy(Path.Combine(_root, "Unsortiert", "p0_00.jpg"), Path.Combine(_library, "a.jpg"));
        File.Copy(Path.Combine(_root, "Unsortiert", "p0_01.jpg"), Path.Combine(_library, "b.jpg"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<JsonElement> OkAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--json", "--db", _dbPath], new CliHost { Out = stdout, Error = stderr });
        Assert.True(exit == ExitCodes.Ok, stdout + stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    private async Task<(int Exit, JsonElement Doc)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--json", "--db", _dbPath], new CliHost { Out = stdout, Error = new StringWriter() });
        using var doc = JsonDocument.Parse(stdout.ToString());
        return (exit, doc.RootElement.Clone());
    }

    private async Task<long> ScanAsync(string path, params string[] extra) =>
        (await OkAsync(["scan", path, .. extra])).GetProperty("job").GetProperty("id").GetInt64();

    [Fact]
    public async Task Build_List_Show_Files_And_Summary()
    {
        var job = await ScanAsync(_root);

        var summaryBefore = await OkAsync("summary", job.ToString());
        Assert.Equal(JsonValueKind.Null, summaryBefore.GetProperty("bundles").ValueKind);

        var built = await OkAsync("bundles", "build", job.ToString());
        Assert.Equal(1, built.GetProperty("stats").GetProperty("dump_count").GetInt32());
        Assert.Equal(8, built.GetProperty("settings").GetProperty("event_gap_hours").GetDouble());

        var top = await OkAsync("bundles", "list", job.ToString());
        var items = top.GetProperty("items").EnumerateArray().ToList();
        var dump = items.Single(i => i.GetProperty("kind").GetString() == "dump");
        Assert.Equal("phone", dump.GetProperty("origin").GetString());
        Assert.Equal(1, dump.GetProperty("sensitive_files").GetInt64());
        var events = items.Where(i => i.GetProperty("kind").GetString() == "event").ToList();
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(10, e.GetProperty("files").GetInt64()));
        Assert.All(events, e => Assert.Equal("modified_date", e.GetProperty("period").GetProperty("time_source").GetString()));
        Assert.Contains(items, i => i.GetProperty("kind").GetString() == "folder"
                                    && i.GetProperty("suggested_name").GetString() == "Skifahrt 2015");
        Assert.All(items, i => Assert.Equal(JsonValueKind.Null, i.GetProperty("styled_name").ValueKind));
        Assert.All(items, i => Assert.Equal(JsonValueKind.Null, i.GetProperty("comparison").ValueKind));

        // Inside the dump: the camera part (worth keeping, with its event) and the noise.
        var shown = await OkAsync("bundles", "show", job.ToString(), dump.GetProperty("id").GetInt64().ToString());
        var children = shown.GetProperty("children").EnumerateArray().ToList();
        var camera = children.Single(c => c.GetProperty("part").GetString() == "camera");
        Assert.Equal("dump_keep", camera.GetProperty("kind").GetString());
        Assert.Equal(3, camera.GetProperty("files").GetInt64());
        Assert.Equal(1, camera.GetProperty("children").GetInt32());
        Assert.Equal("file_name", camera.GetProperty("period").GetProperty("time_source").GetString());
        var noise = children.Single(c => c.GetProperty("kind").GetString() == "dump_noise");
        Assert.True(noise.GetProperty("empty_directories").GetInt32() >= 1);

        // Down to the file: the camera part with its event child, through the output guard.
        var files = await OkAsync("files", job.ToString(), "--bundle", camera.GetProperty("id").GetInt64().ToString());
        Assert.Equal(3, files.GetProperty("total").GetInt64());
        var documents = children.Single(c => c.GetProperty("part").GetString() == "documents");
        var keyFiles = await OkAsync("files", job.ToString(), "--bundle", documents.GetProperty("id").GetInt64().ToString());
        Assert.True(keyFiles.GetProperty("items")[0].GetProperty("protected").GetBoolean());

        var summary = await OkAsync("summary", job.ToString());
        var bundles = summary.GetProperty("bundles");
        Assert.Equal(1, bundles.GetProperty("dump_count").GetInt32());
        Assert.Equal(31, bundles.GetProperty("without_dumps").GetProperty("files").GetInt64());
        Assert.Equal(5, bundles.GetProperty("dumps").GetProperty("files").GetInt64());
        Assert.Equal(36, summary.GetProperty("files").GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task Bundles_CountFilesAlreadyInTheLibrary()
    {
        var job = await ScanAsync(_root);
        var library = await ScanAsync(_library, "--role", "library");
        await OkAsync("compare", job.ToString(), "--against", library.ToString());
        var built = await OkAsync("bundles", "build", job.ToString());
        Assert.Equal(library, built.GetProperty("comparison").GetProperty("against_job_id").GetInt64());

        var top = await OkAsync("bundles", "list", job.ToString(), "--kind", "event");
        var first = top.GetProperty("items").EnumerateArray()
            .OrderBy(e => e.GetProperty("period").GetProperty("from").GetString()).First();

        Assert.Equal(2, first.GetProperty("comparison").GetProperty("backed_up").GetInt64());
        Assert.Equal(8, first.GetProperty("comparison").GetProperty("new").GetInt64());
    }

    [Fact]
    public async Task UnknownBundle_AndUnfinishedScan_FailWithStableCodes()
    {
        var job = await ScanAsync(_root);
        await OkAsync("bundles", "build", job.ToString());

        var (exit, doc) = await RunAsync("bundles", "show", job.ToString(), "999999");
        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal("bundle_not_found", doc.GetProperty("error").GetProperty("code").GetString());

        (exit, doc) = await RunAsync("files", job.ToString(), "--bundle", "999999");
        Assert.Equal("bundle_not_found", doc.GetProperty("error").GetProperty("code").GetString());

        (exit, doc) = await RunAsync("bundles", "build", "424242");
        Assert.Equal("job_not_found", doc.GetProperty("error").GetProperty("code").GetString());

        var bad = Path.Combine(_dir, "bad-patterns.json");
        File.WriteAllText(bad, """{ "schema_version": 2 }""");
        (exit, doc) = await RunAsync("bundles", "build", job.ToString(), "--patterns", bad);
        Assert.Equal("patterns_invalid", doc.GetProperty("error").GetProperty("code").GetString());
    }
}
