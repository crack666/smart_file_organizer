using System.CommandLine;
using SmartFileOrganizer.Cli.Contract;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// Options every command accepts, declared once on the root command as recursive options.
/// One instance per parser: System.CommandLine symbols must not be shared between command trees.
/// </summary>
public sealed class GlobalOptions
{
    public Option<bool> Json { get; } = new("--json")
    {
        Description = "Write exactly one JSON document to stdout (see docs/cli.md). Logs and progress go to stderr.",
        Recursive = true
    };

    public Option<string?> Progress { get; } = CreateProgress();

    public Option<string?> Database { get; } = new("--db")
    {
        Description = "SQLite database file. Overrides SFO_DB. Settings are read from settings.json next to it unless SFO_SETTINGS is set.",
        HelpName = "path",
        Recursive = true
    };

    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Log at information level (default: warnings and errors only).",
        Recursive = true
    };

    public IEnumerable<Option> All => [Json, Progress, Database, Verbose];

    public ProgressMode GetProgressMode(ParseResult parseResult) => parseResult.GetValue(Progress) switch
    {
        "none" => ProgressMode.None,
        "text" => ProgressMode.Text,
        "jsonl" => ProgressMode.Jsonl,
        _ => parseResult.GetValue(Json) ? ProgressMode.None : ProgressMode.Text
    };

    private static Option<string?> CreateProgress()
    {
        var option = new Option<string?>("--progress")
        {
            Description = "Progress on stderr: none, text or jsonl. Default: text, or none with --json.",
            HelpName = "mode",
            Recursive = true
        };
        option.AcceptOnlyFromAmong("none", "text", "jsonl");
        return option;
    }
}
