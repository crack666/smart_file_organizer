using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo summary &lt;job&gt;</c>: a one-page overview meant as the entry point for a model. Its size does not
/// grow with the number of files: only counts, distributions over fixed value sets and the largest subtrees.
/// </summary>
public static class SummaryCommand
{
    public sealed record FileCounts(long Total, long Pending, long Errors, long Sensitive, long TotalBytes);

    /// <summary>
    /// A large directory, kept short: the path relative to the job root (usable with
    /// <c>dirs --under</c> and <c>show</c>) and codes only, no model text.
    /// </summary>
    public sealed record LargeDirectoryView(
        string RelativePath, long SizeBytes, long Files, string? Verdict, string? Retention, long SensitiveFiles)
    {
        public static LargeDirectoryView From(DirectoryItem d) => new(
            d.RelativePath, d.SizeBytes, d.Files, d.VerdictFilterCode, d.Retention, d.SensitiveFiles);
    }

    public sealed record SummaryResult(
        JobView Job,
        long Directories,
        long SensitiveDirectories,
        FileCounts Files,
        IReadOnlyDictionary<string, long> ByCategory,
        IReadOnlyDictionary<string, long> ByFileType,
        IReadOnlyDictionary<string, long> ByStatus,
        IReadOnlyDictionary<string, long> BySensitiveMarker,
        IReadOnlyDictionary<string, long> DirectoriesByVerdict,
        IReadOnlyDictionary<string, long> DirectoriesByRetention,
        IReadOnlyList<LargeDirectoryView> LargestDirectories,
        BundleStatsView? Bundles);

    /// <summary>
    /// The job with and without dumps, from the last <c>sfo bundles build</c>; null when bundles were never built
    /// (or a rescan dropped them). Dumps (phone, computer, stick copies, unpacked software) dominate every
    /// count, so the numbers without them describe what the person actually collected.
    /// </summary>
    public sealed record BundleStatsView(
        DateTime BuiltAt,
        int DumpCount,
        int BundleCount,
        long UnbundledFiles,
        BundleSplitStats.PartStats Dumps,
        BundleSplitStats.PartStats WithoutDumps);

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
                summary.SensitiveDirectories,
                new FileCounts(summary.Files, summary.PendingFiles, summary.ErrorFiles, summary.SensitiveFiles, summary.TotalBytes),
                summary.ByCategory,
                summary.ByFileType,
                summary.ByStatus,
                summary.BySensitiveMarker,
                summary.DirectoriesByVerdict,
                summary.DirectoriesByRetention,
                summary.LargestDirectories.Select(LargeDirectoryView.From).ToList(),
                await services.GetRequiredService<BundleService>().GetOverviewAsync(summary.Job.Id, ct) is { } o
                    ? new BundleStatsView(o.BuiltAt, o.Stats.DumpCount, o.Stats.BundleCount, o.Stats.UnbundledFiles, o.Stats.Dumps, o.Stats.WithoutDumps)
                    : null);
            var language = ReadFormat.Language(services);
            return CommandOutcome.Success(result, () => Render(result, language));
        });

        return command;
    }

    private static string Render(SummaryResult r, DisplayLanguage language)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Job.ToLine());
        sb.AppendLine($"  {r.Directories} directories, {r.Files.Total} files, {ReadFormat.Size(r.Files.TotalBytes)}; " +
                      $"{r.Files.Pending} pending, {r.Files.Errors} errors, {r.Files.Sensitive} sensitive" +
                      (r.SensitiveDirectories > 0 ? $", {r.SensitiveDirectories} sensitive folders" : string.Empty));
        Counts(sb, "categories", r.ByCategory);
        Counts(sb, "types", r.ByFileType);
        Counts(sb, "status", r.ByStatus);
        Counts(sb, "verdicts", r.DirectoriesByVerdict, c => CodeLabels.LabelOfCode<DirectoryVerdictKind>(c, language)!);
        Counts(sb, "retention", r.DirectoriesByRetention, c => CodeLabels.LabelOfCode<RetentionValue>(c, language)!);
        Counts(sb, "sensitive", r.BySensitiveMarker, c => CodeLabels.LabelOfCode<SensitiveMarker>(c, language)!);
        if (r.Bundles is { } b)
        {
            sb.AppendLine($"  dumps       {b.DumpCount}: {BundlesCommand.Line(b.Dumps)}");
            sb.AppendLine($"  w/o dumps   {BundlesCommand.Line(b.WithoutDumps)}");
        }
        if (r.LargestDirectories.Count > 0)
        {
            sb.AppendLine("  largest directories:");
            foreach (var d in r.LargestDirectories)
                sb.AppendLine($"  {ReadFormat.Size(d.SizeBytes),10}  {d.Files,7} files  {CodeLabels.LabelOfCode<DirectoryVerdictKind>(d.Verdict, language),-11} {CodeLabels.LabelOfCode<RetentionValue>(d.Retention, language) ?? "-",-11}"
                              + (d.SensitiveFiles > 0 ? $" [{d.SensitiveFiles} sensitive]" : string.Empty)
                              + $"  {d.RelativePath}");
        }
        return sb.ToString().TrimEnd();
    }

    private static void Counts(StringBuilder sb, string label, IReadOnlyDictionary<string, long> counts, Func<string, string>? word = null)
    {
        if (counts.Count > 0)
            sb.AppendLine($"  {label,-11} {string.Join(", ", counts.Select(c => $"{(word is null ? ReadFormat.Snake(c.Key) : word(c.Key))} {c.Value}"))}");
    }
}
