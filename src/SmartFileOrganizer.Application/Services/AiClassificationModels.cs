namespace SmartFileOrganizer.Application.Services;

public enum AiProcessingState
{
    Idle = 0,
    Starting,
    Running,
    Paused,
    Completed,
    Cancelled,
    Failed,
    Unavailable
}

public sealed record AiClassificationProgress(
    long JobId,
    long ProcessedFiles,
    long TotalFiles,
    int ErrorCount,
    string CurrentPath,
    string StatusText);

public sealed record AiClassificationRunResult(
    bool OllamaAvailable,
    long TotalFiles,
    long ProcessedFiles,
    int ErrorCount,
    string StatusText);

public sealed record AiProcessingStateChanged(
    long JobId,
    AiProcessingState State,
    string StatusText,
    string? ErrorMessage = null);