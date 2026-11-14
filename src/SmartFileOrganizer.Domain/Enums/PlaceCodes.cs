using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// What a refresh (<c>sfo scan &lt;path&gt; --refresh</c>) found for one file against the previous job of the same
/// place (https://github.com/crack666/smart_file_organizer/issues/86), stored in <c>scan_changes</c>. Unchanged files are
/// counted, not stored.
/// </summary>
public enum FileChange
{
    /// <summary>The file is there now and was not before.</summary>
    [JsonStringEnumMemberName("new")] New,

    /// <summary>The file was there before with another size or modification time.</summary>
    [JsonStringEnumMemberName("changed")] Changed,

    /// <summary>The file was there before and is gone, and no plan moved it.</summary>
    [JsonStringEnumMemberName("missing")] Missing,

    /// <summary>The file is gone because a plan moved it; <c>target_path</c> names its new place.</summary>
    [JsonStringEnumMemberName("moved")] Moved
}

/// <summary>Where a move of the history (<c>moves</c>) stands.</summary>
public enum MoveState
{
    /// <summary>The original left its place; the file at the target holds its content.</summary>
    [JsonStringEnumMemberName("moved")] Moved,

    /// <summary>The original was put back by <c>sfo plan undo</c>.</summary>
    [JsonStringEnumMemberName("returned")] Returned
}

/// <summary>What an answer of <c>sfo where</c> rests on.</summary>
public enum WhereSource
{
    /// <summary>The move history of the plans executed with this database.</summary>
    [JsonStringEnumMemberName("journal")] Journal,

    /// <summary>A stored scan that saw the file at that place.</summary>
    [JsonStringEnumMemberName("scan")] Scan,

    /// <summary>A move notice a scan found in the folder, possibly written with another database.</summary>
    [JsonStringEnumMemberName("notice")] Notice,

    /// <summary>A checksum stored for that path (<c>content_fingerprints</c>).</summary>
    [JsonStringEnumMemberName("checksum")] Checksum
}
