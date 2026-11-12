using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Sensitivity;

namespace SmartFileOrganizer.Cli.Contract;

/// <summary>One log entry as written in <see cref="ProgressMode.Jsonl"/> mode.</summary>
public sealed record LogEvent(int SchemaVersion, string Type, string Level, string Category, string Message, string? Exception);

/// <summary>
/// Logs to stderr only, so stdout stays a single JSON document. In JSONL mode every entry is a JSON
/// line (<c>"type": "log"</c>), so a caller can parse all of stderr line by line.
/// </summary>
public sealed class StderrLoggerProvider : ILoggerProvider
{
    private readonly StderrChannel stderr;
    private readonly LogLevel minimumLevel;
    private readonly bool jsonLines;

    public StderrLoggerProvider(StderrChannel stderr, LogLevel minimumLevel, bool jsonLines)
    {
        this.stderr = stderr;
        this.minimumLevel = minimumLevel;
        this.jsonLines = jsonLines;
    }

    /// <summary>
    /// The model traffic (prompts with text excerpts, model answers) never reaches stderr, whatever the
    /// level: an external caller reads stderr. It goes to the local debug log only.
    /// </summary>
    public ILogger CreateLogger(string categoryName) =>
        categoryName == ModelTraffic.Category
            ? NullLogger.Instance
            : new StderrLogger(this, ShortName(categoryName));

    public void Dispose()
    {
    }

    private static string ShortName(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class StderrLogger(StderrLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner.minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            var level = logLevel.ToString().ToLowerInvariant();
            if (owner.jsonLines)
            {
                owner.stderr.WriteLine(CliJson.Serialize(
                    new LogEvent(CliJson.SchemaVersion, "log", level, category, message, exception?.Message),
                    indented: false));
            }
            else
            {
                var suffix = exception != null ? $" ({exception.GetType().Name}: {exception.Message})" : string.Empty;
                owner.stderr.WriteLine($"{level}: {category}: {message}{suffix}");
            }
        }
    }
}
