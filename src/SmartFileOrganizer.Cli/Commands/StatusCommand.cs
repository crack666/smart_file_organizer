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

    /// <summary>Counters of an AI phase. <c>total = done + errors + pending + skipped</c>.</summary>
    public sealed record AiPhaseView(long Total, long Done, long Errors, long Pending, long Skipped);

    public sealed record PhasesView(
        ScanPhaseView Scan,
        AiPhaseView PreAssessment,
        AiPhaseView Classification,
        AiPhaseView Summary);

    public sealed record ErrorView(string Phase, string Path, string Message);

    public sealed record StatusResult(JobView Job, PhasesView Phases, IReadOnlyList<ErrorView> Errors);

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
                Math.Max(0, s.DirectoriesAiRelevant - s.DirectoriesPreAssessed), 0),
            new AiPhaseView(s.FilesAiEligible, s.FilesAiAnalyzed, s.FilesAiErrors, s.FilesAiPending, s.FilesAiSkipped),
            new AiPhaseView(s.DirectoriesAiRelevant, sumDone, s.SummaryErrors,
                Math.Max(0, s.DirectoriesAiRelevant - s.DirectoriesSummarized), 0));

        return new StatusResult(
            JobView.From(j),
            phases,
            s.RecentErrors.Select(e => new ErrorView(e.Phase, e.Path, e.Message)).ToList());
    }

    private static string Render(StatusResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Job.ToLine());
        var scan = r.Phases.Scan;
        sb.AppendLine($"  scan            {scan.Status.ToString().ToLowerInvariant()}: {scan.FilesProcessed} files, {scan.DirectoriesProcessed} dirs, {scan.Errors} errors");
        Line(sb, "pre_assessment", r.Phases.PreAssessment, "dirs");
        Line(sb, "classification", r.Phases.Classification, "files");
        Line(sb, "summary", r.Phases.Summary, "dirs");
        foreach (var e in r.Errors)
            sb.AppendLine($"  ! {e.Phase}: {e.Path}: {e.Message}");
        return sb.ToString().TrimEnd();
    }

    private static void Line(StringBuilder sb, string name, AiPhaseView p, string unit)
    {
        var skipped = p.Skipped > 0 ? $", {p.Skipped} skipped" : string.Empty;
        sb.AppendLine($"  {name,-15} {p.Done}/{p.Total} {unit} done, {p.Errors} errors, {p.Pending} pending{skipped}");
    }
}
