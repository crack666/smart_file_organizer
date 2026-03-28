using Microsoft.Extensions.Logging;

namespace SmartFileOrganizer.Application.Services;

public class AiClassificationCoordinator
{
    private readonly ClassificationService _classificationService;
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
        ILogger<AiClassificationCoordinator> logger)
    {
        _classificationService = classificationService;
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

                    var result = await _classificationService.RunAsync(jobId, progress, WaitIfPausedAsync, _cts.Token);

                    if (!result.OllamaAvailable)
                    {
                        StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Unavailable, result.StatusText));
                        return;
                    }

                    StateChanged?.Invoke(this, new AiProcessingStateChanged(jobId, AiProcessingState.Completed, result.StatusText));
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