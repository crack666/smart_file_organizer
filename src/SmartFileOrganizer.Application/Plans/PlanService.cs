using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Plans;

/// <summary>Why a plan could not be created, read or changed.</summary>
public enum PlanFailure
{
    /// <summary>No plan with this id.</summary>
    NotFound,

    /// <summary>A plan is about a source; a library is never a source.</summary>
    WrongRole,

    /// <summary>The plan has no action with this id.</summary>
    ActionNotFound,

    /// <summary>A job named as library is none, or was not compared with the source.</summary>
    LibraryInvalid,

    /// <summary>A change was refused by the checks; nothing was changed.</summary>
    ChangeRefused
}

public sealed class PlanException(PlanFailure failure, string message, IReadOnlyList<RefusedChange>? refused = null) : Exception(message)
{
    public PlanFailure Failure { get; } = failure;
    public IReadOnlyList<RefusedChange> Refused { get; } = refused ?? [];
}

/// <summary>A change refused by the checks, with the problems it would have.</summary>
public sealed record RefusedChange(long ActionId, IReadOnlyList<PlanProblem> Problems, string Message);

/// <summary>Options of <see cref="PlanService.CreateAsync"/>.</summary>
/// <param name="Libraries">The library jobs whose comparisons count; empty for those of the newest <c>sfo compare</c>.</param>
/// <param name="Bind">The library whose root the targets of <c>sfo decide --targets</c> are put in; null to take the only
/// library when there is exactly one.</param>
public sealed record PlanCreateOptions(IReadOnlyList<long> Libraries, long? Bind = null);

/// <summary>A change of one action; null leaves a field as it is.</summary>
public sealed record PlanChange(long ActionId, PlanActionKind? Kind = null, string? Target = null, bool ClearTarget = false, string? Reason = null);

/// <summary>Which actions a confirmation or rejection is about.</summary>
/// <param name="Dir">Actions whose source lies at or below this folder (full, or relative to the job root).</param>
public sealed record PlanSelection(IReadOnlyList<long> ActionIds, string? Dir = null, bool All = false)
{
    /// <summary>More than the actions named one by one: sensitive actions are never part of it.</summary>
    public bool IsBatch => Dir is not null || All;
}

/// <summary>An action as it stands now: the problems the checks find today, and whether it leads into a managed area.</summary>
public sealed record PlanActionStatus(PlanAction Action, IReadOnlyList<PlanProblem> Problems, bool IntoManagedArea);

/// <summary>A plan with all its actions, checked now.</summary>
public sealed record PlanState(Plan Plan, ScanJob Job, IReadOnlyList<ScanJob> Libraries, IReadOnlyList<PlanActionStatus> Actions);

/// <summary>What a confirmation or rejection did.</summary>
public sealed record PlanDecisionResult(PlanState State, IReadOnlyList<long> Changed, IReadOnlyList<SkippedAction> Skipped);

/// <summary>An action a confirmation passed over, and why: a <see cref="PlanProblem"/> code, <c>single_decision</c> or its state.</summary>
public sealed record SkippedAction(long ActionId, IReadOnlyList<string> Reasons);

/// <summary>
/// Plans (https://github.com/crack666/smart_file_organizer/issues/11): built from accepted findings, changed by a person
/// or a steering agent, confirmed or rejected action by action. Nothing here moves, copies or removes a file; that is
/// the execution step (https://github.com/crack666/smart_file_organizer/issues/12), which runs the same checks again.
/// </summary>
public sealed class PlanService
{
    private readonly IScanJobRepository _jobs;
    private readonly IJobReadRepository _read;
    private readonly IContentComparisonRepository _comparisons;
    private readonly IFileDecisionRepository _decisions;
    private readonly IPlanRepository _plans;
    private readonly FindingService _findings;
    private readonly PlanOptions _options;
    private readonly IProtectedPlaces _protected;
    private readonly TimeProvider _time;

    public PlanService(
        IScanJobRepository jobs,
        IJobReadRepository read,
        IContentComparisonRepository comparisons,
        IFileDecisionRepository decisions,
        IPlanRepository plans,
        FindingService findings,
        PlanOptions options,
        IProtectedPlaces protectedPlaces,
        TimeProvider? time = null)
    {
        _jobs = jobs;
        _read = read;
        _comparisons = comparisons;
        _decisions = decisions;
        _plans = plans;
        _findings = findings;
        _options = options;
        _protected = protectedPlaces;
        _time = time ?? TimeProvider.System;
    }

    // ------------------------------------------------------------------ create

    /// <summary>
    /// A new plan from the accepted findings of the job. Every action that fails a check is stored as <c>leave</c>
    /// with the reason in its note, so no stored action breaks a rule when it is created.
    /// </summary>
    public async Task<PlanState> CreateAsync(long jobId, PlanCreateOptions options, CancellationToken ct = default)
    {
        var job = await _jobs.GetByIdAsync(jobId, ct)
                  ?? throw new JobReadException(ReadFailure.JobNotFound, $"Job {jobId} not found.");
        if (job.Role == ScanRole.Library)
            throw new PlanException(PlanFailure.WrongRole, $"Job {jobId} is a library; a library is never the source of a plan.");

        var (report, values) = await _findings.ComputeValuesAsync(jobId, options.Libraries, ct);
        var (set, _) = await LibraryComparisonSet.LoadAsync(_comparisons, jobId, options.Libraries, ct);

        var libraryIds = (set?.LibraryJobIds ?? []).ToList();
        if (options.Bind is { } bindId && !libraryIds.Contains(bindId))
            libraryIds.Add(bindId);
        var libraries = new List<ScanJob>();
        foreach (var id in libraryIds)
        {
            var library = await _jobs.GetByIdAsync(id, ct);
            if (library is not { Role: ScanRole.Library })
                throw new PlanException(PlanFailure.LibraryInvalid, $"Job {id} is no library (scan it with --role library).");
            libraries.Add(library);
        }
        var bind = options.Bind is { } b ? libraries.First(l => l.Id == b) : libraries.Count == 1 ? libraries[0] : null;

        var now = _time.GetUtcNow().UtcDateTime;
        var accepted = report.Findings.Where(f => f.State == FindingState.Accepted).OrderBy(f => f.Rank).ToList();
        var inputs = new PlanInputs(
            job,
            accepted,
            values.ToDictionary(v => v.File.Id),
            await _read.GetDirectoriesAsync(jobId, ct),
            set?.Rows ?? new Dictionary<long, FileComparison>(),
            await _decisions.GetByJobAsync(jobId, ct),
            libraries,
            bind);
        var draft = PlanBuilder.Build(inputs, now);

        var ctx = await ContextAsync(job, libraries, set, ct);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in draft.Actions)
        {
            if (a.Kind == PlanActionKind.Leave) continue;
            var problems = PlanChecks.Check(a, ctx, claimed.Contains);
            if (problems.Count > 0)
            {
                a.Note = $"Left here: {string.Join(", ", problems.Select(p => p.ToCode()))}"
                         + (a.TargetPath is { } t ? $" (target {t})" : string.Empty) + (a.Note is { } n ? ". " + n : string.Empty);
                a.Kind = PlanActionKind.Leave;
                continue;
            }
            if (PlanChecks.Writes(a.Kind))
                claimed.Add(PlanChecks.Normalize(a.TargetPath!));
        }

        var notes = new List<string>();
        if (accepted.Count == 0)
            notes.Add("No accepted finding: accept findings first (sfo findings accept).");
        if (libraries.Count == 0)
            notes.Add("No library: there is no place for a target, so nothing is moved. Compare the source with a library first (sfo compare --against).");
        notes.AddRange(draft.Notes);

        var plan = await _plans.CreateAsync(new Plan
        {
            JobId = jobId,
            CreatedAt = now,
            Libraries = libraries.Select(l => l.Id).ToList(),
            Notes = notes
        }, draft.Actions, ct);
        return Status(plan, job, libraries, draft.Actions, ctx);
    }

    // ------------------------------------------------------------------ read

    public async Task<PlanState> GetAsync(long planId, CancellationToken ct = default)
    {
        var (plan, job, libraries, actions, ctx) = await LoadAsync(planId, ct);
        return Status(plan, job, libraries, actions, ctx);
    }

    // ------------------------------------------------------------------ change

    /// <summary>
    /// Applies changes of kind, target and reason, all or none: when one change fails a check nothing is stored and the
    /// exception lists every refused change. A changed action is <c>proposed</c> again and must be confirmed anew.
    /// </summary>
    public async Task<PlanState> ChangeAsync(long planId, IReadOnlyList<PlanChange> changes, PlanActionOrigin origin, CancellationToken ct = default)
    {
        if (origin is not (PlanActionOrigin.Human or PlanActionOrigin.Agent))
            throw new ArgumentException("Only a person (human) or a steering agent (agent) changes a plan.", nameof(origin));

        var (plan, job, libraries, actions, ctx) = await LoadAsync(planId, ct);
        var byId = actions.ToDictionary(a => a.Id);
        var now = _time.GetUtcNow().UtcDateTime;
        var refused = new List<RefusedChange>();
        var changed = new Dictionary<long, PlanAction>();
        foreach (var change in changes)
        {
            if (!byId.TryGetValue(change.ActionId, out var current))
            {
                refused.Add(new RefusedChange(change.ActionId, [], $"Plan {planId} has no action {change.ActionId}."));
                continue;
            }
            if (current.State is PlanActionState.Verified or PlanActionState.Executed or PlanActionState.Failed or PlanActionState.Undone)
            {
                refused.Add(new RefusedChange(change.ActionId, [], $"Action {change.ActionId} is {current.State.ToCode()}; it can no longer be changed."));
                continue;
            }

            var a = changed.TryGetValue(change.ActionId, out var already) ? already : Copy(current);
            if (change.Kind is { } kind) a.Kind = kind;
            if (change.ClearTarget)
            {
                a.TargetPath = null;
                a.LibraryJobId = null;
            }
            else if (change.Target is { Length: > 0 } target)
            {
                // A relative target is relative to the library of the plan; with several it names none of them.
                if (!Path.IsPathRooted(target) && libraries.Count != 1)
                {
                    refused.Add(new RefusedChange(change.ActionId, [PlanProblem.TargetOutsideLibrary],
                        $"Action {change.ActionId}: target_outside_library (a relative target needs exactly one library in the plan; give a full path)"));
                    continue;
                }
                a.TargetPath = PlanChecks.Normalize(Path.IsPathRooted(target) ? target : PlanChecks.Join(libraries[0].RootPath, target));
                a.LibraryJobId = PlanChecks.LibraryOf(a.TargetPath, ctx);
            }
            if (change.Reason is { Length: > 0 } reason) a.Reason = reason;
            a.Origin = origin;
            a.Note = null;
            a.State = PlanActionState.Proposed;
            a.DecidedBy = null;
            a.DecidedAt = null;
            a.UpdatedAt = now;
            changed[a.Id] = a;
        }

        var after = actions.Select(a => changed.TryGetValue(a.Id, out var c) ? c : a).ToList();
        var problems = PlanChecks.CheckAll(after, ctx);
        foreach (var a in changed.Values)
        {
            if (problems[a.Id].Count > 0)
                refused.Add(new RefusedChange(a.Id, problems[a.Id], $"Action {a.Id}: {string.Join(", ", problems[a.Id].Select(p => p.ToCode()))}"));
        }
        if (refused.Count > 0)
        {
            throw new PlanException(PlanFailure.ChangeRefused,
                $"Nothing changed; refused: {string.Join("; ", refused.Select(r => r.Message))}", refused);
        }

        await _plans.UpdateActionsAsync(changed.Values.ToList(), ct);
        return Status(plan, job, libraries, after, ctx);
    }

    // ------------------------------------------------------------------ approve, reject

    /// <summary>
    /// Confirms (<paramref name="approve"/>) or rejects the selected actions. Only an action without problems is
    /// confirmed; a sensitive action only when named by id. Executed, failed and undone actions are not touched.
    /// </summary>
    public async Task<PlanDecisionResult> DecideAsync(
        long planId, PlanSelection selection, bool approve, PlanActionOrigin origin, CancellationToken ct = default)
    {
        if (origin is not (PlanActionOrigin.Human or PlanActionOrigin.Agent))
            throw new ArgumentException("Only a person (human) or a steering agent (agent) confirms a plan.", nameof(origin));

        var (plan, job, libraries, actions, ctx) = await LoadAsync(planId, ct);
        var byId = actions.ToDictionary(a => a.Id);
        foreach (var id in selection.ActionIds)
        {
            if (!byId.ContainsKey(id))
                throw new PlanException(PlanFailure.ActionNotFound, $"Plan {planId} has no action {id}.");
        }

        var dir = selection.Dir is { Length: > 0 } d ? (Path.IsPathRooted(d) ? d : Path.Combine(job.RootPath, d)) : null;
        var selected = actions
            .Where(a => selection.All
                        || selection.ActionIds.Contains(a.Id)
                        || dir is not null && PlanChecks.IsAtOrBelow(a.SourcePath, dir))
            .ToList();

        var problems = PlanChecks.CheckAll(actions, ctx);
        var now = _time.GetUtcNow().UtcDateTime;
        var target = approve ? PlanActionState.Approved : PlanActionState.Rejected;
        var changed = new List<PlanAction>();
        var skipped = new List<SkippedAction>();
        foreach (var a in selected)
        {
            if (a.State is PlanActionState.Verified or PlanActionState.Executed or PlanActionState.Failed or PlanActionState.Undone)
            {
                skipped.Add(new SkippedAction(a.Id, [a.State.ToCode()]));
                continue;
            }
            if (approve)
            {
                // Named one by one is the only way to confirm a sensitive action.
                if (a.SingleDecision && !selection.ActionIds.Contains(a.Id))
                {
                    skipped.Add(new SkippedAction(a.Id, ["single_decision"]));
                    continue;
                }
                if (problems[a.Id].Count > 0)
                {
                    skipped.Add(new SkippedAction(a.Id, problems[a.Id].Select(p => p.ToCode()).ToList()));
                    continue;
                }
            }
            if (a.State == target) continue;
            a.State = target;
            a.DecidedBy = origin;
            a.DecidedAt = now;
            a.UpdatedAt = now;
            changed.Add(a);
        }

        await _plans.UpdateActionsAsync(changed, ct);
        return new PlanDecisionResult(Status(plan, job, libraries, actions, ctx), changed.Select(a => a.Id).ToList(), skipped);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The plan, its job, libraries and actions, and the context of the checks as they stand now.</summary>
    internal Task<(Plan Plan, ScanJob Job, IReadOnlyList<ScanJob> Libraries, IReadOnlyList<PlanAction> Actions, PlanCheckContext Ctx)> LoadForRunAsync(
        long planId, CancellationToken ct) => LoadAsync(planId, ct);

    private async Task<(Plan Plan, ScanJob Job, IReadOnlyList<ScanJob> Libraries, IReadOnlyList<PlanAction> Actions, PlanCheckContext Ctx)> LoadAsync(
        long planId, CancellationToken ct)
    {
        var plan = await _plans.GetAsync(planId, ct)
                   ?? throw new PlanException(PlanFailure.NotFound, $"Plan {planId} not found (see sfo plan create).");
        var job = await _jobs.GetByIdAsync(plan.JobId, ct)
                  ?? throw new JobReadException(ReadFailure.JobNotFound, $"Job {plan.JobId} of plan {planId} not found.");
        var libraries = new List<ScanJob>();
        foreach (var id in plan.Libraries)
            if (await _jobs.GetByIdAsync(id, ct) is { Role: ScanRole.Library } library)
                libraries.Add(library);
        var set = libraries.Count > 0
            ? (await LibraryComparisonSet.LoadAsync(_comparisons, job.Id, libraries.Select(l => l.Id).ToList(), ct)).Set
            : null;
        var actions = await _plans.GetActionsAsync(planId, ct);
        return (plan, job, libraries, actions, await ContextAsync(job, libraries, set, ct));
    }

    private async Task<PlanCheckContext> ContextAsync(ScanJob job, IReadOnlyList<ScanJob> libraries, LibraryComparisonSet? set, CancellationToken ct)
    {
        var allLibraries = (await _jobs.GetAllAsync(ct)).Where(j => j.Role == ScanRole.Library).Select(j => j.RootPath).ToList();
        var backedUp = new HashSet<string>(
            (set?.Files ?? []).Where(f => f.State == ContentState.BackedUp).Select(f => f.FullPath), StringComparer.Ordinal);
        return new PlanCheckContext(
            job.RootPath,
            libraries.ToDictionary(l => l.Id, l => l.RootPath),
            allLibraries,
            _options.ManagedAreas.Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
            _protected.Get().Concat(_options.ProtectedPlaces).Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
            backedUp,
            path => File.Exists(path) || Directory.Exists(path));
    }

    private static PlanState Status(Plan plan, ScanJob job, IReadOnlyList<ScanJob> libraries, IReadOnlyList<PlanAction> actions, PlanCheckContext ctx)
    {
        var problems = PlanChecks.CheckAll(actions, ctx);
        return new PlanState(plan, job, libraries,
            actions.Select(a => new PlanActionStatus(a, problems[a.Id], PlanChecks.IntoManagedArea(a, ctx))).ToList());
    }

    private static PlanAction Copy(PlanAction a) => new()
    {
        Id = a.Id, PlanId = a.PlanId, FindingKey = a.FindingKey, FindingKind = a.FindingKind, Unit = a.Unit,
        SourceKind = a.SourceKind, SourcePath = a.SourcePath, RelativePath = a.RelativePath, FileNodeId = a.FileNodeId,
        DirectoryNodeId = a.DirectoryNodeId, Files = a.Files, Bytes = a.Bytes, Kind = a.Kind, Recommended = a.Recommended,
        TargetPath = a.TargetPath, LibraryJobId = a.LibraryJobId, Reason = a.Reason, Note = a.Note, Origin = a.Origin,
        State = a.State, Sensitive = a.Sensitive, SingleDecision = a.SingleDecision, DecidedBy = a.DecidedBy,
        DecidedAt = a.DecidedAt, UpdatedAt = a.UpdatedAt
    };
}
