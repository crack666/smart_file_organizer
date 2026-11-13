using System.CommandLine;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Evaluation;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo eval</c>: measures how good verdicts, categories and sensitive markers are.
/// <c>sfo eval &lt;corpus&gt;</c> scans and analyses a corpus with a truth file and compares;
/// <c>--cases</c> runs the stored corrections again; <c>--compare</c> sets two stored runs side by side;
/// <c>sfo eval generate &lt;dir&gt;</c> writes the small built-in corpus.
/// </summary>
public static class EvalCommand
{
    /// <param name="ScanSeconds">Duration of the scan; null for a case run.</param>
    /// <param name="AnalyzeSeconds">Duration of the model phases (pre-assessment and classification), or of the case run.</param>
    public sealed record EvalRunResult(
        long RunId,
        EvalRunMode Mode,
        string Subject,
        long? JobId,
        string? Model,
        string? Stub,
        string? PromptFingerprint,
        string? AppVersion,
        IReadOnlyDictionary<string, string> Settings,
        double? ScanSeconds,
        double AnalyzeSeconds,
        IReadOnlyList<EvalKindScore> Scores,
        IReadOnlyList<EvalFailure> Failures,
        int UntrackedCount,
        IReadOnlyList<string> Untracked,
        IReadOnlyList<EvalCaseResult>? Cases,
        IReadOnlyList<ModelCallUsage> ModelCalls);

    public sealed record GenerateResult(
        string Directory,
        string Truth,
        string Root,
        string Name,
        int GeneratorVersion,
        int Seed,
        int Directories,
        int Files);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var corpus = new Argument<string?>("corpus")
        {
            Description = "Corpus folder with truth.json (see sfo eval generate). Scanned and analysed into a new job.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var cases = new Option<bool>("--cases")
        {
            Description = "Run the stored test cases (corrections made in the review) again."
        };
        var compare = new Option<long[]>("--compare")
        {
            Description = "Compare two stored runs: none = newest run with the one before it (same mode and corpus), one id = that run with the one before it, two ids = those two.",
            HelpName = "run",
            Arity = new ArgumentArity(0, 2),
            AllowMultipleArgumentsPerToken = true
        };
        var stub = new Option<string?>("--stub")
        {
            Description = "Answer from this file instead of Ollama (same shape as truth.json; the truth itself is a perfect answer sheet). For checking the evaluation without a model.",
            HelpName = "answers.json"
        };
        var model = new Option<string?>("--model")
        {
            Description = "Ollama model for this run. Default: Ollama.Model from settings. Not saved.",
            HelpName = "name"
        };
        var parallel = new Option<int?>("--parallel")
        {
            Description = "Concurrent Ollama requests for this run (1-8). Default: Ollama.MaxParallelRequests.",
            HelpName = "n"
        };
        parallel.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int?>() is { } n && (n < 1 || n > 8))
                r.AddError("--parallel must be between 1 and 8.");
        });

        var command = new Command("eval", "Measure verdicts, categories and sensitive markers against a corpus truth, re-run stored corrections, or compare runs.")
        {
            corpus, cases, compare, stub, model, parallel
        };
        command.Validators.Add(result =>
        {
            var modes = (string.IsNullOrWhiteSpace(result.GetValue(corpus)) ? 0 : 1)
                        + (result.GetValue(cases) ? 1 : 0)
                        + (result.GetResult(compare) != null ? 1 : 0);
            if (modes != 1)
                result.AddError("Give exactly one of: a corpus folder, --cases, --compare.");
            if (result.GetResult(compare) != null && (result.GetValue(stub) != null || result.GetValue(model) != null || result.GetValue(parallel) != null))
                result.AddError("--stub, --model and --parallel do not apply to --compare.");
        });
        command.Subcommands.Add(CreateGenerate(host, globals));

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var p = ctx.ParseResult;
            if (p.GetResult(compare) != null)
                return await CompareAsync(ctx, p.GetValue(compare) ?? [], ct);

            ScriptedAnswerOllamaService? stubService = null;
            if (p.GetValue(stub) is { } stubPath)
            {
                stubService = new ScriptedAnswerOllamaService(LoadTruth(Path.GetFullPath(stubPath)), Path.GetFileName(stubPath));
                ctx.ConfigureServices(s => s.AddSingleton<IOllamaService>(stubService));
            }

            var services = await ctx.GetServicesAsync(ct);
            var ollama = services.GetRequiredService<OllamaOptions>();
            if (p.GetValue(model) is { Length: > 0 } m)
                ollama.Model = m;
            if (p.GetValue(parallel) is { } n)
                ollama.MaxParallelRequests = n;

            return p.GetValue(cases)
                ? await RunCasesAsync(ctx, services, ollama, stubService, ct)
                : await RunCorpusAsync(ctx, services, ollama, stubService, Path.GetFullPath(p.GetValue(corpus)!), ct);
        });

        return command;
    }

    private static Command CreateGenerate(CliHost host, GlobalOptions globals)
    {
        var output = new Argument<string>("dir") { Description = "Folder to write the corpus to; must be empty or missing." };
        var seed = new Option<int>("--seed")
        {
            Description = $"Seed of the generator; same seed, same corpus. Default {SmallCorpusGenerator.DefaultSeed}.",
            DefaultValueFactory = _ => SmallCorpusGenerator.DefaultSeed
        };
        var command = new Command("generate", "Write the small built-in evaluation corpus (invented files) and its truth.json.") { output, seed };

        CommandRunner.SetHandler(command, host, globals, (ctx, _) =>
        {
            GeneratedCorpus generated;
            try
            {
                generated = SmallCorpusGenerator.Generate(ctx.ParseResult.GetValue(output)!, ctx.ParseResult.GetValue(seed));
            }
            catch (IOException ex) when (ex.Message.StartsWith("Output folder is not empty", StringComparison.Ordinal))
            {
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.OutputNotEmpty, ex.Message);
            }
            var result = new GenerateResult(generated.Directory, generated.TruthPath, generated.RootPath,
                generated.Corpus.Name, generated.Corpus.GeneratorVersion, generated.Corpus.Seed, generated.Directories, generated.Files);
            return Task.FromResult(CommandOutcome.Success(result, () =>
                $"Corpus '{result.Name}' v{result.GeneratorVersion} seed {result.Seed}: {result.Directories} folders, {result.Files} files" +
                $"{Environment.NewLine}  truth: {result.Truth}{Environment.NewLine}  run:   sfo eval {result.Directory}"));
        });
        return command;
    }

    // ── Corpus ───────────────────────────────────────────────────────────

    private static async Task<CommandOutcome> RunCorpusAsync(
        CommandContext ctx, IServiceProvider services, OllamaOptions ollama, ScriptedAnswerOllamaService? stub, string corpusDir, CancellationToken ct)
    {
        var truthPath = Path.Combine(corpusDir, EvalTruth.FileName);
        var truth = LoadTruth(truthPath);
        var root = Path.Combine(corpusDir, truth.Root);
        if (!Directory.Exists(root))
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, $"Corpus folder not found: {root} (root '{truth.Root}' of {truthPath})");

        var started = DateTime.UtcNow;
        var jobs = services.GetRequiredService<ScanJobService>();
        var (_, rules) = RulesCommand.Load(null);
        var job = await jobs.CreateJobAsync(root, rules, ct);
        ctx.Note($"Eval of '{truth.Subject}': job {job.Id} for {root}");

        var scanClock = System.Diagnostics.Stopwatch.StartNew();
        var scanned = await jobs.RunAsync(job.Id, null,
            new SynchronousProgress<ScanProgress>(pr => ctx.Progress.Report("scan", pr.ProcessedFiles, pr.TotalFiles, pr.ErrorCount, pr.CurrentPath)), ct);
        scanClock.Stop();
        if (scanned.Status != JobStatus.Completed)
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ScanFailed, $"Scan of job {job.Id} ended as {scanned.Status}: {scanned.LastError}");

        var analyzeClock = System.Diagnostics.Stopwatch.StartNew();
        // Without Phase 3 on purpose: no summary is measured, and each would cost a model call. The job's status
        // shows the summaries as pending; `sfo analyze <job>` writes them.
        var run = await services.GetRequiredService<AiClassificationCoordinator>().RunAsync(
            job.Id, AiPhases.PreAssessment | AiPhases.Classification,
            new SynchronousProgress<AiPhaseProgress>(pr => ctx.Progress.Report(AnalyzeCommand.PhaseName(pr.Phase),
                pr.Progress.ProcessedFiles, pr.Progress.TotalFiles, pr.Progress.ErrorCount, pr.Progress.CurrentPath, pr.Progress.StatusText)),
            ct);
        analyzeClock.Stop();
        ct.ThrowIfCancellationRequested();
        if (!run.OllamaAvailable)
            return CommandOutcome.Fail(ExitCodes.OllamaUnavailable, ErrorCodes.OllamaUnavailable,
                $"Ollama is not reachable at {ollama.BaseUrl}. Check with: sfo doctor, or use --stub for a run without a model.");

        // With findings.json next to the truth, the findings are measured too: they need the duplicates and the
        // bundles, which cost no model call. Without a library, not_backed_up is not computed.
        var expectedFindings = EvalFindingTruth.LoadBeside(truthPath);
        IReadOnlyList<SmartFileOrganizer.Application.Findings.Finding>? listed = null;
        if (expectedFindings is not null)
        {
            await services.GetRequiredService<ContentComparisonService>().CompareAsync(job.Id, null,
                new SynchronousProgress<ComparisonProgress>(pr => ctx.Progress.Report("compare", pr.Processed, pr.Total, 0)), ct);
            await services.GetRequiredService<BundleService>().BuildAsync(job.Id, null, ct);
            (_, listed) = await services.GetRequiredService<SmartFileOrganizer.Application.Findings.FindingService>()
                .ListAsync(new SmartFileOrganizer.Application.Findings.FindingQuery { JobId = job.Id }, ct);
        }

        var report = await services.GetRequiredService<CorpusEvaluator>().EvaluateAsync(truth, job.Id, expectedFindings, listed, ct);
        var context = Context(ollama, services.GetRequiredService<DirectoryAnalysisOptions>(), stub, rules);
        var stored = await services.GetRequiredService<EvalRunService>().RecordAsync(
            EvalRunMode.Corpus, truth.Subject, job.Id, context, report, started, ct);

        var result = ToResult(stored, context, report, scanClock.Elapsed.TotalSeconds, analyzeClock.Elapsed.TotalSeconds,
            services.GetRequiredService<ModelUsageMeter>().Snapshot());
        return CommandOutcome.Success(result, () => Render(result));
    }

    // ── Cases ────────────────────────────────────────────────────────────

    private static async Task<CommandOutcome> RunCasesAsync(
        CommandContext ctx, IServiceProvider services, OllamaOptions ollama, ScriptedAnswerOllamaService? stub, CancellationToken ct)
    {
        var client = services.GetRequiredService<IOllamaService>();
        if (!await client.IsAvailableAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            return CommandOutcome.Fail(ExitCodes.OllamaUnavailable, ErrorCodes.OllamaUnavailable,
                $"Ollama is not reachable at {ollama.BaseUrl}. Check with: sfo doctor, or use --stub for a run without a model.");
        }

        var started = DateTime.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var report = await services.GetRequiredService<EvalCaseService>().RunCasesAsync(
            client, stub?.ModelName ?? ollama.Model,
            new SynchronousProgress<(int Done, int Total, string Path)>(pr => ctx.Progress.Report("eval_cases", pr.Done, pr.Total, 0, pr.Path)),
            ct);
        clock.Stop();

        var context = Context(ollama, sampling: null, stub, rules: null);
        var stored = await services.GetRequiredService<EvalRunService>().RecordAsync(
            EvalRunMode.Cases, "cases", null, context, report, started, ct);
        var result = ToResult(stored, context, report, null, clock.Elapsed.TotalSeconds,
            services.GetRequiredService<ModelUsageMeter>().Snapshot());
        return CommandOutcome.Success(result, () => Render(result));
    }

    // ── Compare ──────────────────────────────────────────────────────────

    private static async Task<CommandOutcome> CompareAsync(CommandContext ctx, long[] ids, CancellationToken ct)
    {
        var services = await ctx.GetServicesAsync(ct);
        EvalComparison? comparison;
        try
        {
            comparison = await services.GetRequiredService<EvalRunService>().CompareAsync(
                ids.Length > 0 ? ids[0] : null, ids.Length > 1 ? ids[1] : null, ct);
        }
        catch (KeyNotFoundException ex)
        {
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.RunNotFound, ex.Message);
        }
        if (comparison is null)
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.RunNotFound,
                "No earlier run with the same mode and corpus to compare with; run sfo eval at least twice.");
        return CommandOutcome.Success(comparison, () => Render(comparison));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static EvalTruth LoadTruth(string path)
    {
        if (!File.Exists(path))
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, $"File not found: {path}");
        try
        {
            return EvalTruth.Load(path);
        }
        catch (EvalTruthException ex)
        {
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.TruthInvalid, $"{path}: {ex.Message}");
        }
    }

    /// <summary>Everything that can change a result besides the code: model, prompts, thresholds, scan rules.</summary>
    private static EvalRunContext Context(
        OllamaOptions o, DirectoryAnalysisOptions? sampling, ScriptedAnswerOllamaService? stub, ScanRulesProfile? rules)
    {
        var inv = CultureInfo.InvariantCulture;
        var settings = new Dictionary<string, string>
        {
            ["ollama.max_parallel_requests"] = Math.Clamp(o.MaxParallelRequests, 1, 8).ToString(inv),
            ["ollama.max_samples_per_directory"] = o.MaxSamplesPerDirectory.ToString(inv),
            ["ollama.max_image_upload_bytes"] = o.MaxImageUploadBytes.ToString(inv),
            ["ollama.max_document_upload_bytes"] = o.MaxDocumentUploadBytes.ToString(inv),
            ["ollama.timeout_seconds"] = o.TimeoutSeconds.ToString(inv),
            ["ollama.summary_language"] = o.SummaryLanguage
        };
        if (sampling != null)
        {
            // The thresholds the sample check decided with (issue 50 changed how they are applied).
            settings["sampling.sample_size"] = sampling.EffectiveSampleSize.ToString(inv);
            settings["sampling.max_deviation_share"] = sampling.MaxDeviationShare.ToString(inv);
            settings["sampling.small_directory_max_files"] = sampling.SmallDirectoryMaxFiles.ToString(inv);
        }
        if (rules != null)
            settings["scan.rules"] = $"{rules.Name} {rules.Hash}";

        var version = typeof(OllamaService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return new EvalRunContext(
            stub?.ModelName ?? o.Model,
            stub is null ? null : stub.ModelName["stub:".Length..],
            OllamaService.PromptFingerprint(o),
            version,
            settings);
    }

    private static EvalRunResult ToResult(EvalRun run, EvalRunContext context, EvalReport report, double? scanSeconds, double analyzeSeconds,
        IReadOnlyList<ModelCallUsage> modelCalls) =>
        new(run.Id, run.Mode, run.Subject, run.JobId, run.Model, run.Stub, run.PromptFingerprint, run.AppVersion,
            context.Settings, scanSeconds is { } s ? Math.Round(s, 2) : null, Math.Round(analyzeSeconds, 2),
            report.Scores, report.Failures, report.UntrackedCount, report.Untracked, report.Cases, modelCalls);

    private static string Render(EvalRunResult r)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Eval run {r.RunId} ({r.Mode.ToCode()}: {r.Subject})");
        if (r.JobId is { } job) sb.Append(CultureInfo.InvariantCulture, $", job {job}");
        sb.Append(CultureInfo.InvariantCulture, $", model {r.Model}, prompts {r.PromptFingerprint}").AppendLine();
        AppendScores(sb, r.Scores);

        if (r.Cases is { Count: > 0 } cases)
        {
            sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Cases ({cases.Count}):");
            foreach (var c in cases)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  #{c.CaseId} {c.Outcome.ToCode(),-7} {c.Path}: expected {c.Expected}, corrected from {c.CorrectedFrom ?? "-"}, now {c.Actual ?? "-"}{(c.Sensitive ? " (sensitive)" : "")}{(c.Detail is null ? "" : "  " + c.Detail)}");
        }

        sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Failures ({r.Failures.Count}):");
        foreach (var f in r.Failures)
            sb.AppendLine("  " + FailureLine(f));
        if (r.UntrackedCount > 0)
            sb.AppendLine().AppendLine(CultureInfo.InvariantCulture,
                $"Not in the truth ({r.UntrackedCount}): {string.Join(", ", r.Untracked.Take(10))}{(r.UntrackedCount > 10 ? ", ..." : "")}");
        if (r.ModelCalls.Count > 0)
            AnalyzeCommand.AppendModelCalls(sb.AppendLine().AppendLine("Model calls:"), r.ModelCalls);
        return sb.ToString().TrimEnd();
    }

    private static string Render(EvalComparison c)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Run {c.Before.Id} -> run {c.After.Id} ({c.After.Mode.ToCode()}: {c.After.Subject})");
        if (!c.SameSubject)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Different subjects ({c.Before.Subject} vs {c.After.Subject}): item lists are not comparable.");
        if (c.Changes.Count == 0)
            sb.AppendLine("  Same model, prompts, version and settings.");
        foreach (var ch in c.Changes)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  changed {ch.Name}: {ch.Before ?? "-"} -> {ch.After ?? "-"}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"  {"kind",-18} {"before",14} {"after",14} {"change",8}");
        foreach (var s in c.Scores)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {s.Kind.ToCode(),-18} {$"{s.BeforeHits}/{s.BeforeTotal} {Pct(s.BeforeAccuracy)}",14} {$"{s.AfterHits}/{s.AfterTotal} {Pct(s.AfterAccuracy)}",14} {(s.Change is { } d ? d.ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture) : "-"),8}");
        sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Fixed ({c.Fixed.Count}):");
        foreach (var f in c.Fixed) sb.AppendLine("  " + FailureLine(f));
        sb.AppendLine(CultureInfo.InvariantCulture, $"New failures ({c.NewFailures.Count}):");
        foreach (var f in c.NewFailures) sb.AppendLine("  " + FailureLine(f));
        sb.Append(CultureInfo.InvariantCulture, $"Still failing: {c.StillFailing}");
        return sb.ToString();
    }

    private static void AppendScores(StringBuilder sb, IReadOnlyList<EvalKindScore> scores)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"  {"kind",-18} {"total",6} {"hit",5} {"wrong",6} {"missing",8} {"accuracy",9}");
        foreach (var s in scores)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {s.Kind.ToCode(),-18} {s.Total,6} {s.Hits,5} {s.Wrong,6} {s.Missing,8} {Pct(s.Accuracy),9}");
            if (s.Sensitive is { } x)
                sb.Append(CultureInfo.InvariantCulture, $"   marked {x.ExpectedMarked}: {x.FullyFound} found, {x.Missed} missed; {x.FalseAlarms} false alarm(s)");
            if (s.Findings is { } fc)
                sb.Append(CultureInfo.InvariantCulture, $"   expected {fc.Expected}, listed {fc.Listed}; {fc.Superfluous} superfluous");
            sb.AppendLine();
        }
    }

    private static string FailureLine(EvalFailure f) =>
        $"{f.Kind.ToCode(),-17} {f.Outcome.ToCode(),-7} {f.Path}: expected {f.Expected ?? "-"}, got {f.Actual ?? "-"}" +
        (f.DecidedBy is null ? "" : $"  [{f.DecidedBy}]") +
        (string.IsNullOrWhiteSpace(f.Detail) ? "" : $"  {f.Detail}");

    private static string Pct(double? value) => value is { } v ? v.ToString("0.0%", CultureInfo.InvariantCulture) : "-";
}
