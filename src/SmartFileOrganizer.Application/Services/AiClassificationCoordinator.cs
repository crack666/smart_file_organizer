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

                    // Single availability check — avoids 3x /api/tags calls
                    if (!await _ollama.IsAvailableAsync(_cts.Token))
                    {
                        StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Unavailable, "Ollama is not available."));
                        return;
                    }

                    // ── Phase 1: Directory reconnaissance ──────────────────
                    StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Running, "Phase 1/3: Directory reconnaissance…"));
                    var phase1 = await _preAssessmentService.RunAsync(jobId, progress, WaitIfPausedAsync, _cts.Token);

                    // ── Phase 2: Targeted per-file deep analysis ────────────
                    StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Running, "Phase 2/3: Deep file analysis…"));
                    var result = await _classificationService.RunAsync(jobId, progress, WaitIfPausedAsync, _cts.Token);

                    // ── Phase 3: Directory summaries ────────────────────────
                    StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Running, "Phase 3/3: Directory summaries…"));
                    var phase3 = await _directorySummaryService.RunAsync(jobId, progress, WaitIfPausedAsync, _cts.Token);

                    var totalErrors = phase1.ErrorCount + result.ErrorCount + phase3.ErrorCount;
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