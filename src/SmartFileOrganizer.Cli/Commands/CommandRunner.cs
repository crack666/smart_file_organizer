using System.CommandLine;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Infrastructure.ModelServer;

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
            catch (ModelSettingsException settings)
            {
                outcome = CommandOutcome.Fail(ExitCodes.Usage, ErrorCodes.SettingsInvalid, settings.Message);
            }
            catch (Exception ex)
            {
                context.Logger.LogError(ex, "Unexpected failure.");
                if (DescribeDatabaseFailure(ex) is { } origin)
                    context.Logger.LogError("{Origin}", origin);
                outcome = CommandOutcome.Fail(ExitCodes.Failure, ErrorCodes.InternalError, ex.Message);
            }

            Write(host, context.Json, CommandName(parseResult), outcome);
            return outcome.ExitCode;
        });
    }

    /// <summary>
    /// Where a failed database call came from: the SQLite error codes and the methods of the program on the stack,
    /// innermost first (<c>FileRepository.InsertBatchAsync &lt; ScanEngine.RunAsync</c>). The envelope carries the
    /// message of the exception only, so a rare failure such as "database is locked" on a busy machine could not
    /// be traced to the call that hit it (https://github.com/crack666/smart_file_organizer/issues/111). Method
    /// names only: no SQL text, no parameter values, no paths. Null when no SQLite error is involved.
    /// </summary>
    internal static string? DescribeDatabaseFailure(Exception exception)
    {
        for (var e = exception; e != null; e = e.InnerException)
        {
            if (e is not SqliteException sqlite)
                continue;
            var frames = new StackTrace(sqlite, fNeedFileInfo: false).GetFrames()
                .Select(OwnMethod)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToList();
            var where = frames.Count == 0 ? "(no method of sfo on the stack)" : string.Join(" < ", frames);
            return $"SQLite error {sqlite.SqliteErrorCode} (extended {sqlite.SqliteExtendedErrorCode}) in: {where}";
        }
        return null;
    }

    /// <summary><c>Type.Method</c> of a frame in the program's own code, with the names the source has: an async
    /// method or a lambda runs in a generated type (<c>&lt;RunAsync&gt;d__5.MoveNext</c>). Null for foreign code.</summary>
    private static string? OwnMethod(StackFrame frame)
    {
        var method = frame.GetMethod();
        var type = method?.DeclaringType;
        if (method is null || type is null)
            return null;

        var name = Unmangled(method.Name) ?? method.Name;
        while (type is { IsNested: true } && type.Name.StartsWith('<'))
        {
            name = Unmangled(type.Name) ?? name;
            type = type.DeclaringType;
        }
        if (type?.Namespace?.StartsWith("SmartFileOrganizer", StringComparison.Ordinal) != true)
            return null;
        return $"{type.Name}.{name}";
    }

    /// <summary>The source name inside a generated one (<c>&lt;RunAsync&gt;d__5</c>, <c>&lt;RunAsync&gt;b__0</c>); null when there is none.</summary>
    private static string? Unmangled(string generated)
    {
        // An async lambda nests the names: <<Create>b__0>d. The innermost pair holds the source name.
        var end = generated.IndexOf('>');
        var start = end > 0 ? generated.LastIndexOf('<', end) : -1;
        return start >= 0 && end > start + 1 ? generated[(start + 1)..end] : null;
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
