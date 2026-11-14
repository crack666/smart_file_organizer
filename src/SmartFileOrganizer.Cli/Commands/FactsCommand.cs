using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Media;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo facts refresh &lt;job&gt;</c>: reads the video facts of a stored job again from container, name and companion
/// files (https://github.com/crack666/smart_file_organizer/issues/103). Never calls the model server and needs no GPU;
/// the analysis of the job stays as it is.
/// </summary>
public static class FactsCommand
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var command = new Command("facts", "Facts read from the files themselves.");
        command.Subcommands.Add(CreateRefresh(host, globals));
        return command;
    }

    private static Command CreateRefresh(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id (see sfo jobs): a source or a library scan.");
        var command = new Command("refresh",
            "Read video kind, device, title, year, season and episode again from container, name and companion files. No model call.") { job };
        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var id = ctx.ParseResult.GetValue(job);
            if (await services.GetRequiredService<IScanJobRepository>().GetByIdAsync(id, ct) is null)
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.JobNotFound, $"Job {id} not found.");

            var result = await services.GetRequiredService<VideoFactRefreshService>().RefreshAsync(id, ct);
            return CommandOutcome.Success(result, () =>
                $"job {result.JobId}: {result.Read} of {result.Videos} video(s) read, {result.Missing} missing" +
                (result.Kinds.Count == 0 ? string.Empty : " - " + string.Join(", ", result.Kinds.Select(k => $"{k.Key} {k.Value}"))) +
                (result.ModelKindsKept > 0 ? $"; {result.ModelKindsKept} kind(s) kept from the model" : string.Empty) +
                (result.Disputed > 0 ? $"; {result.Disputed} disputed" : string.Empty));
        });
        return command;
    }
}
