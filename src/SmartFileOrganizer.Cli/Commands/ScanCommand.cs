using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary><c>sfo scan &lt;path&gt;</c> or <c>sfo scan --resume &lt;job&gt;</c>.</summary>
public static class ScanCommand
{
    /// <param name="RulesChanged">The job was resumed with <c>--rules</c> that differ from its stored rules;
    /// folders stored as skipped or shallow were re-evaluated.</param>
    public sealed record ScanResult(JobView Job, bool Resumed, bool RulesChanged = false);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var path = new Argument<string?>("path")
        {
            Description = "Folder to scan. Creates a new job.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var resume = new Option<long?>("--resume")
        {
            Description = "Continue an interrupted job instead of creating a new one.",
            HelpName = "job"
        };

        var rulesFile = RulesCommand.RulesOption(
            "Rule file (profile) to scan with; stored at the job. Default: the shipped rules. " +
            "With --resume: switch the job to these rules; stored skip and shallow decisions are re-evaluated.");
        var role = new Option<string?>("--role")
        {
            Description = "source (default): a medium to clean up. library: the organised archive; only read, never analysed.",
            HelpName = "role"
        };
        role.AcceptOnlyFromAmong([.. ScanRoleValues.All]);

        var command = new Command("scan", "Create a scan job for a folder and scan it, or resume a job.")
        {
            path,
            resume,
            rulesFile,
            role
        };
        command.Validators.Add(result =>
        {
            var hasPath = !string.IsNullOrWhiteSpace(result.GetValue(path));
            var hasResume = result.GetValue(resume) != null;
            if (hasPath == hasResume)
                result.AddError("Give either a path or --resume <job>, not both and not neither.");
            else if (hasResume && result.GetValue(role) != null)
                result.AddError("--role is set when a job is created; it cannot be given with --resume.");
        });

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var jobs = services.GetRequiredService<ScanJobService>();

            var rulesPath = ctx.ParseResult.GetValue(rulesFile);
            var (_, profile) = RulesCommand.Load(rulesPath);

            long jobId;
            var rulesChanged = false;
            var resumeId = ctx.ParseResult.GetValue(resume);
            if (resumeId is { } id)
            {
                var existing = await JobArguments.RequireJobAsync(jobs, id, ct);
                jobId = existing.Id;
                rulesChanged = rulesPath != null && profile.Hash != existing.RulesHash;
                if (rulesChanged)
                    ctx.Note($"Job {jobId}: switching rules from '{existing.RulesName ?? "(none recorded)"}' to '{profile.Name}'; skipped and shallow folders are re-evaluated");
            }
            else
            {
                var root = Path.GetFullPath(ctx.ParseResult.GetValue(path)!);
                if (!Directory.Exists(root))
                    throw new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, $"Folder not found: {root}");
                var scanRole = ctx.ParseResult.GetValue(role) is { } r ? ScanRoleValues.Parse(r) : ScanRole.Source;
                jobId = (await jobs.CreateJobAsync(root, profile, scanRole, ct)).Id;
                ctx.Note($"Created {scanRole.ToValue()} job {jobId} for {root} with rules '{profile.Name}'");
            }

            var progress = new SynchronousProgress<ScanProgress>(p =>
                ctx.Progress.Report("scan", p.ProcessedFiles, p.TotalFiles, p.ErrorCount, p.CurrentPath));

            try
            {
                // Only an explicit --rules switches a resumed job; otherwise it keeps its stored rules.
                var job = await jobs.RunAsync(jobId, rulesPath != null ? profile : null, progress, ct);
                var result = new ScanResult(JobView.From(job), resumeId != null, rulesChanged);
                return job.Status switch
                {
                    JobStatus.Completed => CommandOutcome.Success(result, () => result.Job.ToLine()),
                    JobStatus.Failed => CommandOutcome.Fail(ExitCodes.Failure, ErrorCodes.ScanFailed,
                        $"Scan of job {jobId} failed: {job.LastError}", result, () => result.Job.ToLine()),
                    _ => CommandOutcome.Fail(ExitCodes.Failure, ErrorCodes.ScanFailed,
                        $"Scan of job {jobId} ended as {job.Status}.", result, () => result.Job.ToLine())
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                var job = await jobs.GetJobAsync(jobId, CancellationToken.None);
                var result = job == null ? null : new ScanResult(JobView.From(job), resumeId != null, rulesChanged);
                return CommandOutcome.Cancelled(
                    $"Scan interrupted. Resume with: sfo scan --resume {jobId}",
                    result,
                    result == null ? null : () => result.Job.ToLine());
            }
        });

        return command;
    }
}

/// <summary>Reports on the calling thread, so no report arrives after the run returned.</summary>
internal sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
