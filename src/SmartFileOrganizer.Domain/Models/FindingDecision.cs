using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// What a person or a steering agent decided about one finding (<c>finding_decisions</c>,
/// https://github.com/crack666/smart_file_organizer/issues/26). Findings themselves are computed from stored data
/// each time they are read; only decisions are stored, keyed by the finding's stable key. No row means open.
/// </summary>
public sealed class FindingDecision
{
    public long JobId { get; set; }

    /// <summary>Stable key of the finding (<c>not_backed_up-1a2b3c4d5e</c>): the same subject gives the same key in every run.</summary>
    public string FindingKey { get; set; } = string.Empty;

    public FindingKind Kind { get; set; }

    /// <summary><see cref="FindingState.Accepted"/>, <see cref="FindingState.Rejected"/> or <see cref="FindingState.Wrong"/>.</summary>
    public FindingState State { get; set; }

    /// <summary><see cref="ResultOrigin.Human"/> or <see cref="ResultOrigin.Agent"/>.</summary>
    public ResultOrigin Origin { get; set; }

    /// <summary>Optional note of the one who decided.</summary>
    public string? Note { get; set; }

    public DateTime DecidedAt { get; set; }

    /// <summary>The finding as it was listed when decided, as JSON: what an accepted finding meant even if the data changes later.</summary>
    public string SnapshotJson { get; set; } = "{}";
}
