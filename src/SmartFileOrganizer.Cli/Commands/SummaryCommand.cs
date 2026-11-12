using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo summary &lt;job&gt;</c>: a one-page overview meant as the entry point for a model. Its size does not
/// grow with the number of files: only counts, distributions over fixed value sets and the largest subtrees.
/// </summary>
public static class SummaryCommand
{
    public sealed record FileCounts(long Total, long Pending, long Errors, long Sensitive, long TotalBytes);

    public sealed record SummaryResult(
        JobView Job,
        long Directories,
        FileCounts Files,
        IReadOnlyDictionary<string, long> ByCategory,
        IReadOnlyDictionary<string, long> ByFileType,
        IReadOnlyDictionary<string, long> ByStatus,
        IReadOnlyDictionary<string, long> BySensitiveMarker,
        IReadOnlyDictionary<string, long> DirectoriesByVerdict,
        IReadOnlyDictionary<string, long> DirectoriesByRetention,
        IReadOnlyList<DirectoryView> LargestDirectories);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id (see sfo jobs).");
        var top = new Option<int>("--top")
        {
            Description = $"How many of the largest directories to list, 0 to {JobReadService.MaxTop}.",
            DefaultValueFactory = _ => JobReadService.DefaultTop
        };
        top.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 0 or > JobReadService.MaxTop)
                r.AddError($"--top must be between 0 and {JobReadService.MaxTop}.");
        });

        var command = new Command("summary", "One-page overview of a job: counts, distributions and the largest directories.")
        {
            job, top
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);

            JobSummary summary;
            try
            {
                summary = await services.GetRequiredService<JobReadService>()
                    .SummarizeAsync(ctx.ParseResult.GetValue(job), ctx.ParseResult.GetValue(top), ct);
            }
            catch (JobReadException ex)
            {
                throw ReadFormat.ToFailure(ex);
            }

            var result = new SummaryResult(
                JobView.From(summary.Job),
                summary.Directories,
                new FileCounts(summary.Files, summary.PendingFiles, summary.ErrorFiles, summary.SensitiveFiles, summary.TotalBytes),
                summary.ByCategory,
                summary.ByFileType,
                summary.ByStatus,
                summary.BySensitiveMarker,
                summary.DirectoriesByVerdict,
                summary.DirectoriesByRetention,
                summary.LargestDirectories.Select(DirectoryView.From).ToList());
            return CommandOutcome.Success(result, () => Render(result));
        });

        return command;
    }

    private static string Render(SummaryResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Job.ToLine());
        sb.AppendLine($"  {r.Directories} directories, {r.Files.Total} files, {ReadFormat.Size(r.Files.TotalBytes)}; " +
                      $"{r.Files.Pending} pending, {r.Files.Errors} errors, {r.Files.Sensitive} sensitive");
        Counts(sb, "categories", r.ByCategory);
        Counts(sb, "types", r.ByFileType);
        Counts(sb, "status", r.ByStatus);
        Counts(sb, "verdicts", r.DirectoriesByVerdict);
        Counts(sb, "retention", r.DirectoriesByRetention);
        Counts(sb, "sensitive", r.BySensitiveMarker);
        if (r.LargestDirectories.Count > 0)
        {
            sb.AppendLine("  largest directories:");
            foreach (var d in r.LargestDirectories)
                sb.AppendLine("  " + d.ToLine());
        }
        return sb.ToString().TrimEnd();
    }

    private static void Counts(StringBuilder sb, string label, IReadOnlyDictionary<string, long> counts)
    {
        if (counts.Count > 0)
            sb.AppendLine($"  {label,-11} {string.Join(", ", counts.Select(c => $"{ReadFormat.Snake(c.Key)} {c.Value}"))}");
    }
}
