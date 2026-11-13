using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

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

    public IReadOnlyList<string> ListFiles(string path) => Directory.EnumerateFiles(path).ToList();

    public IReadOnlyList<string> ListDirectories(string path) => Directory.EnumerateDirectories(path).ToList();

    /// <inheritdoc />
    /// <remarks>Until https://github.com/crack666/smart_file_organizer/issues/57 every error returned 0, so an
    /// unreadable or vanished file looked like an empty one.</remarks>
    public long GetFileSize(string path) => new FileInfo(path).Length;

    public FileSystemLink? GetLink(string path)
    {
        try
        {
            // One attribute read per entry, of the entry itself; only reparse points are looked at further.
            var attributes = new FileInfo(path).Attributes;
            if ((int)attributes == -1 || !attributes.HasFlag(FileAttributes.ReparsePoint))
                return null;
            FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(path) : new FileInfo(path);

            string? target = null;
            try { target = info.LinkTarget; }
            catch (Exception ex) when (ScanErrorCauses.IsReadError(ex)) { }

            LinkKind? kind;
            if (OperatingSystem.IsWindows())
            {
                kind = NativeFileSystem.GetReparseTag(path) switch
                {
                    NativeFileSystem.TagMountPoint => IsVolume(target) ? LinkKind.MountPoint : LinkKind.Junction,
                    NativeFileSystem.TagSymlink or NativeFileSystem.TagWslSymlink => LinkKind.Symlink,
                    // Tag unreadable but .NET resolves a target: a link all the same.
                    null when target != null => info is DirectoryInfo ? LinkKind.Junction : LinkKind.Symlink,
                    // Cloud placeholders, deduplicated files, app execution aliases: own content, no link.
                    _ => null
                };
            }
            else
            {
                // On Unix .NET marks symbolic links (and only those) as reparse points.
                kind = LinkKind.Symlink;
            }

            return kind is { } k ? new FileSystemLink(k, CleanTarget(target)) : null;
        }
        catch (Exception ex) when (ScanErrorCauses.IsReadError(ex) || ex is ArgumentException)
        {
            return null;
        }
    }

    public string? ResolveLinkTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ScanErrorCauses.IsReadError(ex) || ex is ArgumentException)
        {
            return null;
        }
    }

    public SameFileResult SameFile(string first, string second)
    {
        string a, b;
        try
        {
            a = Path.GetFullPath(first);
            b = Path.GetFullPath(second);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SameFileResult(SameFileAnswer.Unknown, $"Not a usable path: {ex.Message}");
        }

        if (string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.Ordinal))
            return new SameFileResult(SameFileAnswer.Same, "Both paths are the same path.");

        var left = NativeFileSystem.GetIdentity(a, out var leftProblem);
        if (left == null)
            return new SameFileResult(SameFileAnswer.Unknown, $"No file identity for '{a}': {leftProblem}.");
        var right = NativeFileSystem.GetIdentity(b, out var rightProblem);
        if (right == null)
            return new SameFileResult(SameFileAnswer.Unknown, $"No file identity for '{b}': {rightProblem}.");

        if (left.Volume == right.Volume && left.FileId == right.FileId)
            return new SameFileResult(SameFileAnswer.Same,
                "Same volume and file id: one file reached by a link, a hard link or a second path to the volume.");

        // A volume reached twice over different mounts can report two device numbers for one file. Equal
        // file id, size and time on two volumes is not proof of two files.
        if (left.FileId == right.FileId && left.Size == right.Size && left.LastWriteUtc == right.LastWriteUtc)
            return new SameFileResult(SameFileAnswer.Unknown,
                "Equal file id, size and modification time on two volumes: possibly one volume mounted twice.");

        return new SameFileResult(SameFileAnswer.Different, "Different file ids: two files.");
    }

    private static bool IsVolume(string? target) =>
        target != null && target.Contains("Volume{", StringComparison.OrdinalIgnoreCase);

    /// <summary>Junction targets can come with the NT prefix (<c>\??\C:\...</c>); people read the plain path.</summary>
    private static string? CleanTarget(string? target) =>
        target != null && target.StartsWith(@"\??\", StringComparison.Ordinal) && !IsVolume(target) ? target[4..] : target;

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

    public byte[] ReadPrefix(string path, int maxBytes)
    {
        if (maxBytes <= 0) return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[maxBytes];
            var total = 0;
            int read;
            while (total < maxBytes && (read = stream.Read(buffer, total, maxBytes - total)) > 0)
                total += read;
            return total == maxBytes ? buffer : buffer[..total];
        }
        catch (UnauthorizedAccessException) { return []; }
        catch (IOException) { return []; }
    }
}
