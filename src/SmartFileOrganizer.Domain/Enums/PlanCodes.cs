using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// Where a plan action stands (https://github.com/crack666/smart_file_organizer/issues/11). A plan only proposes and
/// records consent; <c>executed</c>, <c>failed</c> and <c>undone</c> are set by the execution step
/// (https://github.com/crack666/smart_file_organizer/issues/12), never by the plan itself.
/// </summary>
public enum PlanActionState
{
    /// <summary>Proposed and not confirmed; also after every change of kind, target or reason.</summary>
    [JsonStringEnumMemberName("proposed")] Proposed,

    /// <summary>Confirmed by a person or a steering agent; only an action without problems can be confirmed.</summary>
    [JsonStringEnumMemberName("approved")] Approved,

    /// <summary>Not wanted; it is not executed.</summary>
    [JsonStringEnumMemberName("rejected")] Rejected,

    /// <summary>Carried out by the execution step.</summary>
    [JsonStringEnumMemberName("executed")] Executed,

    /// <summary>The execution step tried and stopped; the original stays.</summary>
    [JsonStringEnumMemberName("failed")] Failed,

    /// <summary>Carried out and taken back.</summary>
    [JsonStringEnumMemberName("undone")] Undone
}

/// <summary>Who set the kind and target of a plan action.</summary>
public enum PlanActionOrigin
{
    /// <summary>The rules of <c>sfo plan create</c>, from accepted findings and stored decisions.</summary>
    [JsonStringEnumMemberName("rule")] Rule,

    /// <summary>A model answer taken over as it was.</summary>
    [JsonStringEnumMemberName("model")] Model,

    /// <summary>A steering agent through <c>sfo plan set</c> or <c>sfo plan import</c> with <c>--by agent</c>.</summary>
    [JsonStringEnumMemberName("agent")] Agent,

    /// <summary>A person.</summary>
    [JsonStringEnumMemberName("human")] Human
}

/// <summary>What the source of a plan action is.</summary>
public enum PlanSourceKind
{
    /// <summary>One file.</summary>
    [JsonStringEnumMemberName("file")] File,

    /// <summary>A whole folder with everything below it; its name stays.</summary>
    [JsonStringEnumMemberName("folder")] Folder
}

/// <summary>
/// Why a plan action cannot be confirmed as it stands. The checks run when an action is created, set, imported and
/// confirmed; the execution step runs them once more before it touches a file.
/// </summary>
public enum PlanProblem
{
    /// <summary>The kind needs a target and there is none.</summary>
    [JsonStringEnumMemberName("no_target")] NoTarget,

    /// <summary>The target does not lie below the root of a library of the plan.</summary>
    [JsonStringEnumMemberName("target_outside_library")] TargetOutsideLibrary,

    /// <summary>The target lies in the source: in the scanned medium or inside the source of the action.</summary>
    [JsonStringEnumMemberName("target_in_source")] TargetInSource,

    /// <summary>The target lies in the user profile, on the system drive or in a place named in <c>Plan:ProtectedPlaces</c>.</summary>
    [JsonStringEnumMemberName("target_protected")] TargetProtected,

    /// <summary>The source lies in a library; a library is only read, never a source.</summary>
    [JsonStringEnumMemberName("source_in_library")] SourceInLibrary,

    /// <summary>The source lies in a managed area (<c>Plan:ManagedAreas</c>): nothing there is moved, renamed or removed.</summary>
    [JsonStringEnumMemberName("source_in_managed_area")] SourceInManagedArea,

    /// <summary>A move or copy of content that some library already holds.</summary>
    [JsonStringEnumMemberName("already_in_library")] AlreadyInLibrary,

    /// <summary>A sensitive file may not go to quarantine, nor be removed unless a person decides it.</summary>
    [JsonStringEnumMemberName("sensitive_kind")] SensitiveKind,

    /// <summary>Another action has the same target, or something already exists there.</summary>
    [JsonStringEnumMemberName("name_collision")] NameCollision
}
