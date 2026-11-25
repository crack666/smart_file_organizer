using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Decisions;
using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo decide &lt;job&gt;</c>: stage 2 (https://github.com/crack666/smart_file_organizer/issues/37). Computes worth and
/// target of every file anew from what stage 1 stored, by rule, and stores it. No model call, no scanned file opened,
/// nothing changed on disk; after a changed target structure or changed rules this is all that has to run.
/// </summary>
public static class DecideCommand
{
    public sealed record InputView(string Key, string Value, string Source)
    {
        public static InputView From(DecisionInput i) => new(i.Key, i.Value, i.Source);
    }

    public sealed record TargetView(
        string Id, string Path, string Rule, IReadOnlyList<InputView> Basis, IReadOnlyList<string> Unresolved, long? LibraryJobId, string? FullPath)
    {
        public static TargetView? From(TargetProposal? t, TargetRoot? root) =>
            t is null ? null : new TargetView(t.TargetId, t.Path, t.Rule, t.Basis.Select(InputView.From).ToList(), t.Unresolved,
                root?.Library?.Id, root?.Library is { } library ? PlanChecks.Join(library.RootPath, t.Path) : null);
    }

    /// <summary>The library root the targets lie in, and the room there (https://github.com/crack666/smart_file_organizer/issues/71).</summary>
    public sealed record TargetStorageView(
        string Status, string? Message, long? LibraryJobId, string? RootPath, string? Under,
        long? FreeBytes, long NeededFiles, long NeededBytes, string? Space, string? Warning)
    {
        public static TargetStorageView? From(TargetRoot? root, TargetRoom? room) => root is null || room is null
            ? null
            : new TargetStorageView(root.Status.ToCode(), root.Message, root.Library?.Id, root.Library?.RootPath,
                root.Library is null ? null : root.Under, room.FreeBytes, room.Files, room.NeededBytes, room.Space?.ToCode(), room.Warning(root));
    }

    public sealed record DecisionView(
        long FileId,
        string RelativePath,
        string? Worth,
        string Certainty,
        string Rule,
        IReadOnlyList<InputView> Basis,
        TargetView? Target,
        string? PreviousTarget,
        bool Changed);

    public sealed record TargetTotals(string? Structure, long Proposed, long Unresolved, long KeptWithoutTarget, TargetStorageView? Storage);

    public sealed record Result(
        long JobId,
        DateTime DecidedAt,
        DateTime? PreviousDecidedAt,
        long Files,
        IReadOnlyDictionary<string, long> ByWorth,
        IReadOnlyDictionary<string, long> ByCertainty,
        TargetTotals Targets,
        long Changed,
        IReadOnlyDictionary<string, long> Findings,
        long Total,
        int Limit,
        int Offset,
        int? NextOffset,
        IReadOnlyList<DecisionView> Items);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id of a source (see sfo jobs).");
        var targets = new Option<string?>("--targets")
        {
            Description = "Target structure file (sfo library propose --out, edited by hand): kept files get a target from it. Without it, worth only.",
            HelpName = "file"
        };
        var library = new Option<long?>("--library")
        {
            Description = "Library job whose root the targets lie in. Default: the library the root of the structure lies in, else the only library of the newest compare.",
            HelpName = "job"
        };
        var changed = new Option<bool>("--changed")
        {
            Description = "List only the files whose worth or target changed against the decisions stored before."
        };
        var read = new ReadOptions();
        var command = new Command("decide",
            "Decide worth and target of every file anew from the stored facts and results, by rule. No model call; run it again after changing the target structure.")
        {
            job, targets, library, changed
        };
        foreach (var option in read.Paging)
            command.Options.Add(option);

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var p = ctx.ParseResult;
            var jobId = p.GetValue(job);
            TargetStructure? structure = null;
            string? structurePath = null;
            if (p.GetValue(targets) is { Length: > 0 } file)
            {
                structurePath = Path.GetFullPath(file);
                if (!File.Exists(structurePath))
                    throw new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, $"Target structure file not found: {structurePath}");
                try
                {
                    structure = TargetStructureFile.Load(structurePath);
                }
                catch (TargetStructureException ex)
                {
                    throw new CliFailureException(ExitCodes.Failure, ErrorCodes.TargetsInvalid, ex.Message);
                }
            }

            if (p.GetValue(library) is not null && structure is null)
                throw new CliFailureException(ExitCodes.Usage, ErrorCodes.Usage, "--library needs --targets: it names the library the target structure lies in.");

            var services = await ctx.GetServicesAsync(ct);
            var service = services.GetRequiredService<DecisionService>();
            DecisionRun run;
            try
            {
                run = await service.DecideAsync(jobId, new DecideOptions { Targets = structure, Library = p.GetValue(library) }, ct);
            }
            catch (TargetRootException ex)
            {
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.LibraryInvalid, ex.Message);
            }
            catch (JobReadException ex)
            {
                throw ReadFormat.ToFailure(ex);
            }
            catch (FindingException ex) when (ex.Failure == FindingFailure.WrongRole)
            {
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.WrongRole, ex.Message);
            }
            catch (ArgumentException ex)
            {
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.SettingsInvalid, ex.Message);
            }

            var result = Build(run, structurePath, p.GetValue(changed), p.GetValue(read.Limit), p.GetValue(read.Offset), out var page);
            return CommandOutcome.Success(result, () => Render(result, page));
        });
        return command;
    }

    public static Result Build(DecisionRun run, string? structurePath, bool onlyChanged, int limit, int offset, out ReadPage<DecisionView> page)
    {
        var changedIds = run.Changed.Select(d => d.FileNodeId).ToHashSet();
        var views = run.Decisions
            .Select(d =>
            {
                var file = run.Files[d.FileNodeId];
                run.Previous.TryGetValue(d.FileNodeId, out var before);
                return new DecisionView(d.FileNodeId, FindingRules.Rel(file.RelativePath), d.Worth?.ToCode(), d.Certainty.ToCode(), d.Rule,
                    d.Basis.Select(InputView.From).ToList(), TargetView.From(d.Target, run.TargetRoot), before?.Target?.Path, changedIds.Contains(d.FileNodeId));
            })
            .Where(v => !onlyChanged || v.Changed)
            .OrderBy(v => v.RelativePath, StringComparer.Ordinal)
            .ToList();
        page = new ReadPage<DecisionView>(views.Count, limit, offset, views.Skip(offset).Take(limit).ToList());

        var byWorth = new Dictionary<string, long>
        {
            [RetentionValue.Keep.ToCode()] = run.Decisions.Count(d => d.Worth == RetentionValue.Keep),
            [RetentionValue.Disposable.ToCode()] = run.Decisions.Count(d => d.Worth == RetentionValue.Disposable),
            ["none"] = run.Decisions.Count(d => d.Worth is null)
        };
        var byCertainty = Enum.GetValues<ResultCertainty>().ToDictionary(c => c.ToCode(),
            c => (long)run.Decisions.Count(d => d.Worth is not null && d.Certainty == c));
        var kept = run.Decisions.Where(d => d.Worth == RetentionValue.Keep).ToList();
        var totals = new TargetTotals(structurePath,
            kept.Count(d => d.Target is not null),
            kept.Count(d => d.Target is { Unresolved.Count: > 0 }),
            kept.Count(d => d.Target is null),
            TargetStorageView.From(run.TargetRoot, run.Room));
        var listed = run.Findings.Findings.Where(f => f.State is FindingState.Open or FindingState.Accepted).ToList();
        var findings = FindingRules.KindOrder.ToDictionary(k => k.ToCode(), k => (long)listed.Count(f => f.Kind == k));
        var previousAt = run.Previous.Count > 0 ? run.Previous.Values.Max(d => d.DecidedAt) : (DateTime?)null;

        return new Result(run.Job.Id, run.DecidedAt, previousAt, run.Decisions.Count, byWorth, byCertainty, totals,
            changedIds.Count, findings, page.Total, page.Limit, page.Offset, page.NextOffset, page.Items);
    }

    private static string Render(Result r, ReadPage<DecisionView> page)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Job {r.JobId}: {r.Files} files decided, {r.Changed} changed; keep {r.ByWorth["keep"]}, disposable {r.ByWorth["disposable"]}, no statement {r.ByWorth["none"]}");
        sb.AppendLine(r.Targets.Structure is null
            ? "  no target structure (--targets): worth only"
            : $"  targets from {r.Targets.Structure}: {r.Targets.Proposed} proposed ({r.Targets.Unresolved} with an open placeholder), {r.Targets.KeptWithoutTarget} kept without target");
        if (r.Targets.Storage is { } s)
        {
            sb.AppendLine(s.LibraryJobId is { } id
                ? $"  in library job {id} ({s.RootPath}{(s.Under is { Length: > 0 } u ? $", below {u}" : string.Empty)}): "
                  + $"{s.NeededFiles} files, {FindingRules.Size(s.NeededBytes)} needed, {(s.FreeBytes is { } free ? FindingRules.Size(free) + " free" : "free space unknown")}"
                : $"  {s.Message}");
            if (s.Warning is { } warning) sb.AppendLine("  " + warning);
        }
        sb.AppendLine("  findings: " + string.Join(", ", r.Findings.Select(f => $"{f.Key} {f.Value}")));
        foreach (var d in r.Items)
        {
            var basis = string.Join(", ", d.Basis.Select(b => $"{b.Key}={b.Value} ({b.Source})"));
            sb.AppendLine($"  {d.Worth ?? "-",-10} {d.Certainty,-9} {d.RelativePath}  [{d.Rule}: {basis}]");
            if (d.Target is { } t)
                sb.AppendLine($"             -> {t.Path}  [{string.Join(", ", t.Basis.Select(b => $"{b.Key}={b.Value} ({b.Source})"))}]"
                              + (t.Unresolved.Count > 0 ? $" open: {string.Join(", ", t.Unresolved)}" : string.Empty)
                              + (d.Changed && d.PreviousTarget is { } before && before != t.Path ? $" (was {before})" : string.Empty));
        }
        sb.Append(ReadFormat.PageFooter(page));
        return sb.ToString();
    }
}
