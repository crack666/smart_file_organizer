using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Decisions;

/// <summary>What <see cref="DecisionService.DecideAsync"/> works with.</summary>
public sealed record DecideOptions
{
    /// <summary>The target structure to propose targets from; none: worth only, no targets.</summary>
    public TargetStructure? Targets { get; init; }

    /// <summary>Another proposer instead of the target structure (the content check of Issue 56 plugs in here).</summary>
    public ITargetProposer? Proposer { get; init; }
}

/// <summary>One stage 2 run: the stored decisions, the ones they replaced, and the findings that rest on the same worth.</summary>
public sealed record DecisionRun(
    ScanJob Job,
    DateTime DecidedAt,
    IReadOnlyList<FileDecision> Decisions,
    IReadOnlyDictionary<long, FileDecision> Previous,
    IReadOnlyDictionary<long, FileNode> Files,
    FindingReport Findings)
{
    /// <summary>Decisions whose worth or target differ from the stored ones (all of them on a first run).</summary>
    public IEnumerable<FileDecision> Changed => Decisions.Where(d => !Previous.TryGetValue(d.FileNodeId, out var p) || DiffersFrom(d, p));

    public static bool DiffersFrom(FileDecision now, FileDecision before) =>
        now.Worth != before.Worth || now.Certainty != before.Certainty || now.Target?.Path != before.Target?.Path
        || now.Target?.TargetId != before.Target?.TargetId;
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

    public DecisionService(FindingService findings, IFileDecisionRepository decisions)
    {
        _findings = findings;
        _decisions = decisions;
    }

    /// <exception cref="JobReadException">The job does not exist.</exception>
    /// <exception cref="FindingException">The job is a library.</exception>
    public async Task<DecisionRun> DecideAsync(long jobId, DecideOptions? options = null, CancellationToken ct = default)
    {
        options ??= new DecideOptions();
        var (report, rows) = await _findings.ComputeValuesAsync(jobId, ct);
        var previous = await _decisions.GetByJobAsync(jobId, ct);
        var proposer = options.Proposer ?? (options.Targets is { } targets ? new TargetStructureProposer(targets) : null);

        var now = DateTime.UtcNow;
        var decisions = rows.Select(r => DecisionRules.Decide(r, proposer, now)).ToList();
        await _decisions.ReplaceAsync(jobId, decisions, ct);
        return new DecisionRun(report.Job, now, decisions, previous, rows.ToDictionary(r => r.File.Id, r => r.File), report);
    }

    /// <summary>The stored decisions of a job (empty before the first run).</summary>
    public Task<IReadOnlyDictionary<long, FileDecision>> GetStoredAsync(long jobId, CancellationToken ct = default) =>
        _decisions.GetByJobAsync(jobId, ct);
}
