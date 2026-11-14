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

    /// <summary>
    /// The first step of the execution is done: the copy at the target is written and checked by its full checksum
    /// (for <c>delete_source</c>: the copy in the library is checked), and the original waits for the second,
    /// separately confirmed step that removes it.
    /// </summary>
    [JsonStringEnumMemberName("verified")] Verified,

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

/// <summary>
/// What one journal step of the execution did (https://github.com/crack666/smart_file_organizer/issues/12), stored in
/// <c>plan_steps</c>. Every step is written as <c>started</c> before the file system is touched and finished afterwards,
/// so a run cut off in the middle is recognised and finished or taken back by the next one.
/// </summary>
public enum PlanStepKind
{
    /// <summary>A folder at the target that did not exist and was created; taken back when it is empty again.</summary>
    [JsonStringEnumMemberName("make_folder")] MakeFolder,

    /// <summary>A file copied to the target under a temporary name, checked by its full checksum and then named.</summary>
    [JsonStringEnumMemberName("copy")] Copy,

    /// <summary>The target held a file with the same full checksum already; nothing was written, nothing is taken back.</summary>
    [JsonStringEnumMemberName("present")] Present,

    /// <summary>For <c>delete_source</c>: the copy in the library was read whole and has the checksum of the original.</summary>
    [JsonStringEnumMemberName("verify")] Verify,

    /// <summary>The original was moved into the hold folder on its own drive; it can be put back.</summary>
    [JsonStringEnumMemberName("hold")] Hold,

    /// <summary>A held original was removed for good (<c>sfo plan purge</c>).</summary>
    [JsonStringEnumMemberName("purge")] Purge,

    /// <summary>An original was put back to its place: from the hold folder, or copied back from the target.</summary>
    [JsonStringEnumMemberName("restore")] Restore,

    /// <summary>A copy this tool wrote was removed again, after the original was back and checked.</summary>
    [JsonStringEnumMemberName("uncopy")] Uncopy,

    /// <summary>A folder this tool created was removed again because it was empty.</summary>
    [JsonStringEnumMemberName("remove_folder")] RemoveFolder,

    /// <summary>
    /// The move notice of a source folder was written, changed or removed so that it lists the originals of this plan
    /// that are away (https://github.com/crack666/smart_file_organizer/issues/86); <c>source_path</c> is the notice.
    /// </summary>
    [JsonStringEnumMemberName("notice")] Notice
}

/// <summary>Where a journal step stands.</summary>
public enum PlanStepState
{
    /// <summary>Written before the file system was touched; still so after a run was cut off in this step.</summary>
    [JsonStringEnumMemberName("started")] Started,

    /// <summary>Finished as intended.</summary>
    [JsonStringEnumMemberName("done")] Done,

    /// <summary>Not finished; <c>detail</c> says why. What it had begun was taken back.</summary>
    [JsonStringEnumMemberName("failed")] Failed
}

/// <summary>
/// Why the execution passed over an action or a file, or why it failed (<c>reasons</c> in the result of
/// <c>sfo plan apply</c>, <c>undo</c> and <c>purge</c>). A <see cref="PlanProblem"/> code can stand beside them: the
/// checks of the plan run again before every action.
/// </summary>
public enum PlanRunReason
{
    /// <summary>Not confirmed; only confirmed actions are executed.</summary>
    [JsonStringEnumMemberName("not_approved")] NotApproved,

    /// <summary>A sensitive action, executed only when named by <c>--action</c>, never in a run over the whole plan.</summary>
    [JsonStringEnumMemberName("single_decision")] SingleDecision,

    /// <summary>The job is set to leave its sources alone (<c>sfo plan keep-source</c>): nothing is removed, a move only copies.</summary>
    [JsonStringEnumMemberName("keep_source")] KeepSource,

    /// <summary>The source is gone.</summary>
    [JsonStringEnumMemberName("source_missing")] SourceMissing,

    /// <summary>The source differs from the scan in size, modification time, set of files or checksum.</summary>
    [JsonStringEnumMemberName("source_changed")] SourceChanged,

    /// <summary>The source is, or holds, a link or junction; links are never followed.</summary>
    [JsonStringEnumMemberName("source_has_link")] SourceHasLink,

    /// <summary>Something with other content lies at the target; nothing is ever overwritten.</summary>
    [JsonStringEnumMemberName("target_collision")] TargetCollision,

    /// <summary>A file with the same content lies at the target already; nothing was written.</summary>
    [JsonStringEnumMemberName("already_present")] AlreadyPresent,

    /// <summary>The copy read back does not have the full checksum of the original; the copy was removed, the original stays.</summary>
    [JsonStringEnumMemberName("hash_mismatch")] HashMismatch,

    /// <summary>The file at the target is gone.</summary>
    [JsonStringEnumMemberName("target_missing")] TargetMissing,

    /// <summary>The file at the target no longer has the checksum it was checked with.</summary>
    [JsonStringEnumMemberName("target_changed")] TargetChanged,

    /// <summary>Source and target are one file (a link, a hard link, a volume reached twice).</summary>
    [JsonStringEnumMemberName("same_file")] SameFile,

    /// <summary>The file system cannot tell whether source and target are one file; treated like <c>same_file</c>.</summary>
    [JsonStringEnumMemberName("identity_unknown")] IdentityUnknown,

    /// <summary>The hold folder would lie on another drive than the original; an original is only ever renamed there.</summary>
    [JsonStringEnumMemberName("other_volume")] OtherVolume,

    /// <summary>Taking back: something lies at the place of the original; it is not overwritten.</summary>
    [JsonStringEnumMemberName("original_occupied")] OriginalOccupied,

    /// <summary>Taking back: the held original no longer has its checksum.</summary>
    [JsonStringEnumMemberName("held_changed")] HeldChanged,

    /// <summary>Taking back: the original was removed for good and no checked copy is left to copy it back from.</summary>
    [JsonStringEnumMemberName("not_undoable")] NotUndoable,

    /// <summary>The file system refused (access, full disk, a path too long); the message is in <c>detail</c>.</summary>
    [JsonStringEnumMemberName("io_error")] IoError
}
