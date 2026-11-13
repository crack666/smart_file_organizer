using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Hosting;
using SmartFileOrganizer.Infrastructure.Evaluation;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>sfo eval</c> end to end, in-process: generate the corpus, run it with scripted answers
/// (the truth as a perfect answer sheet, and a copy with deliberate mistakes), compare runs, turn
/// a correction into a case and run the cases. No model, temp folder and temp database.
/// </summary>
public class EvalCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sfo_evalcli_{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly string _corpus;

    // Deliberate mistakes in the wrong answer sheet: one per kind, plus a false alarm. The files lie in
    // mixed folders and the verdict goes from uniform to mixed, so the sample check of the staged
    // analysis neither hides a mistake nor turns it into a second one.
    private const string WrongVerdictDir = "Videos";
    private const string WrongCategoryFile = "Downloads/IMG_20230812_143012.jpg";
    private const string MissedMarkerFile = "Desktop/wiederherstellungscodes.txt";
    private const string FalseAlarmFile = "Downloads/Rechnung_2024-03_Stromanbieter.pdf";

    public EvalCliTests()
    {
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _corpus = Path.Combine(_dir, "corpus");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr)
    {
        public JsonElement Json()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.Clone();
        }

        public JsonElement Result => Json().GetProperty("result");
    }

    private async Task<Run> SfoAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new CliHost { Out = stdout, Error = stderr };
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], host);
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<Run> OkAsync(params string[] args)
    {
        var run = await SfoAsync(args);
        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}: {run.Stdout}{run.Stderr}");
        return run;
    }

    private async Task GenerateAsync()
    {
        var generated = await OkAsync("eval", "generate", _corpus, "--json");
        var result = generated.Result;
        Assert.Equal("small", result.GetProperty("name").GetString());
        Assert.True(result.GetProperty("directories").GetInt32() >= 30);
        Assert.True(File.Exists(Path.Combine(_corpus, "truth.json")));
    }

    /// <summary>A copy of the truth with four deliberate mistakes, as an answer sheet.</summary>
    private string WriteWrongAnswers()
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(_corpus, "truth.json")))!;
        JsonNode Entry(string list, string path) => doc[list]!.AsArray().Single(e => e!["path"]!.GetValue<string>() == path)!;
        Entry("directories", WrongVerdictDir)["verdict"] = "mixed";
        Entry("files", WrongCategoryFile)["category"] = "screenshot";
        Entry("files", MissedMarkerFile)["sensitive"] = new JsonArray();
        Entry("files", FalseAlarmFile)["sensitive"] = new JsonArray("finance");
        var path = Path.Combine(_dir, "wrong.json");
        File.WriteAllText(path, doc.ToJsonString());
        return path;
    }

    private static Dictionary<string, JsonElement> FailuresByKey(JsonElement result) =>
        result.GetProperty("failures").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);

    private static string Key(EvalKind kind, string path) => CorpusEvaluator.Key(kind, path);

    [Fact]
    public async Task Eval_WithStub_NamesExactlyTheDeliberateFailures()
    {
        await GenerateAsync();
        var truthPath = Path.Combine(_corpus, "truth.json");

        var perfect = (await OkAsync("eval", _corpus, "--stub", truthPath, "--json")).Result;
        Assert.Equal("corpus", perfect.GetProperty("mode").GetString());
        Assert.Equal($"small v{SmallCorpusGenerator.GeneratorVersion} seed 36", perfect.GetProperty("subject").GetString());
        Assert.Equal("stub:truth.json", perfect.GetProperty("model").GetString());
        Assert.False(string.IsNullOrEmpty(perfect.GetProperty("prompt_fingerprint").GetString()));
        Assert.Equal(0, perfect.GetProperty("untracked_count").GetInt32());
        var categoryScore = perfect.GetProperty("scores").EnumerateArray().Single(s => s.GetProperty("kind").GetString() == "file_category");
        Assert.Equal(1.0, categoryScore.GetProperty("accuracy").GetDouble());
        var baseline = FailuresByKey(perfect);
        // With perfect answers, what remains was decided by rules or never reached the model.
        Assert.All(baseline.Values, f => Assert.DoesNotContain("stub:", f.GetProperty("decided_by").GetString() ?? ""));

        var wrong = (await OkAsync("eval", _corpus, "--stub", WriteWrongAnswers(), "--json")).Result;
        var failures = FailuresByKey(wrong);

        var expectedNew = new[]
        {
            Key(EvalKind.DirectoryVerdict, WrongVerdictDir), Key(EvalKind.FileCategory, WrongCategoryFile),
            Key(EvalKind.Sensitive, MissedMarkerFile), Key(EvalKind.Sensitive, FalseAlarmFile)
        };
        Assert.Equal(baseline.Keys.Concat(expectedNew).Order(StringComparer.Ordinal), failures.Keys.Order(StringComparer.Ordinal));

        AssertFailure(failures[expectedNew[0]], "wrong", EvalCodes.Verdict(DirectoryVerdictKind.Uniform), EvalCodes.Verdict(DirectoryVerdictKind.Mixed));
        Assert.Equal("model stub:wrong.json", failures[expectedNew[0]].GetProperty("decided_by").GetString());
        AssertFailure(failures[expectedNew[1]], "wrong", EvalCodes.Category(FileCategory.Photo), EvalCodes.Category(FileCategory.Screenshot));
        AssertFailure(failures[expectedNew[2]], "wrong", EvalCodes.MarkerList(SensitiveMarker.Credentials), EvalCodes.MarkerList(SensitiveMarker.None));
        AssertFailure(failures[expectedNew[3]], "wrong", EvalCodes.MarkerList(SensitiveMarker.None), EvalCodes.MarkerList(SensitiveMarker.Finance));
        Assert.Contains("extra", failures[expectedNew[3]].GetProperty("detail").GetString());

        // --compare without ids: newest run against the one before it.
        var comparison = (await OkAsync("eval", "--compare", "--json")).Result;
        Assert.True(comparison.GetProperty("same_subject").GetBoolean());
        Assert.Equal(perfect.GetProperty("run_id").GetInt64(), comparison.GetProperty("before").GetProperty("id").GetInt64());
        Assert.Equal(expectedNew.Order(StringComparer.Ordinal),
            comparison.GetProperty("new_failures").EnumerateArray().Select(f => f.GetProperty("key").GetString()!).Order(StringComparer.Ordinal));
        Assert.Empty(comparison.GetProperty("fixed").EnumerateArray());
        Assert.Contains(comparison.GetProperty("changes").EnumerateArray(), c => c.GetProperty("name").GetString() == "model");

        // The text form names each failure with expectation and result.
        var text = await OkAsync("eval", "--compare", wrong.GetProperty("run_id").GetInt64().ToString(), perfect.GetProperty("run_id").GetInt64().ToString());
        Assert.Contains($"{WrongCategoryFile}: expected photo, got screenshot", text.Stdout);
        Assert.Contains("Fixed (4)", text.Stdout);
    }

    /// <summary>
    /// The scan does not enter <c>.ssh</c>, so its files are never listed; since the sensitive gaps were closed
    /// the skipped folder itself carries the marker. The evaluation reads that marker for the files inside, on
    /// both sides: the private key counts as found, and the public key next to it as a false alarm, because the
    /// folder marker withholds it as well (issue 50; docs/evaluation.md).
    /// </summary>
    [Fact]
    public async Task Eval_FilesInASkippedMarkedFolder_CarryTheMarkerOfTheFolder()
    {
        await GenerateAsync();
        var perfect = (await OkAsync("eval", _corpus, "--stub", Path.Combine(_corpus, "truth.json"), "--json")).Result;
        var failures = FailuresByKey(perfect);

        Assert.DoesNotContain(Key(EvalKind.Sensitive, ".ssh/id_ed25519"), failures.Keys);
        var pub = failures[Key(EvalKind.Sensitive, ".ssh/id_ed25519.pub")];
        AssertFailure(pub, "wrong", EvalCodes.MarkerList(SensitiveMarker.None), EvalCodes.MarkerList(SensitiveMarker.Keys));
        Assert.Contains("skipped folder .ssh", pub.GetProperty("detail").GetString());
        Assert.Contains("dir:ssh", pub.GetProperty("decided_by").GetString());
    }

    private static void AssertFailure(JsonElement failure, string outcome, string expected, string actual)
    {
        Assert.Equal(outcome, failure.GetProperty("outcome").GetString());
        Assert.Equal(expected, failure.GetProperty("expected").GetString());
        Assert.Equal(actual, failure.GetProperty("actual").GetString());
    }

    [Fact]
    public async Task Correction_BecomesACase_AndEvalCasesRunsIt()
    {
        await GenerateAsync();
        var truthPath = Path.Combine(_corpus, "truth.json");
        var wrongPath = WriteWrongAnswers();
        var jobId = (await OkAsync("eval", _corpus, "--stub", wrongPath, "--json")).Result.GetProperty("job_id").GetInt64();

        // The human corrects two answers in the review: a photo taken for a screenshot, and the
        // category of a bank statement (sensitive by rule, so no evidence may be stored).
        var services = new ServiceCollection()
            .AddSmartFileOrganizerCore(o => { o.DatabasePath = _dbPath; o.ReloadSettingsOnChange = false; })
            .BuildServiceProvider();
        await using (services)
        {
            await services.InitializeSmartFileOrganizerDatabaseAsync();
            var files = services.GetRequiredService<IFileRepository>();
            var root = Path.Combine(_corpus, "tree");
            var downloads = await files.GetByDirectoryAsync(jobId, Path.Combine(root, "Downloads"));
            var photo = downloads.Single(f => f.Name == Path.GetFileName(WrongCategoryFile));
            var statement = downloads.Single(f => f.Name == "Kontoauszug_2024_03.pdf");
            Assert.Equal(FileCategory.Screenshot, photo.Classification!.Category);
            Assert.Equal(FileCategory.PersonalDocument, statement.Classification!.Category);

            // The human files bank statements as plain documents: a correction the answer sheet does not share.
            var review = services.GetRequiredService<ReviewService>();
            await review.ApplyOverrideAsync(photo.Id, FileCategory.Photo, null, null);
            await review.ApplyOverrideAsync(statement.Id, FileCategory.Document, null, null);

            var cases = await services.GetRequiredService<IEvalCaseRepository>().GetAllAsync();
            Assert.Equal(2, cases.Count);
            Assert.NotNull(cases.Single(c => c.FileNodeId == photo.Id).EvidenceJson);
            var sensitiveCase = cases.Single(c => c.FileNodeId == statement.Id);
            Assert.True(sensitiveCase.IsSensitive);
            Assert.Null(sensitiveCase.EvidenceJson);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // With the right answers the photo case is fixed; the bank statement is read again from
        // disk (locally) and the answer sheet still disagrees with the human.
        var fixedRun = await OkAsync("eval", "--cases", "--stub", truthPath, "--json");
        var result = fixedRun.Result;
        Assert.Equal("cases", result.GetProperty("mode").GetString());
        var byPath = result.GetProperty("cases").EnumerateArray().ToDictionary(c => Path.GetFileName(c.GetProperty("path").GetString()!));
        var photoCase = byPath[Path.GetFileName(WrongCategoryFile)];
        Assert.Equal("hit", photoCase.GetProperty("outcome").GetString());
        Assert.Equal("screenshot", photoCase.GetProperty("corrected_from").GetString());
        Assert.Equal("photo", photoCase.GetProperty("actual").GetString());
        var statementCase = byPath["Kontoauszug_2024_03.pdf"];
        Assert.Equal("wrong", statementCase.GetProperty("outcome").GetString());
        Assert.True(statementCase.GetProperty("sensitive").GetBoolean());
        Assert.Equal(EvalCodes.Category(FileCategory.PersonalDocument), statementCase.GetProperty("actual").GetString());

        // Nothing of the evidence leaves the database.
        Assert.DoesNotContain("evidence", fixedRun.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Beispielbank", fixedRun.Stdout, StringComparison.Ordinal);

        // A worse answer sheet breaks the photo case again; --compare shows it as a new failure.
        await OkAsync("eval", "--cases", "--stub", wrongPath, "--json");
        var comparison = (await OkAsync("eval", "--compare", "--json")).Result;
        var newFailure = Assert.Single(comparison.GetProperty("new_failures").EnumerateArray());
        Assert.EndsWith(Path.GetFileName(WrongCategoryFile), newFailure.GetProperty("path").GetString());
        Assert.Equal(1, comparison.GetProperty("still_failing").GetInt32());
    }

    [Theory]
    [InlineData("eval")]
    [InlineData("eval", "somewhere", "--cases")]
    [InlineData("eval", "--cases", "--compare")]
    [InlineData("eval", "--compare", "1", "--stub", "x.json")]
    public async Task Eval_WrongInvocation_IsUsageError(params string[] args)
    {
        var run = await SfoAsync([.. args, "--json"]);
        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Equal(ErrorCodes.Usage, run.Json().GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Eval_Failures_HaveStableCodes()
    {
        var nothing = await SfoAsync("eval", "--compare", "--json");
        Assert.Equal(ExitCodes.Failure, nothing.ExitCode);
        Assert.Equal(ErrorCodes.RunNotFound, nothing.Json().GetProperty("error").GetProperty("code").GetString());

        var missing = await SfoAsync("eval", Path.Combine(_dir, "no-corpus"), "--json");
        Assert.Equal(ErrorCodes.PathNotFound, missing.Json().GetProperty("error").GetProperty("code").GetString());

        Directory.CreateDirectory(Path.Combine(_dir, "bad", "tree"));
        File.WriteAllText(Path.Combine(_dir, "bad", "truth.json"),
            """
            { "schema_version": 1, "corpus": { "name": "bad", "generator_version": 1, "seed": 0 }, "root": "tree",
              "directories": [ { "path": ".", "verdict": "chaos" } ], "files": [] }
            """);
        var invalid = await SfoAsync("eval", Path.Combine(_dir, "bad"), "--json");
        Assert.Equal(ErrorCodes.TruthInvalid, invalid.Json().GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("unknown verdict 'chaos'", invalid.Json().GetProperty("error").GetProperty("message").GetString());

        await GenerateAsync();
        var again = await SfoAsync("eval", "generate", _corpus, "--json");
        Assert.Equal(ErrorCodes.OutputNotEmpty, again.Json().GetProperty("error").GetProperty("code").GetString());
    }
}
