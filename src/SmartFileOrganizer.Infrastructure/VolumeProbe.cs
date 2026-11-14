using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure;

/// <summary>
/// What can be learnt about the drive of a path (https://github.com/crack666/smart_file_organizer/issues/86). On Windows
/// <c>GetVolumeInformation</c> gives label, serial number and file system, also for network paths; elsewhere
/// <see cref="DriveInfo"/> gives label and file system and there is no serial number. Every failure gives null parts.
/// </summary>
public sealed class VolumeProbe : IVolumeProbe
{
    public VolumeInfo? Probe(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return null;
            if (OperatingSystem.IsWindows())
            {
                var dir = root.EndsWith('\\') ? root : root + "\\";
                var label = new StringBuilder(261);
                var format = new StringBuilder(261);
                if (!GetVolumeInformation(dir, label, label.Capacity, out var serial, out _, out _, format, format.Capacity))
                    return null;
                return new VolumeInfo(Empty(label.ToString()), serial.ToString("X8", CultureInfo.InvariantCulture), Empty(format.ToString()));
            }
            var drive = DriveInfo.GetDrives()
                .Where(d => path.StartsWith(d.Name, StringComparison.Ordinal))
                .OrderByDescending(d => d.Name.Length)
                .FirstOrDefault();
            if (drive is null || !drive.IsReady)
                return null;
            return new VolumeInfo(Empty(drive.VolumeLabel), null, Empty(drive.DriveFormat));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException or DllNotFoundException
                                       or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder fileSystemNameBuffer,
        int nFileSystemNameSize);
}
