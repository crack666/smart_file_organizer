using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// The staged part of Phase 2 (https://github.com/crack666/smart_file_organizer/issues/8): draws the sample
/// of every uniform directory, and once its members are judged, either lets the other files inherit or raises
/// the directory to mixed so that every file is judged on its own.
/// </summary>
/// <remarks>
/// <para>Every step is stored before the next one starts and each state change is one transaction
/// (<see cref="IDirectorySampleRepository"/>), so a stopped run resumes where it stopped and never leaves a
/// directory half inherited. Planning skips directories that already have a sample; resolving waits until
/// no member is pending.</para>
/// <para>Mixed directories and directories without a verdict are not touched here: their files stay
/// Discovered and are judged one by one, which is also the fallback whenever anything is unclear.</para>
/// </remarks>
public class DirectorySamplingService
{
    private const string PreAssessmentPhase = "pre_assessment";

    private readonly IFileRepository _fileRepo;
    private readonly IDirectoryVerdictRepository _verdictRepo;
    private readonly IDirectorySampleRepository _sampleRepo;
    private readonly IDirectoryClassificationRepository _dirClassRepo;
    private readonly DirectoryAnalysisOptions _options;
    private readonly ILogger<DirectorySamplingService> _logger;

    public DirectorySamplingService(
        IFileRepository fileRepo,
        IDirectoryVerdictRepository verdictRepo,
        IDirectorySampleRepository sampleRepo,
        IDirectoryClassificationRepository dirClassRepo,
        DirectoryAnalysisOptions options,
        ILogger<DirectorySamplingService> logger)
    {
        _fileRepo = fileRepo;
        _verdictRepo = verdictRepo;
        _sampleRepo = sampleRepo;
        _dirClassRepo = dirClassRepo;
        _options = options;
        _logger = logger;
    }

    /// <summary>What the classification needs to know per file.</summary>
    /// <param name="KeepHintDirectories">Full paths of directories whose files get a keep hint from the model:
    /// mixed ones, and uniform ones, so that the hint is already there if the sample escalates.</param>
    /// <param name="Verdicts">Valid verdicts by directory full path, for the second check of a file against its directory.</param>
    public sealed record Context(IReadOnlySet<string> KeepHintDirectories, IReadOnlyDictionary<string, DirectoryVerdict> Verdicts)
    {
        public DirectoryVerdict? VerdictOf(FileNode file) => Verdicts.GetValueOrDefault(file.ParentPath);
    }

    /// <param name="Released">Deferred files of confirmed samples that do not inherit and wait to be judged one by one.</param>
    public sealed record ResolveResult(int Confirmed, int Escalated, int Inherited, int StillOpen, int Released = 0);

    public static bool IsEligible(FileType t) => t is FileType.Image or FileType.Video or FileType.Document;

    // ------------------------------------------------------------------ plan

    /// <summary>Draws a sample for every uniform directory of the job that has none yet.</summary>
    public async Task<int> PlanAsync(long jobId, Func<CancellationToken, Task>? waitIfPausedAsync = null, CancellationToken ct = default)
    {
        var verdicts = await _verdictRepo.GetForJobAsync(jobId, ct);
        var sampled = (await _sampleRepo.GetForJobAsync(jobId, ct)).Select(s => s.DirectoryNodeId).ToHashSet();

        var drawn = 0;
        foreach (var v in verdicts.Where(v => v.Error is null && v.Verdict == DirectoryVerdictKind.Uniform
                                              && !sampled.Contains(v.DirectoryNodeId)))
        {
            if (waitIfPausedAsync != null) await waitIfPausedAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (await PlanDirectoryAsync(jobId, v, ct)) drawn++;
        }
        return drawn;
    }

    /// <summary>Forgets the sample of a directory that is judged again (focused rescan).</summary>
    public Task ForgetAsync(long directoryNodeId, CancellationToken ct = default) => _sampleRepo.DeleteAsync(directoryNodeId, ct);

    /// <summary>Draws the sample of one directory if it is uniform and has none yet.</summary>
    public async Task<bool> PlanDirectoryAsync(long jobId, long directoryNodeId, CancellationToken ct = default)
    {
        var verdict = await _verdictRepo.GetByNodeIdAsync(directoryNodeId, ct);
        if (verdict is null || verdict.Error is not null || verdict.Verdict != DirectoryVerdictKind.Uniform) return false;
        if (await _sampleRepo.GetByNodeIdAsync(directoryNodeId, ct) is not null) return false;
        return await PlanDirectoryAsync(jobId, verdict, ct);
    }

    private async Task<bool> PlanDirectoryAsync(long jobId, DirectoryVerdict verdict, CancellationToken ct)
    {
        var fullPath = verdict.DirectoryFullPath ?? string.Empty;
        var files = await _fileRepo.GetByDirectoryAsync(jobId, fullPath, ct);
        var eligible = files.Where(f => IsEligible(f.FileType)).ToList();
        if (eligible.Count == 0) return false;

        var seed = DirectorySampler.Seed(jobId, verdict.DirectoryRelativePath ?? fullPath);
        var anomalies = await AnomalousNamesAsync(verdict.DirectoryNodeId, ct);
        var draw = DirectorySampler.Draw(eligible, seed, _options.EffectiveSampleSize, anomalies);

        var sample = new DirectorySample
        {
            DirectoryNodeId = verdict.DirectoryNodeId,
            Seed = seed,
            State = SampleState.Open,
            EligibleCount = eligible.Count,
            Members = draw.Members.ToList(),
            MaxDeviationShare = _options.MaxDeviationShare,
            DrawnAt = DateTime.UtcNow
        };

        if (draw.NotSampleableReason is { } why)
        {
            // Too varied for a sample: mixed right away, every file stays Discovered and is judged on its own.
            sample.State = SampleState.Escalated;
            sample.Reason = why;
            sample.DecidedAt = DateTime.UtcNow;
            await _sampleRepo.EscalateAsync(sample, why, ct);
            _logger.LogInformation("Uniform directory {Path} raised to mixed before sampling: {Reason}", fullPath, why);
            return true;
        }

        var memberIds = draw.Members.Select(m => m.FileNodeId).ToHashSet();
        var deferred = eligible
            .Where(f => !memberIds.Contains(f.Id) && f.Status == FileNodeStatus.Discovered)
            .Select(f => f.Id)
            .ToList();

        await _sampleRepo.CreateAsync(sample, deferred, ct);
        _logger.LogDebug("Sample of {Path}: {Members} of {Eligible} files, {Deferred} deferred (seed {Seed})",
            fullPath, draw.Members.Count, eligible.Count, deferred.Count, seed);
        return true;
    }

    private async Task<IReadOnlySet<string>> AnomalousNamesAsync(long directoryNodeId, CancellationToken ct)
    {
        var pre = await _dirClassRepo.GetByNodeAndPhaseAsync(directoryNodeId, PreAssessmentPhase, ct);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(pre?.AnomalousFileIds)) return names;
        try
        {
            using var doc = JsonDocument.Parse(pre.AnomalousFileIds);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return names;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var n) && n.GetString() is { } s)
                    names.Add(s);
            }
        }
        catch (JsonException)
        {
            // An unreadable anomaly list only means fewer hand-picked outliers.
        }
        return names;
    }

    // ------------------------------------------------------------------ context

    public async Task<Context> GetContextAsync(long jobId, CancellationToken ct = default)
    {
        var verdicts = await _verdictRepo.GetForJobAsync(jobId, ct);
        var keepDirs = verdicts
            .Where(v => v.Verdict is DirectoryVerdictKind.Mixed or DirectoryVerdictKind.Uniform && v.DirectoryFullPath != null)
            .Select(v => v.DirectoryFullPath!)
            .ToHashSet(StringComparer.Ordinal);
        var byPath = new Dictionary<string, DirectoryVerdict>(StringComparer.Ordinal);
        foreach (var v in verdicts.Where(v => v.Error is null && v.DirectoryFullPath != null))
            byPath[v.DirectoryFullPath!] = v;
        return new Context(keepDirs, byPath);
    }

    // ------------------------------------------------------------------ resolve

    /// <summary>Evaluates every open sample whose members are all judged.</summary>
    public async Task<ResolveResult> ResolveAsync(long jobId, Func<CancellationToken, Task>? waitIfPausedAsync = null, CancellationToken ct = default)
    {
        int confirmed = 0, escalated = 0, inherited = 0, open = 0, released = 0;
        foreach (var sample in (await _sampleRepo.GetForJobAsync(jobId, ct)).Where(s => s.State == SampleState.Open))
        {
            if (waitIfPausedAsync != null) await waitIfPausedAsync(ct);
            ct.ThrowIfCancellationRequested();

            var (outcome, freed) = await ResolveSampleAsync(jobId, sample, ct);
            released += freed;
            switch (outcome)
            {
                case SampleState.Confirmed: confirmed++; inherited += sample.InheritedCount; break;
                case SampleState.Escalated: escalated++; break;
                default: open++; break;
            }
        }

        if (confirmed + escalated > 0)
        {
            _logger.LogInformation(
                "Samples of job {JobId}: {Confirmed} confirmed ({Inherited} files inherited, {Released} released to be judged), " +
                "{Escalated} raised to mixed, {Open} still open.",
                jobId, confirmed, inherited, released, escalated, open);
        }
        return new ResolveResult(confirmed, escalated, inherited, open, released);
    }

    /// <summary>Evaluates the sample of one directory if it is open and complete; returns the resulting state.</summary>
    public async Task<SampleState?> ResolveDirectoryAsync(long jobId, long directoryNodeId, CancellationToken ct = default)
    {
        var sample = await _sampleRepo.GetByNodeIdAsync(directoryNodeId, ct);
        if (sample is null) return null;
        return sample.State == SampleState.Open ? (await ResolveSampleAsync(jobId, sample, ct)).State : sample.State;
    }

    /// <returns>The new state, and how many Deferred files were released to be judged one by one.</returns>
    private async Task<(SampleState State, int Released)> ResolveSampleAsync(long jobId, DirectorySample sample, CancellationToken ct)
    {
        var files = await _fileRepo.GetByDirectoryAsync(jobId, sample.DirectoryFullPath ?? string.Empty, ct);
        var byId = files.ToDictionary(f => f.Id);

        if (sample.Members.Any(m => byId.TryGetValue(m.FileNodeId, out var f)
                                    && f.Status is FileNodeStatus.Discovered or FileNodeStatus.Processing))
            return (SampleState.Open, 0);

        var results = sample.Members.ToDictionary(
            m => m.FileNodeId,
            m => byId.TryGetValue(m.FileNodeId, out var f) ? f.Classification : null);
        var eval = DirectorySampler.Evaluate(sample.Members, results, _options, sample.EligibleCount);

        sample.MajorityCategory = eval.Majority;
        sample.DeviatingCount = eval.Deviating;
        sample.FailedCount = eval.Failed;
        sample.DeviationShare = eval.Share;
        sample.MaxDeviationShare = _options.MaxDeviationShare;
        sample.Reason = eval.Reason;
        sample.DecidedAt = DateTime.UtcNow;

        if (!eval.Confirmed)
        {
            sample.State = SampleState.Escalated;
            await _sampleRepo.EscalateAsync(sample, eval.Reason, ct);
            _logger.LogInformation("Sample of {Path} contradicts the uniform verdict, raised to mixed: {Reason}",
                sample.DirectoryRelativePath, eval.Reason);
            return (SampleState.Escalated, 0);
        }

        sample.State = SampleState.Confirmed;
        if (!eval.Inherit || eval.Majority is null)
        {
            // The verdict stands, but the other files are judged one by one: ConfirmAsync releases what does not inherit.
            sample.InheritedConfidence = null;
            var released = files.Count(f => f.Status == FileNodeStatus.Deferred);
            await _sampleRepo.ConfirmAsync(sample, [], ct);
            if (released > 0)
            {
                _logger.LogInformation("Sample of {Path} confirmed, but {Count} file(s) are judged one by one: {Reason}",
                    sample.DirectoryRelativePath, released, eval.Reason);
            }
            return (SampleState.Confirmed, released);
        }

        sample.InheritedConfidence = eval.InheritedConfidence;
        var dirName = Path.GetFileName((sample.DirectoryFullPath ?? string.Empty).TrimEnd('/', '\\'));
        var now = DateTime.UtcNow;
        var inherited = files
            .Where(f => f.Status == FileNodeStatus.Deferred)
            .Select(f =>
            {
                var c = new FileClassification
                {
                    FileNodeId = f.Id,
                    Category = eval.Majority!.Value,
                    Importance = eval.InheritedImportance,
                    Confidence = eval.InheritedConfidence,
                    Summary = $"Not looked at: inherited from the sample of '{dirName}' " +
                              $"({sample.Members.Count(m => m.Category == eval.Majority)} of {sample.Members.Count} are {eval.Majority}).",
                    SuggestedTarget = eval.InheritedTarget,
                    ModelUsed = null,
                    AnalyzedAt = now,
                    IsAiResult = false,
                    Origin = ResultOrigin.Inherited,
                    InheritedFromDirectoryNodeId = sample.DirectoryNodeId,
                    // Not looked at, but the confirmed sample agrees; never certain.
                    Certainty = ResultCertainty.Likely
                };
                // Defensive: marked files are always members, but a marked file must never inherit a trash category.
                ClassificationService.ApplySensitiveReviewRule(f, c);
                return c;
            })
            .ToList();

        await _sampleRepo.ConfirmAsync(sample, inherited, ct);
        return (SampleState.Confirmed, 0);
    }
}
