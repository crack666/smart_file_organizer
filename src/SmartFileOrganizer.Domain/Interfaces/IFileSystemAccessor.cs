namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>
/// Abstracts System.IO access for testability.
/// </summary>
public interface IFileSystemAccessor
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    IEnumerable<string> EnumerateDirectories(string path);
    IEnumerable<string> EnumerateFiles(string path);
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
