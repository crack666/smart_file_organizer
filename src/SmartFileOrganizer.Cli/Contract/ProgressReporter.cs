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
    string? Message,
    double? RatePerMinute = null,
    long? RemainingSeconds = null);

/// <summary>A change in waiting for the model server, as written in <see cref="ProgressMode.Jsonl"/> mode (<c>"type": "wait"</c>).</summary>
/// <param name="State"><c>waiting</c>, <c>resumed</c> or <c>given_up</c>.</param>
/// <param name="Since">When the server was first considered away.</param>
/// <param name="WaitedSeconds">How long the run has waited so far.</param>
public sealed record WaitEventLine(
    int SchemaVersion,
    string Type,
    string Phase,
    string State,
    DateTime Since,
    string Cause,
    double WaitedSeconds,
    string Message);

/// <summary>
/// Writes progress to stderr, throttled to one line per phase every <see cref="Interval"/>,
/// plus always the first event and the one where <c>processed == total</c>. Thread-safe.
/// </summary>
public sealed class ProgressReporter
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly StderrChannel _stderr;
    /// <summary>A rate needs at least this much time, so the first few files do not promise a wrong end.</summary>
    public static readonly TimeSpan MinRateWindow = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, long> _lastWrite = new();
    private readonly Dictionary<string, (long Processed, long At)> _start = new();
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

        double? rate = null;
        long? remaining = null;
        lock (_sync)
        {
            var now = _clock.ElapsedMilliseconds;
            // Files counted as done before this run (skipped by rule, judged earlier) are no part of the rate: it
            // rests on what this run got through since its first event, and the rest is what is still open.
            if (!_start.TryGetValue(phase, out var start))
                _start[phase] = start = (processed, now);
            var elapsedMinutes = (now - start.At) / 60_000.0;
            if (processed > start.Processed && now - start.At >= MinRateWindow.TotalMilliseconds && total > processed)
            {
                rate = (processed - start.Processed) / elapsedMinutes;
                remaining = (long)Math.Ceiling((total - processed) / rate.Value * 60);
                rate = Math.Round(rate.Value, 1);
            }
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
                    string.IsNullOrEmpty(message) ? null : message,
                    rate, remaining),
                indented: false));
        }
        else
        {
            var errorPart = errors > 0 ? $", {errors} error(s)" : string.Empty;
            var ratePart = rate is { } r && remaining is { } left
                ? $", {total - processed} open, {r.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}/min, about {Commands.StatusCommand.Duration(left)} left"
                : string.Empty;
            var pathPart = string.IsNullOrEmpty(currentPath) ? string.Empty : $"  {currentPath}";
            _stderr.WriteLine($"[{phase}] {processed}/{total}{errorPart}{ratePart}{pathPart}");
        }
    }

    /// <summary>A change in waiting for the model server. Never throttled: each one is a single line.</summary>
    public void ReportWait(string phase, string state, DateTime since, string cause, TimeSpan waited, string message)
    {
        if (Mode == ProgressMode.None)
            return;

        if (Mode == ProgressMode.Jsonl)
        {
            _stderr.WriteLine(CliJson.Serialize(
                new WaitEventLine(CliJson.SchemaVersion, "wait", phase, state, since, cause, Math.Round(waited.TotalSeconds, 1), message),
                indented: false));
        }
        else
        {
            _stderr.WriteLine($"[{phase}] {message}");
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
