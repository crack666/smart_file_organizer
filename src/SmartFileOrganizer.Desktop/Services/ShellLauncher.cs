using System.Diagnostics;

namespace SmartFileOrganizer.Desktop.Services;

/// <summary>Hands a file to the operating system: open it with its default program, or show it in the file manager.</summary>
public interface IShellLauncher
{
    /// <summary>Opens the file or folder with the program the system has for it.</summary>
    void Open(string path);

    /// <summary>Shows the file in its folder in the file manager (a folder: the folder itself).</summary>
    void Reveal(string path);
}

/// <summary>
/// <see cref="IShellLauncher"/> through the shell of the platform: the default program and Explorer on Windows,
/// <c>open</c> on macOS, <c>xdg-open</c> elsewhere (which cannot select a file, so it opens the folder).
/// </summary>
public sealed class ShellLauncher : IShellLauncher
{
    public void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if (OperatingSystem.IsWindows())
            Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { path } });
    }

    public void Reveal(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(path))
                Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
            else if (Directory.Exists(path))
                Start(new ProcessStartInfo("explorer.exe", $"\"{path}\""));
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            if (File.Exists(path) || Directory.Exists(path))
                Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path } });
            return;
        }
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (folder is not null && Directory.Exists(folder))
            Start(new ProcessStartInfo("xdg-open") { ArgumentList = { folder } });
    }

    private static void Start(ProcessStartInfo info)
    {
        try
        {
            using var _ = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"[ShellLauncher] {info.FileName}: {ex.Message}");
        }
    }
}
