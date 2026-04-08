namespace SmartFileOrganizer.Application.Services;

public sealed record FocusedRescanProgress(
    string StatusText,
    long ProcessedSteps,
    long TotalSteps,
    string CurrentPath,
    int ErrorCount = 0);