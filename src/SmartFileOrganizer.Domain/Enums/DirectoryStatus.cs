using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// What the scan did with a folder. Stored as an integer in <c>directory_nodes.dir_status</c>; JSON
/// carries the code. Meanings in <c>docs/glossary.md</c>.
/// </summary>
public enum DirectoryStatus
{
    [JsonStringEnumMemberName("pending")]
    Pending = 0,

    [JsonStringEnumMemberName("scanned")]
    Scanned,

    [JsonStringEnumMemberName("skip")]
    Skip,

    [JsonStringEnumMemberName("shallow")]
    Shallow,

    /// <summary>The folder could not be read; never means empty. The cause is in <c>scan_error_cause</c>.</summary>
    [JsonStringEnumMemberName("error")]
    Error,

    /// <summary>The folder is a link (junction, symbolic link, mount point) the scan did not follow.
    /// Kind and target are in <c>link_kind</c> and <c>link_target</c>.</summary>
    [JsonStringEnumMemberName("link")]
    Link
}
