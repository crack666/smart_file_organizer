using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Cli.Commands;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// Rules that hold for every command of <c>sfo</c>, checked against the command tree itself, so a new command
/// is covered without anyone remembering to add it (https://github.com/crack666/smart_file_organizer/issues/111).
/// </summary>
public class CommandCatalogueTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _root;

    public CommandCatalogueTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_catalogue_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _root = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "Shopping list: milk, bread");
        File.WriteAllText(Path.Combine(_root, "docs", "letter.txt"), "Dear landlord");
        File.WriteAllText(Path.Combine(_root, "docs", "letter2.txt"), "Dear landlord, again");
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

    private async Task<Run> SfoAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], new CliHost { Out = stdout, Error = stderr });
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Every command below the root, with its path (<c>plan apply</c>).</summary>
    internal static IEnumerable<(string Path, Command Command)> AllCommands()
    {
        var root = SfoApp.BuildRoot(new CliHost { Out = TextWriter.Null, Error = TextWriter.Null }, new GlobalOptions());
        var open = new Stack<(string Path, Command Command)>(root.Subcommands.Select(c => (c.Name, c)));
        while (open.Count > 0)
        {
            var (path, command) = open.Pop();
            yield return (path, command);
            foreach (var sub in command.Subcommands)
                open.Push(($"{path} {sub.Name}", sub));
        }
    }

    /// <summary>
    /// The command line library reads a number with the language of the machine: on a German system
    /// <c>0.5</c> became 5. Decimal values are therefore taken as text and read with a decimal point by the
    /// command; an option or argument typed as a decimal number would bring the fault back.
    /// </summary>
    [Fact]
    public void NoOptionOrArgument_LeavesADecimalNumberToTheParser()
    {
        static bool IsDecimal(Type type)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;
            t = t.IsArray ? t.GetElementType()! : t;
            return t == typeof(double) || t == typeof(float) || t == typeof(decimal);
        }

        var offenders = AllCommands()
            .SelectMany(c => c.Command.Options.Where(o => IsDecimal(o.ValueType)).Select(o => $"{c.Path} {o.Name}")
                .Concat(c.Command.Arguments.Where(a => IsDecimal(a.ValueType)).Select(a => $"{c.Path} <{a.Name}>")))
            .Concat(new GlobalOptions().All.Where(o => IsDecimal(o.ValueType)).Select(o => o.Name))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Take the value as text and read it with CultureInfo.InvariantCulture (see FilesCommand --max-confidence): "
            + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public async Task DecimalValuesInOptions_AreReadWithADecimalPoint_WhateverTheLanguageOfTheMachine(string culture)
    {
        var scan = await SfoAsync("scan", _root, "--json");
        Assert.True(scan.ExitCode == ExitCodes.Ok, scan.Stdout + scan.Stderr);
        var job = scan.Result().GetProperty("job").GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture);

        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var files = await SfoAsync("files", job, "--max-confidence", "0.5", "--json");
            Assert.True(files.ExitCode == ExitCodes.Ok, files.Stdout + files.Stderr);

            // 0.5K is 512 bytes; read with a decimal comma it would be 5K.
            var duplicates = await SfoAsync("duplicates", job, "--min-size", "0.5K", "--json");
            Assert.True(duplicates.ExitCode == ExitCodes.Ok, duplicates.Stdout + duplicates.Stderr);
            Assert.Equal(512, duplicates.Result().GetProperty("min_size").GetInt64());

            // 0.01K is 10 bytes: both folders stay in the list.
            var dirs = await SfoAsync("dirs", job, "--min-size", "0.01K", "--json");
            Assert.True(dirs.ExitCode == ExitCodes.Ok, dirs.Stdout + dirs.Stderr);
            Assert.Equal(2, dirs.Result().GetProperty("total").GetInt64());

            var bundles = await SfoAsync("bundles", "build", job, "--gap-hours", "0.5", "--max-event-days", "1.5", "--json");
            Assert.True(bundles.ExitCode == ExitCodes.Ok, bundles.Stdout + bundles.Stderr);

            // A decimal comma is refused everywhere, also where the machine writes one.
            var comma = await SfoAsync("files", job, "--max-confidence", "0,5", "--json");
            Assert.Equal(ExitCodes.Usage, comma.ExitCode);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    // ------------------------------------------------------------------ every command against the protection

    /// <summary>Made up; stands for a password in a password list.</summary>
    private static readonly string Secret = "FAKEPW-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Commands that never read a job, a file of a job or the stored results, with the reason. Everything
    /// else has to be called by <see cref="EveryCommand_KeepsTheTextOfAccessDataOut"/>.
    /// </summary>
    private static readonly Dictionary<string, string> WithoutJobData = new(StringComparer.Ordinal);

    /// <summary>Commands whose call here may end with another exit code than 0 (never a usage error), with the reason.</summary>
    private static readonly Dictionary<string, string> MayFail = new(StringComparer.Ordinal)
    {
        ["doctor"] = "the model stand-in lists no model of the configured name",
    };

    private readonly HashSet<string> _called = new(StringComparer.Ordinal);
    private readonly System.Text.StringBuilder _seen = new();

    /// <summary>
    /// Runs one command as JSON and as text with the model stand-in that repeats its whole prompt in every free-text
    /// field, keeps both outputs for the search and notes the command as called. A usage error or an unexpected
    /// exception fails the test: the call would then prove nothing.
    /// </summary>
    private Task<JsonElement> CallAsync(string command, params string[] args) => CallAsync(command, args, alsoAsText: true);

    /// <summary><see cref="CallAsync(string, string[])"/> for a command that cannot run a second time on the same input: JSON only.</summary>
    private Task<JsonElement> CallOnceAsync(string command, params string[] args) => CallAsync(command, args, alsoAsText: false);

    private async Task<JsonElement> CallAsync(string command, string[] args, bool alsoAsText)
    {
        _called.Add(command);
        Run json = null!;
        foreach (var asJson in alsoAsText ? new[] { true, false } : [true])
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var host = new CliHost
            {
                Out = stdout,
                Error = stderr,
                ConfigureServices = services =>
                {
                    services.AddHttpClient<IOllamaService, OllamaService>()
                        .ConfigurePrimaryHttpMessageHandler(() => new EchoingOllamaHandler());
                    // The temp folder lies in the user profile, which is a protected place on a real machine.
                    services.AddSingleton<IProtectedPlaces>(new NoProtectedPlaces());
                }
            };
            string[] line = [.. command.Split(' '), .. args, .. asJson ? new[] { "--json" } : [], "--db", _dbPath];
            var exit = await SfoApp.RunAsync(line, host);
            var run = new Run(exit, stdout.ToString(), stderr.ToString());
            _seen.AppendLine(run.Stdout).AppendLine(run.Stderr);
            var shown = $"sfo {string.Join(' ', line)} (exit {exit}): {run.Stdout}{run.Stderr}";
            Assert.True(exit == ExitCodes.Ok || MayFail.ContainsKey(command) && exit != ExitCodes.Usage, shown);
            Assert.False(run.Stdout.Contains(ErrorCodes.InternalError) || run.Stderr.Contains(ErrorCodes.InternalError), shown);
            if (asJson) json = run;
        }
        using var doc = JsonDocument.Parse(json.Stdout);
        return doc.RootElement.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    private sealed class NoProtectedPlaces : IProtectedPlaces
    {
        public IReadOnlyList<string> Get() => [];
    }

    private void Keep(string file)
    {
        if (File.Exists(file))
            _seen.AppendLine(File.ReadAllText(file));
    }

    private static string Id(JsonElement element, string property) =>
        element.GetProperty(property).GetInt64().ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Every command of the tree is called on a job with a password list, a library that holds a copy of it and a
    /// model that quotes whatever it is shown; nothing any of them prints or writes carries the password. A command
    /// added later fails this test until it is called here (or listed in <see cref="WithoutJobData"/> with a reason),
    /// so no new command gets around the protection unnoticed
    /// (https://github.com/crack666/smart_file_organizer/issues/33).
    /// </summary>
    [Fact]
    public async Task EveryCommand_KeepsTheTextOfAccessDataOut()
    {
        File.WriteAllText(Path.Combine(_root, "docs", "passwords.txt"), $"mail: {Secret}");
        Directory.CreateDirectory(Path.Combine(_root, "Urlaub 2019"));
        for (var i = 1; i <= 4; i++)
            File.WriteAllBytes(Path.Combine(_root, "Urlaub 2019", $"IMG_20190704_12{i}000.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, (byte)i, 1, 2, 3]);
        var library = Path.Combine(_dir, "library");
        Directory.CreateDirectory(Path.Combine(library, "Dokumente", "Briefe"));
        Directory.CreateDirectory(Path.Combine(library, "Fotos", "2018 Ostsee"));
        File.WriteAllText(Path.Combine(library, "Dokumente", "Briefe", "brief.txt"), "Dear landlord");
        File.WriteAllText(Path.Combine(library, "Dokumente", "passwords-backup.txt"), $"mail: {Secret}");
        File.WriteAllBytes(Path.Combine(library, "Fotos", "2018 Ostsee", "IMG_20180704_120000.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 9]);
        var outputs = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;

        // A second scan of the same folder would be a second job.
        var job = Id((await CallOnceAsync("scan", _root)).GetProperty("job"), "id");
        var lib = Id((await CallOnceAsync("scan", library, "--role", "library")).GetProperty("job"), "id");
        await CallAsync("scan", "--resume", job);
        await CallAsync("rules show");
        await CallAsync("rules check", Path.Combine(_root, "docs"), "--root", _root);
        await CallAsync("doctor");
        await CallAsync("jobs");

        await CallAsync("analyze", job, "--verbose");
        await CallAsync("status", job);
        await CallAsync("summary", job);
        await CallAsync("dirs", job);
        await CallAsync("dirs", job, "--sensitive", "yes");
        await CallAsync("files", job);
        await CallAsync("files", lib);
        await CallAsync("show", job, ".");
        await CallAsync("show", job, "docs");
        await CallAsync("show", job, Path.Combine("docs", "passwords.txt"));
        await CallAsync("show", lib, Path.Combine("Dokumente", "passwords-backup.txt"));
        await CallAsync("facts refresh", job);
        await CallAsync("cache stats");

        await CallAsync("compare", job, "--against", lib, "--files", "all");
        await CallAsync("duplicates", job, lib, "--verify");
        var csv = Path.Combine(outputs, "export.csv");
        await CallAsync("export", job, "--csv", csv);
        Keep(csv);

        await CallAsync("library review", lib, "--no-model");
        await CallAsync("library review", lib, "--refresh");
        var targets = Path.Combine(outputs, "targets.json");
        await CallAsync("library propose", lib, "--no-model", "--out", targets);
        Keep(targets);
        await CallAsync("library show", lib);
        await CallAsync("library match", job, "docs", "--against", lib);
        await CallAsync("library match", job, "Urlaub 2019", "--against", lib, "--no-model");
        await CallAsync("library folder", lib, "Dokumente");
        await CallAsync("library describe", lib, "Dokumente", "--refresh");

        await CallAsync("bundles build", job);
        var bundles = await CallAsync("bundles list", job);
        foreach (var bundle in bundles.GetProperty("items").EnumerateArray())
            await CallAsync("bundles show", job, Id(bundle, "id"));

        var findings = await CallAsync("findings", job);
        var keys = findings.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()!).ToList();
        Assert.Contains(findings.GetProperty("items").EnumerateArray(), i => i.GetProperty("kind").GetString() == "sensitive");
        foreach (var key in keys)
            await CallAsync("findings show", job, key);
        await CallAsync("findings targets", job, "--against", lib);
        await CallAsync("findings targets", job, "--against", lib, "--local-model", "--refresh");
        await CallAsync("findings candidates", job, "--against", lib);
        await CallAsync("findings place", job, keys[0], "--status", "inbox", "--reason", "checked by the test", "--against", lib);
        await CallAsync("findings reject", job, keys[0]);
        await CallAsync("findings wrong", job, keys[0]);
        await CallAsync("findings reopen", job, keys[0]);
        await CallAsync("findings accept", [job, .. keys], alsoAsText: true);
        await CallAsync("decide", job);
        await CallAsync("decide", job, "--targets", targets);

        var plan = Id(await CallAsync("plan create", job, "--against", lib), "plan_id");
        var shown = await CallAsync("plan show", plan);
        await CallAsync("plan summary", plan);
        var actions = shown.GetProperty("items").EnumerateArray().Select(a => Id(a, "id")).ToList();
        Assert.NotEmpty(actions);
        await CallAsync("plan set", plan, actions[0], "--reason", "checked by the test");
        var import = Path.Combine(outputs, "import.json");
        File.WriteAllText(import, "{\"actions\": [{\"id\": " + actions[0] + ", \"reason\": \"from a file\"}]}");
        await CallAsync("plan import", plan, import);
        await CallAsync("plan approve", plan, "--all");
        await CallAsync("plan reject", plan, "--action", actions[0]);
        await CallAsync("plan apply", plan);
        await CallAsync("plan undo", plan);
        await CallAsync("plan purge", plan);
        await CallAsync("plan keep-source", job);
        await CallAsync("plan keep-source", job, "--off");
        await CallAsync("places");
        await CallAsync("where", Path.Combine(_root, "docs", "passwords.txt"));

        // The evaluation runs on a corpus of its own; the password list is put into it.
        var corpus = Path.Combine(_dir, "corpus");
        await CallOnceAsync("eval generate", corpus);
        File.WriteAllText(Path.Combine(corpus, "passwords.txt"), $"mail: {Secret}");
        await CallAsync("eval", corpus, "--stub", Path.Combine(corpus, "truth.json"));
        await CallAsync("eval", "--compare");
        await CallAsync("cache clear", "--stale");

        // The stand-in's quotes do reach the output: the text of an unmarked file is there, the password is not.
        Assert.Contains("Dear landlord", _seen.ToString());
        Keep(Path.Combine(Path.GetDirectoryName(_dbPath)!, CliLogFiles.DebugLogName));
        var seen = _seen.ToString();
        Assert.Contains("passwords.txt", seen);   // the file is named; its text is not shown
        Assert.DoesNotContain(Secret, seen);

        var expected = AllCommands().Where(c => c.Command.Action is not null).Select(c => c.Path).ToList();
        var missing = expected.Where(path => !_called.Contains(path) && !WithoutJobData.ContainsKey(path)).Order().ToList();
        Assert.True(missing.Count == 0,
            "Not called by this test, so not checked against the protection of marked files: " + string.Join(", ", missing)
            + ". Call them above on the job that holds the password list, or list them in WithoutJobData with the reason.");
        Assert.All(_called.Concat(WithoutJobData.Keys), path => Assert.Contains(path, expected));
    }
}
