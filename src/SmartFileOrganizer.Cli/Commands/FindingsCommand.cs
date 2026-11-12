using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo findings</c>: the short list of hints a job ends in (https://github.com/crack666/smart_file_organizer/issues/26),
/// and the decisions about them. Computed from stored data; no model call, no scanned file opened, nothing changed on
/// disk. accept, reject, wrong and reopen write only the decision (and, for wrong, a test case).
/// </summary>
public static class FindingsCommand
{
    public sealed record ActionView(string Kind, string Reason, bool SingleDecision);

    public sealed record TargetView(
        string Path, string RelativePath, string Certainty, string Reason, int SameContentFiles, string ContentCheck);

    public sealed record PeriodView(string? From, string? To, string? TimeSource);

    public sealed record FindingView(
        int Rank,
        string Key,
        string Kind,
        string Certainty,
        string Section,
        string State,
        string Title,
        string Reason,
        string? Path,
        string? RelativePath,
        long? BundleId,
        string? BundleKind,
        long Files,
        long Bytes,
        IReadOnlyDictionary<string, long> ByFileType,
        PeriodView? Period,
        IReadOnlyDictionary<string, long>? Comparison,
        IReadOnlyList<string> Markers,
        IReadOnlyList<string> SensitiveRules,
        ActionView Action,
        TargetView? Target,
        FindingDuplicates? Duplicates,
        IReadOnlyList<string> Examples,
        IReadOnlyList<string> Notes,
        string? DecidedBy,
        DateTime? DecidedAt,
        string? DecisionNote)
    {
        public static FindingView From(Finding f) => new(
            f.Rank, f.Key, f.Kind.ToCode(), f.Certainty.ToCode(), f.Unsure ? "unsure" : "sure", f.State.ToCode(),
            f.Title, f.Reason, f.Path, f.RelativePath, f.BundleId, f.BundleKind.ToCode(), f.Files, f.Bytes, f.ByFileType,
            f.Period is { } p ? new PeriodView(BundlesCommand.Local(p.From), BundlesCommand.Local(p.To), p.Source.ToCode()) : null,
            f.Comparison, f.Markers, f.SensitiveRules,
            new ActionView(f.Action.Kind.ToCode(), f.Action.Reason, f.Action.SingleDecision),
            f.Target is { } t
                ? new TargetView(t.Path, t.RelativePath, t.Certainty.ToCode(), t.Reason, t.SameContentFiles, t.ContentCheck.ToCode())
                : null,
            f.Duplicates, f.Examples, f.Notes, f.DecidedBy.ToCode(), f.DecidedAt, f.DecisionNote);

        public string ToLine() =>
            $"{Rank,4}  {Key,-24} {Certainty,-9} {Files,7} files {ReadFormat.Size(Bytes),10}  {Action.Kind,-13}"
            + (State == "open" ? string.Empty : $" [{State}]")
            + $"  {Title}";
    }

    public sealed record InputsView(
        long Files,
        long FilesJudged,
        long FilesPending,
        long Directories,
        long DirectoriesJudged,
        DateTime? BundlesBuiltAt,
        long? LibraryJobId,
        DateTime? LibraryComparedAt,
        DateTime? DuplicatesComparedAt)
    {
        public static InputsView From(FindingInputsInfo i) => new(
            i.Files, i.FilesJudged, i.FilesPending, i.Directories, i.DirectoriesJudged, i.BundlesBuiltAt,
            i.LibraryJobId, i.LibraryComparison?.ComparedAt, i.DuplicateComparison?.ComparedAt);
    }

    public sealed record CoverageView(string Coverage, string? Note);

    public sealed record ListResult(
        long JobId,
        long Total,
        int Limit,
        int Offset,
        int? NextOffset,
        IReadOnlyList<FindingView> Items,
        InputsView Inputs,
        IReadOnlyDictionary<string, CoverageView> Coverage,
        IReadOnlyList<string> MissingSteps);

    public sealed record KindTotalsView(long Findings, long Sure, long Unsure, long Files, long Bytes);

    public sealed record BriefView(
        int Rank, string Key, string Kind, string Certainty, string State, string Title, string? RelativePath,
        long Files, long Bytes, string Action);

    public sealed record SummaryResult(
        long JobId,
        string RootPath,
        InputsView Inputs,
        IReadOnlyDictionary<string, CoverageView> Coverage,
        IReadOnlyList<string> MissingSteps,
        long Listed,
        long Sure,
        long Unsure,
        IReadOnlyDictionary<string, KindTotalsView> ByKind,
        IReadOnlyDictionary<string, long> ByCertainty,
        IReadOnlyDictionary<string, long> ByState,
        IReadOnlyDictionary<string, long> BelowThreshold,
        IReadOnlyList<BriefView> Top);

    public sealed record ShowResult(
        long JobId, FindingView Finding, long Total, int Limit, int Offset, int? NextOffset, IReadOnlyList<FileOutputRecord> Files);

    public sealed record DecisionResult(long JobId, string Key, string State, string? DecidedBy, DateTime? DecidedAt, string? Note, long? EvalCaseId, FindingView Finding);

    public sealed record DecisionsResult(long JobId, IReadOnlyList<DecisionResult> Items);

    public const int DefaultTop = 10;
    public const int MaxTop = 50;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id of a source (see sfo jobs).");
        var summary = new Option<bool>("--summary")
        {
            Description = "One page: counts per kind, certainty and state, what is missing, and the most important findings."
        };
        var top = new Option<int>("--top")
        {
            Description = $"With --summary: how many findings to list, 0 to {MaxTop}.",
            DefaultValueFactory = _ => DefaultTop
        };
        top.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 0 or > MaxTop)
                r.AddError($"--top must be between 0 and {MaxTop}.");
        });
        var kind = new Option<string[]>("--kind")
        {
            Description = $"Only findings of this kind: {string.Join(", ", VerdictCodes.All<FindingKind>())}. Repeatable.",
            HelpName = "code"
        };
        kind.AcceptOnlyFromAmong([.. VerdictCodes.All<FindingKind>()]);
        var under = new Option<string?>("--under")
        {
            Description = "Only findings about files or folders at or below this folder (full path, or relative to the job root).",
            HelpName = "path"
        };
        var minCertainty = new Option<string?>("--min-certainty")
        {
            Description = $"Only findings at least this certain: {string.Join(", ", VerdictCodes.All<ResultCertainty>())}.",
            HelpName = "code"
        };
        minCertainty.AcceptOnlyFromAmong([.. VerdictCodes.All<ResultCertainty>()]);
        var state = new Option<string[]>("--state")
        {
            Description = $"Only findings in this state: {string.Join(", ", VerdictCodes.All<FindingState>())}. Repeatable. Default: open and accepted.",
            HelpName = "code"
        };
        state.AcceptOnlyFromAmong([.. VerdictCodes.All<FindingState>()]);
        var read = new ReadOptions();

        var command = new Command("findings",
            "The findings of a source: worth keeping and not backed up, worth keeping among noise, misplaced, sensitive, duplicate. Ranked; unsure ones last. No model call.")
        {
            job, summary, top, kind, under, minCertainty, state
        };
        foreach (var option in read.Paging)
            command.Options.Add(option);

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var service = services.GetRequiredService<FindingService>();
            var p = ctx.ParseResult;
            var jobId = p.GetValue(job);

            if (p.GetValue(summary))
            {
                var s = await Run(() => service.SummarizeAsync(jobId, p.GetValue(top), ct));
                var view = Summary(s);
                return CommandOutcome.Success(view, () => RenderSummary(view));
            }

            var query = new FindingQuery
            {
                JobId = jobId,
                Kinds = (p.GetValue(kind) ?? []).Select(k => VerdictCodes.ParseNullable<FindingKind>(k)!.Value).ToList(),
                Under = p.GetValue(under),
                MinCertainty = VerdictCodes.ParseNullable<ResultCertainty>(p.GetValue(minCertainty)),
                States = (p.GetValue(state) ?? []).Select(k => VerdictCodes.ParseNullable<FindingState>(k)!.Value).ToList()
            };
            var (report, items) = await Run(() => service.ListAsync(query, ct));
            var limit = p.GetValue(read.Limit);
            var offset = p.GetValue(read.Offset);
            var page = new ReadPage<Finding>(items.Count, limit, offset, items.Skip(offset).Take(limit).ToList());
            var result = new ListResult(jobId, page.Total, page.Limit, page.Offset, page.NextOffset,
                page.Items.Select(FindingView.From).ToList(), InputsView.From(report.Inputs), Coverage(report.Coverage), report.MissingSteps);
            return CommandOutcome.Success(result, () =>
            {
                var sb = new StringBuilder();
                var unsureStarted = false;
                foreach (var f in result.Items)
                {
                    if (f.Section == "unsure" && !unsureStarted)
                    {
                        sb.AppendLine("  -- unsure --");
                        unsureStarted = true;
                    }
                    sb.AppendLine(f.ToLine());
                }
                sb.AppendLine(ReadFormat.PageFooter(page));
                foreach (var step in result.MissingSteps)
                    sb.AppendLine("missing: " + step);
                return sb.ToString().TrimEnd();
            });
        });

        command.Subcommands.Add(CreateShow(host, globals));
        command.Subcommands.Add(CreateDecision(host, globals, "accept", FindingState.Accepted,
            "Accept findings: the plan turns them into actions. Changes no file."));
        command.Subcommands.Add(CreateDecision(host, globals, "reject", FindingState.Rejected,
            "Reject findings: they are not listed again. Changes no file."));
        command.Subcommands.Add(CreateDecision(host, globals, "wrong", FindingState.Wrong,
            "Report findings as wrong: not listed again, and kept as test cases (eval_cases). Changes no file."));
        command.Subcommands.Add(CreateDecision(host, globals, "reopen", FindingState.Open,
            "Withdraw the decision about findings: they are open again."));
        return command;
    }

    private static Command CreateShow(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id of a source (see sfo jobs).");
        var key = new Argument<string>("key") { Description = "Finding key (see sfo findings).", HelpName = "key" };
        var read = new ReadOptions();
        var command = new Command("show", "One finding with its files, one page at a time; marked files show path and marker only.")
        {
            job, key
        };
        foreach (var option in read.Paging.Append(read.IncludeSensitive))
            command.Options.Add(option);

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var jobId = p.GetValue(job);
            var output = read.GetOutput(ctx);
            var (finding, files) = await Run(() =>
                services.GetRequiredService<FindingService>().GetWithFilesAsync(jobId, p.GetValue(key)!, output, ct));
            var page = new ReadPage<FileOutputRecord>(files.Count, p.GetValue(read.Limit), p.GetValue(read.Offset),
                files.Skip(p.GetValue(read.Offset)).Take(p.GetValue(read.Limit)).ToList());
            var result = new ShowResult(jobId, FindingView.From(finding), page.Total, page.Limit, page.Offset, page.NextOffset, page.Items);
            return CommandOutcome.Success(result, () =>
            {
                var f = result.Finding;
                var sb = new StringBuilder();
                sb.AppendLine(f.ToLine());
                sb.AppendLine($"      {f.Reason}");
                sb.AppendLine($"      action: {f.Action.Kind}: {f.Action.Reason}");
                if (f.Target is { } t)
                    sb.AppendLine($"      target: {t.RelativePath} ({t.Certainty}, content {t.ContentCheck})");
                if (f.Markers.Count > 0)
                    sb.AppendLine($"      markers: {string.Join(", ", f.Markers)} ({string.Join(", ", f.SensitiveRules)})");
                foreach (var note in f.Notes)
                    sb.AppendLine($"      note: {note}");
                foreach (var file in result.Files)
                    sb.AppendLine($"  {ReadFormat.Size(file.SizeBytes),10}  {file.FullPath}"
                                  + (file.Sensitive.Count > 0 ? $" [{string.Join(",", file.Sensitive)}]" : string.Empty));
                sb.Append(ReadFormat.PageFooter(page));
                return sb.ToString();
            });
        });
        return command;
    }

    private static Command CreateDecision(CliHost host, GlobalOptions globals, string name, FindingState target, string description)
    {
        var job = JobArguments.Create("Job id of a source (see sfo jobs).");
        var keys = new Argument<string[]>("key")
        {
            Description = "One or more finding keys (see sfo findings).",
            HelpName = "key",
            Arity = ArgumentArity.OneOrMore
        };
        var note = new Option<string?>("--note") { Description = "Why: kept with the decision.", HelpName = "text" };
        var by = new Option<string>("--by")
        {
            Description = "Who decides: human (default) or agent (a steering model).",
            HelpName = "human|agent",
            DefaultValueFactory = _ => ResultOrigin.Human.ToCode()
        };
        by.AcceptOnlyFromAmong(ResultOrigin.Human.ToCode(), ResultOrigin.Agent.ToCode());
        var command = new Command(name, description) { job, keys, note, by };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var service = services.GetRequiredService<FindingService>();
            var p = ctx.ParseResult;
            var jobId = p.GetValue(job);
            var origin = VerdictCodes.ParseNullable<ResultOrigin>(p.GetValue(by))!.Value;
            var items = new List<DecisionResult>();
            foreach (var key in (p.GetValue(keys) ?? []).Distinct(StringComparer.Ordinal))
            {
                var (finding, evalCase) = await Run(() => service.DecideAsync(jobId, key, target, origin, p.GetValue(note), ct));
                items.Add(new DecisionResult(jobId, finding.Key, finding.State.ToCode(), finding.DecidedBy.ToCode(), finding.DecidedAt,
                    finding.DecisionNote, evalCase?.Id, FindingView.From(finding)));
            }

            var result = new DecisionsResult(jobId, items);
            return CommandOutcome.Success(result, () => string.Join(Environment.NewLine,
                result.Items.Select(i => $"{i.Key}: {i.State}" + (i.EvalCaseId is { } id ? $" (test case {id})" : string.Empty))));
        });
        return command;
    }

    /// <summary>Runs a service call and maps its failures to the machine contract.</summary>
    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (JobReadException ex)
        {
            throw ReadFormat.ToFailure(ex);
        }
        catch (FindingException ex)
        {
            throw ex.Failure switch
            {
                FindingFailure.WrongRole => new CliFailureException(ExitCodes.Failure, ErrorCodes.WrongRole, ex.Message),
                _ => new CliFailureException(ExitCodes.Failure, ErrorCodes.FindingNotFound, ex.Message)
            };
        }
        catch (ArgumentException ex)
        {
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.SettingsInvalid, ex.Message);
        }
    }

    private static IReadOnlyDictionary<string, CoverageView> Coverage(IReadOnlyDictionary<FindingKind, KindCoverage> coverage) =>
        FindingRules.KindOrder.Where(coverage.ContainsKey)
            .ToDictionary(k => k.ToCode(), k => new CoverageView(coverage[k].Coverage.ToCode(), coverage[k].Note));

    public static SummaryResult Summary(FindingSummary s) => new(
        s.Job.Id,
        s.Job.RootPath,
        InputsView.From(s.Inputs),
        Coverage(s.Coverage),
        s.MissingSteps,
        s.Listed,
        s.Sure,
        s.Unsure,
        s.ByKind.ToDictionary(k => k.Key.ToCode(), k => new KindTotalsView(k.Value.Findings, k.Value.Sure, k.Value.Unsure, k.Value.Files, k.Value.Bytes)),
        s.ByCertainty.ToDictionary(k => k.Key.ToCode(), k => k.Value),
        s.ByState.ToDictionary(k => k.Key.ToCode(), k => k.Value),
        s.BelowThreshold.ToDictionary(k => k.Key.ToCode(), k => k.Value),
        s.Top.Select(b => new BriefView(b.Rank, b.Key, b.Kind.ToCode(), b.Certainty.ToCode(), b.State.ToCode(), b.Title,
            b.RelativePath, b.Files, b.Bytes, b.Action.ToCode())).ToList());

    private static string RenderSummary(SummaryResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Job {r.JobId}: {r.Listed} findings ({r.Sure} sure, {r.Unsure} unsure)");
        foreach (var (kind, t) in r.ByKind)
        {
            var coverage = r.Coverage.TryGetValue(kind, out var c) ? c : null;
            sb.AppendLine($"  {kind,-14} {t.Findings,4} ({t.Sure} sure, {t.Unsure} unsure) {t.Files,7} files {ReadFormat.Size(t.Bytes),10}"
                          + (coverage is null || coverage.Coverage == "complete" ? string.Empty : $"  [{coverage.Coverage}: {coverage.Note}]"));
        }
        foreach (var b in r.Top)
            sb.AppendLine($"  {b.Rank,4}  {b.Key,-24} {b.Certainty,-9} {b.Action,-13} {b.Title}");
        foreach (var step in r.MissingSteps)
            sb.AppendLine("  missing: " + step);
        return sb.ToString().TrimEnd();
    }
}
