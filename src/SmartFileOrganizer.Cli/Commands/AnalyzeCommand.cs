using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary><c>sfo analyze &lt;job&gt;</c>: runs the AI phases for a scanned job and waits for them.</summary>
public static class AnalyzeCommand
{
    public sealed record PhaseRunView(AiPhases Phase, long Total, long Processed, int Errors, string Message, int Retried, int SecondChecks);

    /// <summary>Set when the run ended because the model server stayed away (exit code 4).</summary>
    public sealed record ModelServerLostView(string Phase, DateTime Since, string Cause, double WaitedSeconds);

    /// <param name="ModelCalls">Calls and tokens of this run per call kind, as the model server counted them.</param>
    public sealed record AnalyzeResult(long JobId, string Model, int Parallel, IReadOnlyList<PhaseRunView> Phases, int ErrorCount,
        ModelServerLostView? ModelServerLost = null, IReadOnlyList<ModelCallUsage>? ModelCalls = null);

    /// <summary>Accepted values of <c>--phase</c>.</summary>
    public static readonly IReadOnlyDictionary<string, AiPhases> PhaseNames = new Dictionary<string, AiPhases>
    {
        ["pre_assessment"] = AiPhases.PreAssessment,
        ["classification"] = AiPhases.Classification,
        ["summary"] = AiPhases.Summary,
        ["all"] = AiPhases.All
    };

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id of a completed scan (see sfo jobs).");
        var phase = new Option<string[]>("--phase")
        {
            Description = "Phase(s) to run: pre_assessment, classification, summary or all (default). Repeatable.",
            HelpName = "phase",
            AllowMultipleArgumentsPerToken = true
        };
        phase.AcceptOnlyFromAmong([.. PhaseNames.Keys]);
        var parallel = new Option<int?>("--parallel")
        {
            Description = "Concurrent Ollama requests for this run (1-8). Default: Ollama.MaxParallelRequests from settings.",
            HelpName = "n"
        };
        parallel.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int?>() is { } n && (n < 1 || n > 8))
                r.AddError("--parallel must be between 1 and 8.");
        });
        var model = new Option<string?>("--model")
        {
            Description = "Ollama model for this run. Default: Ollama.Model from settings. Not saved.",
            HelpName = "name"
        };

        var retryErrors = new Option<bool>("--retry-errors")
        {
            Description = "Try files that failed in an earlier run again (classification phase). Files with a result are not called again."
        };

        var command = new Command("analyze", "Run the AI phases (directory pre-assessment, file classification, directory summaries) for a job.")
        {
            job, phase, parallel, model, retryErrors
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var jobId = ctx.ParseResult.GetValue(job);
            var scanJob = await JobArguments.RequireJobAsync(services.GetRequiredService<ScanJobService>(), jobId, ct);
            if (scanJob.Role == ScanRole.Library)
            {
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.WrongRole,
                    $"Job {jobId} is a library (the organised archive); it is only read and compared, never analysed by a model.");
            }
            if (scanJob.Status != JobStatus.Completed)
            {
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ScanNotCompleted,
                    $"Job {jobId} is {scanJob.Status.ToString().ToLowerInvariant()}; finish the scan first (sfo scan --resume {jobId}).");
            }

            // Per-run overrides; OllamaSettingsService is not called, so settings.json stays untouched.
            var ollama = services.GetRequiredService<OllamaOptions>();
            if (ctx.ParseResult.GetValue(model) is { Length: > 0 } m)
                ollama.Model = m;
            if (ctx.ParseResult.GetValue(parallel) is { } n)
                ollama.MaxParallelRequests = n;

            var phases = ParsePhases(ctx.ParseResult.GetValue(phase));
            var progress = new SynchronousProgress<AiPhaseProgress>(p => ctx.Progress.Report(
                PhaseName(p.Phase), p.Progress.ProcessedFiles, p.Progress.TotalFiles, p.Progress.ErrorCount,
                p.Progress.CurrentPath, p.Progress.StatusText));

            AiPipelineRunResult run;
            try
            {
                run = await services.GetRequiredService<AiClassificationCoordinator>().RunAsync(
                    jobId, phases, progress, ct, ctx.ParseResult.GetValue(retryErrors),
                    (p, wait) => ctx.Progress.ReportWait(PhaseName(p), wait.State.ToCode(), wait.Since, wait.Cause.ToCode(),
                        wait.Waited, WaitText(wait)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return CommandOutcome.Cancelled($"Analysis interrupted. Results so far are saved; resume with: sfo analyze {jobId}");
            }

            if (!run.OllamaAvailable)
            {
                return CommandOutcome.Fail(ExitCodes.OllamaUnavailable, ErrorCodes.OllamaUnavailable,
                    $"Ollama is not reachable at {ollama.BaseUrl}. Check with: sfo doctor");
            }

            var result = new AnalyzeResult(
                jobId,
                ollama.Model,
                Math.Clamp(ollama.MaxParallelRequests, 1, 8),
                run.Phases.Select(p => new PhaseRunView(p.Phase, p.Result.TotalFiles, p.Result.ProcessedFiles, p.Result.ErrorCount, p.Result.StatusText,
                    p.Result.RetriedFiles, p.Result.SecondChecks)).ToList(),
                run.ErrorCount,
                run.Lost is { } l ? new ModelServerLostView(PhaseName(l.Phase), l.Since, l.Cause.ToCode(), Math.Round(l.Waited.TotalSeconds, 1)) : null,
                services.GetRequiredService<ModelUsageMeter>().Snapshot());

            if (run.Lost is { } lost)
            {
                return CommandOutcome.Fail(ExitCodes.ModelServerLost, ErrorCodes.ModelServerLost,
                    $"The model server at {ollama.BaseUrl} did not answer for {lost.Waited.TotalMinutes:F1} min " +
                    $"(since {lost.Since:yyyy-MM-dd HH:mm:ss}Z, {lost.Cause.ToCode()}), during {PhaseName(lost.Phase)}. " +
                    $"Files and folders not judged stay open; continue with: sfo analyze {jobId}",
                    result, () => Render(result));
            }
            return CommandOutcome.Success(result, () => Render(result));
        });

        return command;
    }

    public static AiPhases ParsePhases(string[]? names)
    {
        if (names == null || names.Length == 0)
            return AiPhases.All;
        return names.Aggregate(AiPhases.None, (acc, name) => acc | PhaseNames[name]);
    }

    public static string PhaseName(AiPhases phase) => phase switch
    {
        AiPhases.PreAssessment => "pre_assessment",
        AiPhases.Classification => "classification",
        AiPhases.Summary => "summary",
        _ => "analyze"
    };

    private static string Render(AnalyzeResult r)
    {
        var sb = new StringBuilder($"Job {r.JobId}, model {r.Model}, parallel {r.Parallel}");
        foreach (var p in r.Phases)
            sb.Append($"{Environment.NewLine}  {PhaseName(p.Phase),-15} {p.Processed}/{p.Total}, {p.Errors} errors  {p.Message}");
        if (r.ModelServerLost is { } lost)
            sb.Append($"{Environment.NewLine}  {lost.Phase,-15} stopped: the model server did not answer for {lost.WaitedSeconds:F0}s");
        if (r.ModelCalls is { Count: > 0 } calls)
            AppendModelCalls(sb.AppendLine().AppendLine("Model calls:"), calls);
        return sb.ToString().TrimEnd();
    }

    /// <summary>One line per call kind: calls, prompt tokens and the share from the server cache, answer tokens, mean time.</summary>
    public static StringBuilder AppendModelCalls(StringBuilder sb, IReadOnlyList<ModelCallUsage> calls)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var c in calls)
        {
            var cached = c.PromptTokens > 0 ? (double)c.CachedPromptTokens / c.PromptTokens : 0;
            sb.AppendLine(inv, $"  {c.Kind,-15} {c.Calls} calls, prompt {c.PromptTokens} tokens ({cached:P0} cached), answer {c.AnswerTokens} tokens, {c.MeanSeconds:F2}s per call{(c.CutOff > 0 ? $", {c.CutOff} cut off" : "")}");
        }
        return sb;
    }

    /// <summary>The line people read on stderr when the run starts or stops waiting.</summary>
    public static string WaitText(ModelServerWaitEvent wait) => wait.State switch
    {
        ModelServerWaitState.Waiting =>
            $"waiting for the model server since {wait.Since:yyyy-MM-dd HH:mm:ss}Z ({wait.Cause.ToCode()}); held calls continue when it answers",
        ModelServerWaitState.Resumed =>
            $"model server answers again after {wait.Waited.TotalSeconds:F0}s; continuing",
        _ => $"model server did not answer for {wait.Waited.TotalMinutes:F1} min; stopping, open items stay open"
    };
}
