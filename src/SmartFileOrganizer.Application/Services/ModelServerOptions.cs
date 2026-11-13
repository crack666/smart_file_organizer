namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Section <c>ModelServer</c>: how calls are repeated and how long a run waits for a model server that went away
/// (https://github.com/crack666/smart_file_organizer/issues/63). Independent of the provider: the same settings
/// apply to any model service. The number of retries per call is <c>Ollama.MaxRetries</c>, the time limit of a
/// call <c>Ollama.TimeoutSeconds</c>. Reasons for the defaults: docs/architecture.md, "When the model server goes away".
/// </summary>
public sealed class ModelServerOptions
{
    /// <summary>Pause before the first retry of a call; doubles with each further retry.</summary>
    public double RetryDelaySeconds { get; set; } = 2;

    /// <summary>Longest pause taken for a <c>Retry-After</c> of the server before a retry.</summary>
    public double MaxRetryAfterSeconds { get; set; } = 60;

    /// <summary>Calls in a row that found no server before the run holds all calls and waits.</summary>
    public int OutageAfterFailures { get; set; } = 3;

    /// <summary>Pause between two probes while waiting.</summary>
    public double ProbeIntervalSeconds { get; set; } = 10;

    /// <summary>Time limit of one probe (and of the availability check before a run).</summary>
    public double ProbeTimeoutSeconds { get; set; } = 5;

    /// <summary>Longest wait for the server; then the run ends with exit code 4 and can be started again.</summary>
    public double MaxWaitMinutes { get; set; } = 30;

    /// <summary>Time limit for loading the model again after the server came back.</summary>
    public double WarmupTimeoutSeconds { get; set; } = 600;

    /// <summary>How often the server may fail while one item is being sent before that item is stored as an error.</summary>
    public int MaxOutagesPerItem { get; set; } = 3;

    public TimeSpan RetryDelay => Seconds(RetryDelaySeconds);
    public TimeSpan MaxRetryAfter => Seconds(MaxRetryAfterSeconds);
    public TimeSpan ProbeInterval => Seconds(ProbeIntervalSeconds);
    public TimeSpan ProbeTimeout => Seconds(ProbeTimeoutSeconds);
    public TimeSpan MaxWait => TimeSpan.FromMinutes(Math.Max(0, MaxWaitMinutes));
    public TimeSpan WarmupTimeout => Seconds(WarmupTimeoutSeconds);

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(Math.Max(0, value));
}
