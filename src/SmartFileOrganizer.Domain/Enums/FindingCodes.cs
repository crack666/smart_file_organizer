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

/// <summary>
/// What the target match of a finding (<c>sfo findings targets</c>, https://github.com/crack666/smart_file_organizer/issues/83)
/// gave: a place, or why there is none. Stored per finding in <c>finding_targets.status</c>, except
/// <see cref="NotMatched"/>, which means no stored match fits the finding as it is now.
/// </summary>
public enum FindingTargetStatus
{
    /// <summary>An existing folder of a library is the target.</summary>
    [JsonStringEnumMemberName("existing_folder")] ExistingFolder,

    /// <summary>No existing folder fits; a new folder in the style of the library is the target.</summary>
    [JsonStringEnumMemberName("new_folder")] NewFolder,

    /// <summary>The target match has not run for the finding as it is now (never, or its files or descriptions changed).</summary>
    [JsonStringEnumMemberName("not_matched")] NotMatched,

    /// <summary>No folder of any library fits, and no complete name for a new one could be found.</summary>
    [JsonStringEnumMemberName("no_fitting_folder")] NoFittingFolder,

    /// <summary>The content could not be told: the files are not described, so neither a folder nor a name could be chosen.</summary>
    [JsonStringEnumMemberName("content_unclear")] ContentUnclear,

    /// <summary>A model was needed (content check or the name of a new folder) and none was used or reachable.</summary>
    [JsonStringEnumMemberName("no_model")] NoModel
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

/// <summary>
/// Whether a permanent storage is known for target proposals (https://github.com/crack666/smart_file_organizer/issues/74).
/// Targets only ever lie in a root scanned with the role <c>library</c>; without one there is no target path at all.
/// </summary>
public enum StorageStatus
{
    /// <summary>At least one library is known; targets lie in its roots.</summary>
    [JsonStringEnumMemberName("known")] Known,

    /// <summary>No library is known: no target is proposed, never a folder of the user profile or the system drive instead.</summary>
    [JsonStringEnumMemberName("no_library")] NoLibrary
}

/// <summary>Whether a library root has room for what the proposals send there (https://github.com/crack666/smart_file_organizer/issues/74).</summary>
public enum TargetSpace
{
    /// <summary>The free space at the root is at least what the proposals need.</summary>
    [JsonStringEnumMemberName("enough")] Enough,

    /// <summary>The proposals need more than is free at the root.</summary>
    [JsonStringEnumMemberName("not_enough")] NotEnough,

    /// <summary>The free space could not be determined (root unreachable, the file system does not tell).</summary>
    [JsonStringEnumMemberName("unknown")] Unknown
}
