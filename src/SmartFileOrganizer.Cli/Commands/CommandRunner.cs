using System.CommandLine;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Cli.Contract;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// Binds a handler to a command and enforces the machine contract for it: one JSON envelope on stdout
/// with <c>--json</c> (text otherwise), logs and progress on stderr, exit codes from <see cref="ExitCodes"/>,
/// Ctrl+C as exit code 130. A new command only implements the handler and returns a <see cref="CommandOutcome"/>.
/// </summary>
public static class CommandRunner
{
    public delegate Task<CommandOutcome> Handler(CommandContext context, CancellationToken ct);

    public static void SetHandler(Command command, CliHost host, GlobalOptions globals, Handler handler)
    {
        command.SetAction(async (parseResult, ct) =>
        {
            await using var context = new CommandContext(parseResult, host, globals);
            CommandOutcome outcome;
            try
            {
                outcome = await handler(context, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcome = CommandOutcome.Cancelled("Interrupted. Finished work is saved; run the command again to resume.");
            }
            catch (CliFailureException failure)
            {
                outcome = failure.ToOutcome();
            }
            catch (Exception ex)
            {
                context.Logger.LogError(ex, "Unexpected failure.");
                outcome = CommandOutcome.Fail(ExitCodes.Failure, ErrorCodes.InternalError, ex.Message);
            }

            Write(host, context.Json, CommandName(parseResult), outcome);
            return outcome.ExitCode;
        });
    }

    /// <summary>Writes the outcome: the envelope on stdout with <c>--json</c>; otherwise text on stdout and the error on stderr.</summary>
    public static void Write(CliHost host, bool json, string command, CommandOutcome outcome)
    {
        if (json)
        {
            var envelope = new CliEnvelope(CliJson.SchemaVersion, command, outcome.Ok, outcome.ExitCode, outcome.Result, outcome.Error);
            host.Out.WriteLine(CliJson.Serialize(envelope));
            host.Out.Flush();
            return;
        }

        if (outcome.Text != null)
        {
            host.Out.WriteLine(outcome.Text());
            host.Out.Flush();
        }

        if (outcome.Error != null)
        {
            host.Error.WriteLine($"error ({outcome.Error.Code}): {outcome.Error.Message}");
            host.Error.Flush();
        }
    }

    /// <summary>Command path below the root, for example <c>scan</c>; <c>sfo</c> for the root itself.</summary>
    public static string CommandName(ParseResult parseResult)
    {
        var names = new List<string>();
        for (var result = parseResult.CommandResult; result != null; result = result.Parent as System.CommandLine.Parsing.CommandResult)
        {
            if (result.Parent == null)
                break;
            names.Insert(0, result.Command.Name);
        }
        return names.Count == 0 ? "sfo" : string.Join(' ', names);
    }
}
