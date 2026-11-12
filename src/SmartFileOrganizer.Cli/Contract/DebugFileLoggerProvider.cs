using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SmartFileOrganizer.Cli.Contract;

/// <summary>Names of the local log files of the CLI.</summary>
public static class CliLogFiles
{
    /// <summary>Debug log next to the database, written with <c>--verbose</c>.</summary>
    public const string DebugLogName = "sfo-debug.log";

    /// <summary>The debug log next to <paramref name="databasePath"/>.</summary>
    public static string DebugLogPath(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, DebugLogName);
}

/// <summary>
/// Writes every log entry from level Debug on, the model traffic included, to a local file next to the
/// database. Only this sink receives prompts and answers; it lives on the machine like the database
/// itself and never reaches a caller (https://github.com/crack666/smart_file_organizer/issues/33).
/// </summary>
/// <remarks>
/// Appends; a file above <see cref="RotateBytes"/> is moved to <c>.1</c> when it is opened, so the log
/// stays bounded across runs. The file is created on the first entry only.
/// </remarks>
public sealed class DebugFileLoggerProvider : ILoggerProvider
{
    public const long RotateBytes = 50L * 1024 * 1024;

    private readonly string _path;
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private bool _failed;

    public DebugFileLoggerProvider(string path) => _path = path;

    public string Path => _path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(string line)
    {
        lock (_sync)
        {
            if (_failed) return;
            try
            {
                _writer ??= Open();
                _writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be written must not break the command.
                _failed = true;
            }
        }
    }

    private StreamWriter Open()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var info = new FileInfo(_path);
        if (info.Exists && info.Length > RotateBytes)
            File.Move(_path, _path + ".1", overwrite: true);
        return new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    private sealed class FileLogger(DebugFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var time = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var suffix = exception != null ? Environment.NewLine + exception : string.Empty;
            owner.Write($"{time} {logLevel.ToString().ToLowerInvariant()} {category}: {formatter(state, exception)}{suffix}");
        }
    }
}
