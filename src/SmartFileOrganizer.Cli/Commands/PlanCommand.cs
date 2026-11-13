using System.CommandLine;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo plan</c> (https://github.com/crack666/smart_file_organizer/issues/11): a plan from the accepted findings of a
/// source, to read, change and confirm action by action. Nothing is moved, copied or removed.
/// </summary>
public static class PlanCommand
{
    public sealed record ActionView(
        long Id,
        string FindingKey,
        string FindingKind,
        string Unit,
        string SourceKind,
        string SourcePath,
        string RelativePath,
        long Files,
        long Bytes,
        string Kind,
        string Recommended,
        string? Target,
        long? LibraryJobId,
        string Reason,
        string? Note,
        string Origin,
        string State,
        bool Sensitive,
        bool SingleDecision,
        bool IntoManagedArea,
        IReadOnlyList<string> Problems,
        string? DecidedBy,
        DateTime? DecidedAt)
    {
        public static ActionView From(PlanActionStatus s)
        {
            var a = s.Action;
            return new ActionView(a.Id, a.FindingKey, a.FindingKind.ToCode(), a.Unit, a.SourceKind.ToCode(), a.SourcePath,
                a.RelativePath, a.Files, a.Bytes, a.Kind.ToCode(), a.Recommended.ToCode(), a.TargetPath, a.LibraryJobId,
                a.Reason, a.Note, a.Origin.ToCode(), a.State.ToCode(), a.Sensitive, a.SingleDecision, s.IntoManagedArea,
                s.Problems.Select(p => p.ToCode()).ToList(), a.DecidedBy.ToCode(), a.DecidedAt);
        }

        public string ToLine() =>
            $"  {Id,5} {State,-9} {Kind,-13} {RelativePath}"
            + (Target is { } t ? $" -> {t}" : string.Empty)
            + (Sensitive ? " [sensitive, one by one]" : string.Empty)
            + (IntoManagedArea ? " [into managed area]" : string.Empty)
            + (Problems.Count > 0 ? $" [problems: {string.Join(", ", Problems)}]" : string.Empty);
    }

    public sealed record Tally(long Actions, long Files, long Bytes);

    public sealed record LibraryView(long JobId, string RootPath, long Actions, long NeededBytes, long? FreeBytes, string Space);

    public sealed record UnitView(string FindingKey, string FindingKind, string Unit, long Actions, long Files, long Bytes,
        IReadOnlyDictionary<string, long> ByKind, IReadOnlyDictionary<string, long> ByState);

    public sealed record SummaryResult(
        long PlanId,
        long JobId,
        string RootPath,
        DateTime CreatedAt,
        long Actions,
        IReadOnlyDictionary<string, Tally> ByKind,
        IReadOnlyDictionary<string, long> ByState,
        long ChangedFromRecommendation,
        long WithProblems,
        long Sensitive,
        long IntoManagedArea,
        IReadOnlyList<LibraryView> Libraries,
        long Units,
        IReadOnlyList<UnitView> TopUnits,
        IReadOnlyList<string> Notes);

    public sealed record ListResult(long PlanId, long JobId, long Total, int Limit, int Offset, int? NextOffset, IReadOnlyList<ActionView> Items);

    public sealed record ChangeResult(long PlanId, IReadOnlyList<ActionView> Items);

    public sealed record RefusedView(long ActionId, IReadOnlyList<string> Problems, string Message);

    public sealed record RefusedResult(long PlanId, IReadOnlyList<RefusedView> Refused);

    public sealed record SkippedView(long ActionId, IReadOnlyList<string> Reasons);

    public sealed record DecisionResult(long PlanId, string State, IReadOnlyList<long> Changed, IReadOnlyList<SkippedView> Skipped,
        IReadOnlyDictionary<string, long> ByState);

    public const int DefaultTopUnits = 10;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var command = new Command("plan",
            "Plans: actions from accepted findings, to read, change and confirm. Nothing is moved, copied or removed.");
        command.Subcommands.Add(CreateCreate(host, globals));
        command.Subcommands.Add(CreateShow(host, globals));
        command.Subcommands.Add(CreateSummary(host, globals));
        command.Subcommands.Add(CreateSet(host, globals));
        command.Subcommands.Add(CreateImport(host, globals));
        command.Subcommands.Add(CreateDecision(host, globals, "approve", approve: true,
            "Confirm actions. Only actions without problems; sensitive ones only by --action."));
        command.Subcommands.Add(CreateDecision(host, globals, "reject", approve: false, "Reject actions: they are not executed."));
        return command;
    }

    private static Argument<long> PlanArgument() => new("plan") { Description = "Plan id (see sfo plan create).", HelpName = "plan" };

    private static Option<string> ByOption(string what) => new Option<string>("--by")
    {
        Description = $"Who {what}: human (default) or agent (a steering model).",
        HelpName = "human|agent",
        DefaultValueFactory = _ => PlanActionOrigin.Human.ToCode()
    }.WithOnly(PlanActionOrigin.Human.ToCode(), PlanActionOrigin.Agent.ToCode());

    private static Option<string> WithOnly(this Option<string> option, params string[] values)
    {
        option.AcceptOnlyFromAmong(values);
        return option;
    }

    // ------------------------------------------------------------------ create

    private static Command CreateCreate(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id of a source (see sfo jobs).");
        var against = new Option<long[]>("--against")
        {
            Description = "Library job whose stored comparison counts, as in sfo findings. Repeatable. Default: the libraries of the newest sfo compare.",
            HelpName = "job"
        };
        var library = new Option<long?>("--library")
        {
            Description = "Library job whose root the targets of sfo decide --targets go under. Default: the only library, when there is one.",
            HelpName = "job"
        };
        var command = new Command("create",
            "A new plan from the accepted findings of a source: one action per whole folder or per worth-keeping file. Changes no file.")
        {
            job, against, library
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var options = new PlanCreateOptions((p.GetValue(against) ?? []).Distinct().ToList(), p.GetValue(library));
            var state = await Run(() => services.GetRequiredService<PlanService>().CreateAsync(p.GetValue(job), options, ct));
            var result = Summary(state, services.GetService<IFreeSpaceProbe>(), DefaultTopUnits);
            return CommandOutcome.Success(result, () => RenderSummary(result));
        });
        return command;
    }

    // ------------------------------------------------------------------ read

    private static Command CreateShow(CliHost host, GlobalOptions globals)
    {
        var plan = PlanArgument();
        var kind = new Option<string[]>("--kind")
        {
            Description = $"Only actions of this kind: {string.Join(", ", VerdictCodes.All<PlanActionKind>())}. Repeatable.",
            HelpName = "code"
        };
        kind.AcceptOnlyFromAmong([.. VerdictCodes.All<PlanActionKind>()]);
        var state = new Option<string[]>("--state")
        {
            Description = $"Only actions in this state: {string.Join(", ", VerdictCodes.All<PlanActionState>())}. Repeatable.",
            HelpName = "code"
        };
        state.AcceptOnlyFromAmong([.. VerdictCodes.All<PlanActionState>()]);
        var finding = new Option<string?>("--finding") { Description = "Only actions of this finding (key).", HelpName = "key" };
        var under = new Option<string?>("--under")
        {
            Description = "Only actions whose source lies at or below this folder (full path, or relative to the job root).",
            HelpName = "path"
        };
        var problems = new Option<bool>("--problems") { Description = "Only actions with problems." };
        var read = new ReadOptions();
        var command = new Command("show", "The actions of a plan, one page at a time, with the problems the checks find now.")
        {
            plan, kind, state, finding, under, problems
        };
        foreach (var option in read.Paging)
            command.Options.Add(option);

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var s = await Run(() => services.GetRequiredService<PlanService>().GetAsync(p.GetValue(plan), ct));
            var kinds = (p.GetValue(kind) ?? []).Select(k => VerdictCodes.ParseNullable<PlanActionKind>(k)!.Value).ToHashSet();
            var states = (p.GetValue(state) ?? []).Select(k => VerdictCodes.ParseNullable<PlanActionState>(k)!.Value).ToHashSet();
            var key = p.GetValue(finding);
            var dir = p.GetValue(under) is { Length: > 0 } u ? (Path.IsPathRooted(u) ? u : Path.Combine(s.Job.RootPath, u)) : null;
            var onlyProblems = p.GetValue(problems);
            var items = s.Actions
                .Where(a => kinds.Count == 0 || kinds.Contains(a.Action.Kind))
                .Where(a => states.Count == 0 || states.Contains(a.Action.State))
                .Where(a => key is null || a.Action.FindingKey == key)
                .Where(a => dir is null || PlanChecks.IsAtOrBelow(a.Action.SourcePath, dir))
                .Where(a => !onlyProblems || a.Problems.Count > 0)
                .ToList();
            var limit = p.GetValue(read.Limit);
            var offset = p.GetValue(read.Offset);
            var page = new ReadPage<PlanActionStatus>(items.Count, limit, offset, items.Skip(offset).Take(limit).ToList());
            var result = new ListResult(s.Plan.Id, s.Job.Id, page.Total, page.Limit, page.Offset, page.NextOffset,
                page.Items.Select(ActionView.From).ToList());
            return CommandOutcome.Success(result, () =>
            {
                var sb = new StringBuilder();
                foreach (var a in result.Items)
                {
                    sb.AppendLine(a.ToLine());
                    sb.AppendLine($"        {a.Reason}");
                    if (a.Note is { } note)
                        sb.AppendLine($"        note: {note}");
                }
                sb.Append(ReadFormat.PageFooter(page));
                return sb.ToString();
            });
        });
        return command;
    }

    private static Command CreateSummary(CliHost host, GlobalOptions globals)
    {
        var plan = PlanArgument();
        var top = new Option<int>("--top")
        {
            Description = "How many units (findings) to list, 0 to 50.",
            DefaultValueFactory = _ => DefaultTopUnits
        };
        top.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 0 or > 50)
                r.AddError("--top must be between 0 and 50.");
        });
        var command = new Command("summary", "One page about a plan: counts per kind and state, room in the libraries, the largest units.")
        {
            plan, top
        };
        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var s = await Run(() => services.GetRequiredService<PlanService>().GetAsync(p.GetValue(plan), ct));
            var result = Summary(s, services.GetService<IFreeSpaceProbe>(), p.GetValue(top));
            return CommandOutcome.Success(result, () => RenderSummary(result));
        });
        return command;
    }

    // ------------------------------------------------------------------ change

    private static Command CreateSet(CliHost host, GlobalOptions globals)
    {
        var plan = PlanArgument();
        var action = new Argument<long>("action") { Description = "Action id (see sfo plan show).", HelpName = "action" };
        var kind = new Option<string?>("--kind")
        {
            Description = $"New kind: {string.Join(", ", VerdictCodes.All<PlanActionKind>())}.",
            HelpName = "code"
        };
        kind.AcceptOnlyFromAmong([.. VerdictCodes.All<PlanActionKind>()]);
        var target = new Option<string?>("--target")
        {
            Description = "New target: full path of the file or folder at the target, below a library root of the plan (relative: below the only library).",
            HelpName = "path"
        };
        var clear = new Option<bool>("--clear-target") { Description = "Remove the target." };
        var reason = new Option<string?>("--reason") { Description = "Why: kept with the action.", HelpName = "text" };
        var by = ByOption("changes it");
        var command = new Command("set",
            "Change kind, target or reason of one action. Checked at once; a refused change changes nothing. The action must be confirmed again.")
        {
            plan, action, kind, target, clear, reason, by
        };
        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var id = p.GetValue(action);
            if (p.GetValue(clear) && p.GetValue(target) is { Length: > 0 })
                throw new CliFailureException(ExitCodes.Usage, ErrorCodes.Usage, "--target and --clear-target exclude each other.");
            var change = new PlanChange(id, VerdictCodes.ParseNullable<PlanActionKind>(p.GetValue(kind)), p.GetValue(target),
                p.GetValue(clear), p.GetValue(reason));
            var origin = VerdictCodes.ParseNullable<PlanActionOrigin>(p.GetValue(by))!.Value;
            return await ChangeAsync(services, p.GetValue(plan), [change], origin, ct);
        });
        return command;
    }

    private static Command CreateImport(CliHost host, GlobalOptions globals)
    {
        var plan = PlanArgument();
        var file = new Argument<string>("file")
        {
            Description = "JSON file: { \"actions\": [ { \"id\": 3, \"kind\": \"move\", \"target\": \"...\", \"reason\": \"...\" } ] }; a missing field stays, \"target\": null removes the target.",
            HelpName = "file"
        };
        var by = ByOption("changes them");
        var command = new Command("import",
            "Change many actions at once, all or none: a steering model writes its proposals here. Every change is checked as with sfo plan set.")
        {
            plan, file, by
        };
        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var p = ctx.ParseResult;
            var path = Path.GetFullPath(p.GetValue(file)!);
            if (!File.Exists(path))
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, $"Import file not found: {path}");
            var changes = ParseImport(await File.ReadAllTextAsync(path, ct));
            var services = await ctx.GetServicesAsync(ct);
            var origin = VerdictCodes.ParseNullable<PlanActionOrigin>(p.GetValue(by))!.Value;
            return await ChangeAsync(services, p.GetValue(plan), changes, origin, ct);
        });
        return command;
    }

    /// <summary>Reads an import file; every problem of the file itself is a usage error of the file.</summary>
    public static IReadOnlyList<PlanChange> ParseImport(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ImportInvalid, $"Import file is not JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("actions", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ImportInvalid, "Import file needs an object with a list \"actions\".");

            var changes = new List<PlanChange>();
            var problems = new List<string>();
            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                index++;
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idProp) || !idProp.TryGetInt64(out var id))
                {
                    problems.Add($"entry {index}: \"id\" (number) is missing");
                    continue;
                }

                PlanActionKind? kind = null;
                if (item.TryGetProperty("kind", out var k) && k.ValueKind != JsonValueKind.Null)
                {
                    if (k.ValueKind != JsonValueKind.String || !VerdictCodes.TryParse<PlanActionKind>(k.GetString(), out var parsed))
                    {
                        problems.Add($"entry {index}: \"kind\" must be one of {string.Join(", ", VerdictCodes.All<PlanActionKind>())}");
                        continue;
                    }
                    kind = parsed;
                }

                string? target = null;
                var clear = false;
                if (item.TryGetProperty("target", out var t))
                {
                    if (t.ValueKind == JsonValueKind.Null) clear = true;
                    else if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } s) target = s;
                    else
                    {
                        problems.Add($"entry {index}: \"target\" must be a path or null");
                        continue;
                    }
                }

                var reason = item.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                changes.Add(new PlanChange(id, kind, target, clear, reason));
            }

            if (problems.Count > 0)
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ImportInvalid, "Import file: " + string.Join("; ", problems));
            return changes;
        }
    }

    private static async Task<CommandOutcome> ChangeAsync(
        IServiceProvider services, long planId, IReadOnlyList<PlanChange> changes, PlanActionOrigin origin, CancellationToken ct)
    {
        try
        {
            var state = await services.GetRequiredService<PlanService>().ChangeAsync(planId, changes, origin, ct);
            var ids = changes.Select(c => c.ActionId).ToHashSet();
            var result = new ChangeResult(state.Plan.Id, state.Actions.Where(a => ids.Contains(a.Action.Id)).Select(ActionView.From).ToList());
            return CommandOutcome.Success(result, () => string.Join(Environment.NewLine, result.Items.Select(a => a.ToLine())));
        }
        catch (PlanException ex) when (ex.Failure == PlanFailure.ChangeRefused)
        {
            var result = new RefusedResult(planId, ex.Refused.Select(r => new RefusedView(r.ActionId, r.Problems.Select(p => p.ToCode()).ToList(), r.Message)).ToList());
            return CommandOutcome.Fail(ExitCodes.Failure, ErrorCodes.PlanChangeRefused, ex.Message, result,
                () => string.Join(Environment.NewLine, result.Refused.Select(r => "refused: " + r.Message)));
        }
        catch (Exception ex) when (ex is PlanException or JobReadException or ArgumentException)
        {
            throw ToFailure(ex);
        }
    }

    // ------------------------------------------------------------------ approve, reject

    private static Command CreateDecision(CliHost host, GlobalOptions globals, string name, bool approve, string description)
    {
        var plan = PlanArgument();
        var action = new Option<long[]>("--action") { Description = "Action id. Repeatable.", HelpName = "action" };
        var dir = new Option<string?>("--dir")
        {
            Description = "Every action whose source lies at or below this folder (full path, or relative to the job root).",
            HelpName = "path"
        };
        var all = new Option<bool>("--all") { Description = "Every action of the plan." };
        var by = ByOption(approve ? "confirms" : "rejects");
        var command = new Command(name, description) { plan, action, dir, all, by };
        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var p = ctx.ParseResult;
            var ids = (p.GetValue(action) ?? []).Distinct().ToList();
            var selection = new PlanSelection(ids, p.GetValue(dir), p.GetValue(all));
            if (ids.Count == 0 && selection.Dir is null && !selection.All)
                throw new CliFailureException(ExitCodes.Usage, ErrorCodes.Usage, "Name the actions: --action, --dir or --all.");
            var services = await ctx.GetServicesAsync(ct);
            var origin = VerdictCodes.ParseNullable<PlanActionOrigin>(p.GetValue(by))!.Value;
            var r = await Run(() => services.GetRequiredService<PlanService>().DecideAsync(p.GetValue(plan), selection, approve, origin, ct));
            var result = new DecisionResult(r.State.Plan.Id,
                (approve ? PlanActionState.Approved : PlanActionState.Rejected).ToCode(),
                r.Changed,
                r.Skipped.Select(s => new SkippedView(s.ActionId, s.Reasons)).ToList(),
                ByState(r.State));
            return CommandOutcome.Success(result, () =>
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Plan {result.PlanId}: {result.Changed.Count} actions {result.State}, {result.Skipped.Count} skipped");
                foreach (var s in result.Skipped)
                    sb.AppendLine($"  skipped {s.ActionId}: {string.Join(", ", s.Reasons)}");
                return sb.ToString().TrimEnd();
            });
        });
        return command;
    }

    // ------------------------------------------------------------------ summary

    public static SummaryResult Summary(PlanState s, IFreeSpaceProbe? probe, int top)
    {
        var actions = s.Actions;
        var byKind = Enum.GetValues<PlanActionKind>().ToDictionary(k => k.ToCode(), k =>
        {
            var of = actions.Where(a => a.Action.Kind == k).ToList();
            return new Tally(of.Count, of.Sum(a => a.Action.Files), of.Sum(a => a.Action.Bytes));
        });
        var libraries = s.Libraries.Select(l =>
        {
            var into = actions.Where(a => a.Action.LibraryJobId == l.Id && PlanChecks.Writes(a.Action.Kind)
                                          && a.Action.State is not PlanActionState.Rejected).ToList();
            var needed = into.Sum(a => a.Action.Bytes);
            var free = probe?.AvailableBytes(l.RootPath);
            var space = free is null ? TargetSpace.Unknown : free >= needed ? TargetSpace.Enough : TargetSpace.NotEnough;
            return new LibraryView(l.Id, l.RootPath, into.Count, needed, free, space.ToCode());
        }).ToList();
        var units = actions.GroupBy(a => a.Action.FindingKey, StringComparer.Ordinal)
            .Select(g => new UnitView(g.Key, g.First().Action.FindingKind.ToCode(), g.First().Action.Unit, g.Count(),
                g.Sum(a => a.Action.Files), g.Sum(a => a.Action.Bytes),
                g.GroupBy(a => a.Action.Kind.ToCode()).ToDictionary(x => x.Key, x => (long)x.Count()),
                g.GroupBy(a => a.Action.State.ToCode()).ToDictionary(x => x.Key, x => (long)x.Count())))
            .OrderByDescending(u => u.Bytes).ThenBy(u => u.FindingKey, StringComparer.Ordinal)
            .ToList();
        return new SummaryResult(
            s.Plan.Id, s.Job.Id, s.Job.RootPath, s.Plan.CreatedAt, actions.Count, byKind, ByState(s),
            actions.Count(a => a.Action.Kind != a.Action.Recommended),
            actions.Count(a => a.Problems.Count > 0),
            actions.Count(a => a.Action.Sensitive),
            actions.Count(a => a.IntoManagedArea),
            libraries, units.Count, units.Take(Math.Max(0, top)).ToList(), s.Plan.Notes);
    }

    private static IReadOnlyDictionary<string, long> ByState(PlanState s) =>
        Enum.GetValues<PlanActionState>().ToDictionary(k => k.ToCode(), k => (long)s.Actions.Count(a => a.Action.State == k));

    private static string RenderSummary(SummaryResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Plan {r.PlanId} for job {r.JobId} ({r.RootPath}): {r.Actions} actions in {r.Units} units");
        foreach (var (kind, t) in r.ByKind.Where(k => k.Value.Actions > 0))
            sb.AppendLine($"  {kind,-14} {t.Actions,6} actions {t.Files,7} files {ReadFormat.Size(t.Bytes),10}");
        sb.AppendLine("  states: " + string.Join(", ", r.ByState.Where(s => s.Value > 0).Select(s => $"{s.Key} {s.Value}")));
        if (r.WithProblems > 0) sb.AppendLine($"  with problems: {r.WithProblems} (sfo plan show {r.PlanId} --problems)");
        if (r.Sensitive > 0) sb.AppendLine($"  sensitive, decided one by one: {r.Sensitive}");
        if (r.IntoManagedArea > 0) sb.AppendLine($"  into a managed area (its program must read it in again): {r.IntoManagedArea}");
        foreach (var l in r.Libraries)
            sb.AppendLine($"  library job {l.JobId} {l.RootPath}: {ReadFormat.Size(l.NeededBytes)} needed, "
                          + $"{(l.FreeBytes is { } free ? ReadFormat.Size(free) : "unknown")} free ({l.Space})");
        foreach (var u in r.TopUnits)
            sb.AppendLine($"  {u.FindingKey,-24} {u.Actions,5} actions {ReadFormat.Size(u.Bytes),10}  {u.Unit}");
        foreach (var note in r.Notes)
            sb.AppendLine("  note: " + note);
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ errors

    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is PlanException or JobReadException or FindingException or ArgumentException)
        {
            throw ToFailure(ex);
        }
    }

    private static CliFailureException ToFailure(Exception ex) => ex switch
    {
        JobReadException j => ReadFormat.ToFailure(j),
        FindingException { Failure: FindingFailure.WrongRole } => new CliFailureException(ExitCodes.Failure, ErrorCodes.WrongRole, ex.Message),
        FindingException { Failure: FindingFailure.ComparisonNotFound } => new CliFailureException(ExitCodes.Failure, ErrorCodes.ComparisonNotFound, ex.Message),
        PlanException p => p.Failure switch
        {
            PlanFailure.NotFound => new CliFailureException(ExitCodes.Failure, ErrorCodes.PlanNotFound, ex.Message),
            PlanFailure.ActionNotFound => new CliFailureException(ExitCodes.Failure, ErrorCodes.ActionNotFound, ex.Message),
            PlanFailure.WrongRole => new CliFailureException(ExitCodes.Failure, ErrorCodes.WrongRole, ex.Message),
            PlanFailure.LibraryInvalid => new CliFailureException(ExitCodes.Failure, ErrorCodes.LibraryInvalid, ex.Message),
            _ => new CliFailureException(ExitCodes.Failure, ErrorCodes.PlanChangeRefused, ex.Message)
        },
        ArgumentException => new CliFailureException(ExitCodes.Failure, ErrorCodes.SettingsInvalid, ex.Message),
        _ => new CliFailureException(ExitCodes.Failure, ErrorCodes.InternalError, ex.Message)
    };
}
