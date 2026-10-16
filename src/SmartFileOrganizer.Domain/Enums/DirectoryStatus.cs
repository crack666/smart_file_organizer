namespace SmartFileOrganizer.Domain.Enums;

public enum DirectoryStatus
{
    Pending = 0,
    Scanned,
    Skip,
    Shallow,
    Error
}
