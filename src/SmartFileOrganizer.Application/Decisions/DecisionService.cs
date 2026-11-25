using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Decisions;

/// <summary>What <see cref="DecisionService.DecideAsync"/> works with.</summary>
public sealed record DecideOptions
{
    /// <summary>The target structure to propose targets from; none: worth only, no targets.</summary>
    public TargetStructure? Targets { get; init; }

    /// <summary>The library job whose root the target structure lies in (<c>--library</c>); null to find it (<see cref="TargetRoot.Of"/>).</summary>
    public long? Library { get; init; }

    /// <summary>Another proposer instead of the target structure (the content check of Issue 56 plugs in here).</summary>
    public ITargetProposer? Proposer { get; init; }
}

/// <summary>One stage 2 run: the stored decisions, the ones they replaced, and the findings that rest on the same worth.</summary>
/// <param name="TargetRoot">Where the targets of the structure lie; null without a target structure.</param>
/// <param name="Room">Room at that root; null without a target structure.</param>
public sealed record DecisionRun(
    ScanJob Job,
    DateTime DecidedAt,
    IReadOnlyList<FileDecision> Decisions,
    IReadOnlyDictionary<long, FileDecision> Previous,
    IReadOnlyDictionary<long, FileNode> Files,
    FindingReport Findings,
    TargetRoot? TargetRoot = null,
    TargetRoom? Room = null)
{
    /// <summary>Decisions whose worth or target differ from the stored ones (all of them on a first run).</summary>
    public IEnumerable<FileDecision> Changed => Decisions.Where(d => !Previous.TryGetValue(d.FileNodeId, out var p) || DiffersFrom(d, p));

    public static bool DiffersFrom(FileDecision now, FileDecision before) =>
        now.Worth != before.Worth || now.Certainty != before.Certainty || now.Target?.Path != before.Target?.Path
        || now.Target?.TargetId != before.Target?.TargetId || now.Target?.LibraryJobId != before.Target?.LibraryJobId;
}

/// <summary>
/// Stage 2, deciding (https://github.com/crack666/smart_file_organizer/issues/37): worth and target of every file of a
/// source job, computed by rules from what stage 1 stored (facts, results, folder verdicts) and from bundles and the
/// comparison, and stored in <c>file_decisions</c>. Has no model service: a new target structure or new rules need
/// only this run, never a new description. Reads no scanned file and changes nothing on disk.
/// </summary>
public sealed class DecisionService
{
    private readonly FindingService _findings;
    private readonly IFileDecisionRepository _decisions;
    private readonly IScanJobRepository _jobs;
    private readonly IContentComparisonRepository _comparisons;
    private readonly IFreeSpaceProbe? _freeSpace;

    public DecisionService(
        FindingService findings, IFileDecisionRepository decisions, IScanJobRepository jobs, IContentComparisonRepository comparisons,
        IFreeSpaceProbe? freeSpace = null)
    {
        _findings = findings;
        _decisions = decisions;
        _jobs = jobs;
        _comparisons = comparisons;
        _freeSpace = freeSpace;
    }

    /// <exception cref="JobReadException">The job does not exist.</exception>
    /// <exception cref="FindingException">The job is a library.</exception>
    /// <exception cref="TargetRootException">The library named is none, or the structure's root does not lie in it.</exception>
    public async Task<DecisionRun> DecideAsync(long jobId, DecideOptions? options = null, CancellationToken ct = default)
    {
        options ??= new DecideOptions();
        var (report, rows) = await _findings.ComputeValuesAsync(jobId, ct);
        var previous = await _decisions.GetByJobAsync(jobId, ct);
        var proposer = options.Proposer ?? (options.Targets is { } targets ? new TargetStructureProposer(targets) : null);

        var root = options.Targets is { } structure ? await RootAsync(structure, report, options.Library, ct) : null;

        var now = DateTime.UtcNow;
        var decisions = rows.Select(r => DecisionRules.Decide(r, proposer, now)).ToList();
        // A structure gives relative paths; here they get the library root they lie in, or go when that is no library.
        if (root is not null && options.Proposer is null)
            decisions = decisions.Select(d => d with { Target = root.Place(d.Target) }).ToList();
        await _decisions.ReplaceAsync(jobId, decisions, ct);

        var files = rows.ToDictionary(r => r.File.Id, r => r.File);
        return new DecisionRun(report.Job, now, decisions, previous, files, report, root,
            root is null ? null : await RoomAsync(root, report, decisions, files, ct));
    }

    private async Task<TargetRoot> RootAsync(TargetStructure structure, FindingReport report, long? library, CancellationToken ct)
    {
        ScanJob? named = null;
        if (library is { } id)
        {
            named = await _jobs.GetByIdAsync(id, ct);
            if (named is not { Role: ScanRole.Library, Status: JobStatus.Completed })
                throw new TargetRootException($"Job {id} is no completed library job (sfo scan <path> --role library).");
        }
        var libraries = (await _jobs.GetAllAsync(ct)).Where(j => j is { Role: ScanRole.Library, Status: JobStatus.Completed }).ToList();
        return TargetRoot.Of(structure, libraries, report.Inputs.LibraryJobIds ?? [], named);
    }

    /// <summary>What the targets need: the files with a target no library holds yet (one that does, as in <c>sfo library match</c>, needs no room).</summary>
    private async Task<TargetRoom> RoomAsync(
        TargetRoot root, FindingReport report, IReadOnlyList<FileDecision> decisions, IReadOnlyDictionary<long, FileNode> files, CancellationToken ct)
    {
        var (set, _) = await LibraryComparisonSet.LoadAsync(_comparisons, report.Job.Id, report.Inputs.LibraryJobIds, ct);
        var placed = decisions.Where(d => d.Target is not null && set?.Rows.GetValueOrDefault(d.FileNodeId)?.State != ContentState.BackedUp).ToList();
        return TargetRoom.Of(root, placed.Count, placed.Sum(d => files[d.FileNodeId].Size), p => _freeSpace?.AvailableBytes(p));
    }

    /// <summary>The stored decisions of a job (empty before the first run).</summary>
    public Task<IReadOnlyDictionary<long, FileDecision>> GetStoredAsync(long jobId, CancellationToken ct = default) =>
        _decisions.GetByJobAsync(jobId, ct);
}
