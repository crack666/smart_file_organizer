using Microsoft.Extensions.DependencyInjection;

namespace SmartFileOrganizer.Cli;

/// <summary>
/// The process environment of one <c>sfo</c> invocation: where stdout and stderr go and how services are
/// adjusted. <see cref="Program"/> uses the console; tests pass string writers and a fake Ollama.
/// </summary>
public sealed class CliHost
{
    public required TextWriter Out { get; init; }
    public required TextWriter Error { get; init; }

    /// <summary>Called after <c>AddSmartFileOrganizerCore</c>; registrations here replace core services.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>Directory with the shipped <c>appsettings.json</c>; null means the application base directory.</summary>
    public string? AppSettingsDirectory { get; init; }

    public static CliHost FromConsole() => new() { Out = Console.Out, Error = Console.Error };
}
