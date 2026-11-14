using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// The stored target match of one finding (<c>finding_targets</c>, https://github.com/crack666/smart_file_organizer/issues/83):
/// the place <c>sfo findings targets</c> found in a library by the rules and the content check of <c>sfo library match</c>,
/// or why there is none. Findings read it while <see cref="InputHash"/> still describes the finding (same files, same
/// descriptions, same libraries); otherwise the finding counts as not matched.
/// </summary>
public sealed class FindingTargetRecord
{
    public long JobId { get; set; }

    /// <summary>Stable key of the finding.</summary>
    public string FindingKey { get; set; } = string.Empty;

    /// <summary>SHA-256 of what the match rested on: the files of the finding, their description facts and the libraries.</summary>
    public string InputHash { get; set; } = string.Empty;

    /// <summary>Never <see cref="FindingTargetStatus.NotMatched"/>.</summary>
    public FindingTargetStatus Status { get; set; }

    /// <summary>The library the target lies in; null without a target.</summary>
    public long? LibraryJobId { get; set; }

    /// <summary>The target folder (full path); null without a target.</summary>
    public string? Path { get; set; }

    /// <summary>The target folder relative to the library root, with <c>/</c>.</summary>
    public string? RelativePath { get; set; }

    public ResultCertainty? Certainty { get; set; }
    public TargetContentCheck ContentCheck { get; set; }

    /// <summary>A <see cref="ContentCheckStatus"/>: whether the content check of the match ran, and why not.</summary>
    public ContentCheckStatus ContentStatus { get; set; }

    /// <summary>One sentence: why this place, or why none.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Files of the finding whose content already lies at the target.</summary>
    public int SameContentFiles { get; set; }

    /// <summary>Whether the match was allowed to ask the model (not <c>--no-model</c>).</summary>
    public bool WithModel { get; set; }

    public string? Model { get; set; }
    public DateTime MatchedAt { get; set; }
}
