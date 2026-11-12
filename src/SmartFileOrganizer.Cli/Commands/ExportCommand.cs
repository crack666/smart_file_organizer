using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary><c>sfo export &lt;job&gt; --csv &lt;file&gt;</c>: every file of a job as CSV, through the output guard.</summary>
public static class ExportCommand
{
    public sealed record ExportResult(long JobId, string Path, int Rows, bool IncludeSensitive);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id (see sfo jobs).");
        var csv = new Option<string>("--csv")
        {
            Description = "Target file (UTF-8 with BOM). An existing file is replaced.",
            HelpName = "file",
            Required = true
        };
        var read = new ReadOptions();

        var command = new Command("export", "Write every file of a job to a CSV file. Writes nothing else.")
        {
            job, csv, read.IncludeSensitive
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var jobId = ctx.ParseResult.GetValue(job);

            // Read the job through the repository: ScanJobService.GetJobAsync may store a normalised status.
            if (await services.GetRequiredService<IScanJobRepository>().GetByIdAsync(jobId, ct) is null)
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.JobNotFound, $"Job {jobId} not found.");

            var target = Path.GetFullPath(ctx.ParseResult.GetValue(csv)!);
            var folder = Path.GetDirectoryName(target);
            if (folder is null || !Directory.Exists(folder))
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, $"Folder of '{target}' does not exist.");

            var output = read.GetOutput(ctx);
            int rows;
            try
            {
                rows = await services.GetRequiredService<ScanExportService>().ExportJobToFileAsync(jobId, target, output, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ExportFailed, ex.Message);
            }

            var result = new ExportResult(jobId, target, rows, output.IncludeSensitive);
            return CommandOutcome.Success(result, () => $"{rows} rows written to {target}");
        });

        return command;
    }
}
