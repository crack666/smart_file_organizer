using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo library review &lt;job&gt;</c> and <c>sfo library propose &lt;job&gt;</c>: judge a tree by its structure only
/// and suggest improvements or a target structure (https://github.com/crack666/smart_file_organizer/issues/29).
/// Read-only for the judged tree: suggestions are data, nothing is moved or renamed.
/// </summary>
public static class LibraryCommand
{
    public const int DefaultLimit = 50;

    public sealed record FindingView(
        string Kind,
        string Impact,
        string Path,
        string RelativePath,
        string Reason,
        IReadOnlyList<string> Items,
        int More,
        int Directories,
        long Files,
        long Bytes,
        IReadOnlyDictionary<string, object?> Details)
    {
        public static FindingView From(LibraryFinding f) => new(
            f.Kind.ToCode(), f.Impact.ToCode(), f.Path, f.RelativePath, f.Reason, f.Items, f.More,
            f.Directories, f.Files, f.Bytes, f.Details);

        public string ToLine() =>
            $"{Impact,-6} {Kind,-21} {RelativePath}: {Reason}"
            + (Items.Count > 0 ? $"\n{"",29}{string.Join("; ", Items)}{(More > 0 ? $"; +{More} more" : string.Empty)}" : string.Empty);
    }

    public sealed record RoleView(string Path, string Role, string? ChildrenRole, string? Reason);

    public sealed record ChangeView(string Action, string Path, string? Target, string Reason, string? Finding);

    public sealed record AdviceView(
        string Status,
        string? Model,
        DateTime? CreatedAt,
        IReadOnlyList<AdviceKeep> Good,
        IReadOnlyList<RoleView> Roles,
        IReadOnlyList<ChangeView> Changes,
        IReadOnlyList<AdviceOutline> Outline,
        int Dropped,
        string? Error)
    {
        public static AdviceView From(LibraryAdvice a) => new(
            a.Status, a.Model, a.CreatedAt, a.Good,
            a.Roles.Select(r => new RoleView(r.Path, r.Role.ToCode(), r.ChildrenRole.ToCode(), r.Reason)).ToList(),
            a.Changes.Select(c => new ChangeView(c.Action.ToCode(), c.Path, c.Target, c.Reason, c.Finding.ToCode())).ToList(),
            a.Outline, a.Dropped, a.Error);
    }

    public sealed record ReviewResult(
        JobView Job,
        LibraryStructure Structure,
        IReadOnlyDictionary<string, int> ByKind,
        int ExcludedByExceptions,
        string Words,
        string? RoleNote,
        long JobId,
        int Total,
        int Limit,
        int Offset,
        int? NextOffset,
        IReadOnlyList<FindingView> Items,
        AdviceView Advice);

    public sealed record ProposeResult(
        JobView Job,
        string Status,
        string? Error,
        int Dropped,
        string? Path,
        TargetStructure Target);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var command = new Command("library", "Judge a folder tree by its structure, describe a library as a map, and find places in it for what a source holds. Changes nothing.");
        command.Subcommands.Add(CreateReview(host, globals));
        command.Subcommands.Add(CreatePropose(host, globals));
        command.Subcommands.Add(LibraryTargetCommands.CreateShow(host, globals));
        command.Subcommands.Add(LibraryTargetCommands.CreateMatch(host, globals));
        return command;
    }

    internal static Option<bool> NoModelOption() => NoModel();
    internal static Option<string?> ModelOption() => Model();
    internal static Option<bool> RefreshOption() => Refresh();
    internal static Option<string?> WordsOption() => Words();
    internal static LibraryWords LoadWordsFile(string? path) => LoadWords(path);

    private static Option<bool> NoModel() => new("--no-model")
    {
        Description = "Rules only: do not ask the local model."
    };

    private static Option<string?> Model() => new("--model")
    {
        Description = "Ollama model for this run. Default: Ollama.Model.",
        HelpName = "name"
    };

    private static Option<bool> Refresh() => new("--refresh")
    {
        Description = "Ask the model again even if an answer to the same facts is stored."
    };

    private static Option<string?> Words() => new("--words")
    {
        Description = "Word file that adds placeholder, catch-all and type words and lists exceptions (docs/library-review.md).",
        HelpName = "file"
    };

    private static Command CreateReview(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job to review (see sfo jobs). Best scanned with --structure-only.");
        var noModel = NoModel();
        var model = Model();
        var refresh = Refresh();
        var words = Words();
        var kinds = VerdictCodes.All<LibraryFindingKind>().ToArray();
        var kind = new Option<string[]>("--kind")
        {
            Description = $"Only findings of this kind: {string.Join(", ", kinds)}. Repeatable.",
            HelpName = "code"
        };
        kind.AcceptOnlyFromAmong(kinds);
        var limit = new Option<int>("--limit")
        {
            Description = "Findings per page, 1 to 1000.",
            DefaultValueFactory = _ => DefaultLimit
        };
        limit.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 1 or > 1000)
                r.AddError("--limit must be between 1 and 1000.");
        });
        var offset = new Option<int>("--offset")
        {
            Description = "Findings to skip (see next_offset in the result).",
            DefaultValueFactory = _ => 0
        };
        offset.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() < 0)
                r.AddError("--offset must not be negative.");
        });

        var command = new Command("review", "Findings about the structure of a job's tree, most important first, and the model's suggestions.")
        {
            job, noModel, model, refresh, kind, limit, offset, words
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var query = new LibraryReviewQuery
            {
                JobId = p.GetValue(job),
                Words = LoadWords(p.GetValue(words)),
                Kinds = (p.GetValue(kind) ?? []).Select(k => { VerdictCodes.TryParse<LibraryFindingKind>(k, out var v); return v; }).ToList(),
                Limit = p.GetValue(limit),
                Offset = p.GetValue(offset),
                UseModel = !p.GetValue(noModel),
                Refresh = p.GetValue(refresh),
                Model = p.GetValue(model)
            };

            LibraryReview review;
            try
            {
                review = await services.GetRequiredService<LibraryService>().ReviewAsync(query, ct);
            }
            catch (LibraryServiceException ex)
            {
                throw ToFailure(ex);
            }

            // The review is meant for the organised archive (role library). A source job is judged
            // too, because a medium can be worth a look before it is sorted, but the caller is told.
            string? roleNote = null;
            if (review.Job.Role != ScanRole.Library)
            {
                roleNote = $"Job {review.Job.Id} has the role {review.Job.Role.ToValue()}; library review is meant for a library job (sfo scan <path> --role library --structure-only).";
                ctx.Note(roleNote);
            }

            var result = new ReviewResult(JobView.From(review.Job), review.Structure, review.ByKind, review.ExcludedByExceptions,
                review.WordsSource, roleNote, review.Job.Id, review.Total, review.Limit, review.Offset, review.NextOffset,
                review.Items.Select(FindingView.From).ToList(), AdviceView.From(review.Advice));

            return review.Advice.Status == AdviceStatus.Unavailable
                ? CommandOutcome.Fail(ExitCodes.OllamaUnavailable, ErrorCodes.OllamaUnavailable,
                    "Ollama is not reachable; the findings of the rules are in the result. Use --no-model for the rules alone.",
                    result, () => Render(result))
                : CommandOutcome.Success(result, () => Render(result));
        });
        return command;
    }

    private static Command CreatePropose(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Source job (see sfo jobs) whose content the target structure is for.");
        var noModel = NoModel();
        var model = Model();
        var refresh = Refresh();
        var words = Words();
        var output = new Option<string?>("--out")
        {
            Description = "Write the target file here (replaced if it exists). Must not lie inside the job's folder.",
            HelpName = "file"
        };
        var root = new Option<string?>("--root")
        {
            Description = "Folder the targets go under, written into the file. Default: left empty for you to fill in.",
            HelpName = "dir"
        };
        var lang = new Option<string>("--lang")
        {
            Description = "Language of folder names when the rules build the structure (without the model): en or de.",
            DefaultValueFactory = _ => "en"
        };
        lang.AcceptOnlyFromAmong("en", "de");

        var command = new Command("propose", "Propose a target structure for a source without an ordered library, as an editable target file.")
        {
            job, noModel, model, refresh, output, root, lang, words
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var rootPath = p.GetValue(root) is { } r ? Path.GetFullPath(r) : null;
            var query = new LibraryProposeQuery
            {
                JobId = p.GetValue(job),
                Words = LoadWords(p.GetValue(words)),
                UseModel = !p.GetValue(noModel),
                Refresh = p.GetValue(refresh),
                Model = p.GetValue(model),
                Root = rootPath,
                Language = p.GetValue(lang) ?? "en"
            };

            LibraryProposal proposal;
            try
            {
                proposal = await services.GetRequiredService<LibraryService>().ProposeAsync(query, ct);
            }
            catch (LibraryServiceException ex)
            {
                throw ToFailure(ex);
            }

            string? written = null;
            if (p.GetValue(output) is { } outPath)
            {
                written = Path.GetFullPath(outPath);
                if (IsInside(written, proposal.Job.RootPath))
                    throw new CliFailureException(ExitCodes.Failure, ErrorCodes.OutputInsideJob,
                        $"{written} lies inside the job's folder {proposal.Job.RootPath}; this command never writes there.");
                var folder = Path.GetDirectoryName(written);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                    throw new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, $"Folder not found: {folder}");
                try
                {
                    await File.WriteAllTextAsync(written, TargetStructureFile.Serialize(proposal.Target) + Environment.NewLine, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ExportFailed, $"Could not write {written}: {ex.Message}");
                }
            }

            if (proposal.Status == AdviceStatus.Unavailable)
                ctx.Note("Ollama is not reachable; the target structure was built by the rules.");
            else if (proposal.Status == AdviceStatus.Failed)
                ctx.Note($"The model gave no usable answer ({proposal.Error}); the target structure was built by the rules.");

            var result = new ProposeResult(JobView.From(proposal.Job), proposal.Status, proposal.Error, proposal.Dropped, written, proposal.Target);
            return CommandOutcome.Success(result, () => RenderProposal(result));
        });
        return command;
    }

    private static LibraryWords LoadWords(string? path)
    {
        if (path is null) return LibraryWords.Default;
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, $"Word file not found: {full}");
        try
        {
            return LibraryWords.LoadWithDefaults(full);
        }
        catch (LibraryWordsException ex)
        {
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.WordsInvalid, ex.Message);
        }
    }

    internal static CliFailureException ToFailure(LibraryServiceException ex) => ex.Failure switch
    {
        LibraryFailure.JobNotFound => new CliFailureException(ExitCodes.Failure, ErrorCodes.JobNotFound, ex.Message),
        LibraryFailure.WrongRole => new CliFailureException(ExitCodes.Failure, ErrorCodes.WrongRole, ex.Message),
        LibraryFailure.PathNotFound => new CliFailureException(ExitCodes.Failure, ErrorCodes.PathNotFound, ex.Message),
        LibraryFailure.BundleNotFound => new CliFailureException(ExitCodes.Failure, ErrorCodes.BundleNotFound, ex.Message),
        LibraryFailure.PatternsInvalid => new CliFailureException(ExitCodes.Failure, ErrorCodes.LibraryPatternsInvalid, ex.Message),
        LibraryFailure.OptionsInvalid => new CliFailureException(ExitCodes.Failure, ErrorCodes.SettingsInvalid, ex.Message),
        LibraryFailure.LibraryUnreachable => new CliFailureException(ExitCodes.Failure, ErrorCodes.LibraryUnreachable, ex.Message),
        _ => new CliFailureException(ExitCodes.Failure, ErrorCodes.ScanNotCompleted, ex.Message)
    };

    private static bool IsInside(string path, string folder)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return path.Equals(f, comparison) || path.StartsWith(f + Path.DirectorySeparatorChar, comparison);
    }

    private static string Render(ReviewResult r)
    {
        var sb = new StringBuilder();
        var s = r.Structure;
        sb.AppendLine($"Job {r.JobId}: {s.Directories} folders, {s.Files} files, {ReadFormat.Size(s.Bytes)}"
                      + (s.Dumps > 0 ? $"; {s.Dumps} dump(s) with {s.DumpDirectories} folders left out" : string.Empty)
                      + (s.Complete ? string.Empty : "; some folders were skipped or recorded shallow"));
        sb.AppendLine($"{r.Total} finding(s): {string.Join(", ", r.ByKind.Select(k => $"{k.Value} {k.Key}"))}");
        sb.AppendLine();
        foreach (var f in r.Items)
            sb.AppendLine(f.ToLine());
        if (r.NextOffset is { } next)
            sb.AppendLine($"... more with --offset {next}");

        var a = r.Advice;
        sb.AppendLine();
        sb.AppendLine($"Model: {a.Status}" + (a.Model is null ? string.Empty : $" ({a.Model})") + (a.Error is null ? string.Empty : $": {a.Error}"));
        foreach (var g in a.Good)
            sb.AppendLine($"  good    {g.Path}: {g.Reason}");
        foreach (var c in a.Changes)
            sb.AppendLine($"  {c.Action,-7} {c.Path}" + (c.Target is null ? string.Empty : $" -> {c.Target}") + $": {c.Reason}");
        foreach (var o in a.Outline)
            sb.AppendLine($"  outline {o.Path}" + (o.Pattern is null ? string.Empty : $" [{o.Pattern}]") + $": {o.Purpose}");
        return sb.ToString();
    }

    private static string RenderProposal(ProposeResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Target structure for job {r.Job.Id} ({r.Target.Origin.ToCode()}{(r.Target.Model is null ? string.Empty : ", " + r.Target.Model)}):");
        foreach (var t in r.Target.Targets)
            sb.AppendLine($"  {t.Path}" + (t.Pattern is null ? string.Empty : $"/{t.Pattern}") + $"  [{string.Join(", ", t.Holds)}]  {t.Reason}");
        sb.AppendLine(r.Path is null ? "Not written; use --out <file>." : $"Written to {r.Path}");
        return sb.ToString();
    }
}
