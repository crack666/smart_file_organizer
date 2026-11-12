using System.Diagnostics;

namespace SmartFileOrganizer.Cli.Contract;

public enum ProgressMode
{
    /// <summary>No progress output.</summary>
    None,
    /// <summary>Human-readable lines on stderr.</summary>
    Text,
    /// <summary>One JSON object per line on stderr (<c>"type": "progress"</c>).</summary>
    Jsonl
}

/// <summary>One progress event as written in <see cref="ProgressMode.Jsonl"/> mode.</summary>
public sealed record ProgressEvent(
    int SchemaVersion,
    string Type,
    string Phase,
    long Processed,
    long Total,
    long Errors,
    string? CurrentPath,
    string? Message);

/// <summary>
/// Writes progress to stderr, throttled to one line per phase every <see cref="Interval"/>,
/// plus always the first event and the one where <c>processed == total</c>. Thread-safe.
/// </summary>
public sealed class ProgressReporter
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly StderrChannel _stderr;
    private readonly Dictionary<string, long> _lastWrite = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _sync = new();

    public ProgressReporter(ProgressMode mode, StderrChannel stderr)
    {
        Mode = mode;
        _stderr = stderr;
    }

    public ProgressMode Mode { get; }

    public void Report(string phase, long processed, long total, long errors, string? currentPath = null, string? message = null)
    {
        if (Mode == ProgressMode.None)
            return;

        lock (_sync)
        {
            var now = _clock.ElapsedMilliseconds;
            var final = total > 0 && processed >= total;
            if (!final && _lastWrite.TryGetValue(phase, out var last) && now - last < Interval.TotalMilliseconds)
                return;
            _lastWrite[phase] = now;
        }

        if (Mode == ProgressMode.Jsonl)
        {
            _stderr.WriteLine(CliJson.Serialize(
                new ProgressEvent(CliJson.SchemaVersion, "progress", phase, processed, total, errors,
                    string.IsNullOrEmpty(currentPath) ? null : currentPath,
                    string.IsNullOrEmpty(message) ? null : message),
                indented: false));
        }
        else
        {
            var errorPart = errors > 0 ? $", {errors} error(s)" : string.Empty;
            var pathPart = string.IsNullOrEmpty(currentPath) ? string.Empty : $"  {currentPath}";
            _stderr.WriteLine($"[{phase}] {processed}/{total}{errorPart}{pathPart}");
        }
    }
}

/// <summary>The one writer for stderr, shared by progress and logging so lines never interleave.</summary>
public sealed class StderrChannel(TextWriter writer)
{
    private readonly object _sync = new();

    public void WriteLine(string line)
    {
        lock (_sync)
        {
            writer.WriteLine(line);
            writer.Flush();
        }
    }
}
