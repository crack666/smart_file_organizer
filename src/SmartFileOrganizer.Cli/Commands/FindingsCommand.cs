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
        string Path, string RelativePath, string Certainty, string Reason, int SameContentFiles, string ContentCheck,
        long? LibraryJobId = null, string? LibraryRoot = null);

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
        string? Group,
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
        string? DecisionNote,
        string? TargetStatus = null)
    {
        public static FindingView From(Finding f) => new(
            f.Rank, f.Key, f.Kind.ToCode(), f.Certainty.ToCode(), f.Unsure ? "unsure" : "sure", f.State.ToCode(),
            f.Title, f.Reason, f.Path, f.RelativePath, f.BundleId, f.BundleKind.ToCode(), f.Files, f.Bytes, f.Group, f.ByFileType,
            f.Period is { } p ? new PeriodView(BundlesCommand.Local(p.From), BundlesCommand.Local(p.To), p.Source.ToCode()) : null,
            f.Comparison, f.Markers, f.SensitiveRules,
            new ActionView(f.Action.Kind.ToCode(), f.Action.Reason, f.Action.SingleDecision),
            f.Target is { } t
                ? new TargetView(t.Path, t.RelativePath, t.Certainty.ToCode(), t.Reason, t.SameContentFiles, t.ContentCheck.ToCode(),
                    t.LibraryJobId, t.LibraryRoot)
                : null,
            f.Duplicates, f.Examples, f.Notes, f.DecidedBy.ToCode(), f.DecidedAt, f.DecisionNote, f.TargetStatus.ToCode());

        public string ToLine() =>
            $"{Rank,4}  {Key,-24} {Certainty,-9} {Files,7} files {ReadFormat.Size(Bytes),10}  {Action.Kind,-13}"
            + (State == "open" ? string.Empty : $" [{State}]")
            + $"  {Title}"
            + (Target is { } t ? $"  -> {t.RelativePath} (library job {t.LibraryJobId})" : TargetStatus is { } s && s != "not_matched" ? $"  -> none ({s})" : string.Empty);
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
        DateTime? DuplicatesComparedAt,
        IReadOnlyList<long> LibraryJobIds)
    {
        public static InputsView From(FindingInputsInfo i) => new(
            i.Files, i.FilesJudged, i.FilesPending, i.Directories, i.DirectoriesJudged, i.BundlesBuiltAt,
            i.LibraryJobId, i.LibraryComparison?.ComparedAt, i.DuplicateComparison?.ComparedAt, i.LibraryJobIds ?? []);
    }

    public sealed record TallyView(long Files, long Bytes)
    {
        public static TallyView From(FileTally t) => new(t.Files, t.Bytes);
    }

    public sealed record LibraryTallyView(long LibraryJobId, string RootPath, long Files, long Bytes);

    public sealed record WorthKeepingView(long Files, long Bytes, TallyView NotBackedUp, TallyView BackedUp, TallyView Unknown);

    public sealed record BackedUpView(long Files, long Bytes, IReadOnlyList<LibraryTallyView> ByLibrary);

    /// <summary>The first answer of the summary: what lies on the source, file by file.</summary>
    public sealed record OverviewView(
        TallyView Files, WorthKeepingView WorthKeeping, BackedUpView BackedUp, TallyView Disposable, TallyView Sensitive, TallyView NotCompared)
    {
        public static OverviewView From(FindingOverview o) => new(
            TallyView.From(o.All),
            new WorthKeepingView(o.WorthKeeping.Files, o.WorthKeeping.Bytes, TallyView.From(o.WorthKeepingNotBackedUp),
                TallyView.From(o.WorthKeepingBackedUp), TallyView.From(o.WorthKeepingUnknown)),
            new BackedUpView(o.BackedUp.Files, o.BackedUp.Bytes,
                o.BackedUpByLibrary.Select(l => new LibraryTallyView(l.LibraryJobId, l.RootPath, l.Files, l.Bytes)).ToList()),
            TallyView.From(o.Disposable), TallyView.From(o.Sensitive), TallyView.From(o.NotCompared));
    }

    public sealed record RootStorageView(long LibraryJobId, string RootPath, long? FreeBytes, long NeededBytes, int Findings, string Space);

    /// <summary>Where the proposals go: library roots only, with free and needed space.</summary>
    /// <param name="UnplacedByReason">Findings without a target per reason code (<c>not_matched</c>, <c>no_fitting_folder</c>,
    /// <c>content_unclear</c>, <c>no_model</c>).</param>
    public sealed record StorageView(string Status, string? Message, IReadOnlyList<RootStorageView> Roots, int UnplacedFindings,
        TallyView Unplaced, IReadOnlyList<string> Warnings, IReadOnlyDictionary<string, int>? UnplacedByReason = null)
    {
        public static StorageView From(FindingStorage s) => new(
            s.Status.ToCode(), s.Message,
            s.Roots.Select(r => new RootStorageView(r.LibraryJobId, r.RootPath, r.FreeBytes, r.NeededBytes, r.Findings, r.Space.ToCode())).ToList(),
            s.UnplacedFindings, TallyView.From(s.Unplaced), s.Warnings, s.UnplacedByReason ?? new Dictionary<string, int>());
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

    /// <param name="Target">The place of the finding in a library, with its reason; null without one.</param>
    /// <param name="TargetStatus">For findings that should move: a <c>FindingTargetStatus</c> code; null for the other kinds.</param>
    public sealed record BriefView(
        int Rank, string Key, string Kind, string Certainty, string State, string Title, string? RelativePath,
        long Files, long Bytes, string Action, TargetView? Target = null, string? TargetStatus = null);

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
        IReadOnlyList<BriefView> Top,
        OverviewView? Overview = null,
        StorageView? Storage = null);

    public sealed record ShowResult(
        long JobId, FindingView Finding, long Total, int Limit, int Offset, int? NextOffset, IReadOnlyList<FileOutputRecord> Files);

    public sealed record DecisionResult(long JobId, string Key, string State, string? DecidedBy, DateTime? DecidedAt, string? Note, long? EvalCaseId, FindingView Finding);

    public sealed record DecisionsResult(long JobId, IReadOnlyList<DecisionResult> Items);

    public const int DefaultTop = 10;
    /// <summary>Largest --top: keeps the summary of a large job under 20 kB of JSON (measured in the tests).</summary>
    public const int MaxTop = 20;

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
        var against = new Option<long[]>("--against")
        {
            Description = "Library job whose stored comparison counts. Repeatable: a file is backed up as soon as one of them holds it. Default: the libraries of the newest sfo compare.",
            HelpName = "job"
        };
        var read = new ReadOptions();

        var command = new Command("findings",
            "The findings of a source: worth keeping and not backed up, worth keeping among noise, misplaced, sensitive, duplicate. Ranked; unsure ones last. No model call.")
        {
            job, summary, top, kind, under, minCertainty, state, against
        };
        foreach (var option in read.Paging)
            command.Options.Add(option);

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var service = services.GetRequiredService<FindingService>();
            var p = ctx.ParseResult;
            var jobId = p.GetValue(job);
            var libraries = (p.GetValue(against) ?? []).Distinct().ToList();

            if (p.GetValue(summary))
            {
                var s = await Run(() => service.SummarizeAsync(jobId, p.GetValue(top), libraries, ct));
                var view = Summary(s);
                return CommandOutcome.Success(view, () => RenderSummary(view));
            }

            var query = new FindingQuery
            {
                JobId = jobId,
                Kinds = (p.GetValue(kind) ?? []).Select(k => VerdictCodes.ParseNullable<FindingKind>(k)!.Value).ToList(),
                Under = p.GetValue(under),
                MinCertainty = VerdictCodes.ParseNullable<ResultCertainty>(p.GetValue(minCertainty)),
                States = (p.GetValue(state) ?? []).Select(k => VerdictCodes.ParseNullable<FindingState>(k)!.Value).ToList(),
                Libraries = libraries
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
        command.Subcommands.Add(CreateTargets(host, globals));
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
                {
                    sb.AppendLine($"      target: {t.RelativePath} in library job {t.LibraryJobId} ({t.Certainty}, content {t.ContentCheck})");
                    sb.AppendLine($"      why there: {t.Reason}");
                }
                else if (f.TargetStatus is { } none)
                {
                    sb.AppendLine($"      target: none ({none})");
                }
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

    public sealed record TargetItemView(
        string Key, string Kind, string Title, string Status, bool Reused, long? LibraryJobId, string? Path, string? RelativePath,
        string? Certainty, string ContentCheck, string ContentStatus, string Reason);

    public sealed record TargetsResult(
        long JobId,
        string Status,
        string? Message,
        IReadOnlyList<long> LibraryJobIds,
        int Considered,
        int Matched,
        int Reused,
        IReadOnlyDictionary<string, int> ByStatus,
        IReadOnlyList<TargetItemView> Items,
        IReadOnlyList<ModelCallUsage> ModelCalls);

    /// <summary>
    /// <c>sfo findings targets</c> (https://github.com/crack666/smart_file_organizer/issues/83): the target match of
    /// <c>sfo library match</c> for every finding that should move, stored per finding. Resumable; a finding whose stored
    /// match still fits is not matched again.
    /// </summary>
    private static Command CreateTargets(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id of a source (see sfo jobs).");
        var against = new Option<long[]>("--against")
        {
            Description = "Library job to look in. Repeatable. Default: the libraries of the newest sfo compare.",
            HelpName = "job"
        };
        var key = new Option<string[]>("--key")
        {
            Description = "Only this finding (see sfo findings). Repeatable. Default: every open or accepted finding of the kinds not_backed_up, in_noise and misplaced.",
            HelpName = "key"
        };
        var noModel = LibraryCommand.NoModelOption();
        var model = LibraryCommand.ModelOption();
        var refresh = new Option<bool>("--refresh")
        {
            Description = "Match every finding again and ask the model again, even where a stored match still fits."
        };
        var command = new Command("targets",
            "Find a place in the libraries for every finding that should move (the match of sfo library match: time, names, kinds of files, content), and store it per finding. Resumable.")
        {
            job, against, key, noModel, model, refresh
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var query = new FindingTargetQuery
            {
                JobId = p.GetValue(job),
                Libraries = (p.GetValue(against) ?? []).Distinct().ToList(),
                Keys = (p.GetValue(key) ?? []).Distinct(StringComparer.Ordinal).ToList(),
                UseModel = !p.GetValue(noModel),
                Refresh = p.GetValue(refresh),
                Model = p.GetValue(model)
            };
            var progress = new SynchronousProgress<FindingTargetProgress>(pr =>
                ctx.Progress.Report("targets", pr.Done, pr.Total, 0, pr.Key, pr.Reused ? "stored: " + pr.Status.ToCode() : pr.Status.ToCode()));

            FindingTargetRun run;
            try
            {
                run = await Run(() => services.GetRequiredService<FindingTargetService>().MatchAsync(query, progress, ct));
            }
            catch (Application.Library.LibraryServiceException ex)
            {
                throw LibraryCommand.ToFailure(ex);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return CommandOutcome.Cancelled(
                    "Target match interrupted. The findings matched so far are stored; run the command again to go on.");
            }

            var result = new TargetsResult(run.Job.Id, run.Status.ToCode(), run.Message, run.LibraryJobIds, run.Considered, run.Matched,
                run.Reused, run.ByStatus,
                run.Items.Select(i => new TargetItemView(i.Key, i.Kind.ToCode(), i.Title, i.Status.ToCode(), i.Reused, i.LibraryJobId,
                    i.Path, i.RelativePath, i.Certainty.ToCode(), i.ContentCheck.ToCode(), i.ContentStatus.ToCode(), i.Reason)).ToList(),
                services.GetRequiredService<ModelUsageMeter>().Snapshot());
            return CommandOutcome.Success(result, () => RenderTargets(result));
        });
        return command;
    }

    private static string RenderTargets(TargetsResult r)
    {
        var sb = new StringBuilder();
        if (r.Message is { } message)
            return message;
        sb.AppendLine($"Job {r.JobId}: {r.Considered} findings to place, {r.Matched} matched now, {r.Reused} kept from an earlier run "
                      + $"(library jobs {string.Join(", ", r.LibraryJobIds)})");
        foreach (var (status, n) in r.ByStatus)
            sb.AppendLine($"  {status,-18} {n,4}");
        foreach (var i in r.Items)
        {
            sb.AppendLine($"  {i.Key,-24} {i.Status,-18} "
                          + (i.RelativePath is { } rel ? $"{rel} (library job {i.LibraryJobId}, {i.Certainty}, content {i.ContentCheck})" : "-"));
            sb.AppendLine($"      {i.Reason}");
        }
        if (r.ModelCalls.Count > 0)
            AnalyzeCommand.AppendModelCalls(sb.AppendLine("Model calls:"), r.ModelCalls);
        return sb.ToString().TrimEnd();
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
                FindingFailure.ComparisonNotFound => new CliFailureException(ExitCodes.Failure, ErrorCodes.ComparisonNotFound, ex.Message),
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
            b.RelativePath, b.Files, b.Bytes, b.Action.ToCode(),
            b.Target is { } t
                ? new TargetView(t.Path, t.RelativePath, t.Certainty.ToCode(), t.Reason, t.SameContentFiles, t.ContentCheck.ToCode(), t.LibraryJobId, t.LibraryRoot)
                : null,
            b.TargetStatus.ToCode())).ToList(),
        s.Overview is { } o ? OverviewView.From(o) : null,
        s.Storage is { } st ? StorageView.From(st) : null);

    private static string RenderSummary(SummaryResult r)
    {
        var sb = new StringBuilder();
        if (r.Overview is { } o)
        {
            sb.AppendLine($"Job {r.JobId}: {o.Files.Files} files ({ReadFormat.Size(o.Files.Bytes)})");
            sb.AppendLine($"  worth keeping, not backed up {o.WorthKeeping.NotBackedUp.Files,7} files {ReadFormat.Size(o.WorthKeeping.NotBackedUp.Bytes),10}");
            sb.AppendLine($"  worth keeping, unknown       {o.WorthKeeping.Unknown.Files,7} files {ReadFormat.Size(o.WorthKeeping.Unknown.Bytes),10}");
            sb.AppendLine($"  backed up                    {o.BackedUp.Files,7} files {ReadFormat.Size(o.BackedUp.Bytes),10}");
            foreach (var l in o.BackedUp.ByLibrary)
                sb.AppendLine($"    in library job {l.LibraryJobId,-10} {l.Files,7} files {ReadFormat.Size(l.Bytes),10}  {l.RootPath}");
            sb.AppendLine($"  disposable                   {o.Disposable.Files,7} files {ReadFormat.Size(o.Disposable.Bytes),10}");
            sb.AppendLine($"  sensitive                    {o.Sensitive.Files,7} files {ReadFormat.Size(o.Sensitive.Bytes),10}");
        }
        if (r.Storage is { } st)
        {
            if (st.Message is { } message)
                sb.AppendLine("  " + message);
            foreach (var root in st.Roots)
                sb.AppendLine($"  target {root.RootPath}: {(root.FreeBytes is { } free ? ReadFormat.Size(free) : "unknown")} free, {ReadFormat.Size(root.NeededBytes)} needed ({root.Space})");
            if (st.UnplacedFindings > 0)
                sb.AppendLine($"  without target yet: {st.UnplacedFindings} findings, {ReadFormat.Size(st.Unplaced.Bytes)}"
                              + (st.UnplacedByReason is { Count: > 0 } why
                                  ? $" ({string.Join(", ", why.Select(w => $"{w.Key} {w.Value}"))})"
                                  : string.Empty));
            foreach (var warning in st.Warnings)
                sb.AppendLine("  warning: " + warning);
        }
        sb.AppendLine($"Job {r.JobId}: {r.Listed} findings ({r.Sure} sure, {r.Unsure} unsure)");
        foreach (var (kind, t) in r.ByKind)
        {
            var coverage = r.Coverage.TryGetValue(kind, out var c) ? c : null;
            sb.AppendLine($"  {kind,-14} {t.Findings,4} ({t.Sure} sure, {t.Unsure} unsure) {t.Files,7} files {ReadFormat.Size(t.Bytes),10}"
                          + (coverage is null || coverage.Coverage == "complete" ? string.Empty : $"  [{coverage.Coverage}: {coverage.Note}]"));
        }
        foreach (var b in r.Top)
        {
            sb.AppendLine($"  {b.Rank,4}  {b.Key,-24} {b.Certainty,-9} {b.Action,-13} {b.Title}");
            if (b.Target is { } t)
                sb.AppendLine($"        -> {t.RelativePath} (library job {t.LibraryJobId}, {t.Certainty}, content {t.ContentCheck}): {t.Reason}");
        }
        foreach (var step in r.MissingSteps)
            sb.AppendLine("  missing: " + step);
        return sb.ToString().TrimEnd();
    }
}
