using System.Diagnostics;
using System.Globalization;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>
/// Takes still frames from a video with <c>ffmpeg</c>, if it is installed. The tool is optional:
/// without it videos get their metadata only, and the evidence says that frames are missing.
/// </summary>
public class VideoFrameExtractor
{
    private const int FrameWidth = 768;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Lazy<string?> _toolPath;

    /// <summary>Looks up <c>ffmpeg</c> on the PATH.</summary>
    public VideoFrameExtractor() : this(FindOnPath) { }

    /// <summary>Uses the given lookup; return null for "no tool" (tests).</summary>
    public VideoFrameExtractor(Func<string?> locateTool) => _toolPath = new Lazy<string?>(locateTool);

    public bool IsAvailable => _toolPath.Value is not null;

    /// <summary>Name of the tool for the evidence notes.</summary>
    public const string ToolName = "ffmpeg";

    /// <summary>
    /// One PNG per position (seconds from the start), scaled to 768 px width. Positions the tool
    /// cannot decode are left out. Empty when no tool is available.
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> ExtractAsync(string videoPath, IReadOnlyList<double> positions, CancellationToken ct)
    {
        var tool = _toolPath.Value;
        if (tool is null) return [];

        var frames = new List<byte[]>();
        foreach (var position in positions)
        {
            var frame = await ExtractOneAsync(tool, videoPath, position, ct);
            if (frame is { Length: > 0 }) frames.Add(frame);
        }
        return frames;
    }

    private static async Task<byte[]?> ExtractOneAsync(string tool, string videoPath, double position, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin",
                     "-ss", position.ToString("0.###", CultureInfo.InvariantCulture),
                     "-i", videoPath,
                     "-frames:v", "1",
                     "-vf", $"scale={FrameWidth}:-2",
                     "-f", "image2pipe", "-vcodec", "png", "-"
                 })
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return null;
        }
        catch (Exception)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            var drainErrors = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(copy, drainErrors);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? output.ToArray() : null;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static string? FindOnPath()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "ffmpeg.exe" } : ["ffmpeg"];
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry
                }
            }
        }
        return null;
    }
}
