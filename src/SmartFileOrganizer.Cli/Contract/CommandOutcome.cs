namespace SmartFileOrganizer.Cli.Contract;

/// <summary>
/// What a command handler returns. The runner turns it into either the JSON envelope (stdout) or
/// human-readable text, and into the process exit code. Handlers never write to stdout themselves.
/// </summary>
public sealed class CommandOutcome
{
    public int ExitCode { get; private init; }
    public object? Result { get; private init; }
    public CliError? Error { get; private init; }

    /// <summary>Human-readable rendering of <see cref="Result"/> for the terminal. Optional.</summary>
    public Func<string>? Text { get; private init; }

    public bool Ok => ExitCode == ExitCodes.Ok;

    public static CommandOutcome Success(object result, Func<string>? text = null) =>
        new() { ExitCode = ExitCodes.Ok, Result = result, Text = text };

    public static CommandOutcome Fail(int exitCode, string code, string message, object? result = null, Func<string>? text = null) =>
        new() { ExitCode = exitCode, Error = new CliError(code, message), Result = result, Text = text };

    public static CommandOutcome Cancelled(string message, object? result = null, Func<string>? text = null) =>
        Fail(ExitCodes.Cancelled, ErrorCodes.Cancelled, message, result, text);
}

/// <summary>Thrown by a handler to stop with a specific exit code and error code.</summary>
public sealed class CliFailureException(int exitCode, string code, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
    public string Code { get; } = code;

    public CommandOutcome ToOutcome() => CommandOutcome.Fail(ExitCode, Code, Message);
}
