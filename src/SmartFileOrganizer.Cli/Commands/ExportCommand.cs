using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Exchange;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo export &lt;job&gt; --csv &lt;file&gt;</c>: every file of a job as CSV; <c>sfo export &lt;job&gt; --format jsonl
/// --out &lt;folder&gt;</c>: the job in the open exchange format (docs/data-format.md). Both through the output guard.
/// </summary>
public static class ExportCommand
{
    public const string Csv = "csv";
    public const string Jsonl = "jsonl";

    public sealed record ExportResult(long JobId, string Path, int Rows, bool IncludeSensitive);

    /// <summary>Result of <c>--format jsonl</c>: the folder, the files written and the lines in each.</summary>
    public sealed record ExchangeExportResult(long JobId, string Format, string FormatVersion, string Path,
        IReadOnlyList<string> Files, int Directories, int FileLines, bool IncludeSensitive);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id (see sfo jobs).");
        var csv = new Option<string>("--csv")
        {
            Description = "Target file of the CSV export (UTF-8 with BOM). An existing file is replaced.",
            HelpName = "file"
        };
        var format = new Option<string>("--format")
        {
            Description = "csv (default, needs --csv) or jsonl: job.json, directories.jsonl and files.jsonl in --out (docs/data-format.md).",
            HelpName = "format"
        };
        format.AcceptOnlyFromAmong(Csv, Jsonl);
        var outFolder = new Option<string>("--out")
        {
            Description = "Folder of the jsonl export; created when its parent exists. Files of the same names are replaced.",
            HelpName = "folder"
        };
        var read = new ReadOptions();

        var command = new Command("export", "Write a job to a CSV file or, with --format jsonl, to the open exchange format. Writes nothing else.")
        {
            job, csv, format, outFolder, read.IncludeSensitive
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var chosen = ctx.ParseResult.GetValue(format) ?? (ctx.ParseResult.GetValue(outFolder) is not null ? Jsonl : Csv);
            var csvPath = ctx.ParseResult.GetValue(csv);
            var outPath = ctx.ParseResult.GetValue(outFolder);
            if (chosen == Csv && (csvPath is null || outPath is not null))
                throw new CliFailureException(ExitCodes.Usage, ErrorCodes.Usage, "The CSV export needs --csv <file> and no --out.");
            if (chosen == Jsonl && (outPath is null || csvPath is not null))
                throw new CliFailureException(ExitCodes.Usage, ErrorCodes.Usage, "--format jsonl needs --out <folder> and no --csv.");

            var services = await ctx.GetServicesAsync(ct);
            var jobId = ctx.ParseResult.GetValue(job);

            // Read the job through the repository: ScanJobService.GetJobAsync may store a normalised status.
            if (await services.GetRequiredService<IScanJobRepository>().GetByIdAsync(jobId, ct) is null)
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.JobNotFound, $"Job {jobId} not found.");

            var output = read.GetOutput(ctx);
            return chosen == Jsonl
                ? await ExportJsonlAsync(services, jobId, outPath!, output, ct)
                : await ExportCsvAsync(services, jobId, csvPath!, output, ct);
        });

        return command;
    }

    private static async Task<CommandOutcome> ExportCsvAsync(IServiceProvider services, long jobId, string path,
        Application.Sensitivity.SensitiveOutputOptions output, CancellationToken ct)
    {
        var target = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(target);
        if (folder is null || !Directory.Exists(folder))
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, $"Folder of '{target}' does not exist.");

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
    }

    private static async Task<CommandOutcome> ExportJsonlAsync(IServiceProvider services, long jobId, string path,
        Application.Sensitivity.SensitiveOutputOptions output, CancellationToken ct)
    {
        var target = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(target);
        if (!Directory.Exists(target) && (parent is null || !Directory.Exists(parent)))
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, $"Neither '{target}' nor its parent folder exists.");

        ExchangeExportService.ExportSummary summary;
        try
        {
            Directory.CreateDirectory(target);
            summary = await services.GetRequiredService<ExchangeExportService>().ExportAsync(jobId, target, output, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ExportFailed, ex.Message);
        }
        catch (JobReadException ex)
        {
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.JobNotFound, ex.Message);
        }

        var result = new ExchangeExportResult(jobId, ExchangeFormat.Name, ExchangeFormat.Version, target, summary.Written,
            summary.Directories, summary.Files, output.IncludeSensitive);
        return CommandOutcome.Success(result,
            () => $"{summary.Directories} directories and {summary.Files} files written to {target} ({ExchangeFormat.Name} {ExchangeFormat.Version})");
    }
}
