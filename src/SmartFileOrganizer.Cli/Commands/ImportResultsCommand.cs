using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Exchange;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo import-results &lt;job&gt; &lt;files.jsonl&gt;</c>: judgements of another program in the line format of the
/// exchange format (docs/data-format.md), stored with origin <c>external</c>. Changes nothing on disk.
/// </summary>
public static class ImportResultsCommand
{
    /// <summary>At most this many problems are named in the error message.</summary>
    private const int NamedProblems = 10;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id (see sfo jobs) whose files get the results.");
        var file = new Argument<string>("file")
        {
            Description = "A files.jsonl: one file per line, checked against schemas/exchange/v1/file.schema.json. Lines are matched by relative_path."
        };

        var command = new Command("import-results",
            "Store the judgements of another program (origin external), in the line format of files.jsonl. " +
            "Every line is checked first; one invalid line and nothing is stored. Corrections of a person stay and win.")
        {
            job, file
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var path = Path.GetFullPath(ctx.ParseResult.GetValue(file)!);
            if (!File.Exists(path))
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, $"File '{path}' does not exist.");

            var services = await ctx.GetServicesAsync(ct);
            var jobId = ctx.ParseResult.GetValue(job);

            ExchangeImportService.ImportSummary summary;
            try
            {
                using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                summary = await services.GetRequiredService<ExchangeImportService>().ImportAsync(jobId, reader, ct);
            }
            catch (JobReadException ex)
            {
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.JobNotFound, ex.Message);
            }
            catch (ExchangeImportService.InvalidImportException ex)
            {
                var named = string.Join("; ", ex.Problems.Take(NamedProblems));
                var more = ex.Problems.Count > NamedProblems ? $" (and {ex.Problems.Count - NamedProblems} more)" : string.Empty;
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ImportInvalid,
                    $"Nothing imported: {ex.Problems.Count} problem(s) in {path}: {named}{more}");
            }
            catch (IOException ex)
            {
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ImportInvalid, $"File cannot be read: {ex.Message}");
            }

            return CommandOutcome.Success(summary, () =>
                $"job {summary.JobId}: {summary.Imported} of {summary.Lines} line(s) imported, {summary.WithoutResult} without result, " +
                $"{summary.Unmatched} not in the job" +
                (summary.UnmatchedPaths.Count == 0 ? string.Empty : $" ({string.Join(", ", summary.UnmatchedPaths)})"));
        });

        return command;
    }
}
