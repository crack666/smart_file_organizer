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
    Processing   // reserved by producer — prevents double-fetch
}
