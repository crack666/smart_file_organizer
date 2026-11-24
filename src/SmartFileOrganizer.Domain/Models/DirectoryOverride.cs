using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// The verdict a person set for one directory, the counterpart of <see cref="UserOverride"/> for a file
/// (https://github.com/crack666/smart_file_organizer/issues/14). Stored on its own in <c>directory_overrides</c>: the
/// verdict of rule or model stays in <c>directory_verdicts</c>, a new judgement does not touch the correction, and
/// removing the correction brings the old verdict back. Every reader sees the corrected verdict with the source
/// <see cref="VerdictSource.Human"/>.
/// </summary>
public sealed class DirectoryOverride
{
    public long DirectoryNodeId { get; set; }
    public DirectoryVerdictKind Verdict { get; set; }

    /// <summary>Null: the retention value of rule or model stays.</summary>
    public RetentionValue? Retention { get; set; }

    public string? Note { get; set; }
    public DateTime OverriddenAt { get; set; }
}
