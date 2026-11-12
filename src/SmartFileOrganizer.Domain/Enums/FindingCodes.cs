using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// What a finding says about a part of a source (https://github.com/crack666/smart_file_organizer/issues/26).
/// Findings are the short list the tool hands the person: what is worth keeping and not yet safe, what is
/// sensitive, what is double. Codes as in <see cref="VerdictCodes"/>.
/// </summary>
public enum FindingKind
{
    /// <summary>Files worth keeping whose content is not in the library. Needs a comparison with a library.</summary>
    [JsonStringEnumMemberName("not_backed_up")] NotBackedUp,

    /// <summary>Files worth keeping in a place without value: app data, a program folder, a mixed or disposable folder.</summary>
    [JsonStringEnumMemberName("in_noise")] InNoise,

    /// <summary>Files whose kind does not fit the purpose of their folder, with a proposed place when there is a library.</summary>
    [JsonStringEnumMemberName("misplaced")] Misplaced,

    /// <summary>Files or skipped folders marked sensitive. Always decided one by one; never removed on their own.</summary>
    [JsonStringEnumMemberName("sensitive")] Sensitive,

    /// <summary>Files with exactly the same content at several places of the source.</summary>
    [JsonStringEnumMemberName("duplicate")] Duplicate
}

/// <summary>Where a finding stands. Only decided states are stored (<c>finding_decisions.state</c>); no row means open.</summary>
public enum FindingState
{
    /// <summary>Not decided yet.</summary>
    [JsonStringEnumMemberName("open")] Open,

    /// <summary>Taken over: the plan turns it into an action.</summary>
    [JsonStringEnumMemberName("accepted")] Accepted,

    /// <summary>Not wanted; it is not listed again.</summary>
    [JsonStringEnumMemberName("rejected")] Rejected,

    /// <summary>The finding itself is wrong; not listed again and kept as a test case.</summary>
    [JsonStringEnumMemberName("wrong")] Wrong
}

/// <summary>
/// Kind of a plan action, as findings recommend it and the plan stores it
/// (https://github.com/crack666/smart_file_organizer/issues/11, rules in the comment on
/// https://github.com/crack666/smart_file_organizer/issues/12).
/// </summary>
public enum PlanActionKind
{
    /// <summary>Copy to the target, verify, then remove the original.</summary>
    [JsonStringEnumMemberName("move")] Move,

    /// <summary>Copy to the target; the original stays.</summary>
    [JsonStringEnumMemberName("copy")] Copy,

    /// <summary>Move to a quarantine folder from which it can be restored.</summary>
    [JsonStringEnumMemberName("quarantine")] Quarantine,

    /// <summary>Remove the original, because the same content is already in the library.</summary>
    [JsonStringEnumMemberName("delete_source")] DeleteSource,

    /// <summary>Leave the file where it is.</summary>
    [JsonStringEnumMemberName("leave")] Leave
}

/// <summary>
/// Whether the content of a unit was compared with the content of a proposed target folder. Today no step
/// compares them; https://github.com/crack666/smart_file_organizer/issues/56 adds the check, and findings
/// already read its answer.
/// </summary>
public enum TargetContentCheck
{
    /// <summary>Not compared: the target rests on period, names and file types.</summary>
    [JsonStringEnumMemberName("not_checked")] NotChecked,

    /// <summary>The content of the unit fits what the target holds.</summary>
    [JsonStringEnumMemberName("fits")] Fits,

    /// <summary>The content contradicts the target.</summary>
    [JsonStringEnumMemberName("contradicts")] Contradicts,

    /// <summary>Compared, without a clear answer.</summary>
    [JsonStringEnumMemberName("unclear")] Unclear
}

/// <summary>Whether a kind of finding could be computed from the stored steps of a job.</summary>
public enum FindingCoverage
{
    /// <summary>Every step the kind needs has run.</summary>
    [JsonStringEnumMemberName("complete")] Complete,

    /// <summary>Computed, but a step that sharpens it is missing; the note says which.</summary>
    [JsonStringEnumMemberName("partial")] Partial,

    /// <summary>Not computed: a step it needs is missing, or there is no library.</summary>
    [JsonStringEnumMemberName("not_computed")] NotComputed
}
