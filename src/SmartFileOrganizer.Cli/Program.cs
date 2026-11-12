using System.Globalization;
using SmartFileOrganizer.Cli;

// All own messages are English; keep the parser's messages (localised by System.CommandLine) consistent,
// so a calling model sees one language regardless of the machine's UI culture.
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

using var cts = new CancellationTokenSource();

// First Ctrl+C: cancel cleanly (the handler saves state, exit code 130). Second Ctrl+C: terminate.
Console.CancelKeyPress += (_, e) =>
{
    if (cts.IsCancellationRequested)
        return;
    e.Cancel = true;
    Console.Error.WriteLine("Cancelling… (press Ctrl+C again to terminate immediately)");
    cts.Cancel();
};

return await SfoApp.RunAsync(args, CliHost.FromConsole(), cts.Token);
