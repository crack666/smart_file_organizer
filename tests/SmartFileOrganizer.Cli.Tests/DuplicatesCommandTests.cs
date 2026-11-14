using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartFileOrganizer.Cli.Contract;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>sfo duplicates</c> in-process over generated folders in a temp folder this test makes
/// (https://github.com/crack666/smart_file_organizer/issues/87): one JSON document, groups across jobs, a marked file
/// with path and markers only, and a second run that reads nothing.
/// </summary>
public class DuplicatesCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public DuplicatesCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_dupcli_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private string Write(string relative, byte[] content)
    {
        var path = Path.GetFullPath(Path.Combine(_dir, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Bytes(int seed, int length)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public async Task GroupsAcrossJobs_MarkedFilesWithoutChecksum_SecondRunReadsNothing()
    {
        var photo = Bytes(1, 400_000);
        Write("stick/DCIM/IMG_0001.jpg", photo);
        Write("ablage/Fotos/2020/IMG_0001.jpg", photo);
        Write("ablage/Fotos/Kopie/IMG_0001.jpg", photo);
        var key = Bytes(2, 3_000);
        Write("stick/schluessel/id_rsa", key);
        Write("ablage/Sicherung/id_rsa", key);
        Write("stick/einzeln.bin", Bytes(3, 12_345));

        var stick = (await OkAsync("scan", Path.Combine(_dir, "stick"))).GetProperty("job").GetProperty("id").GetInt64();
        var library = (await OkAsync("scan", Path.Combine(_dir, "ablage"), "--role", "library")).GetProperty("job").GetProperty("id").GetInt64();

        var first = await OkAsync("duplicates", stick.ToString(), library.ToString(), "--verify");
        Assert.True(first.GetProperty("verified").GetBoolean());
        Assert.True(first.GetProperty("complete").GetBoolean());
        Assert.Equal(2, first.GetProperty("jobs").GetArrayLength());
        Assert.Equal(2, first.GetProperty("groups_total").GetInt32());
        Assert.True(first.GetProperty("read").GetProperty("bytes").GetInt64() > 0);

        var groups = first.GetProperty("groups").EnumerateArray().ToList();
        var photos = groups[0]; // largest waste first
        Assert.Equal("files", photos.GetProperty("scope").GetString());
        Assert.Equal("certain", photos.GetProperty("certainty").GetString());
        Assert.Equal("source_copies_held", photos.GetProperty("advice").GetString());
        Assert.Equal(800_000, photos.GetProperty("wasted_bytes").GetInt64());
        Assert.Equal(64, photos.GetProperty("hash").GetString()!.Length);
        Assert.True(photos.GetProperty("across_jobs").GetBoolean());
        Assert.True(photos.GetProperty("in_library").GetBoolean());
        Assert.Equal(3, photos.GetProperty("files").GetArrayLength());

        var keys = groups[1];
        Assert.Equal(JsonValueKind.Null, keys.GetProperty("hash").ValueKind);
        Assert.All(keys.GetProperty("files").EnumerateArray(), f => Assert.Contains("keys",
            f.GetProperty("sensitive").EnumerateArray().Select(t => t.GetString())));

        var second = await OkAsync("duplicates", stick.ToString(), library.ToString(), "--summary");
        Assert.Equal(0, second.GetProperty("read").GetProperty("files").GetInt32());
        Assert.Equal(0, second.GetProperty("read").GetProperty("bytes").GetInt64());
        Assert.Equal(2, second.GetProperty("summary").GetProperty("certain").GetInt32());
        Assert.Equal(0, second.GetProperty("groups").GetArrayLength());
        Assert.Equal(2, second.GetProperty("groups_total").GetInt32());

        var large = await OkAsync("duplicates", stick.ToString(), library.ToString(), "--min-size", "100K");
        Assert.Equal(1, large.GetProperty("groups_total").GetInt32());
    }

    [Fact]
    public async Task AnUnknownJob_FailsWithJobNotFound()
    {
        var stdout = new StringWriter();
        var exit = await SfoApp.RunAsync(["duplicates", "999", "--json", "--db", _dbPath], new CliHost { Out = stdout, Error = new StringWriter() });
        Assert.Equal(ExitCodes.Failure, exit);
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(ErrorCodes.JobNotFound, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private async Task<JsonElement> OkAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--json", "--db", _dbPath], new CliHost { Out = stdout, Error = stderr });
        Assert.True(exit == ExitCodes.Ok, string.Join(' ', args) + ": " + stdout + stderr);
        using var doc = JsonDocument.Parse(stdout.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }
}
