using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Application.Services;

public class AiClassificationCoordinator
{
    private readonly ClassificationService _classificationService;
    private readonly DirectoryPreAssessmentService _preAssessmentService;
    private readonly DirectorySummaryService _directorySummaryService;
    private readonly IOllamaService _ollama;
    private readonly ILogger<AiClassificationCoordinator> _logger;

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
        ILogger<AiClassificationCoordinator> logger)
    {
        _classificationService = classificationService;
        _preAssessmentService = preAssessmentService;
        _directorySummaryService = directorySummaryService;
        _ollama = ollama;
        _logger = logger;
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
                        _cts.Token);

                    if (!run.OllamaAvailable)
                    {
                        StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Unavailable, "Ollama is not available."));
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
    public Task<AiPipelineRunResult> RunAsync(
        long jobId,
        AiPhases phases = AiPhases.All,
        IProgress<AiPhaseProgress>? progress = null,
        CancellationToken ct = default)
    {
        return RunPhasesAsync(
            jobId,
            phases,
            onPhaseStarting: null,
            progressFor: phase => progress == null
                ? null
                : new SynchronousProgress<AiClassificationProgress>(p => progress.Report(new AiPhaseProgress(phase, p))),
            waitIfPausedAsync: null,
            ct);
    }

    private async Task<AiPipelineRunResult> RunPhasesAsync(
        long jobId,
        AiPhases phases,
        Action<AiPhases>? onPhaseStarting,
        Func<AiPhases, IProgress<AiClassificationProgress>?> progressFor,
        Func<CancellationToken, Task>? waitIfPausedAsync,
        CancellationToken ct)
    {
        // Single availability check — avoids 3x /api/tags calls
        if (!await _ollama.IsAvailableAsync(ct))
            return new AiPipelineRunResult(false, []);

        var results = new List<AiPhaseRunResult>();

        // ── Phase 1: Directory reconnaissance ──────────────────
        if (phases.HasFlag(AiPhases.PreAssessment))
        {
            onPhaseStarting?.Invoke(AiPhases.PreAssessment);
            var r = await _preAssessmentService.RunAsync(jobId, progressFor(AiPhases.PreAssessment), waitIfPausedAsync, ct);
            results.Add(new AiPhaseRunResult(AiPhases.PreAssessment, r));
        }

        // ── Phase 2: Targeted per-file deep analysis ────────────
        if (phases.HasFlag(AiPhases.Classification))
        {
            onPhaseStarting?.Invoke(AiPhases.Classification);
            var r = await _classificationService.RunAsync(jobId, progressFor(AiPhases.Classification), waitIfPausedAsync, ct);
            results.Add(new AiPhaseRunResult(AiPhases.Classification, r));
        }

        // ── Phase 3: Directory summaries ────────────────────────
        if (phases.HasFlag(AiPhases.Summary))
        {
            onPhaseStarting?.Invoke(AiPhases.Summary);
            var r = await _directorySummaryService.RunAsync(jobId, progressFor(AiPhases.Summary), waitIfPausedAsync, ct);
            results.Add(new AiPhaseRunResult(AiPhases.Summary, r));
        }

        return new AiPipelineRunResult(true, results);
    }

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