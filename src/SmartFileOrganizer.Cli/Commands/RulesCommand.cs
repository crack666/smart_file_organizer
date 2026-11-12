using System.CommandLine;
using System.Text;
using System.Text.Json;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure;
using SmartFileOrganizer.Scanning.Engine;
using SmartFileOrganizer.Scanning.Rules;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo rules check &lt;path&gt;...</c> and <c>sfo rules show</c>: inspect scan rules without scanning.
/// Neither touches the database.
/// </summary>
public static class RulesCommand
{
    public sealed record RulesView(string Name, string Source, string Hash)
    {
        public static RulesView From(ScanRulesProfile profile) => new(profile.Name, profile.Source, profile.Hash);
    }

    public sealed record RuleView(int Index, string Match, string Action, int? MaxDepth, string? Why);
    public sealed record ContentView(int Files, int UserFiles, int ProgramFiles);
    public sealed record BlockView(string Path, ScanDecision Decision, string Reason);

    public sealed record PathCheck(
        string Path,
        string Root,
        int Depth,
        bool Exists,
        ScanDecision Decision,
        DecisionBasis Basis,
        string Reason,
        RuleView? Rule,
        ContentView? Content,
        bool Reached,
        BlockView? BlockedBy);

    public sealed record CheckResult(RulesView Rules, IReadOnlyList<PathCheck> Checks);
    public sealed record ShowResult(RulesView Rules, JsonElement Content);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var command = new Command("rules", "Inspect scan rules (profiles) without scanning.");
        command.Subcommands.Add(CreateCheck(host, globals));
        command.Subcommands.Add(CreateShow(host, globals));
        return command;
    }

    /// <summary>The <c>--rules &lt;file&gt;</c> option shared by <c>scan</c> and <c>rules</c>.</summary>
    public static Option<string?> RulesOption(string description) => new("--rules")
    {
        Description = description,
        HelpName = "file"
    };

    /// <summary>Loads a rule file, or the shipped rules for null. Fails with exit code 1:
    /// <c>path_not_found</c> or <c>rules_invalid</c>.</summary>
    public static (ScanRuleSet Rules, ScanRulesProfile Profile) Load(string? file)
    {
        if (file == null)
            return (ScanRulesFile.Default, ScanRulesFile.DefaultProfile);

        var path = Path.GetFullPath(file);
        if (!File.Exists(path))
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, $"Rules file not found: {path}");
        try
        {
            var rules = ScanRulesFile.Load(path);
            return (rules, ScanRulesFile.ToProfile(rules, path));
        }
        catch (ScanRulesException ex)
        {
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.RulesInvalid, ex.Message);
        }
    }

    private static Command CreateCheck(CliHost host, GlobalOptions globals)
    {
        var paths = new Argument<string[]>("path")
        {
            Description = "Folders to check. They need not exist; without them the content check is reported as unknown.",
            Arity = ArgumentArity.OneOrMore
        };
        var root = new Option<string?>("--root")
        {
            Description = "Scan root the paths are checked against. Default: the drive or file system root of each path.",
            HelpName = "dir"
        };
        var rulesFile = RulesOption("Rule file to check with. Default: the shipped rules.");

        var command = new Command("check", "Show for each path which rule applies and why, and whether a scan would reach it.")
        {
            paths,
            root,
            rulesFile
        };

        CommandRunner.SetHandler(command, host, globals, (ctx, ct) =>
        {
            var (rules, profile) = Load(ctx.ParseResult.GetValue(rulesFile));
            var engine = new HeuristicsEngine(rules);
            var fs = new FileSystemAccessor();
            var rootOption = ctx.ParseResult.GetValue(root);

            var checks = new List<PathCheck>();
            foreach (var raw in ctx.ParseResult.GetValue(paths)!)
            {
                ct.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(raw);
                var scanRoot = rootOption != null ? Path.GetFullPath(rootOption) : Path.GetPathRoot(full)!;

                RuleCheck check;
                try
                {
                    check = engine.Check(scanRoot, full, folder => Probe(fs, folder));
                }
                catch (ArgumentException ex)
                {
                    throw new CliFailureException(ExitCodes.Usage, ErrorCodes.Usage, ex.Message);
                }
                checks.Add(ToView(check, rules, fs.DirectoryExists(full)));
            }

            var result = new CheckResult(RulesView.From(profile), checks);
            return Task.FromResult(CommandOutcome.Success(result, () => ToText(result)));
        });

        return command;
    }

    private static Command CreateShow(CliHost host, GlobalOptions globals)
    {
        var rulesFile = RulesOption("Rule file to show. Default: the shipped rules.");
        var command = new Command("show",
            "Print the rules with every setting spelled out. Without --json the output is a valid rule file: " +
            "'sfo rules show > my-disk.json' starts a profile from the shipped rules.")
        {
            rulesFile
        };

        CommandRunner.SetHandler(command, host, globals, (ctx, ct) =>
        {
            var (rules, profile) = Load(ctx.ParseResult.GetValue(rulesFile));
            var indented = ScanRulesFile.Serialize(rules, indented: true);
            using var document = JsonDocument.Parse(indented);
            var result = new ShowResult(RulesView.From(profile), document.RootElement.Clone());
            return Task.FromResult(CommandOutcome.Success(result, () => indented));
        });

        return command;
    }

    private static DirectoryContent? Probe(IFileSystemAccessor fs, string folder) =>
        fs.DirectoryExists(folder)
            ? new DirectoryContent(fs.EnumerateFiles(folder).Select(f => FileTypeDetector.Detect(fs.GetExtension(f))))
            : null;

    private static PathCheck ToView(RuleCheck check, ScanRuleSet rules, bool exists)
    {
        var e = check.Evaluation;
        var rule = e.Rule is { } r
            ? new RuleView(e.RuleIndex!.Value, r.Match, ScanRulesFile.ActionName(r.Action),
                r.Action == ScanRuleAction.Shallow ? r.MaxDepth : null, r.Why)
            : null;
        var content = e.Files is { } files ? new ContentView(files, e.UserFiles ?? 0, e.ProgramFiles ?? 0) : null;
        var blocked = check.BlockedBy is { } b ? new BlockView(check.BlockedAt!, b.Decision, b.Reason) : null;
        return new PathCheck(check.Path, check.Root, e.Depth, exists, e.Decision, e.Basis, e.Reason, rule, content, check.Reached, blocked);
    }

    private static string ToText(CheckResult result)
    {
        var text = new StringBuilder();
        text.AppendLine($"Rules '{result.Rules.Name}' ({result.Rules.Source})");
        foreach (var c in result.Checks)
        {
            text.AppendLine($"{c.Decision.ToString().ToLowerInvariant(),-8} {c.Path}");
            text.AppendLine($"         {c.Reason}");
            if (c.BlockedBy is { } b)
                text.AppendLine($"         not reached: {b.Path} is {b.Decision.ToString().ToLowerInvariant()} ({b.Reason})");
        }
        return text.ToString().TrimEnd();
    }
}
