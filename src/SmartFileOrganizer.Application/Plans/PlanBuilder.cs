using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Plans;

/// <summary>What a plan is built from: the accepted findings and what the earlier steps stored about their files.</summary>
/// <param name="Accepted">Accepted findings, in rank order.</param>
/// <param name="Files">Every file of the job with its place and worth, by id.</param>
/// <param name="Comparison">The joined comparison with the libraries, by file id; empty without a library.</param>
/// <param name="Decisions">The stored decisions of <c>sfo decide</c>, by file id.</param>
/// <param name="Libraries">The libraries of the plan.</param>
/// <param name="Bind">The library whose root the targets of <c>sfo decide --targets</c> (relative paths) are bound to; null
/// when none is known, so such targets are not used.</param>
public sealed record PlanInputs(
    ScanJob Job,
    IReadOnlyList<Finding> Accepted,
    IReadOnlyDictionary<long, FileValueRow> Files,
    IReadOnlyList<DirectoryReadRow> Directories,
    IReadOnlyDictionary<long, FileComparison> Comparison,
    IReadOnlyDictionary<long, FileDecision> Decisions,
    IReadOnlyList<ScanJob> Libraries,
    ScanJob? Bind);

/// <summary>Actions built from the inputs, before the checks, and what was left out.</summary>
public sealed record PlanDraft(IReadOnlyList<PlanAction> Actions, IReadOnlyList<string> Notes);

/// <summary>
/// Turns accepted findings into plan actions (https://github.com/crack666/smart_file_organizer/issues/11). No database,
/// no file opened. The rules:
/// <list type="bullet">
/// <item>A folder judged <c>unit</c> or <c>uniform</c> whose files all go (worth keeping, not in a library, not marked)
/// becomes one action for the folder; its name stays. The topmost such folder wins.</item>
/// <item>Everything else of a finding to move becomes one action per file, for worth-keeping files only; the rest of a
/// <c>mixed</c> folder stays where it is.</item>
/// <item>Content a library holds already gets no move: the file is left out and counted in the notes.</item>
/// <item>A sensitive finding becomes <c>leave</c>, decided one by one.</item>
/// <item>A copy of a duplicate becomes <c>delete_source</c> only when a library holds its content; the kept copy and
/// copies only the source holds get nothing.</item>
/// <item>A target comes from the finding (a place in a library), else from <c>sfo decide</c> bound to a library root;
/// without one the action is <c>leave</c> with a note. A finding that recommends nothing else is one <c>leave</c>.</item>
/// </list>
/// </summary>
public static class PlanBuilder
{
    private static readonly string[] WholeVerdicts =
    [
        DirectoryVerdictKind.Unit.ToCode(), DirectoryVerdictKind.Uniform.ToCode()
    ];

    public static PlanDraft Build(PlanInputs inputs, DateTime now)
    {
        var actions = new List<PlanAction>();
        var notes = new List<string>();
        var backedUpLeftOut = 0L;
        var markedLeftOut = 0L;

        // ---- findings that move files: one pool, so a uniform folder split by kind of file stays one action
        var owner = new Dictionary<long, Finding>();
        foreach (var f in inputs.Accepted.Where(f => f.Kind is FindingKind.NotBackedUp or FindingKind.InNoise or FindingKind.Misplaced))
        {
            if (f.Action.Kind != PlanActionKind.Move)
            {
                actions.Add(LeaveForFinding(inputs.Job, f, f.Action.Kind, f.Action.Reason, now));
                continue;
            }

            var any = false;
            foreach (var id in f.FileIds)
            {
                if (!inputs.Files.TryGetValue(id, out var row)) continue;
                if (SensitiveOutputGuard.IsSensitive(row.File))
                {
                    markedLeftOut++;
                    continue;
                }
                if (IsBackedUp(inputs, id))
                {
                    backedUpLeftOut++;
                    continue;
                }
                owner.TryAdd(id, f);
                any = true;
            }
            if (!any)
                notes.Add($"Finding {f.Key}: nothing left to move; every file is in a library already or marked.");
        }

        actions.AddRange(MoveActions(inputs, owner, now));

        // ---- sensitive: leave, one by one
        foreach (var f in inputs.Accepted.Where(f => f.Kind == FindingKind.Sensitive))
        {
            var files = f.FileIds.Where(inputs.Files.ContainsKey).Select(id => inputs.Files[id].File).ToList();
            if (files.Count == 0)
            {
                var leave = LeaveForFinding(inputs.Job, f, PlanActionKind.Leave, f.Action.Reason, now);
                leave.Sensitive = true;
                leave.SingleDecision = true;
                actions.Add(leave);
                continue;
            }
            foreach (var file in files)
            {
                var a = FileAction(inputs.Job, f, file, now);
                a.Kind = PlanActionKind.Leave;
                a.Recommended = PlanActionKind.Leave;
                a.Reason = f.Action.Reason;
                a.Sensitive = true;
                a.SingleDecision = true;
                actions.Add(a);
            }
        }

        // ---- duplicates: remove a copy only when a library holds its content
        foreach (var f in inputs.Accepted.Where(f => f.Kind == FindingKind.Duplicate))
        {
            var keep = f.Duplicates?.Keep is { } keepRel ? PlanChecks.Join(inputs.Job.RootPath, keepRel == "." ? string.Empty : keepRel) : null;
            var before = actions.Count;
            if (f.Action.Kind == PlanActionKind.DeleteSource)
            {
                foreach (var id in f.FileIds)
                {
                    if (!inputs.Files.TryGetValue(id, out var row)) continue;
                    var file = row.File;
                    if (keep is not null && PlanChecks.IsAtOrBelow(file.FullPath, keep)) continue;
                    if (SensitiveOutputGuard.IsSensitive(file)) continue;
                    if (!inputs.Comparison.TryGetValue(id, out var match) || match.State != ContentState.BackedUp || match.MatchPath is null)
                        continue;
                    var a = FileAction(inputs.Job, f, file, now);
                    a.Kind = PlanActionKind.DeleteSource;
                    a.Recommended = PlanActionKind.DeleteSource;
                    a.TargetPath = match.MatchPath;
                    a.LibraryJobId = match.MatchJobId;
                    a.Reason = f.Action.Reason;
                    a.Note = "The same content lies in the library at the target; the execution step reads both files again before it removes anything.";
                    actions.Add(a);
                }
            }
            if (actions.Count == before)
            {
                actions.Add(LeaveForFinding(inputs.Job, f, f.Action.Kind,
                    f.Action.Kind == PlanActionKind.DeleteSource
                        ? f.Action.Reason + " No copy here is in a library yet, so none is removed."
                        : f.Action.Reason, now));
            }
        }

        if (backedUpLeftOut > 0)
            notes.Add($"{backedUpLeftOut} files of accepted findings are in a library with the same content already: no action moves them.");
        if (markedLeftOut > 0)
            notes.Add($"{markedLeftOut} marked files of accepted findings are left out of moves; a sensitive finding decides them one by one.");
        return new PlanDraft(actions, notes);
    }

    private static IEnumerable<PlanAction> MoveActions(PlanInputs inputs, Dictionary<long, Finding> owner, DateTime now)
    {
        if (owner.Count == 0) yield break;

        var root = PlanChecks.Normalize(inputs.Job.RootPath);
        // Files below every folder, through the chain of parents.
        var dirByPath = inputs.Directories.ToDictionary(d => d.FullPath, StringComparer.Ordinal);
        var below = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var (id, row) in inputs.Files)
        {
            for (var p = row.File.ParentPath; dirByPath.TryGetValue(p, out var d); p = d.ParentPath)
            {
                if (!below.TryGetValue(p, out var list)) below[p] = list = [];
                list.Add(id);
                if (string.Equals(PlanChecks.Normalize(p), root, StringComparison.OrdinalIgnoreCase)) break;
            }
        }

        var used = new HashSet<long>();
        var folders = inputs.Directories
            .Where(d => d.Verdict is { } v && WholeVerdicts.Contains(v))
            .Where(d => !string.Equals(PlanChecks.Normalize(d.FullPath), root, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Depth).ThenBy(d => d.FullPath, StringComparer.Ordinal);
        foreach (var d in folders)
        {
            if (!below.TryGetValue(d.FullPath, out var ids) || ids.Count == 0) continue;
            // Misplaced files leave their folder on their own; a folder holding one does not move as a whole.
            if (!ids.All(id => owner.TryGetValue(id, out var o) && o.Kind != FindingKind.Misplaced) || ids.Any(used.Contains)) continue;
            // Files of a finding split by content (Issue 85) move with their part; a folder spanning parts does not move whole.
            if (ids.Select(id => owner[id]).Any(o => o.TargetParts.Count > 0)
                && ids.Select(id => owner[id].TargetParts.FirstOrDefault(p => p.FileIds.Contains(id))).Distinct().Count() != 1)
                continue;

            var first = ids.Select(id => inputs.Files[id].File).OrderBy(f => f.FullPath, StringComparer.Ordinal).First();
            var f = owner[first.Id];
            var a = new PlanAction
            {
                FindingKey = f.Key,
                FindingKind = f.Kind,
                Unit = f.Title,
                SourceKind = PlanSourceKind.Folder,
                SourcePath = d.FullPath,
                RelativePath = FindingRules.Rel(d.RelativePath),
                DirectoryNodeId = d.Id,
                Files = ids.Count,
                Bytes = ids.Sum(id => inputs.Files[id].File.Size),
                Recommended = PlanActionKind.Move,
                Reason = $"The folder is one whole ({d.Verdict}): it moves as it is, its name stays. {f.Action.Reason}",
                UpdatedAt = now
            };
            var (target, library, why) = TargetOf(inputs, f, ids, d.Name, folder: true);
            SetTarget(a, target, library, why);
            yield return a;
            foreach (var id in ids) used.Add(id);
        }

        foreach (var (id, f) in owner.OrderBy(o => inputs.Files[o.Key].File.FullPath, StringComparer.Ordinal))
        {
            if (used.Contains(id)) continue;
            var file = inputs.Files[id].File;
            var a = FileAction(inputs.Job, f, file, now);
            a.Recommended = PlanActionKind.Move;
            a.Reason = f.Action.Reason;
            var (target, library, why) = TargetOf(inputs, f, [id], file.Name);
            SetTarget(a, target, library, why);
            yield return a;
        }
    }

    private static void SetTarget(PlanAction a, string? target, long? library, string? why)
    {
        if (target is null)
        {
            a.Kind = PlanActionKind.Leave;
            a.Note = why ?? "No target in a library: set one with sfo plan set.";
            return;
        }
        a.Kind = PlanActionKind.Move;
        a.TargetPath = target;
        a.LibraryJobId = library;
    }

    /// <summary>
    /// The target of files that move together under <paramref name="name"/>: the place the finding proposes in a
    /// library, else the one target <c>sfo decide</c> gave all of them, bound to a library root.
    /// </summary>
    private static (string? Target, long? Library, string? Why) TargetOf(
        PlanInputs inputs, Finding f, IReadOnlyList<long> ids, string name, bool folder = false)
    {
        // A finding split by content (Issue 85): each file goes to the place of its part; a folder moves whole only when
        // all of its files are in one placed part.
        if (f.TargetParts.Count > 0)
        {
            var parts = ids.Select(id => f.TargetParts.FirstOrDefault(p => p.FileIds.Contains(id))).Distinct().ToList();
            if (parts.Count != 1 || parts[0] is not { } part)
                return (null, null, "The files of this folder belong to different parts of the finding; each moves on its own.");
            if (!FindingRules.HasPlace(part.Status) || part.Path is null || part.LibraryJobId is null)
                return (null, null, $"This part of the finding ({part.Label}) has no target: {part.Reason}");
            if (folder && string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(part.Path)), name, StringComparison.OrdinalIgnoreCase))
                return (part.Path, part.LibraryJobId, null);
            return (PlanChecks.Join(part.Path, name), part.LibraryJobId, null);
        }

        if (f.Target is { LibraryJobId: { } lib } t)
        {
            // A folder kept whole whose target already carries its name (sfo findings targets: a folder of that name, or
            // a new one named after it) goes there itself, not into a folder of its own name below it.
            if (folder && string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(t.Path)), name, StringComparison.OrdinalIgnoreCase))
                return (t.Path, lib, null);
            return (PlanChecks.Join(t.Path, name), lib, null);
        }

        // The controller decided on no target (Issue 95): no target of sfo decide either.
        if (f.TargetDecidedBy == TargetDecidedBy.Controller)
            return (null, null, "The controller decided on no target for these files (sfo findings place); they stay.");

        var paths = ids.Select(id => inputs.Decisions.TryGetValue(id, out var d) ? d.Target?.Path : null).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count != 1 || paths[0] is not { } rel)
            return (null, null, paths.Count > 1
                ? "The files have different targets in sfo decide; set one with sfo plan set."
                : "No target in a library: run sfo decide --targets, or set one with sfo plan set.");
        if (inputs.Bind is not { } bind)
            return (null, null, $"sfo decide proposes {rel}, but no library root to put it in: name one with sfo plan create --library.");
        return (PlanChecks.Join(bind.RootPath, rel, name), bind.Id, null);
    }

    private static PlanAction FileAction(ScanJob job, Finding f, FileNode file, DateTime now) => new()
    {
        FindingKey = f.Key,
        FindingKind = f.Kind,
        Unit = f.Title,
        SourceKind = PlanSourceKind.File,
        SourcePath = file.FullPath,
        RelativePath = FindingRules.Rel(file.RelativePath),
        FileNodeId = file.Id,
        Files = 1,
        Bytes = file.Size,
        UpdatedAt = now
    };

    /// <summary>One <c>leave</c> for a finding that recommends nothing to do, or nothing that can be done here.</summary>
    private static PlanAction LeaveForFinding(ScanJob job, Finding f, PlanActionKind recommended, string reason, DateTime now)
    {
        var path = f.Path ?? f.Directories.FirstOrDefault() ?? job.RootPath;
        return new PlanAction
        {
            FindingKey = f.Key,
            FindingKind = f.Kind,
            Unit = f.Title,
            SourceKind = PlanSourceKind.Folder,
            SourcePath = path,
            RelativePath = f.RelativePath ?? ".",
            Files = f.Files,
            Bytes = f.Bytes,
            Kind = PlanActionKind.Leave,
            Recommended = recommended,
            Reason = reason,
            UpdatedAt = now
        };
    }

    private static bool IsBackedUp(PlanInputs inputs, long id) =>
        inputs.Comparison.TryGetValue(id, out var row) && row.State == ContentState.BackedUp;
}
