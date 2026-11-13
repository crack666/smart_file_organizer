using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>
/// Abstracts System.IO access for testability.
/// </summary>
public interface IFileSystemAccessor
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    /// <summary>Subdirectories; an unreadable directory yields an empty list.</summary>
    IEnumerable<string> EnumerateDirectories(string path);

    /// <summary>Files; an unreadable directory yields an empty list.</summary>
    IEnumerable<string> EnumerateFiles(string path);

    /// <summary>
    /// Files of a directory, read completely. Unlike <see cref="EnumerateFiles"/> a directory that
    /// cannot be read throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>,
    /// so the scan can record it instead of treating it as empty.
    /// </summary>
    IReadOnlyList<string> ListFiles(string path) => EnumerateFiles(path).ToList();

    /// <summary>Subdirectories, read completely; throws like <see cref="ListFiles"/>.</summary>
    IReadOnlyList<string> ListDirectories(string path) => EnumerateDirectories(path).ToList();

    /// <summary>
    /// Size of a file in bytes. A file that cannot be read throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>; it never reads as an empty file.
    /// </summary>
    long GetFileSize(string path);

    /// <summary>
    /// The link at <paramref name="path"/> itself, without following it: a junction, a symbolic link or a
    /// volume mount point. Null when the path is no link, does not exist, or cannot be looked at. Reparse points
    /// that hold their own content (cloud placeholders, deduplicated files) are no links.
    /// </summary>
    FileSystemLink? GetLink(string path) => null;

    /// <summary>The full path a link finally leads to (every link on the way resolved); null when it cannot be resolved.</summary>
    string? ResolveLinkTarget(string path) => null;

    /// <summary>
    /// Whether two paths mean the same file: the same path written differently, a link, a hard link,
    /// or a volume reached twice. Decided by the identity the file system gives a file (volume and file id),
    /// not by name or content. <see cref="SameFileAnswer.Unknown"/> when a path is missing or unreadable or
    /// the file system gives no identity; a caller about to remove a file treats that like <see cref="SameFileAnswer.Same"/>.
    /// </summary>
    SameFileResult SameFile(string first, string second) =>
        new(SameFileAnswer.Unknown, "This file system accessor cannot tell file identities apart.");
    DateTime GetLastWriteTime(string path);
    string GetFileName(string path);
    string GetDirectoryName(string path);
    string GetExtension(string path);
    string GetRelativePath(string relativeTo, string path);

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> bytes from the start of the file.
    /// Returns an empty array if the file cannot be read. Used for header rules
    /// (for example a private-key header line); never for whole files.
    /// </summary>
    byte[] ReadPrefix(string path, int maxBytes);
}
