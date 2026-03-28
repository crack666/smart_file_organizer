using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Infrastructure;

/// <summary>
/// Real System.IO-backed implementation of IFileSystemAccessor.
/// </summary>
public class FileSystemAccessor : IFileSystemAccessor
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch (UnauthorizedAccessException) { return []; }
        catch (IOException) { return []; }
    }

    public IEnumerable<string> EnumerateFiles(string path)
    {
        try { return Directory.EnumerateFiles(path); }
        catch (UnauthorizedAccessException) { return []; }
        catch (IOException) { return []; }
    }

    public long GetFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    public DateTime GetLastWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    public string GetFileName(string path) => Path.GetFileName(path) ?? string.Empty;
    public string GetDirectoryName(string path) => Path.GetDirectoryName(path) ?? string.Empty;
    public string GetExtension(string path) => Path.GetExtension(path).ToLowerInvariant();

    public string GetRelativePath(string relativeTo, string path) =>
        Path.GetRelativePath(relativeTo, path);
}
