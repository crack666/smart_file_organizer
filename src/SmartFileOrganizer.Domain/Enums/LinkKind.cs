using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// Kind of a file system link the scan found and did not count as content
/// (https://github.com/crack666/smart_file_organizer/issues/57). Stored as the code in
/// <c>directory_nodes.link_kind</c> and <c>file_links.link_kind</c>; meanings in <c>docs/glossary.md</c>.
/// </summary>
/// <remarks>
/// Only reparse points that point somewhere else are links. Other reparse points (cloud placeholders,
/// deduplicated files, app execution aliases) hold their own content and are scanned like any file or folder.
/// </remarks>
public enum LinkKind
{
    /// <summary>A Windows directory junction (mount-point reparse point to a folder).</summary>
    [JsonStringEnumMemberName("junction")]
    Junction,

    /// <summary>A symbolic link, to a file or a folder (Windows or Unix, also a WSL symlink).</summary>
    [JsonStringEnumMemberName("symlink")]
    Symlink,

    /// <summary>A Windows volume mount point: a folder that shows another volume.</summary>
    [JsonStringEnumMemberName("mount_point")]
    MountPoint
}
