using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>sfo scan --refresh</c> keeps what the previous job said about unchanged folders
/// (https://github.com/crack666/smart_file_organizer/issues/86): verdict, pre-assessment, summary and the status of their
/// files. A generated source in a temp folder this test makes, the real <see cref="OllamaService"/> against
/// <see cref="FakeModelServer"/>, and the calls counted.
/// </summary>
public class RefreshFolderJudgementTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _source;
    private readonly FakeModelServer _server = new();

    public RefreshFolderJudgementTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_refresh_folders_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _source = Path.Combine(_dir, "stick");

        Write("letters/landlord.txt", "Letter to the landlord, March: the heating is broken again.");
        Write("letters/bank.txt", "Letter to the bank about the standing order for the rent.");
        Write("letters/old/school.txt", "An old letter from school, about the class trip.");
        Write("recipes/cake.txt", "Recipe: apple cake with cinnamon and a lot of butter.");
        Write("recipes/soup.txt", "Recipe: pumpkin soup with ginger.");
        Write("notes/today.txt", "Notes for today: call the plumber.");
        Write("notes/later.txt", "Notes for later: buy a new kettle.");

        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dbPath)!, "settings.json"), $$"""
            {
              "Ollama": { "BaseUrl": "http://model-server.test:11999", "Model": "{{FakeModelServer.ModelName}}",
                          "TimeoutSeconds": 5, "MaxRetries": 2, "MaxParallelRequests": 2 },
              "ModelServer": { "RetryDelaySeconds": 0.002, "MaxRetryAfterSeconds": 0.01, "OutageAfterFailures": 2,
                               "ProbeIntervalSeconds": 0.005, "ProbeTimeoutSeconds": 0.2,
                               "MaxWaitMinutes": 0.5, "WarmupTimeoutSeconds": 1 }
            }
            """);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task UnchangedFolders_KeepVerdictAndSummary_AndTheNextAnalysisAsksNothingAboutThem()
    {
        var first = await ScanAsync();
        await AnalyzeAsync(first);
        Assert.True(_server.Log.Any(r => r.Kind is "pre_assessment" or "summary"), "the first analysis judged folders with the model");
        var judgedBefore = await ScalarAsync($"SELECT COUNT(*) FROM directory_verdicts v JOIN directory_nodes d ON d.id = v.directory_node_id WHERE d.job_id = {first}");
        var summariesBefore = await ScalarAsync($"SELECT COUNT(*) FROM ai_directory_results a JOIN directory_nodes d ON d.id = a.directory_node_id WHERE d.job_id = {first}");

        // One file in notes changes; letters (with its subfolder) and recipes stay as they were.
        var changed = Path.Combine(_source, "notes", "today.txt");
        File.AppendAllText(changed, " And the electrician.");
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));

        var scan = await SfoAsync("scan", _source, "--refresh", "--json");
        Assert.True(scan.ExitCode == 0, scan.Stderr + scan.Stdout);
        var result = scan.Json().GetProperty("result");
        var second = result.GetProperty("job").GetProperty("id").GetInt64();
        var refresh = result.GetProperty("refresh");
        var folders = refresh.GetProperty("folders");
        var toJudge = folders.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).ToList();
        // The changed folder and the root above it are judged anew; the others keep what was said about them.
        Assert.Equal([_source, Path.Combine(_source, "notes")], toJudge);
        Assert.Equal(2, folders.GetProperty("to_judge").GetInt64());
        Assert.Equal(3, folders.GetProperty("unchanged").GetInt64());
        var carried = refresh.GetProperty("carried");
        Assert.Equal(judgedBefore - 2, carried.GetProperty("verdicts").GetInt64());
        Assert.True(carried.GetProperty("summaries").GetInt64() > 0);
        Assert.True(carried.GetProperty("summaries").GetInt64() < summariesBefore);

        var before = _server.Log.Count;
        await AnalyzeAsync(second);
        var asked = _server.Log.Skip(before).Where(r => r.Kind is "pre_assessment" or "summary" or "classification" or "second_check").ToList();

        // Nothing about letters, letters/old, recipes or their files reached the model again.
        Assert.DoesNotContain(asked, r => r.Name is { } n && (n.StartsWith("letters", StringComparison.Ordinal)
                                                              || n.StartsWith("recipes", StringComparison.Ordinal)
                                                              || n is "landlord.txt" or "bank.txt" or "school.txt" or "cake.txt" or "soup.txt"));
        // The changed file is classified again; nothing else of the files.
        Assert.Equal(["today.txt"], asked.Where(r => r.Kind == "classification").Select(r => r.Name));

        // Every folder of the new job has its verdict and summary again, the kept ones without a call.
        Assert.Equal(judgedBefore, await ScalarAsync($"SELECT COUNT(*) FROM directory_verdicts v JOIN directory_nodes d ON d.id = v.directory_node_id WHERE d.job_id = {second}"));
        Assert.Equal(summariesBefore, await ScalarAsync($"SELECT COUNT(*) FROM ai_directory_results a JOIN directory_nodes d ON d.id = a.directory_node_id WHERE d.job_id = {second}"));
        // Carried rows point at folders of the new job only.
        Assert.Equal(0, await ScalarAsync(
            $"SELECT COUNT(*) FROM directory_verdicts v JOIN directory_nodes d ON d.id = v.directory_node_id JOIN directory_nodes u ON u.id = v.unit_directory_node_id " +
            $"WHERE d.job_id = {second} AND u.job_id <> {second}"));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr)
    {
        public JsonElement Json()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.Clone();
        }
    }

    private async Task<Run> SfoAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost
        {
            Out = stdout,
            Error = stderr,
            ConfigureServices = services =>
                services.AddHttpClient<IOllamaService, OllamaService>().ConfigurePrimaryHttpMessageHandler(() => _server)
        };
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], host, CancellationToken.None);
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<long> ScanAsync()
    {
        var scan = await SfoAsync("scan", _source, "--json");
        Assert.True(scan.ExitCode == 0, scan.Stderr + scan.Stdout);
        return scan.Json().GetProperty("result").GetProperty("job").GetProperty("id").GetInt64();
    }

    private async Task AnalyzeAsync(long job)
    {
        var run = await SfoAsync("analyze", job.ToString(), "--json");
        Assert.True(run.ExitCode == 0, run.Stderr + run.Stdout);
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
