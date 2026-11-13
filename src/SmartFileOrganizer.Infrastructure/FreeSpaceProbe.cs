using System.Runtime.InteropServices;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Infrastructure;

/// <summary>
/// Free space of the file system that holds a path. On Windows <c>GetDiskFreeSpaceEx</c>, which answers for drive
/// letters and network paths (<c>\\server\share\folder</c>) alike; elsewhere <see cref="DriveInfo"/>, which asks
/// <c>statvfs</c> for the given path, so a mounted share answers for itself and not for the root file system. Every
/// failure gives null.
/// </summary>
public sealed class FreeSpaceProbe : IFreeSpaceProbe
{
    public long? AvailableBytes(string path)
    {
        try
        {
            var existing = NearestExisting(path);
            if (existing is null)
                return null;
            if (OperatingSystem.IsWindows())
            {
                var dir = existing.EndsWith('\\') || existing.EndsWith('/') ? existing : existing + Path.DirectorySeparatorChar;
                return GetDiskFreeSpaceEx(dir, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;
            }
            var free = new DriveInfo(existing).AvailableFreeSpace;
            return free >= 0 ? free : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException or DllNotFoundException
                                       or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The path itself, or its nearest parent that exists as a folder; null when none does.</summary>
    private static string? NearestExisting(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            if (Directory.Exists(p))
                return p;
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}
