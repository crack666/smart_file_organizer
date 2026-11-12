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

    long GetFileSize(string path);
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
