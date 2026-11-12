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

    public sealed record AnalyzeResult(long JobId, string Model, int Parallel, IReadOnlyList<PhaseRunView> Phases, int ErrorCount);

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
                    jobId, phases, progress, ct, ctx.ParseResult.GetValue(retryErrors));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return CommandOutcome.Cancelled($"Analysis interrupted. Results so far are saved; resume with: sfo analyze {jobId}");
            }

            // IsAvailableAsync swallows cancellation and reports "not available".
            ct.ThrowIfCancellationRequested();

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
                run.ErrorCount);
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
        return sb.ToString();
    }
}
