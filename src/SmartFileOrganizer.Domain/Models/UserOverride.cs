using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

public class UserOverride
{
    public long Id { get; set; }
    public long FileNodeId { get; set; }
    public FileCategory OverriddenCategory { get; set; }
    public string? OverriddenTarget { get; set; }
    public string? Note { get; set; }
    public DateTime OverriddenAt { get; set; }
}
