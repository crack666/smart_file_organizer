using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Hosting;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// Everything a command handler needs: parsed arguments, the core services (built on first use),
/// progress output and a logger. Disposed by <see cref="CommandRunner"/> after the handler returns.
/// </summary>
public sealed class CommandContext : IAsyncDisposable
{
    private readonly CliHost _host;
    private readonly GlobalOptions _globals;
    private readonly StderrChannel _stderr;
    private ServiceProvider? _services;
    private bool _databaseInitialized;

    internal CommandContext(ParseResult parseResult, CliHost host, GlobalOptions globals)
    {
        ParseResult = parseResult;
        _host = host;
        _globals = globals;
        _stderr = new StderrChannel(host.Error);

        Json = parseResult.GetValue(globals.Json);
        var mode = globals.GetProgressMode(parseResult);
        Progress = new ProgressReporter(mode, _stderr);
        LoggerProvider = new StderrLoggerProvider(
            _stderr,
            parseResult.GetValue(globals.Verbose) ? LogLevel.Information : LogLevel.Warning,
            jsonLines: mode == ProgressMode.Jsonl);
        Logger = LoggerProvider.CreateLogger("sfo");
    }

    public ParseResult ParseResult { get; }
    public bool Json { get; }
    public ProgressReporter Progress { get; }
    public ILogger Logger { get; }
    internal StderrLoggerProvider LoggerProvider { get; }

    /// <summary>
    /// A short note for the person at the terminal, on stderr. Follows the progress mode: plain text,
    /// a JSON log line (<c>"type": "log"</c>, level <c>information</c>) in JSONL mode, nothing with <c>none</c>.
    /// </summary>
    public void Note(string line)
    {
        switch (Progress.Mode)
        {
            case ProgressMode.Text:
                _stderr.WriteLine(line);
                break;
            case ProgressMode.Jsonl:
                _stderr.WriteLine(CliJson.Serialize(
                    new LogEvent(CliJson.SchemaVersion, "log", "information", "sfo", line, null), indented: false));
                break;
        }
    }

    /// <summary>The core services without touching the database file.</summary>
    public IServiceProvider BuildServices()
    {
        if (_services != null)
            return _services;

        var services = new ServiceCollection();
        services.AddSmartFileOrganizerCore(o =>
        {
            o.DatabasePath = ParseResult.GetValue(_globals.Database);
            o.AppSettingsDirectory = _host.AppSettingsDirectory;
            o.ReloadSettingsOnChange = false;
        });
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(LoggerProvider);
        });
        _host.ConfigureServices?.Invoke(services);

        _services = services.BuildServiceProvider();
        return _services;
    }

    /// <summary>The core services with the database created and migrated.</summary>
    public async Task<IServiceProvider> GetServicesAsync(CancellationToken ct)
    {
        var services = BuildServices();
        if (!_databaseInitialized)
        {
            await services.InitializeSmartFileOrganizerDatabaseAsync(ct);
            _databaseInitialized = true;
        }
        return services;
    }

    public async ValueTask DisposeAsync()
    {
        if (_services != null)
            await _services.DisposeAsync();
    }
}
