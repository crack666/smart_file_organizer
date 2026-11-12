using System.CommandLine;
using System.CommandLine.Invocation;
using SmartFileOrganizer.Cli.Commands;
using SmartFileOrganizer.Cli.Contract;

namespace SmartFileOrganizer.Cli;

/// <summary>Builds the command tree and runs one invocation. Entry point for <see cref="Program"/> and tests.</summary>
public static class SfoApp
{
    public static RootCommand BuildRoot(CliHost host, GlobalOptions globals)
    {
        var root = new RootCommand("Smart File Organizer: scan folders into a local database and analyse them with a local Ollama model.");
        foreach (var option in globals.All)
            root.Options.Add(option);

        root.Subcommands.Add(ScanCommand.Create(host, globals));
        root.Subcommands.Add(AnalyzeCommand.Create(host, globals));
        root.Subcommands.Add(JobsCommand.Create(host, globals));
        root.Subcommands.Add(StatusCommand.Create(host, globals));
        root.Subcommands.Add(CompareCommand.Create(host, globals));
        root.Subcommands.Add(DoctorCommand.Create(host, globals));
        root.Subcommands.Add(RulesCommand.Create(host, globals));
        root.Subcommands.Add(SummaryCommand.Create(host, globals));
        root.Subcommands.Add(DirsCommand.Create(host, globals));
        root.Subcommands.Add(FilesCommand.Create(host, globals));
        root.Subcommands.Add(ShowCommand.Create(host, globals));
        root.Subcommands.Add(ExportCommand.Create(host, globals));
        root.Subcommands.Add(LibraryCommand.Create(host, globals));
        root.Subcommands.Add(EvalCommand.Create(host, globals));
        return root;
    }

    /// <summary>
    /// Parses and runs <paramref name="args"/>. Parse errors end with exit code 2 (and an envelope with
    /// <c>error.code = "usage"</c> when <c>--json</c> was given). Cancelling <paramref name="ct"/> is Ctrl+C.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, CliHost host, CancellationToken ct = default)
    {
        var globals = new GlobalOptions();
        var root = BuildRoot(host, globals);
        var parseResult = root.Parse(args);

        if (parseResult.Action is ParseErrorAction)
        {
            var json = args.Contains("--json", StringComparer.Ordinal);
            if (!json)
            {
                foreach (var error in parseResult.Errors)
                    host.Error.WriteLine($"error (usage): {error.Message}");
                host.Error.WriteLine("Run 'sfo --help' for usage.");
                host.Error.Flush();
                return ExitCodes.Usage;
            }

            var message = string.Join(" ", parseResult.Errors.Select(e => e.Message));
            CommandRunner.Write(host, json: true, CommandRunner.CommandName(parseResult),
                CommandOutcome.Fail(ExitCodes.Usage, ErrorCodes.Usage, message));
            return ExitCodes.Usage;
        }

        var configuration = new InvocationConfiguration
        {
            Output = host.Out,
            Error = host.Error,
            // Ctrl+C is handled by the caller through ct, so the handler can persist state before exiting.
            ProcessTerminationTimeout = null,
            EnableDefaultExceptionHandler = false
        };
        return await parseResult.InvokeAsync(configuration, ct);
    }
}
