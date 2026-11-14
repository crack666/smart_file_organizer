using System.Security;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Application.Library;

/// <summary>
/// Reads the listing of a library root (https://github.com/crack666/smart_file_organizer/issues/85). A root that does
/// not exist, or whose listing fails, cannot be read: its folders cannot be described and nothing there can be checked,
/// so no target there may be proposed.
/// </summary>
public sealed class FileSystemLibraryRootProbe : ILibraryRootProbe
{
    public static FileSystemLibraryRootProbe Instance { get; } = new();

    public string? Problem(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return "the folder is not there (a share that is not connected, a drive that is not plugged in)";
            using var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
            entries.MoveNext();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return $"its listing cannot be read ({ex.Message.TrimEnd('.')})";
        }
    }
}
