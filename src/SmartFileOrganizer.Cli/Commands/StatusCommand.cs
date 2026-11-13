using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary><c>sfo status &lt;job&gt;</c>: counters per phase and the most recent errors.</summary>
public static class StatusCommand
{
    /// <summary>Counters of the scan phase.</summary>
    public sealed record ScanPhaseView(
        JobStatus Status,
        long FilesTotal,
        long FilesProcessed,
        long DirectoriesTotal,
        long DirectoriesProcessed,
        long Errors);

    /// <summary>
    /// Counters of an AI phase. <c>total = done + errors + pending + skipped</c>. For the file classification, while a
    /// run goes on: files per minute the model judged lately and the estimated seconds left for the pending files
    /// (skipped and inherited files are not counted); null otherwise. <c>BackedUp</c>: of <c>Skipped</c>, those
    /// <c>sfo analyze --against</c> left out because the library holds them.
    /// </summary>
    public sealed record AiPhaseView(long Total, long Done, long Errors, long Pending, long Skipped,
        double? RatePerMinute = null, long? RemainingSeconds = null, long BackedUp = 0);

    public sealed record PhasesView(
        ScanPhaseView Scan,
        AiPhaseView PreAssessment,
        AiPhaseView Classification,
        AiPhaseView Summary);

    public sealed record ErrorView(string Phase, string Path, string Message);

    /// <summary>Read errors of the scan with one cause (<c>access_denied</c>, ...): counts and the first paths.</summary>
    public sealed record ScanErrorGroupView(string Cause, long Directories, long Files, IReadOnlyList<ScanErrorExampleView> Examples);

    public sealed record ScanErrorExampleView(string Path, string? Message);

    /// <summary>Links of one kind: folders not entered, folders entered by a <c>follow_link</c> rule, files that are links.</summary>
    public sealed record LinkGroupView(string Kind, long NotFollowed, long Followed, long Files, IReadOnlyList<LinkExampleView> Examples);

    public sealed record LinkExampleView(string Path, string? Target, bool Followed);

    /// <summary>An analysis of the job waits for the model server. <c>stale</c>: its run has stopped probing (killed).</summary>
    public sealed record ModelServerWaitView(DateTime Since, DateTime CheckedAt, string Cause, bool Stale);

    public sealed record StatusResult(
        JobView Job,
        PhasesView Phases,
        IReadOnlyList<ErrorView> Errors,
        IReadOnlyList<ScanErrorGroupView> ScanErrors,
        IReadOnlyList<LinkGroupView> Links,
        ModelServerWaitView? ModelServerWait = null);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id (see sfo jobs).");
        var maxErrors = new Option<int>("--max-errors")
        {
            Description = "How many recent errors to list.",
            DefaultValueFactory = _ => 20
        };
        maxErrors.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() < 0)
                r.AddError("--max-errors must not be negative.");
        });

        var command = new Command("status", "Show counters per phase and recent errors for a job.") { job, maxErrors };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var id = ctx.ParseResult.GetValue(job);
            var report = await services.GetRequiredService<JobStatusService>()
                .GetAsync(id, ctx.ParseResult.GetValue(maxErrors), ct)
                ?? throw new CliFailureException(ExitCodes.Failure, ErrorCodes.JobNotFound, $"Job {id} not found.");

            var result = Map(report);
            return CommandOutcome.Success(result, () => Render(result));
        });

        return command;
    }

    public static StatusResult Map(JobStatusReport report)
    {
        var s = report.Statistics;
        var j = report.Job;

        var preDone = s.DirectoriesPreAssessed - s.PreAssessmentErrors;
        var sumDone = s.DirectoriesSummarized - s.SummaryErrors;

        var phases = new PhasesView(
            new ScanPhaseView(j.Status, j.TotalFiles, j.ProcessedFiles, j.TotalDirectories, j.ProcessedDirectories, j.ErrorCount),
            new AiPhaseView(s.DirectoriesAiRelevant, preDone, s.PreAssessmentErrors,
                Math.Max(0, s.DirectoriesAiRelevant - s.DirectoriesPreAssessed - s.PreAssessmentBackedUp), s.PreAssessmentBackedUp,
                BackedUp: s.PreAssessmentBackedUp),
            // Inherited files count as done, files waiting for their sample as pending.
            new AiPhaseView(s.FilesAiEligible, s.FilesAiAnalyzed + s.FilesAiInherited, s.FilesAiErrors,
                s.FilesAiPending + s.FilesAiDeferred, s.FilesAiSkipped,
                s.FilesAiRatePerMinute is { } rate ? Math.Round(rate, 1) : null,
                RemainingSeconds(s.FilesAiPending, s.FilesAiRatePerMinute), s.FilesAiBackedUp),
            // Folders that need no summary (judged by rule without a pre-assessment, or without files, or left out as
            // backed_up) are skipped, not pending.
            new AiPhaseView(s.DirectoriesAiRelevant, sumDone, s.SummaryErrors,
                Math.Max(0, s.DirectoriesAiRelevant - s.DirectoriesSummarized - s.SummariesNotNeeded - s.SummaryBackedUp),
                s.SummariesNotNeeded + s.SummaryBackedUp, BackedUp: s.SummaryBackedUp));

        return new StatusResult(
            JobView.From(j),
            phases,
            s.RecentErrors.Select(e => new ErrorView(e.Phase, e.Path, e.Message)).ToList(),
            s.ScanErrors.Select(g => new ScanErrorGroupView(g.Cause, g.Directories, g.Files,
                g.Examples.Select(e => new ScanErrorExampleView(e.Path, e.Message)).ToList())).ToList(),
            s.Links.Select(g => new LinkGroupView(g.Kind, g.NotFollowed, g.Followed, g.Files,
                g.Examples.Select(e => new LinkExampleView(e.Path, e.Target, e.Followed)).ToList())).ToList(),
            report.Wait is { } w ? new ModelServerWaitView(w.Since, w.CheckedAt, w.Cause.ToCode(), report.WaitIsStale) : null);
    }

    private static string Render(StatusResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Job.ToLine());
        if (r.ModelServerWait is { } wait)
        {
            sb.AppendLine(wait.Stale
                ? $"  analysis        stopped while waiting for the model server (last probe {wait.CheckedAt:yyyy-MM-dd HH:mm:ss}Z); run sfo analyze {r.Job.Id} again"
                : $"  analysis        waiting for the model server since {wait.Since:yyyy-MM-dd HH:mm:ss}Z ({wait.Cause}), last probe {wait.CheckedAt:HH:mm:ss}Z");
        }
        var scan = r.Phases.Scan;
        sb.AppendLine($"  scan            {scan.Status.ToString().ToLowerInvariant()}: {scan.FilesProcessed} files, {scan.DirectoriesProcessed} dirs, {scan.Errors} errors");
        Line(sb, "pre_assessment", r.Phases.PreAssessment, "dirs");
        Line(sb, "classification", r.Phases.Classification, "files");
        Line(sb, "summary", r.Phases.Summary, "dirs");
        if (r.ScanErrors.Count > 0)
        {
            sb.AppendLine($"  scan errors     {string.Join(", ", r.ScanErrors.Select(g => $"{g.Directories + g.Files} {g.Cause}"))}");
            foreach (var g in r.ScanErrors)
            {
                var what = string.Join(", ", new[] { Count(g.Directories, "dir"), Count(g.Files, "file") }.Where(p => p != null));
                sb.AppendLine($"    {g.Cause,-14} {what}");
                foreach (var e in g.Examples)
                    sb.AppendLine($"      {e.Path}");
            }
        }
        if (r.Links.Count > 0)
        {
            sb.AppendLine($"  links           {string.Join(", ", r.Links.Select(g => $"{g.NotFollowed + g.Followed + g.Files} {g.Kind}"))} (not counted as content)");
            foreach (var g in r.Links)
            {
                var what = string.Join(", ", new[] { Count(g.NotFollowed, "dir") is { } n ? n + " not entered" : null,
                    Count(g.Followed, "dir") is { } f ? f + " followed" : null, Count(g.Files, "file") }.Where(p => p != null));
                sb.AppendLine($"    {g.Kind,-14} {what}");
                foreach (var e in g.Examples)
                    sb.AppendLine($"      {e.Path} -> {e.Target ?? "?"}");
            }
        }
        foreach (var e in r.Errors)
            sb.AppendLine($"  ! {e.Phase}: {e.Path}: {e.Message}");
        return sb.ToString().TrimEnd();
    }

    private static string? Count(long n, string unit) => n == 0 ? null : $"{n} {unit}{(n == 1 ? "" : "s")}";

    private static void Line(StringBuilder sb, string name, AiPhaseView p, string unit)
    {
        var skipped = p.Skipped > 0
            ? $", {p.Skipped} skipped{(p.BackedUp > 0 ? $" ({p.BackedUp} already in the library)" : string.Empty)}"
            : string.Empty;
        var rate = p.RatePerMinute is { } r && p.RemainingSeconds is { } left
            ? $", {r.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} {unit}/min, about {Duration(left)} left"
            : string.Empty;
        sb.AppendLine($"  {name,-15} {p.Done}/{p.Total} {unit} done, {p.Errors} errors, {p.Pending} pending{skipped}{rate}");
    }

    /// <summary>Seconds left for <paramref name="pending"/> files at <paramref name="ratePerMinute"/>; null without a rate.</summary>
    public static long? RemainingSeconds(long pending, double? ratePerMinute) =>
        ratePerMinute is > 0 && pending > 0 ? (long)Math.Ceiling(pending / ratePerMinute.Value * 60) : null;

    /// <summary>"45 s", "12 min", "2 h 5 min".</summary>
    public static string Duration(long seconds) =>
        seconds < 60 ? $"{seconds} s"
        : seconds < 3600 ? $"{(seconds + 59) / 60} min"
        : $"{seconds / 3600} h {(seconds % 3600 + 59) / 60} min";
}
