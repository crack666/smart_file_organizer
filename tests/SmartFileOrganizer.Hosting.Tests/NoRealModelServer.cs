using System.Runtime.CompilerServices;

namespace SmartFileOrganizer.Hosting.Tests;

/// <summary>
/// Suite-wide guard: no test may reach a real model server by falling back to the shipped default address. Before the
/// first test, the default address becomes a port nothing listens on (<c>SFO_OLLAMA_URL</c>); a test with a stand-in
/// names its own address in <c>settings.json</c> or replaces the service, and that still wins.
/// </summary>
internal static class NoRealModelServer
{
    public const string DeadAddress = "http://127.0.0.1:9";

    [ModuleInitializer]
    internal static void Install()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SmartFileOrganizerHostOptions.OllamaUrlVariable)))
            Environment.SetEnvironmentVariable(SmartFileOrganizerHostOptions.OllamaUrlVariable, DeadAddress);
    }
}
