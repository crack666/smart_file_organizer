using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Places;
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
    /// <param name="Refresh">With <c>--refresh</c>: what changed against the previous job of the place; null otherwise.</param>
    public sealed record ScanResult(JobView Job, bool Resumed, bool RulesChanged = false, RefreshView? Refresh = null);

    /// <summary>A refresh against the previous job of the same place (https://github.com/crack666/smart_file_organizer/issues/86).</summary>
    /// <param name="Carried">What was taken over for unchanged files instead of being worked out again.</param>
    /// <param name="Changes">The files that are new, changed, missing or moved, by path; at most <see cref="MaxChanges"/>.</param>
    public sealed record RefreshView(long PreviousJobId, long Unchanged, long New, long Changed, long Missing, long Moved,
        CarriedView Carried, IReadOnlyList<ChangeView> Changes, bool ChangesTruncated);

    /// <param name="Checksums">Unchanged files whose stored checksum still holds; no file is read again.</param>
    /// <param name="Results">Unchanged files whose analysis result was taken over; no model is asked for them.</param>
    public sealed record CarriedView(long Checksums, long Results, long Facts, long Corrections);

    public sealed record ChangeView(string Path, string Change, long Size, string? Target, long? PlanId, DateTime? MovedAt);

    public const int MaxChanges = 200;

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

        var structureOnly = new Option<bool>("--structure-only")
        {
            Description = "Record the tree only (names, sizes, times, types); open no file, not even for the " +
                          "sensitive-file header probe. Stored at the job, so --resume keeps it."
        };

        var refresh = new Option<bool>("--refresh")
        {
            Description = "Scan a known place again and compare with its previous job: unchanged files keep checksum, " +
                          "analysis result and facts; new, changed, missing and moved files are listed. Keeps the rules and " +
                          "the structure-only setting of the previous job unless --rules is given."
        };

        var command = new Command("scan", "Create a scan job for a folder and scan it, or resume a job.")
        {
            path,
            resume,
            rulesFile,
            role,
            structureOnly,
            refresh
        };
        command.Validators.Add(result =>
        {
            var hasPath = !string.IsNullOrWhiteSpace(result.GetValue(path));
            var hasResume = result.GetValue(resume) != null;
            if (hasPath == hasResume)
                result.AddError("Give either a path or --resume <job>, not both and not neither.");
            else if (hasResume && result.GetValue(role) != null)
                result.AddError("--role is set when a job is created; it cannot be given with --resume.");
            if (hasResume && result.GetValue(structureOnly))
                result.AddError("--structure-only is fixed when a job is created; a resumed job keeps its setting.");
            if (hasResume && result.GetValue(refresh))
                result.AddError("--refresh creates a new job for a path; it cannot be given with --resume.");
        });

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var jobs = services.GetRequiredService<ScanJobService>();

            var rulesPath = ctx.ParseResult.GetValue(rulesFile);
            var (_, profile) = RulesCommand.Load(rulesPath);

            long jobId;
            long? previousJobId = null;
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
                var shape = ctx.ParseResult.GetValue(structureOnly);
                if (ctx.ParseResult.GetValue(refresh))
                {
                    var places = services.GetRequiredService<IPlaceRepository>();
                    previousJobId = await places.GetPreviousJobIdAsync(root, scanRole, 0, ct);
                    if (previousJobId is null && ctx.ParseResult.GetValue(role) is null)
                    {
                        previousJobId = await places.GetPreviousJobIdAsync(root, ScanRole.Library, 0, ct);
                        if (previousJobId is not null) scanRole = ScanRole.Library;
                    }
                    if (previousJobId is { } prev && await jobs.GetJobAsync(prev, ct) is { } previous)
                    {
                        if (rulesPath == null && previous.Rules is { } kept) profile = kept;
                        shape |= previous.StructureOnly;
                        ctx.Note($"Refreshing job {prev} of {root}");
                    }
                    else
                    {
                        ctx.Note($"No completed scan of {root} is known; scanning it in full");
                    }
                }
                jobId = (await jobs.CreateJobAsync(root, profile, scanRole, shape, ct)).Id;
                ctx.Note($"Created {scanRole.ToValue()} job {jobId} for {root} with rules '{profile.Name}'"
                         + (shape ? ", structure only" : string.Empty));
            }

            var progress = new SynchronousProgress<ScanProgress>(p =>
                ctx.Progress.Report("scan", p.ProcessedFiles, p.TotalFiles, p.ErrorCount, p.CurrentPath));

            try
            {
                // Only an explicit --rules switches a resumed job; otherwise it keeps its stored rules.
                var job = await jobs.RunAsync(jobId, rulesPath != null ? profile : null, progress, ct);
                RefreshView? refreshed = null;
                if (previousJobId is { } before && job.Status == JobStatus.Completed)
                    refreshed = View(await services.GetRequiredService<PlaceService>().RefreshAsync(before, job.Id, ct));
                var result = new ScanResult(JobView.From(job), resumeId != null, rulesChanged, refreshed);
                return job.Status switch
                {
                    JobStatus.Completed => CommandOutcome.Success(result, () => Render(result)),
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

    private static RefreshView View(RefreshReport r) => new(
        r.Counts.PreviousJobId, r.Counts.Unchanged, r.Counts.New, r.Counts.Changed, r.Counts.Missing, r.Counts.Moved,
        new CarriedView(r.Counts.Checksums, r.Counts.Results, r.Counts.Facts, r.Counts.Corrections),
        r.Changes.Take(MaxChanges).Select(c => new ChangeView(c.FullPath, c.Change.ToCode(), c.Size, c.TargetPath, c.PlanId, c.MovedAt)).ToList(),
        r.Changes.Count > MaxChanges);

    private static string Render(ScanResult result)
    {
        if (result.Refresh is not { } r)
            return result.Job.ToLine();
        var lines = new List<string>
        {
            result.Job.ToLine(),
            $"  against job {r.PreviousJobId}: {r.Unchanged} unchanged, {r.New} new, {r.Changed} changed, {r.Missing} missing, {r.Moved} moved",
            $"  taken over: {r.Carried.Checksums} checksums, {r.Carried.Results} results, {r.Carried.Facts} facts, {r.Carried.Corrections} corrections"
        };
        lines.AddRange(r.Changes.Select(c => $"  {c.Change,-8} {c.Path}" + (c.Target is { } t ? $" -> {t}" : string.Empty)));
        if (r.ChangesTruncated)
            lines.Add($"  (first {MaxChanges} changes listed)");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Reports on the calling thread, so no report arrives after the run returned.</summary>
internal sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
