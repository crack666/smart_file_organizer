using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// A link found at a path: what kind it is and where it points, as stored in the link itself
/// (not resolved further). <see cref="Target"/> is null when the target cannot be read.
/// </summary>
public sealed record FileSystemLink(LinkKind Kind, string? Target);

/// <summary>Result of <see cref="Interfaces.IFileSystemAccessor.SameFile"/> with a reason for people and logs.</summary>
public sealed record SameFileResult(SameFileAnswer Answer, string Reason);

/// <summary>A file that is itself a link. Stored in <c>file_links</c> instead of <c>file_nodes</c>, so it never counts as content.</summary>
public sealed class FileLinkNode
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ParentPath { get; set; } = string.Empty;

    /// <summary>Code of <see cref="Enums.LinkKind"/>.</summary>
    public string LinkKind { get; set; } = string.Empty;

    public string? LinkTarget { get; set; }
    public DateTime ScannedAt { get; set; }
}
