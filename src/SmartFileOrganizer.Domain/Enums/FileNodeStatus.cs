namespace SmartFileOrganizer.Domain.Enums;

public enum FileNodeStatus
{
    Discovered = 0,
    Classified,
    AiAnalyzed,
    ReviewNeeded,
    Approved,
    Skipped,
    Error,
    Processing,  // reserved by producer — prevents double-fetch

    /// <summary>Waits for the sample of its uniform directory; then inherits (<see cref="Inherited"/>) or is judged on its own.</summary>
    Deferred,

    /// <summary>Carries a result inherited from the confirmed sample of its directory (<c>ai_results.origin = inherited</c>).</summary>
    Inherited
}
