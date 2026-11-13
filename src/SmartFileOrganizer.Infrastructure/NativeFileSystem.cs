using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace SmartFileOrganizer.Infrastructure;

/// <summary>
/// The identity the file system gives a file or folder: the volume it lies on and its number there
/// (NTFS/ReFS file id, Unix inode). Two paths with equal identity are the same file, whatever their names.
/// <see cref="Size"/> and <see cref="LastWriteUtc"/> are only used to flag a volume that may be reached twice.
/// </summary>
internal sealed record FileIdentity(string Volume, string FileId, long Size, long LastWriteUtc);

/// <summary>
/// What .NET does not offer: the reparse tag of a Windows entry (to tell junctions, symbolic links and
/// mount points from cloud placeholders) and file identities on Windows and Linux. Other systems get
/// null, which callers turn into "unknown", never into "different".
/// </summary>
internal static class NativeFileSystem
{
    // ── Windows reparse tags ─────────────────────────────────────────────

    public const uint TagMountPoint = 0xA0000003;   // junction or volume mount point
    public const uint TagSymlink = 0xA000000C;
    public const uint TagWslSymlink = 0xA000001D;   // symbolic link created by WSL

    private const uint FileAttributeReparsePoint = 0x400;

    /// <summary>The reparse tag of the entry itself; null when it is no reparse point or cannot be looked at.</summary>
    public static uint? GetReparseTag(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var handle = FindFirstFile(ToExtendedPath(path), out var data);
        if (handle == InvalidHandle)
            return null;
        FindClose(handle);
        return (data.dwFileAttributes & FileAttributeReparsePoint) != 0 ? data.dwReserved0 : null;
    }

    // ── File identity ────────────────────────────────────────────────────

    /// <summary>
    /// The identity of the file or folder a path leads to (links followed). Null with a reason when the path
    /// is missing or unreadable, or the system gives no usable identity.
    /// </summary>
    public static FileIdentity? GetIdentity(string fullPath, out string? problem)
    {
        if (OperatingSystem.IsWindows())
            return WindowsIdentity(fullPath, out problem);
        if (OperatingSystem.IsLinux())
            return LinuxIdentity(fullPath, out problem);
        problem = $"file identities are not read on {RuntimeInformation.OSDescription}";
        return null;
    }

    private static FileIdentity? WindowsIdentity(string fullPath, out string? problem)
    {
        // Attribute access only, shared with everyone: works for files another program holds open, and
        // for folders (backup semantics). Links are followed, so a link and its target give the same identity.
        using var handle = CreateFile(ToExtendedPath(fullPath), FileReadAttributes, FileShareAll, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            problem = Describe(Marshal.GetLastPInvokeError());
            return null;
        }
        if (!GetFileInformationByHandle(handle, out var basic))
        {
            problem = Describe(Marshal.GetLastPInvokeError());
            return null;
        }

        var size = ((long)basic.nFileSizeHigh << 32) | basic.nFileSizeLow;
        var written = ((long)(uint)basic.ftLastWriteTime.dwHighDateTime << 32) | (uint)basic.ftLastWriteTime.dwLowDateTime;

        // 128-bit ids (ReFS) and 64-bit volume serials need FileIdInfo; the 64-bit index is the fallback.
        if (GetFileInformationByHandleEx(handle, FileIdInfoClass, out var id, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            if (id.FileIdLow == 0 && id.FileIdHigh == 0)
            {
                problem = "the file system reports no file id";
                return null;
            }
            problem = null;
            return new FileIdentity(id.VolumeSerialNumber.ToString("x16"), $"{id.FileIdHigh:x16}{id.FileIdLow:x16}", size, written);
        }

        if (basic.nFileIndexHigh == 0 && basic.nFileIndexLow == 0)
        {
            problem = "the file system reports no file id";
            return null;
        }
        problem = null;
        return new FileIdentity(basic.dwVolumeSerialNumber.ToString("x8"),
            $"{basic.nFileIndexHigh:x8}{basic.nFileIndexLow:x8}", size, written);
    }

    private static FileIdentity? LinuxIdentity(string fullPath, out string? problem)
    {
        // struct stat of the 64-bit Linux ABIs (x86-64 and arm64 alike): st_dev at 0, st_ino at 8,
        // st_size at 48, st_mtim at 88. Other layouts are not guessed.
        if (!Environment.Is64BitProcess || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            problem = $"file identities are not read on {RuntimeInformation.ProcessArchitecture}";
            return null;
        }

        var buffer = new byte[256];
        int result;
        try
        {
            result = LinuxStat(fullPath, buffer);
        }
        catch (EntryPointNotFoundException)
        {
            // glibc before 2.33 exports stat only as __xstat (version 1 on x86-64, 0 on arm64).
            try { result = LinuxXStat(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 1 : 0, fullPath, buffer); }
            catch (EntryPointNotFoundException) { problem = "the C library offers no stat"; return null; }
        }
        catch (DllNotFoundException)
        {
            problem = "the C library could not be loaded";
            return null;
        }

        if (result != 0)
        {
            problem = Describe(Marshal.GetLastPInvokeError());
            return null;
        }

        problem = null;
        var device = BitConverter.ToUInt64(buffer, 0);
        var inode = BitConverter.ToUInt64(buffer, 8);
        var size = BitConverter.ToInt64(buffer, 48);
        var written = BitConverter.ToInt64(buffer, 88) * 1_000_000_000L + BitConverter.ToInt64(buffer, 96);
        return new FileIdentity(device.ToString("x"), inode.ToString("x"), size, written);
    }

    /// <summary>Error numbers both systems share for the common cases, in words.</summary>
    private static string Describe(int error) => error switch
    {
        2 or 3 => "not found",
        5 or 13 => "access denied",
        _ => $"system error {error}"
    };

    /// <summary>Paths over 260 characters only work with the extended prefix.</summary>
    private static string ToExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    // ── Windows interop ──────────────────────────────────────────────────

    private static readonly IntPtr InvalidHandle = new(-1);
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileIdInfoClass = 18;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFile(string fileName, out Win32FindData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr handle);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdInfo info, uint size);

    // ── Linux interop ────────────────────────────────────────────────────

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int LinuxStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [In, Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "__xstat", SetLastError = true)]
    private static extern int LinuxXStat(int version, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [In, Out] byte[] buffer);
}
