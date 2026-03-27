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
}
