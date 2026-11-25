using System.Text;
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

    /// <summary>
    /// The streams of the process. A redirected stream is UTF-8 without BOM: the console encoding is the OEM code page
    /// on Windows and would replace every character outside it, paths of the JSON contract included
    /// (https://github.com/crack666/smart_file_organizer/issues/127). An interactive console keeps its own encoding.
    /// </summary>
    public static CliHost FromConsole() => new()
    {
        Out = Console.IsOutputRedirected ? Utf8(Console.OpenStandardOutput()) : Console.Out,
        Error = Console.IsErrorRedirected ? Utf8(Console.OpenStandardError()) : Console.Error
    };

    /// <summary>Like the console's own writers: flushed on every write and safe for the Ctrl+C thread.</summary>
    private static TextWriter Utf8(Stream stream) =>
        TextWriter.Synchronized(new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true });
}
