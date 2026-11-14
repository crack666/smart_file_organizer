using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Tests.Support;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// Known places and the refresh of a place (https://github.com/crack666/smart_file_organizer/issues/86), in-process on a
/// generated source and library in a temp folder this test makes. The model address of the test project is a dead port:
/// a call to the model would fail the analysis, so a refresh that completes without one asked nothing.
/// </summary>
public class RefreshAndPlacesTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _corpus;
    private readonly string _tree;
    private readonly string _library;
    private readonly List<TruthDirectoryEntry> _dirs = [new(".", "mixed")];
    private readonly List<TruthFileEntry> _files = [];

    public RefreshAndPlacesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_refresh_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _corpus = Path.Combine(_dir, "corpus");
        _tree = Path.Combine(_corpus, "tree");
        _library = Path.Combine(_dir, "library");

        _dirs.Add(new TruthDirectoryEntry("Fotos", "mixed"));
        Write("Fotos/IMG_20220501_120000.jpg", "photo", "may photo " + new string('m', 300));
        Write("Fotos/IMG_20220502_120000.jpg", "photo", "second photo " + new string('n', 500));
        Write("Fotos/IMG_20220503_120000.jpg", "photo", "third photo " + new string('o', 700));
        Write("Rechnung_Wasser.pdf", "invoice", "%PDF-1.4 water");
        Write("Notizen.txt", "document", "shopping list");

        var truth = new EvalTruthDocument(EvalTruth.SchemaVersion, new EvalCorpusInfo("refresh-test", 1, 7), "tree", _dirs, _files);
        File.WriteAllText(Path.Combine(_corpus, EvalTruth.FileName), JsonSerializer.Serialize(truth, EvalJson.Options));

        // The library holds a copy of two files, so the comparison reads their checksums.
        Directory.CreateDirectory(Path.Combine(_library, "Archiv"));
        File.Copy(Path.Combine(_tree, "Fotos", "IMG_20220501_120000.jpg"), Path.Combine(_library, "Archiv", "IMG_20220501_120000.jpg"));
        File.Copy(Path.Combine(_tree, "Rechnung_Wasser.pdf"), Path.Combine(_library, "Archiv", "Rechnung_Wasser.pdf"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { FileSystemLinks.DeleteTree(_dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ARefresh_HashesNoUnchangedFile_AsksNoModel_AndNamesOneNewOneChangedOneMissing()
    {
        var first = (await OkAsync("eval", _corpus, "--stub", Path.Combine(_corpus, EvalTruth.FileName))).GetProperty("job_id").GetInt64();
        var library = (await OkAsync("scan", _library, "--role", "library")).GetProperty("job").GetProperty("id").GetInt64();
        await OkAsync("compare", first.ToString(), "--against", library.ToString());
        var checksumsBefore = await FingerprintsAsync();
        Assert.Contains(checksumsBefore.Keys, p => p.EndsWith("Rechnung_Wasser.pdf", StringComparison.Ordinal));
        var analysed = await ScalarAsync($"SELECT COUNT(*) FROM ai_results a JOIN file_nodes f ON f.id = a.file_node_id WHERE f.job_id = {first}");
        Assert.Equal(5, analysed);

        // One file changes, one appears, one goes.
        var changed = Path.Combine(_tree, "Notizen.txt");
        File.AppendAllText(changed, " and milk");
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));
        var added = Path.Combine(_tree, "Fotos", "IMG_20220504_120000.jpg");
        File.WriteAllText(added, "a new photo");
        var gone = Path.Combine(_tree, "Fotos", "IMG_20220503_120000.jpg");
        File.Delete(gone);

        var scan = await OkAsync("scan", _tree, "--refresh");
        var second = scan.GetProperty("job").GetProperty("id").GetInt64();
        var refresh = scan.GetProperty("refresh");
        Assert.Equal(first, refresh.GetProperty("previous_job_id").GetInt64());
        Assert.Equal(3, refresh.GetProperty("unchanged").GetInt64());
        Assert.Equal(1, refresh.GetProperty("new").GetInt64());
        Assert.Equal(1, refresh.GetProperty("changed").GetInt64());
        Assert.Equal(1, refresh.GetProperty("missing").GetInt64());
        Assert.Equal(0, refresh.GetProperty("moved").GetInt64());
        var changes = refresh.GetProperty("changes").EnumerateArray().ToDictionary(c => c.GetProperty("path").GetString()!, c => c.GetProperty("change").GetString());
        Assert.Equal("new", changes[added]);
        Assert.Equal("changed", changes[changed]);
        Assert.Equal("missing", changes[gone]);

        // The three unchanged files keep their result and facts; nothing of the changed or new file is taken over.
        var carried = refresh.GetProperty("carried");
        Assert.Equal(3, carried.GetProperty("results").GetInt64());
        Assert.Equal(2, carried.GetProperty("checksums").GetInt64());
        Assert.Equal(3, await ScalarAsync($"SELECT COUNT(*) FROM ai_results a JOIN file_nodes f ON f.id = a.file_node_id WHERE f.job_id = {second}"));
        Assert.Equal(0, await ScalarAsync(
            $"SELECT COUNT(*) FROM ai_results a JOIN file_nodes f ON f.id = a.file_node_id WHERE f.job_id = {second} AND f.full_path IN ('{changed}', '{added}')"));
        Assert.Equal(
            await ScalarAsync($"SELECT COUNT(*) FROM file_facts x JOIN file_nodes f ON f.id = x.file_node_id WHERE f.job_id = {first} AND f.full_path NOT IN ('{changed}', '{gone}')"),
            await ScalarAsync($"SELECT COUNT(*) FROM file_facts x JOIN file_nodes f ON f.id = x.file_node_id WHERE f.job_id = {second}"));
        // No model was asked: nothing waited for the dead model server.
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM model_server_waits"));

        // Comparing the refreshed job again computes no checksum of an unchanged file anew.
        await OkAsync("compare", second.ToString(), "--against", library.ToString());
        var checksumsAfter = await FingerprintsAsync();
        foreach (var (path, stamp) in checksumsBefore)
            Assert.Equal(stamp, checksumsAfter[path]);
    }

    [Fact]
    public async Task Places_ShowsAScannedPlaceWithItsReport_AlsoWhenItCannotBeReached()
    {
        var job = (await OkAsync("eval", _corpus, "--stub", Path.Combine(_corpus, EvalTruth.FileName))).GetProperty("job_id").GetInt64();
        var library = (await OkAsync("scan", _library, "--role", "library")).GetProperty("job").GetProperty("id").GetInt64();
        await OkAsync("compare", job.ToString(), "--against", library.ToString());

        // The medium is unplugged: its folder is gone.
        var away = Path.Combine(_dir, "unplugged");
        Directory.Move(_corpus, away);

        var places = (await OkAsync("places")).GetProperty("places").EnumerateArray().ToList();
        Assert.Equal(2, places.Count);
        var source = places.Single(p => p.GetProperty("root_path").GetString() == _tree);
        Assert.Equal("source", source.GetProperty("role").GetString());
        Assert.False(source.GetProperty("reachable").GetBoolean());
        Assert.Equal(5, source.GetProperty("files").GetInt64());
        Assert.True(source.GetProperty("bytes").GetInt64() > 0);
        Assert.Equal(0, source.GetProperty("age_days").GetInt32());
        var report = source.GetProperty("report");
        Assert.Equal(JsonValueKind.Object, report.ValueKind);
        Assert.True(report.GetProperty("worth_keeping_not_backed_up").GetProperty("files").GetInt64() > 0);
        Assert.Equal(2, report.GetProperty("backed_up").GetProperty("files").GetInt64());
        var lib = places.Single(p => p.GetProperty("root_path").GetString() == _library);
        Assert.True(lib.GetProperty("reachable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, lib.GetProperty("report").ValueKind);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private void Write(string relative, string category, string content)
    {
        var path = Path.Combine(_tree, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _files.Add(new TruthFileEntry(relative, category, []));
    }

    private async Task<Dictionary<string, string>> FingerprintsAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT full_path, computed_at || '|' || COALESCE(full_hash_at, '') FROM content_fingerprints WHERE full_hash IS NOT NULL";
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
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
