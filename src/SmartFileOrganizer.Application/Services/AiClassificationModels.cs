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
    string StatusText,
    int RetriedFiles = 0,
    int SecondChecks = 0);

/// <summary>The three AI phases. Combine flags to run a subset; the order is always 1 → 2 → 3.</summary>
[Flags]
public enum AiPhases
{
    None = 0,
    /// <summary>Phase 1: directory pre-assessment.</summary>
    PreAssessment = 1,
    /// <summary>Phase 2: per-file classification.</summary>
    Classification = 2,
    /// <summary>Phase 3: directory summaries.</summary>
    Summary = 4,
    All = PreAssessment | Classification | Summary
}

/// <summary>Progress of one phase of an awaited AI run.</summary>
public sealed record AiPhaseProgress(AiPhases Phase, AiClassificationProgress Progress);

/// <summary>Outcome of one phase of an awaited AI run.</summary>
public sealed record AiPhaseRunResult(AiPhases Phase, AiClassificationRunResult Result);

/// <summary>
/// Outcome of <see cref="AiClassificationCoordinator.RunAsync"/>. When Ollama was not reachable,
/// <see cref="OllamaAvailable"/> is false and no phase ran.
/// </summary>
public sealed record AiPipelineRunResult(bool OllamaAvailable, IReadOnlyList<AiPhaseRunResult> Phases)
{
    public int ErrorCount => Phases.Sum(p => p.Result.ErrorCount);
}

public sealed record AiProcessingStateChanged(
    long JobId,
    AiProcessingState State,
    string StatusText,
    string? ErrorMessage = null);