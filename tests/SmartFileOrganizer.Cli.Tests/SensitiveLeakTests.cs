using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// Paths by which the content of a marked file could reach a caller outside the machine
/// (https://github.com/crack666/smart_file_organizer/issues/33). Every test plants a made-up token in
/// a marked file and searches stdout, stderr and exports for it.
/// </summary>
public class SensitiveLeakTests : IDisposable
{
    /// <summary>Made up; stands for a password in a password list.</summary>
    private static readonly string Secret = "FAKEPW-" + Guid.NewGuid().ToString("N")[..12];

    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _root;

    public SensitiveLeakTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_leak_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _root = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "docs", "letter.txt"), "Dear landlord, the heating is broken.");
        // Marked by the name rule (keyword "passw"); a text file, so its text goes to the local model.
        File.WriteAllText(Path.Combine(_root, "docs", "passwords.txt"), $"mail: {Secret}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr)
    {
        public JsonElement Result()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.GetProperty("result").Clone();
        }
    }

    /// <summary>Runs <c>sfo</c> with the real Ollama client against <see cref="EchoingOllamaHandler"/>.</summary>
    private async Task<Run> SfoAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost
        {
            Out = stdout,
            Error = stderr,
            ConfigureServices = services => services
                .AddHttpClient<IOllamaService, OllamaService>()
                .ConfigurePrimaryHttpMessageHandler(() => new EchoingOllamaHandler())
        };
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], host);
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<long> ScanAsync()
    {
        var scan = await SfoAsync("scan", _root, "--json");
        Assert.True(scan.ExitCode == ExitCodes.Ok, scan.Stdout + scan.Stderr);
        return scan.Result().GetProperty("job").GetProperty("id").GetInt64();
    }

    private string DebugLogPath => Path.Combine(Path.GetDirectoryName(_dbPath)!, CliLogFiles.DebugLogName);

    [Fact]
    public async Task AnalyzeVerbose_WritesNoExcerptOfAMarkedFileToStderr()
    {
        var job = await ScanAsync();

        var run = await SfoAsync("analyze", job.ToString(), "--verbose", "--json");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        Assert.DoesNotContain(Secret, run.Stderr);
        Assert.DoesNotContain(Secret, run.Stdout);
        // No prompt and no model answer on stderr, not even for an unmarked file.
        Assert.DoesNotContain("Dear landlord", run.Stderr);
        Assert.DoesNotContain("PROMPT", run.Stderr);
    }

    [Fact]
    public async Task AnalyzeVerbose_LogsPromptsToALocalFile_WithoutExcerptOfAMarkedFile()
    {
        var job = await ScanAsync();

        var run = await SfoAsync("analyze", job.ToString(), "--verbose", "--json");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        var log = await File.ReadAllTextAsync(DebugLogPath);
        Assert.Contains("Dear landlord", log);
        Assert.Contains("passwords.txt", log);
        Assert.DoesNotContain(Secret, log);
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Pooling=False;");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> QueryTextAsync(string sql)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Pooling=False;");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    private async Task<bool> ColumnExistsAsync(string table, string column)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Pooling=False;");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    /// <summary>Every read command and the export, as JSON or CSV text.</summary>
    private async Task<string> AllOutputsAsync(long job)
    {
        var sb = new StringBuilder();
        foreach (var args in new[]
                 {
                     new[] { "dirs" }, ["dirs", "--sensitive", "yes"], ["files"], ["show", "docs"], ["show", "."],
                     ["show", Path.Combine("docs", "passwords.txt")], ["summary"], ["status"]
                 })
        {
            var run = await SfoAsync([args[0], job.ToString(), .. args.Skip(1), "--json"]);
            Assert.True(run.ExitCode == ExitCodes.Ok, string.Join(' ', args) + ": " + run.Stdout + run.Stderr);
            sb.AppendLine(run.Stdout).AppendLine(run.Stderr);
        }

        var csv = Path.Combine(_dir, $"export-{Guid.NewGuid():N}.csv");
        var export = await SfoAsync("export", job.ToString(), "--csv", csv, "--json");
        Assert.True(export.ExitCode == ExitCodes.Ok, export.Stdout + export.Stderr);
        sb.AppendLine(await File.ReadAllTextAsync(csv));
        return sb.ToString();
    }

    /// <summary>
    /// A directory summary written before a file was marked (older version, rules added later) can
    /// paraphrase the file without naming it, so the name check does not catch it.
    /// </summary>
    [Fact]
    public async Task DirectoryJson_DoesNotCarryContentOfAMarkedFile_FromASummaryWrittenBeforeTheMarker()
    {
        var job = await ScanAsync();
        var docs = Path.Combine(_root, "docs");
        await ExecuteSqlAsync($"""
            INSERT INTO ai_directory_results (directory_node_id, phase, summary, theme, sampling_strategy, sample_size, model_used, analyzed_at)
            SELECT id, 'summary', 'Letters and a mail login {Secret}.', 'mail login {Secret}', 'analyze_all', 2, 'older-model', '2026-01-01T00:00:00Z'
            FROM directory_nodes WHERE full_path = '{docs.Replace("'", "''")}'
            """);

        var before = await AllOutputsAsync(job);
        Assert.DoesNotContain(Secret, before);
        var shown = (await SfoAsync("show", job.ToString(), "docs", "--json")).Result().GetProperty("directory");
        Assert.True(shown.GetProperty("text_withheld").GetBoolean());

        // The next analysis writes the summary again, now with the marked file as its neutral line.
        var analyze = await SfoAsync("analyze", job.ToString(), "--verbose", "--json");
        Assert.True(analyze.ExitCode == ExitCodes.Ok, analyze.Stdout + analyze.Stderr);
        var summarySql = $"""
            SELECT s.{"{0}"} FROM ai_directory_results s JOIN directory_nodes d ON d.id = s.directory_node_id
            WHERE s.phase = 'summary' AND d.full_path = '{docs.Replace("'", "''")}'
            """;
        Assert.NotEqual("older-model", await QueryTextAsync(summarySql.Replace("{0}", "model_used")));
        Assert.Equal("""["passwords.txt"]""", await QueryTextAsync(summarySql.Replace("{0}", "marked_inputs")));
        var rewritten = await QueryTextAsync(summarySql.Replace("{0}", "summary"));
        Assert.Contains("letter.txt", rewritten);
        Assert.Contains("passwords.txt", rewritten);
        Assert.DoesNotContain(Secret, rewritten);
        // Shown again: the echo names the marked file, so the name check may still hold the text back, but not as stale.
        var after = (await SfoAsync("show", job.ToString(), "docs", "--json")).Result().GetProperty("directory");
        Assert.DoesNotContain("marked after this summary", after.ToString());
        Assert.DoesNotContain(Secret, await AllOutputsAsync(job));
        Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(DebugLogPath));
    }

    /// <summary>
    /// Rows scanned before the header rules existed have no header check. The next analysis checks them
    /// before any model call, so a key file with an innocent name is marked before its text is used.
    /// </summary>
    [Fact]
    public async Task Analyze_ChecksHeadersOfOlderRows_BeforeTheirContentIsUsed()
    {
        // Made-up key file with an innocent name: only the header rule finds it.
        File.WriteAllText(Path.Combine(_root, "docs", "notes-2009.txt"), "-----BEGIN RSA " + "PRIVATE KEY-----\n" + Secret + "\n");
        var job = await ScanAsync();
        // As written by a version before the header rules.
        await ExecuteSqlAsync("UPDATE file_nodes SET sensitive_markers = NULL, sensitive_rule = NULL WHERE name = 'notes-2009.txt'");
        if (await ColumnExistsAsync("file_nodes", "sensitive_rules_version"))
            await ExecuteSqlAsync("UPDATE file_nodes SET sensitive_rules_version = NULL WHERE name = 'notes-2009.txt'");

        var analyze = await SfoAsync("analyze", job.ToString(), "--verbose", "--json");

        Assert.True(analyze.ExitCode == ExitCodes.Ok, analyze.Stdout + analyze.Stderr);
        Assert.DoesNotContain(Secret, analyze.Stderr);
        var marked = (await SfoAsync("files", job.ToString(), "--sensitive", "yes", "--json")).Result();
        var notes = marked.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "notes-2009.txt");
        Assert.Equal(["schluessel"], notes.GetProperty("sensitive").EnumerateArray().Select(t => t.GetString()));
        Assert.DoesNotContain(Secret, await AllOutputsAsync(job));
        Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(DebugLogPath));
    }

    [Fact]
    public async Task SkippedSshFolder_IsMarkedSensitive_WithoutBeingEntered()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".ssh"));
        Directory.CreateDirectory(Path.Combine(home, ".cache"));
        Directory.CreateDirectory(Path.Combine(home, "AppData", "Local"));
        // Made-up key file: a header line plus filler, put together at run time.
        File.WriteAllText(Path.Combine(home, ".ssh", "id_ed25519"), "-----BEGIN OPENSSH " + "PRIVATE KEY-----\n" + Secret);
        var job = await ScanAsync();

        var dirs = await SfoAsync("dirs", job.ToString(), "--sensitive", "yes", "--json");
        Assert.True(dirs.ExitCode == ExitCodes.Ok, dirs.Stdout + dirs.Stderr);
        var marked = dirs.Result();

        var items = marked.GetProperty("items").EnumerateArray()
            .ToDictionary(i => Path.GetFileName(i.GetProperty("path").GetString()!), i => i);
        var ssh = items[".ssh"];
        Assert.Equal("skip", ssh.GetProperty("status").GetString());
        Assert.Equal(["schluessel"], ssh.GetProperty("sensitive").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("dir:ssh", ssh.GetProperty("sensitive_rule").GetString());
        Assert.Equal(0, ssh.GetProperty("files").GetInt64());
        Assert.Equal("dir:appdata", items["AppData"].GetProperty("sensitive_rule").GetString());
        Assert.False(items.ContainsKey(".cache"));
        // The parent holds a marked folder, so it is listed as sensitive as well.
        Assert.Equal(2, items["home"].GetProperty("sensitive_dirs").GetInt64());
        Assert.DoesNotContain(Secret, marked.GetRawText());

        var files = await SfoAsync("files", job.ToString(), "--json");
        Assert.True(files.ExitCode == ExitCodes.Ok, files.Stdout + files.Stderr);
        Assert.DoesNotContain("id_ed25519", files.Stdout);
    }

    /// <summary>
    /// The full SHA-256 of a short secret file confirms a guess of its content. compare keeps it for
    /// unmarked files only; the match path still says where the backup is.
    /// </summary>
    [Fact]
    public async Task Compare_ShowsNoChecksumOfAMarkedFile()
    {
        var library = Path.Combine(_dir, "library");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "passwords-backup.txt"), $"mail: {Secret}");
        File.WriteAllText(Path.Combine(library, "letter-copy.txt"), "Dear landlord, the heating is broken.");
        var source = await ScanAsync();
        var scan = await SfoAsync("scan", library, "--role", "library", "--json");
        Assert.True(scan.ExitCode == ExitCodes.Ok, scan.Stdout + scan.Stderr);
        var libraryJob = scan.Result().GetProperty("job").GetProperty("id").GetInt64();

        var run = await SfoAsync("compare", source.ToString(), "--against", libraryJob.ToString(), "--files", "all", "--json");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        var files = run.Result().GetProperty("files").EnumerateArray()
            .ToDictionary(f => Path.GetFileName(f.GetProperty("path").GetString()!), f => f);
        Assert.Equal("backed_up", files["passwords.txt"].GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, files["passwords.txt"].GetProperty("match_hash").ValueKind);
        Assert.Equal("backed_up", files["letter.txt"].GetProperty("state").GetString());
        Assert.Equal(64, files["letter.txt"].GetProperty("match_hash").GetString()!.Length);
    }

    [Fact]
    public async Task Analyze_WithoutVerbose_WritesNoDebugLog()
    {
        var job = await ScanAsync();

        var run = await SfoAsync("analyze", job.ToString(), "--json");

        Assert.True(run.ExitCode == ExitCodes.Ok, run.Stdout + run.Stderr);
        Assert.False(File.Exists(DebugLogPath));
        Assert.DoesNotContain(Secret, run.Stderr);
    }
}

/// <summary>
/// Stands in for the Ollama HTTP API. Its answers repeat the whole prompt in every free-text field:
/// the worst case of a model that quotes what it was shown.
/// </summary>
internal sealed class EchoingOllamaHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/api/tags", StringComparison.Ordinal))
            return Json("""{"models":[{"name":"echo","details":{}}]}""");

        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var root = body.RootElement;
        var prompt = string.Join("\n", root.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("content").GetString()));
        var properties = root.GetProperty("format").GetProperty("properties");

        object answer;
        if (properties.TryGetProperty("verdict", out _))
        {
            answer = new
            {
                verdict = "gemischt", purpose = prompt, retention = "pruefen", reason = prompt,
                homogeneity = "low", dominant_type = "document", theme = prompt,
                anomalous_files = Array.Empty<object>(), sampling_strategy = "analyze_all", suggested_sample_size = 2
            };
        }
        else if (properties.TryGetProperty("category", out _))
        {
            answer = new
            {
                category = "Document", importance = "Medium", confidence = 0.9,
                summary = prompt, suggested_target = prompt, sensitive = Array.Empty<string>()
            };
        }
        else
        {
            answer = new { summary = prompt, theme = prompt, confirmed_anomalies = Array.Empty<object>() };
        }

        var content = JsonSerializer.Serialize(answer);
        return Json(JsonSerializer.Serialize(new { message = new { role = "assistant", content }, done = true }));
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
