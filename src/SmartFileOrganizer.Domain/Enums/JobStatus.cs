namespace SmartFileOrganizer.Domain.Enums;

public enum JobStatus
{
    Created = 0,
    Running,
    Paused,
    Completed,
    Cancelled,
    Failed
}
