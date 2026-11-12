using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Phase 1 of the 3-phase AI pipeline: the directory verdict.
/// </summary>
/// <remarks>
/// <para>For every directory of the job a <see cref="DirectoryProfile"/> is computed from scan data, then
/// <see cref="DirectoryVerdictRules"/> decide the clear cases without a model call (part of a unit,
/// unit markers, skipped or hidden, empty, judged from subdirectories). Only the rest goes to Ollama,
/// which gets the profile with the file list and returns verdict, purpose, retention and reason together
/// with the sampling recommendation.</para>
/// <para>Directories are processed level by level from the root, so a directory the model judges as
/// <c>unit</c> spares all calls below it. Directories without direct files are decided last, bottom-up,
/// from their subdirectories.</para>
/// <para>Every directory ends with a row in <c>directory_verdicts</c>: a verdict or a stored error.
/// Rows with an error are retried on the next run.</para>
/// <para>The pre-assessment row in <c>ai_directory_results</c> is still written for AI-relevant directories.
/// Its sampling strategy now follows the verdict, not the model: units and system directories get
/// <c>skip</c> (their files are not analysed one by one), uniform directories with more eligible files than
/// the sample size get <see cref="StratifiedSample"/> (drawn in Phase 2 by <see cref="DirectorySamplingService"/>),
/// everything else <c>analyze_all</c>.</para>
/// </remarks>
public class DirectoryPreAssessmentService
{
    private const string PreAssessmentPhase = "pre_assessment";

    /// <summary><c>sampling_strategy</c> of a uniform directory with more eligible files than the sample size.</summary>
    public const string StratifiedSample = "stratified_sample";

    private readonly IDirectoryRepository _dirRepo;
    private readonly IFileRepository _fileRepo;
    private readonly IDirectoryClassificationRepository _dirClassRepo;
    private readonly IDirectoryVerdictRepository _verdictRepo;
    private readonly IOllamaService _ollama;
    private readonly Func<int> _getMaxParallelRequests;
    private readonly DirectoryAnalysisOptions _options;
    private readonly ILogger<DirectoryPreAssessmentService> _logger;

    public DirectoryPreAssessmentService(
        IDirectoryRepository dirRepo,
        IFileRepository fileRepo,
        IDirectoryClassificationRepository dirClassRepo,
        IDirectoryVerdictRepository verdictRepo,
        IOllamaService ollama,
        Func<int> getMaxParallelRequests,
        DirectoryAnalysisOptions options,
        ILogger<DirectoryPreAssessmentService> logger)
    {
        _dirRepo = dirRepo;
        _fileRepo = fileRepo;
        _dirClassRepo = dirClassRepo;
        _verdictRepo = verdictRepo;
        _ollama = ollama;
        _getMaxParallelRequests = getMaxParallelRequests;
        _options = options;
        _logger = logger;
    }

    public async Task<AiClassificationRunResult> RunAsync(
        long jobId,
        IProgress<AiClassificationProgress>? progress = null,
        Func<CancellationToken, Task>? waitIfPausedAsync = null,
        CancellationToken ct = default)
    {
        var allDirs = await _dirRepo.GetAllForJobOrderedByDepthAsync(jobId, ct);
        if (allDirs.Count == 0)
            return new AiClassificationRunResult(true, 0, 0, 0, "No directories found.");

        var tree = await JobTree.LoadAsync(jobId, allDirs, _verdictRepo, ct);
        var pending = allDirs.Where(d => !tree.HasVerdict(d.Id)).ToList();

        if (pending.Count == 0)
        {
            progress?.Report(new AiClassificationProgress(jobId, allDirs.Count, allDirs.Count, 0, string.Empty,
                "Phase 1/3: All directories already judged."));
            return new AiClassificationRunResult(true, allDirs.Count, allDirs.Count, 0, "All directories already judged.");
        }

        long processed = 0;
        var errorCount = 0;
        var modelCalls = 0;
        var workerCount = Math.Clamp(_getMaxParallelRequests(), 1, 8);
        var fromSubdirectories = new List<PreparedDirectory>();

        void Report(string path)
        {
            var done = Interlocked.Read(ref processed);
            progress?.Report(new AiClassificationProgress(
                jobId, done, pending.Count, Volatile.Read(ref errorCount), path,
                $"Phase 1/3: Judging directories… {done}/{pending.Count} (parallel={workerCount})"));
        }

        void Count(DirectoryVerdict verdict, string path)
        {
            if (verdict.Error != null)
            {
                _logger.LogWarning("Directory verdict error for {Path}: {Err}", path, verdict.Error);
                Interlocked.Increment(ref errorCount);
            }
            Interlocked.Increment(ref processed);
            Report(path);
        }

        // Top-down, one depth level at a time: rules first, then the model in parallel for the rest.
        foreach (var level in pending.GroupBy(d => d.Depth).OrderBy(g => g.Key))
        {
            var askModel = new List<PreparedDirectory>();

            foreach (var dir in level)
            {
                if (waitIfPausedAsync != null) await waitIfPausedAsync(ct);
                ct.ThrowIfCancellationRequested();

                var prepared = await PrepareAsync(jobId, dir, tree, ct);
                var (next, verdict) = DirectoryVerdictRules.Evaluate(
                    dir, prepared.Profile, prepared.UnitAncestor, prepared.UnitAncestorPath);

                switch (next)
                {
                    case DirectoryVerdictRules.Next.Decided:
                        await StoreRuleVerdictAsync(prepared, verdict!, tree, ct);
                        Count(verdict!, dir.FullPath);
                        break;
                    case DirectoryVerdictRules.Next.FromSubdirectories:
                        fromSubdirectories.Add(prepared);
                        break;
                    default:
                        askModel.Add(prepared);
                        break;
                }
            }

            await Parallel.ForEachAsync(
                askModel,
                new ParallelOptions { MaxDegreeOfParallelism = workerCount, CancellationToken = ct },
                async (prepared, token) =>
                {
                    if (waitIfPausedAsync != null) await waitIfPausedAsync(token);
                    Report(prepared.Directory.FullPath);
                    Interlocked.Increment(ref modelCalls);
                    var verdict = await AskModelAsync(prepared, tree, token);
                    Count(verdict, prepared.Directory.FullPath);
                });
        }

        // Bottom-up: directories without direct files, from their subdirectories.
        foreach (var prepared in fromSubdirectories.OrderByDescending(p => p.Directory.Depth))
        {
            ct.ThrowIfCancellationRequested();
            var verdict = await StoreFromSubdirectoriesAsync(prepared, tree, ct);
            Count(verdict, prepared.Directory.FullPath);
        }

        _logger.LogInformation(
            "Phase 1 for job {JobId}: {Judged} directories judged, {Calls} by the model, {Errors} error(s).",
            jobId, processed, modelCalls, errorCount);

        var completedMsg = errorCount > 0
            ? $"Phase 1/3 finished with {errorCount} error(s)."
            : "Phase 1/3: Directory verdicts complete.";

        progress?.Report(new AiClassificationProgress(
            jobId, processed, pending.Count, errorCount, string.Empty, completedMsg));

        return new AiClassificationRunResult(true, pending.Count, processed, errorCount, completedMsg);
    }

    /// <summary>Judges one directory again (focused rescan) and returns its pre-assessment.</summary>
    public async Task<DirectoryClassificationResult> PreAssessSingleAsync(
        long jobId,
        DirectoryNode dir,
        CancellationToken ct = default)
    {
        var allDirs = await _dirRepo.GetAllForJobOrderedByDepthAsync(jobId, ct);
        if (!allDirs.Any(d => d.Id == dir.Id))
            allDirs = [.. allDirs, dir];

        var tree = await JobTree.LoadAsync(jobId, allDirs, _verdictRepo, ct);
        var prepared = await PrepareAsync(jobId, dir, tree, ct);
        var (next, verdict) = DirectoryVerdictRules.Evaluate(
            dir, prepared.Profile, prepared.UnitAncestor, prepared.UnitAncestorPath);

        switch (next)
        {
            case DirectoryVerdictRules.Next.Decided:
                await StoreRuleVerdictAsync(prepared, verdict!, tree, ct);
                break;
            case DirectoryVerdictRules.Next.FromSubdirectories:
                await StoreFromSubdirectoriesAsync(prepared, tree, ct);
                break;
            default:
                await AskModelAsync(prepared, tree, ct);
                break;
        }

        return await _dirClassRepo.GetByNodeAndPhaseAsync(dir.Id, PreAssessmentPhase, ct)
            ?? NoFilesAssessment(dir);
    }

    private async Task<PreparedDirectory> PrepareAsync(long jobId, DirectoryNode dir, JobTree tree, CancellationToken ct)
    {
        var files = await _fileRepo.GetFileInfoForDirectoryAsync(jobId, dir.FullPath, ct);
        var children = tree.ChildrenOf(dir);
        var profile = DirectoryProfileBuilder.Build(files, children.Select(c => c.Name).ToList());
        var (unitAncestor, unitPath) = tree.FindUnitAncestor(dir);
        return new PreparedDirectory(dir, files, children, profile, unitAncestor, unitPath);
    }

    private async Task StoreRuleVerdictAsync(PreparedDirectory p, DirectoryVerdict verdict, JobTree tree, CancellationToken ct)
    {
        var dir = p.Directory;
        await _verdictRepo.UpsertAsync(verdict, ct);
        tree.Set(verdict);

        var handledAsWhole = verdict.Verdict is DirectoryVerdictKind.Unit or DirectoryVerdictKind.System;

        if (verdict.Rule == DirectoryVerdictRules.InsideUnit || !IsAiCandidate(dir))
        {
            // No pre-assessment of its own: the unit root (or nothing) stands for it. Its files are not analysed singly.
            if (handledAsWhole && p.Files.Count > 0)
                await _fileRepo.MarkAsSkippedAsync(p.Files.Select(f => f.Id).ToList(), ct);
            return;
        }

        var assessment = handledAsWhole
            ? new DirectoryClassificationResult
            {
                DirectoryNodeId = dir.Id,
                Phase = PreAssessmentPhase,
                Summary = verdict.Reason,
                Homogeneity = "high",
                DominantType = verdict.Verdict.ToCode(),
                SamplingStrategy = "skip",
                SampleSize = 0,
                AnalyzedAt = DateTime.UtcNow
            }
            : NoFilesAssessment(dir);

        await ApplyVerdictToFilesAsync(dir, p.Files, assessment, verdict, ct);
        await _dirClassRepo.UpsertAsync(assessment, ct);
    }

    private async Task<DirectoryVerdict> StoreFromSubdirectoriesAsync(PreparedDirectory p, JobTree tree, CancellationToken ct)
    {
        var childVerdicts = p.Children.Select(c => tree.Get(c.Id)).ToList();
        var verdict = DirectoryVerdictRules.EvaluateFromSubdirectories(p.Directory, p.Profile, childVerdicts);

        await _verdictRepo.UpsertAsync(verdict, ct);
        tree.Set(verdict);

        if (IsAiCandidate(p.Directory))
            await _dirClassRepo.UpsertAsync(NoFilesAssessment(p.Directory), ct);
        return verdict;
    }

    private async Task<DirectoryVerdict> AskModelAsync(PreparedDirectory p, JobTree tree, CancellationToken ct)
    {
        var dir = p.Directory;
        var input = new DirectoryPreAssessmentInput
        {
            Directory = dir,
            DirectFiles = p.Files,
            SubdirectoryNames = p.Children.Select(c => c.Name).ToList(),
            Profile = p.Profile
        };

        DirectoryPreAssessmentOutcome outcome;
        try
        {
            outcome = await _ollama.PreAssessDirectoryAsync(input, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The service contract is to return errors, but a failing call must still leave a stored error.
            var error = $"Pre-assessment failed: {ex.Message}";
            outcome = new DirectoryPreAssessmentOutcome(
                new DirectoryClassificationResult { Phase = PreAssessmentPhase, AnalyzedAt = DateTime.UtcNow, Error = error },
                new DirectoryVerdict { Source = VerdictSource.Model, JudgedAt = DateTime.UtcNow, Error = error });
        }

        var assessment = outcome.Assessment;
        assessment.DirectoryNodeId = dir.Id;
        assessment.Phase = PreAssessmentPhase;

        var verdict = outcome.Verdict;
        verdict.DirectoryNodeId = dir.Id;
        verdict.Source = VerdictSource.Model;
        verdict.Rule = null;
        verdict.Profile = p.Profile;
        if (verdict.Verdict is null && verdict.Error is null)
            verdict.Error = "The model answer contained no valid verdict.";
        if (verdict.Error is not null)
            verdict.Verdict = null;
        verdict.UnitDirectoryNodeId = verdict.Verdict == DirectoryVerdictKind.Unit ? dir.Id : null;

        if (IsAiCandidate(dir))
        {
            if (assessment.Error == null)
                await ApplyVerdictToFilesAsync(dir, p.Files, assessment, verdict, ct);
            await _dirClassRepo.UpsertAsync(assessment, ct);
        }

        await _verdictRepo.UpsertAsync(verdict, ct);
        tree.Set(verdict);
        return verdict;
    }

    /// <summary>Directories Phase 2 and Phase 3 work on; the others get a verdict only.</summary>
    private static bool IsAiCandidate(DirectoryNode d) =>
        d.DirStatus != DirectoryStatus.Skip
        && !string.IsNullOrWhiteSpace(d.Name)
        && !d.Name.StartsWith('.');

    private static DirectoryClassificationResult NoFilesAssessment(DirectoryNode dir) => new()
    {
        DirectoryNodeId = dir.Id,
        Phase = PreAssessmentPhase,
        Summary = "Directory contains no direct files.",
        Homogeneity = "high",
        DominantType = "empty",
        SamplingStrategy = "analyze_all",
        SampleSize = 0,
        AnalyzedAt = DateTime.UtcNow,
        Error = null
    };

    /// <summary>
    /// What the verdict means for the files of a model-judged directory. The sampling fields of the model
    /// answer are no longer followed: they could leave files without any result.
    /// <list type="bullet">
    /// <item>unit or system: no file is analysed (Skipped); the directory carries the result.</item>
    /// <item>uniform: Phase 2 draws a stratified sample (<see cref="DirectorySamplingService"/>).</item>
    /// <item>mixed, no valid verdict, or "empty" for a directory that has files: every file is judged.</item>
    /// </list>
    /// </summary>
    private async Task ApplyVerdictToFilesAsync(
        DirectoryNode dir,
        IReadOnlyList<DirectoryFileInfo> fileInfos,
        DirectoryClassificationResult assessment,
        DirectoryVerdict verdict,
        CancellationToken ct)
    {
        if (fileInfos.Count == 0) return;

        switch (verdict.Verdict)
        {
            case DirectoryVerdictKind.Unit or DirectoryVerdictKind.System:
                assessment.SamplingStrategy = "skip";
                assessment.SampleSize = 0;
                await _fileRepo.MarkAsSkippedAsync(fileInfos.Select(f => f.Id).ToList(), ct);
                _logger.LogDebug("Skipped all {Count} files in {Path} ({Verdict})", fileInfos.Count, dir.FullPath, verdict.Verdict);
                break;
            case DirectoryVerdictKind.Uniform:
                var eligible = fileInfos.Count(f => DirectorySamplingService.IsEligible((FileType)f.FileType));
                var sampled = eligible > _options.EffectiveSampleSize;
                assessment.SamplingStrategy = sampled ? StratifiedSample : "analyze_all";
                assessment.SampleSize = sampled ? _options.EffectiveSampleSize : eligible;
                break;
            default:
                assessment.SamplingStrategy = "analyze_all";
                assessment.SampleSize = fileInfos.Count;
                break;
        }

        // Resolve anomaly names to ids for later use (the sample takes them as outliers).
        var anomalousNames = ParseAnomalousNames(assessment.AnomalousFileIds);
        if (anomalousNames.Count > 0)
        {
            var resolved = fileInfos
                .Where(f => anomalousNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
                .Select(f => new { id = f.Id, name = f.Name })
                .ToList();
            assessment.AnomalousFileIds = JsonSerializer.Serialize(resolved);
        }
    }

    private static HashSet<string> ParseAnomalousNames(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var nameProp))
                    names.Add(nameProp.GetString() ?? string.Empty);
            }
            return names;
        }
        catch
        {
            return [];
        }
    }

    private sealed record PreparedDirectory(
        DirectoryNode Directory,
        IReadOnlyList<DirectoryFileInfo> Files,
        IReadOnlyList<DirectoryNode> Children,
        DirectoryProfile Profile,
        DirectoryVerdict? UnitAncestor,
        string? UnitAncestorPath);

    /// <summary>All directories of a job with their verdicts so far; safe for the parallel model calls.</summary>
    private sealed class JobTree
    {
        private readonly Dictionary<string, DirectoryNode> _byPath;
        private readonly Dictionary<string, List<DirectoryNode>> _children;
        private readonly ConcurrentDictionary<long, DirectoryVerdict> _verdicts;

        private JobTree(IReadOnlyList<DirectoryNode> dirs, IEnumerable<DirectoryVerdict> verdicts)
        {
            _byPath = new Dictionary<string, DirectoryNode>(StringComparer.Ordinal);
            foreach (var d in dirs) _byPath[d.FullPath] = d;

            _children = dirs
                .Where(d => !string.IsNullOrEmpty(d.ParentPath))
                .GroupBy(d => d.ParentPath, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(d => d.Name, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

            _verdicts = new ConcurrentDictionary<long, DirectoryVerdict>(verdicts.ToDictionary(v => v.DirectoryNodeId));
        }

        public static async Task<JobTree> LoadAsync(
            long jobId, IReadOnlyList<DirectoryNode> dirs, IDirectoryVerdictRepository repo, CancellationToken ct) =>
            new(dirs, await repo.GetForJobAsync(jobId, ct));

        public bool HasVerdict(long id) => _verdicts.TryGetValue(id, out var v) && v.Error == null;

        public DirectoryVerdict? Get(long id) => _verdicts.TryGetValue(id, out var v) ? v : null;

        public void Set(DirectoryVerdict v) => _verdicts[v.DirectoryNodeId] = v;

        public IReadOnlyList<DirectoryNode> ChildrenOf(DirectoryNode dir) =>
            _children.TryGetValue(dir.FullPath, out var list) ? list : [];

        /// <summary>Nearest ancestor judged <c>unit</c>, with its path relative to the scan root.</summary>
        public (DirectoryVerdict? Verdict, string? RelativePath) FindUnitAncestor(DirectoryNode dir)
        {
            var parentPath = dir.ParentPath;
            var guard = 0;
            while (!string.IsNullOrEmpty(parentPath) && _byPath.TryGetValue(parentPath, out var parent) && guard++ < 512)
            {
                if (_verdicts.TryGetValue(parent.Id, out var v) && v.Verdict == DirectoryVerdictKind.Unit)
                    return (v, parent.RelativePath);
                parentPath = parent.ParentPath;
            }
            return (null, null);
        }
    }
}
