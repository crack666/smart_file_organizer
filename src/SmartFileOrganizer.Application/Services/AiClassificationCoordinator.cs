using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Application.Services;

public class AiClassificationCoordinator
{
    private readonly ClassificationService _classificationService;
    private readonly DirectoryPreAssessmentService _preAssessmentService;
    private readonly DirectorySummaryService _directorySummaryService;
    private readonly IOllamaService _ollama;
    private readonly ILogger<AiClassificationCoordinator> _logger;
    private readonly SensitiveRulesBackfill? _rulesBackfill;
    private readonly ModelServerOptions _serverOptions;
    private readonly IModelServerWaitRepository? _waits;
    private readonly TimeProvider _time;
    private readonly BackedUpSkipService? _backedUpSkip;

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _pauseRequested;
    private long _activeJobId;

    public event EventHandler<AiClassificationProgress>? ProgressChanged;
    public event EventHandler<AiProcessingStateChanged>? StateChanged;

    public bool IsRunning => _runTask != null && !_runTask.IsCompleted && !_pauseRequested;
    public bool IsPaused => _runTask != null && !_runTask.IsCompleted && _pauseRequested;
    public long ActiveJobId => _activeJobId;

    public AiClassificationCoordinator(
        ClassificationService classificationService,
        DirectoryPreAssessmentService preAssessmentService,
        DirectorySummaryService directorySummaryService,
        IOllamaService ollama,
        ILogger<AiClassificationCoordinator> logger,
        SensitiveRulesBackfill? rulesBackfill = null,
        ModelServerOptions? serverOptions = null,
        IModelServerWaitRepository? waits = null,
        TimeProvider? time = null,
        BackedUpSkipService? backedUpSkip = null)
    {
        _backedUpSkip = backedUpSkip;
        _rulesBackfill = rulesBackfill;
        _classificationService = classificationService;
        _preAssessmentService = preAssessmentService;
        _directorySummaryService = directorySummaryService;
        _ollama = ollama;
        _logger = logger;
        _serverOptions = serverOptions ?? new ModelServerOptions();
        _waits = waits;
        _time = time ?? TimeProvider.System;
    }

    public Task StartOrResumeAsync(long jobId, CancellationToken appCt = default)
    {
        lock (_sync)
        {
            if (_runTask != null && !_runTask.IsCompleted)
            {
                if (_pauseRequested && _activeJobId == jobId)
                {
                    _pauseRequested = false;
                    StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Running, "AI classification resumed."));
                }

                return Task.CompletedTask;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
            _activeJobId = jobId;
            _pauseRequested = false;
            StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Starting, "Starting AI classification…"));

            var progress = new Progress<AiClassificationProgress>(p => ProgressChanged?.Invoke(this, p));

            _runTask = Task.Run(async () =>
            {
                try
                {
                    StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Running, "AI classification running…"));

                    var run = await RunPhasesAsync(
                        jobId,
                        AiPhases.All,
                        phase => StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Running, PhaseStartText(phase))),
                        _ => progress,
                        WaitIfPausedAsync,
                        (phase, wait) => StateChanged?.Invoke(this, WaitStateChange(jobId, phase, wait)),
                        _cts.Token);

                    if (!run.OllamaAvailable)
                    {
                        StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Unavailable, "Ollama is not available."));
                        return;
                    }

                    if (run.Lost is { } lost)
                    {
                        StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Unavailable,
                            $"The model server did not answer for {lost.Waited.TotalMinutes:F0} min; the analysis stopped. " +
                            "Files not judged stay open: start the analysis again when the server is back."));
                        return;
                    }

                    var totalErrors = run.ErrorCount;
                    var finalMsg = totalErrors > 0
                        ? $"AI classification finished with {totalErrors} error(s)."
                        : "AI classification completed.";

                    StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Completed, finalMsg));
                }
                catch (OperationCanceledException)
                {
                    StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Cancelled, "AI classification cancelled."));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AI classification failed for job {JobId}.", jobId);
                    StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Failed, "AI classification failed.", ex.Message));
                }
            }, _cts.Token);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs the selected phases in the caller's flow and returns when they are done, for hosts
    /// without an event loop (CLI, tests). Independent of <see cref="StartOrResumeAsync"/>: no
    /// <see cref="StateChanged"/> events, no pause. Cancelling <paramref name="ct"/> throws
    /// <see cref="OperationCanceledException"/>; finished work is persisted, so a later run resumes.
    /// </summary>
    /// <param name="retryErrors">Phase 2 first sets failed files back to Discovered and tries them again; Phase 3
    /// then writes again the summaries that failed or that predate a new result in their directory.</param>
    /// <param name="onWait">Called when the run starts or stops waiting for the model server, with the phase.</param>
    /// <param name="againstJobIds">Library jobs whose stored comparisons decide what is left out: files <c>backed_up</c>
    /// in any of them are not classified, folders fully <c>backed_up</c> get no pre-assessment and no summary. Null or
    /// empty: the whole job is analysed, and the marks of an earlier run with a library job are released.</param>
    /// <param name="reuse">Phase 2 takes over what the content store holds for the same content and keeps new answers
    /// there (Issue 38); false for a run that has to ask the model for every file, such as an evaluation.</param>
    public Task<AiPipelineRunResult> RunAsync(
        long jobId,
        AiPhases phases = AiPhases.All,
        IProgress<AiPhaseProgress>? progress = null,
        CancellationToken ct = default,
        bool retryErrors = false,
        Action<AiPhases, ModelServerWaitEvent>? onWait = null,
        IReadOnlyList<long>? againstJobIds = null,
        bool reuse = true)
    {
        return RunPhasesAsync(
            jobId,
            phases,
            onPhaseStarting: null,
            progressFor: phase => progress == null
                ? null
                : new SynchronousProgress<AiClassificationProgress>(p => progress.Report(new AiPhaseProgress(phase, p))),
            waitIfPausedAsync: null,
            onWait,
            ct,
            retryErrors,
            againstJobIds,
            reuse);
    }

    private async Task<AiPipelineRunResult> RunPhasesAsync(
        long jobId,
        AiPhases phases,
        Action<AiPhases>? onPhaseStarting,
        Func<AiPhases, IProgress<AiClassificationProgress>?> progressFor,
        Func<CancellationToken, Task>? waitIfPausedAsync,
        Action<AiPhases, ModelServerWaitEvent>? onWait,
        CancellationToken ct,
        bool retryErrors = false,
        IReadOnlyList<long>? againstJobIds = null,
        bool reuse = true)
    {
        // Single availability check — avoids 3x /api/tags calls. A server that is away when the run starts is
        // reported at once; waiting is for a server that goes away during the run.
        if (!await _ollama.IsAvailableAsync(ct))
            return new AiPipelineRunResult(false, []);

        // Rows scanned with older file rules: run the header rules before any of their content reaches a model.
        if (_rulesBackfill != null)
            await _rulesBackfill.RunAsync(jobId, ct);

        // What the library already holds by content is left out; without a library job, earlier marks are released.
        var backedUp = _backedUpSkip is null ? null : await _backedUpSkip.ApplyAsync(jobId, againstJobIds, ct);

        var results = new List<AiPhaseRunResult>();
        var current = AiPhases.None;
        var gate = new ModelServerGate(_ollama, _serverOptions, jobId, _waits, _time, _logger,
            wait => onWait?.Invoke(current, wait), ct);
        // A wait left behind by a run that was killed while waiting.
        await ClearWaitAsync(jobId);

        try
        {
            // ── Phase 1: Directory reconnaissance ──────────────────
            if (phases.HasFlag(AiPhases.PreAssessment))
            {
                current = AiPhases.PreAssessment;
                onPhaseStarting?.Invoke(AiPhases.PreAssessment);
                var r = await _preAssessmentService.RunAsync(jobId, progressFor(AiPhases.PreAssessment), waitIfPausedAsync, ct, gate);
                results.Add(new AiPhaseRunResult(AiPhases.PreAssessment, r));
            }

            // ── Phase 2: Targeted per-file deep analysis ────────────
            if (phases.HasFlag(AiPhases.Classification))
            {
                current = AiPhases.Classification;
                onPhaseStarting?.Invoke(AiPhases.Classification);
                var r = await _classificationService.RunAsync(jobId, progressFor(AiPhases.Classification), waitIfPausedAsync, ct, retryErrors, gate, reuse);
                results.Add(new AiPhaseRunResult(AiPhases.Classification, r));
            }

            // ── Phase 3: Directory summaries ────────────────────────
            if (phases.HasFlag(AiPhases.Summary))
            {
                current = AiPhases.Summary;
                onPhaseStarting?.Invoke(AiPhases.Summary);
                var r = await _directorySummaryService.RunAsync(jobId, progressFor(AiPhases.Summary), waitIfPausedAsync, ct, retryErrors, gate);
                results.Add(new AiPhaseRunResult(AiPhases.Summary, r));
            }
        }
        catch (ModelServerLostException lost)
        {
            return new AiPipelineRunResult(true, results, new AiModelServerLost(current, lost.Since, lost.Cause, lost.Waited)) { BackedUp = backedUp };
        }
        finally
        {
            await ClearWaitAsync(jobId);
        }

        return new AiPipelineRunResult(true, results) { BackedUp = backedUp };
    }

    private async Task ClearWaitAsync(long jobId)
    {
        if (_waits is null) return;
        try { await _waits.ClearAsync(jobId, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning("The wait of job {JobId} could not be cleared: {Error}", jobId, ex.Message); }
    }

    private static AiProcessingStateChanged WaitStateChange(long jobId, AiPhases phase, ModelServerWaitEvent wait) => wait.State switch
    {
        ModelServerWaitState.Waiting => new AiProcessingStateChanged(jobId, AiProcessingState.WaitingForModelServer,
            $"Waiting for the model server since {wait.Since.ToLocalTime():HH:mm:ss} ({wait.Cause.ToCode()}); " +
            $"{PhaseStartText(phase).TrimEnd('…')} continues when it answers."),
        ModelServerWaitState.Resumed => new AiProcessingStateChanged(jobId, AiProcessingState.Running,
            $"Model server is back after {wait.Waited.TotalSeconds:F0}s; AI classification continues…"),
        _ => new AiProcessingStateChanged(jobId, AiProcessingState.WaitingForModelServer,
            $"The model server did not answer for {wait.Waited.TotalMinutes:F0} min; stopping…")
    };

    private static string PhaseStartText(AiPhases phase) => phase switch
    {
        AiPhases.PreAssessment => "Phase 1/3: Directory reconnaissance…",
        AiPhases.Classification => "Phase 2/3: Deep file analysis…",
        _ => "Phase 3/3: Directory summaries…"
    };

    /// <summary>Reports on the calling thread. <see cref="Progress{T}"/> would post to the thread pool,
    /// so a caller could still receive reports after <see cref="RunAsync"/> returned.</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_runTask == null || _runTask.IsCompleted || _pauseRequested)
                return;

            _pauseRequested = true;
            StateChanged?.Invoke(this, new AiProcessingStateChanged(_activeJobId, AiProcessingState.Paused, "AI classification paused."));
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _cts?.Cancel();
        }
    }

    private async Task WaitIfPausedAsync(CancellationToken ct)
    {
        while (_pauseRequested)
            await Task.Delay(200, ct);
    }
}
