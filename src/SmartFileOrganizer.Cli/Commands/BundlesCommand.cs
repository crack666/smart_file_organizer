using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Bundles;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo bundles build|list|show</c>: group a job into bundles the person decides once
/// (https://github.com/crack666/smart_file_organizer/issues/39). Building writes only bundle tables;
/// no scanned file is opened and no model is called.
/// </summary>
public static class BundlesCommand
{
    public sealed record PeriodView(string? From, string? To, string? TimeSource);

    public sealed record BundleView(
        long Id,
        long? ParentId,
        string Kind,
        string? Origin,
        string? Part,
        string SuggestedName,
        string? StyledName,
        string? RootPath,
        PeriodView Period,
        long Files,
        long TotalBytes,
        int Directories,
        int EmptyDirectories,
        int Children,
        long SensitiveFiles,
        IReadOnlyDictionary<string, long>? Comparison,
        string Reason,
        BundleFacts Facts)
    {
        public static BundleView From(BundleItem item)
        {
            var b = item.Bundle;
            return new BundleView(b.Id, b.ParentId, b.Kind.ToCode(), b.Origin.ToCode(), b.Part.ToCode(),
                b.SuggestedName, b.StyledName, b.RootPath,
                new PeriodView(Local(b.FirstTime), Local(b.LastTime), b.TimeSource.ToCode()),
                b.FileCount, b.TotalBytes, b.DirectoryCount, b.EmptyDirectoryCount, item.Children,
                item.SensitiveFiles, item.Comparison, b.Reason, b.Facts);
        }

        public string ToLine()
        {
            var extra = Origin ?? Part;
            var comparison = Comparison is { } c && c.Count > 0
                ? $" [{string.Join(", ", c.Select(p => $"{p.Key} {p.Value}"))}]"
                : string.Empty;
            return $"{Id,6}  {Kind + (extra is null ? "" : ":" + extra),-24} {Files,7} files {ReadFormat.Size(TotalBytes),10}"
                   + (Children > 0 ? $"  {Children} children" : string.Empty)
                   + (SensitiveFiles > 0 ? $"  [{SensitiveFiles} sensitive]" : string.Empty)
                   + comparison
                   + $"  {SuggestedName}";
        }
    }

    public sealed record OverviewView(
        long JobId,
        DateTime BuiltAt,
        JsonElement Settings,
        BundleSplitStats Stats,
        IReadOnlyDictionary<string, long> TopLevelByKind,
        BundleComparisonInfo? Comparison)
    {
        public static OverviewView From(BundleOverview o) =>
            new(o.JobId, o.BuiltAt, o.Settings, o.Stats, o.TopLevelByKind, o.Comparison);
    }

    public sealed record ShowResult(long JobId, BundleView Bundle, IReadOnlyList<BundleView> Children);

    /// <summary>Wall-clock time as recorded, without zone: capture dates and dates in names carry none.</summary>
    public static string? Local(DateTime? time) =>
        time?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var command = new Command("bundles", "Group a job into bundles to decide at once: dumps with their worth-keeping parts, named folders, events.");
        command.Subcommands.Add(CreateBuild(host, globals));
        command.Subcommands.Add(CreateList(host, globals));
        command.Subcommands.Add(CreateShow(host, globals));
        return command;
    }

    private static Command CreateBuild(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id (see sfo jobs). The scan must be completed.");
        var patterns = new Option<string?>("--patterns")
        {
            Description = "Pattern file for this run instead of Bundles:PatternsFile or the shipped patterns.",
            HelpName = "file"
        };
        var gap = new Option<string?>("--gap-hours")
        {
            Description = "A pause longer than this starts a new event (hours, decimal point). Default: Bundles:EventGapHours (8).",
            HelpName = "hours"
        };
        var minFiles = new Option<int?>("--min-event-files")
        {
            Description = "Smaller groups go to the year's scattered bundle. Default: Bundles:MinEventFiles (3).",
            HelpName = "n"
        };
        var maxDays = new Option<string?>("--max-event-days")
        {
            Description = "Longer events are cut at their largest pauses (days). Default: Bundles:MaxEventDays (14).",
            HelpName = "days"
        };

        var command = new Command("build", "Build the bundles of a job from scan data and stored metadata, replacing earlier ones. No model call.")
        {
            job, patterns, gap, minFiles, maxDays
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var service = services.GetRequiredService<BundleService>();
            var p = ctx.ParseResult;

            var options = service.DefaultOptions();
            if (p.GetValue(patterns) is { } file) options.PatternsFile = Path.GetFullPath(file);
            if (p.GetValue(gap) is { } g) options.EventGapHours = ParseNumber(g, "--gap-hours");
            if (p.GetValue(minFiles) is { } n) options.MinEventFiles = n;
            if (p.GetValue(maxDays) is { } d) options.MaxEventDays = ParseNumber(d, "--max-event-days");

            BundleOverview overview;
            try
            {
                overview = await service.BuildAsync(p.GetValue(job), options, ct);
            }
            catch (BundleException ex)
            {
                throw ex.Failure switch
                {
                    BundleFailure.JobNotFound => new CliFailureException(ExitCodes.Failure, ErrorCodes.JobNotFound, ex.Message),
                    BundleFailure.ScanNotCompleted => new CliFailureException(ExitCodes.Failure, ErrorCodes.ScanNotCompleted, ex.Message),
                    _ => new CliFailureException(ExitCodes.Failure, ErrorCodes.PatternsInvalid, ex.Message)
                };
            }

            var result = OverviewView.From(overview);
            return CommandOutcome.Success(result, () => Render(result));
        });

        return command;
    }

    private static Command CreateList(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id (see sfo jobs).");
        var parent = new Option<long?>("--parent")
        {
            Description = "List the children of this bundle (a dump's parts, a camera part's events). Default: the top level.",
            HelpName = "id"
        };
        var all = new Option<bool>("--all") { Description = "List every bundle, whatever its parent." };
        var kind = new Option<string[]>("--kind")
        {
            Description = $"Only bundles of this kind: {string.Join(", ", VerdictCodes.All<BundleKind>())}. Repeatable.",
            HelpName = "code"
        };
        kind.AcceptOnlyFromAmong([.. VerdictCodes.All<BundleKind>()]);
        var read = new ReadOptions();

        var command = new Command("list", "List bundles of a job, one page at a time, with counts of marked and already backed-up files.")
        {
            job, parent, all, kind
        };
        foreach (var option in read.Paging)
            command.Options.Add(option);

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var query = new BundleListQuery
            {
                JobId = p.GetValue(job),
                Parent = p.GetValue(parent),
                All = p.GetValue(all),
                Kinds = (p.GetValue(kind) ?? []).Select(k => VerdictCodes.ParseNullable<BundleKind>(k)!.Value).ToList(),
                Limit = p.GetValue(read.Limit),
                Offset = p.GetValue(read.Offset)
            };

            ReadPage<BundleItem> page;
            try
            {
                page = await services.GetRequiredService<BundleService>().ListAsync(query, ct);
            }
            catch (JobReadException ex)
            {
                throw ReadFormat.ToFailure(ex);
            }

            var result = PageView<BundleView>.From(query.JobId, page, BundleView.From);
            return CommandOutcome.Success(result, () =>
            {
                var sb = new StringBuilder();
                foreach (var b in result.Items)
                    sb.AppendLine(b.ToLine());
                sb.Append(ReadFormat.PageFooter(page));
                return sb.ToString();
            });
        });

        return command;
    }

    private static Command CreateShow(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id (see sfo jobs).");
        var id = new Argument<long>("bundle") { Description = "Bundle id (see sfo bundles list).", HelpName = "bundle" };
        var command = new Command("show", "One bundle with its facts and its direct children. Files: sfo files <job> --bundle <id>.")
        {
            job, id
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var jobId = ctx.ParseResult.GetValue(job);
            (BundleItem Bundle, IReadOnlyList<BundleItem> Children) found;
            try
            {
                found = await services.GetRequiredService<BundleService>().GetAsync(jobId, ctx.ParseResult.GetValue(id), ct);
            }
            catch (JobReadException ex)
            {
                throw ReadFormat.ToFailure(ex);
            }

            var result = new ShowResult(jobId, BundleView.From(found.Bundle), found.Children.Select(BundleView.From).ToList());
            return CommandOutcome.Success(result, () =>
            {
                var sb = new StringBuilder();
                sb.AppendLine(result.Bundle.ToLine());
                sb.AppendLine($"        {result.Bundle.Reason}");
                if (result.Bundle.Period.From is not null)
                    sb.AppendLine($"        {result.Bundle.Period.From} to {result.Bundle.Period.To} ({result.Bundle.Period.TimeSource})");
                foreach (var child in result.Children)
                    sb.AppendLine("  " + child.ToLine());
                return sb.ToString().TrimEnd();
            });
        });

        return command;
    }

    private static double ParseNumber(string text, string option) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new CliFailureException(ExitCodes.Usage, ErrorCodes.Usage, $"{option} must be a positive number with a decimal point, for example 8 or 1.5.");

    private static string Render(OverviewView o)
    {
        var s = o.Stats;
        var sb = new StringBuilder();
        sb.AppendLine($"Job {o.JobId}: {s.BundleCount} bundles, {s.DumpCount} dumps, {s.UnbundledFiles} loose files in no bundle");
        sb.AppendLine($"  top level   {string.Join(", ", o.TopLevelByKind.Select(k => $"{k.Key} {k.Value}"))}");
        sb.AppendLine($"  all         {Line(s.All)}");
        sb.AppendLine($"  dumps       {Line(s.Dumps)}");
        sb.AppendLine($"  without     {Line(s.WithoutDumps)}");
        sb.Append("List them with: sfo bundles list " + o.JobId.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static string Line(BundleSplitStats.PartStats p) =>
        $"{p.Directories} directories ({p.EmptyDirectories} empty), {p.Files} files, {ReadFormat.Size(p.TotalBytes)}";
}
