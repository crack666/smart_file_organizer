using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary><c>sfo jobs</c>: all scan jobs, newest first as stored.</summary>
public static class JobsCommand
{
    public sealed record JobsResult(IReadOnlyList<JobView> Jobs);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var command = new Command("jobs", "List all scan jobs.");

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var jobs = await services.GetRequiredService<ScanJobService>().GetAllJobsAsync(ct);
            var result = new JobsResult(jobs.Select(JobView.From).ToList());

            return CommandOutcome.Success(result, () => result.Jobs.Count == 0
                ? "No jobs."
                : string.Join(Environment.NewLine, result.Jobs.Select(j => j.ToLine())));
        });

        return command;
    }
}
