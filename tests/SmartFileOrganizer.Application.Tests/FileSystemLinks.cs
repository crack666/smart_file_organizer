using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SmartFileOrganizer.Tests.Support;

/// <summary>What a test needs from the machine it runs on.</summary>
public enum LinkNeed
{
    /// <summary>A link to a folder: a junction on Windows (no rights needed), a symbolic link elsewhere.</summary>
    DirectoryLink,
    /// <summary>A symbolic link to a file. On Windows this needs developer mode or admin rights.</summary>
    FileSymlink,
    /// <summary>A hard link.</summary>
    HardLink,
    /// <summary>A folder the test account cannot list (fails when the tests run as root).</summary>
    UnreadableDirectory
}

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped, visibly and with the reason, when the machine cannot create
/// what the test needs. The probe runs once per need, in its own temp folder, and removes what it made.
/// </summary>
public sealed class LinkFactAttribute : FactAttribute
{
    public LinkFactAttribute(params LinkNeed[] needs)
    {
        foreach (var need in needs)
        {
            if (FileSystemLinks.Unavailable(need) is { } why)
            {
                Skip = $"Skipped on this machine: {why}";
                break;
            }
        }
    }
}

/// <summary>
/// Creates and removes links and unreadable folders for tests, only inside temp folders the tests made.
/// Removing a link removes the link itself, never its target; <see cref="DeleteTree"/> never walks through a link.
/// </summary>
public static class FileSystemLinks
{
    private static readonly Dictionary<LinkNeed, string?> Probes = [];
    private static readonly Lock ProbeLock = new();

    /// <summary>Null when the need can be met here, else the reason.</summary>
    public static string? Unavailable(LinkNeed need)
    {
        lock (ProbeLock)
        {
            if (!Probes.TryGetValue(need, out var why))
                Probes[need] = why = Probe(need);
            return why;
        }
    }

    /// <summary>A link to a folder: a junction on Windows, a symbolic link elsewhere.</summary>
    public static void DirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
            Run("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"");
        else
            Directory.CreateSymbolicLink(link, target);
        if (!Directory.Exists(link) || !new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"No link was created at {link}.");
    }

    public static void FileSymlink(string link, string target) => File.CreateSymbolicLink(link, target);

    public static void HardLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
            Run("cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"");
        else
            Run("ln", $"\"{target}\" \"{link}\"");
        if (!File.Exists(link))
            throw new InvalidOperationException($"No hard link was created at {link}.");
    }

    /// <summary>Removes a link without touching its target (a junction is removed like an empty folder).</summary>
    public static void RemoveLink(string link)
    {
        var attributes = new FileInfo(link).Attributes;
        if ((int)attributes == -1)
            return;
        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"{link} is no link; refusing to remove it as one.");
        if (OperatingSystem.IsWindows() && attributes.HasFlag(FileAttributes.Directory))
            Directory.Delete(link, recursive: false);
        else
            File.Delete(link);
    }

    /// <summary>Makes a folder unreadable for the test account; <see cref="MakeReadable"/> undoes it.</summary>
    public static void MakeUnreadable(string folder)
    {
        if (OperatingSystem.IsWindows())
            DenyList(folder, deny: true);
        else
            File.SetUnixFileMode(folder, UnixFileMode.None);
    }

    public static void MakeReadable(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        if (OperatingSystem.IsWindows())
            DenyList(folder, deny: false);
        else
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Deletes a test folder. Links inside are removed as links first, so nothing outside the folder is touched,
    /// and folders made unreadable are made readable again.
    /// </summary>
    public static void DeleteTree(string folder)
    {
        var info = new DirectoryInfo(folder);
        if (!info.Exists)
            return;
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            RemoveLink(folder);
            return;
        }

        MakeReadable(folder);
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                RemoveLink(entry.FullName);
            else if (entry is DirectoryInfo)
                DeleteTree(entry.FullName);
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }
        info.Delete();
    }

    private static string? Probe(LinkNeed need)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sfo_probe_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "target"));
            File.WriteAllText(Path.Combine(dir, "target", "file.txt"), "x");
            switch (need)
            {
                case LinkNeed.DirectoryLink:
                    DirectoryLink(Path.Combine(dir, "link"), Path.Combine(dir, "target"));
                    break;
                case LinkNeed.FileSymlink:
                    FileSymlink(Path.Combine(dir, "link.txt"), Path.Combine(dir, "target", "file.txt"));
                    if (!new FileInfo(Path.Combine(dir, "link.txt")).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return "a symbolic link to a file could not be created";
                    break;
                case LinkNeed.HardLink:
                    HardLink(Path.Combine(dir, "hard.txt"), Path.Combine(dir, "target", "file.txt"));
                    break;
                case LinkNeed.UnreadableDirectory:
                    var locked = Path.Combine(dir, "locked");
                    Directory.CreateDirectory(locked);
                    MakeUnreadable(locked);
                    try
                    {
                        _ = Directory.EnumerateFiles(locked).ToList();
                        return "the test account can still list a folder it has no right to (running as root or administrator with backup rights)";
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return null;
                    }
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"{need} cannot be created ({ex.GetType().Name}: {ex.Message})";
        }
        finally
        {
            try { DeleteTree(dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Run(string file, string arguments)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{file} did not start.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {arguments} failed ({process.ExitCode}): {output}");
    }

    [SupportedOSPlatform("windows")]
    private static void DenyList(string folder, bool deny)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        var me = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(me, FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        if (deny)
            security.AddAccessRule(rule);
        else
            security.RemoveAccessRuleAll(new FileSystemAccessRule(me, FileSystemRights.ListDirectory, AccessControlType.Deny));
        info.SetAccessControl(security);
    }
}
