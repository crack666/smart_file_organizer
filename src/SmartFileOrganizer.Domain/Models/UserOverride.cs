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

    /// <summary><see cref="ResultOrigin.Human"/> (null for rows before schema step 22: all set by a person), or
    /// <see cref="ResultOrigin.Reused"/> for a person's correction of the same content carried over from the content
    /// store (https://github.com/crack666/smart_file_organizer/issues/38). Stored as code in <c>user_overrides.origin</c>.</summary>
    public ResultOrigin? Origin { get; set; }
}
