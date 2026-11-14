using System.CommandLine;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo library show &lt;library-job&gt;</c> and <c>sfo library match &lt;source-job&gt; &lt;path&gt;</c>: the target map of a
/// library and the best places in it for a file, folder or bundle of a source
/// (https://github.com/crack666/smart_file_organizer/issues/25). Read-only for source and library; proposals are data.
/// </summary>
public static class LibraryTargetCommands
{
    public const int DefaultShowLimit = 100;

    public sealed record PeriodView(string? From, string? To, string? TimeSource);

    public sealed record NamePeriodView(string From, string To);

    public sealed record ChildrenPatternView(string Template, string NamePattern, int Count, int Of, IReadOnlyList<string> Examples);

    public sealed record FindingRef(string Kind, string Impact, string RelativePath);

    public sealed record MapEntryView(
        string Path,
        string RelativePath,
        string Name,
        int Depth,
        string? Parent,
        string Role,
        string? ChildrenRole,
        string Purpose,
        string PurposeSource,
        long Files,
        long DirectFiles,
        int Subdirs,
        long Bytes,
        IReadOnlyDictionary<string, long> ByFileType,
        PeriodView Period,
        NamePeriodView? NamePeriod,
        ChildrenPatternView? ChildrenPattern,
        string? Pattern,
        IReadOnlyList<FindingRef> Findings,
        bool Weak,
        string? DumpKind,
        string? Closed = null,
        string? ClosedAt = null)
    {
        public static MapEntryView From(LibraryMapEntry e) => new(
            e.FullPath, e.RelativePath, e.Name, e.Depth, e.Parent?.RelativePath,
            e.Role.ToCode(), e.ChildrenRole.ToCode(), e.Purpose, e.PurposeSource.ToCode(),
            e.Files, e.DirectFiles, e.Subdirs, e.Bytes, ByType(e.Types),
            new PeriodView(BundlesCommand.Local(e.From), BundlesCommand.Local(e.To), e.TimeSource.ToCode()),
            e.NamePeriod is { } np ? new NamePeriodView(LibraryMap.Day(np.From), LibraryMap.Day(np.To)) : null,
            e.ChildrenPattern is { } p ? new ChildrenPatternView(p.Template, p.NamePattern, p.Count, p.Of, p.Examples) : null,
            e.Pattern,
            Refs(e.Findings),
            e.Findings.Count > 0,
            e.Dump.ToCode(),
            e.Closed.ToCode(),
            e.ClosedAt);

        public string ToLine() =>
            $"{new string(' ', Depth * 2)}{(Depth == 0 ? "." : Name)}  [{Role}{(ChildrenRole is null ? "" : " of " + ChildrenRole)}]  {Files} files"
            + (Pattern is null ? string.Empty : $"  names: {Pattern}")
            + (Weak ? $"  weak: {string.Join(", ", Findings.Select(f => f.Kind).Distinct())}" : string.Empty)
            + (Closed is null ? string.Empty : ClosedAt == RelativePath ? $"  closed: {Closed}" : $"  closed: {Closed} of {ClosedAt}")
            + $"\n{new string(' ', Depth * 2 + 2)}{Purpose}";
    }

    public sealed record PatternsView(string? File, bool Created, IReadOnlyList<TargetEntry> Targets, IReadOnlyList<string> Ignored, string? Note)
    {
        public static PatternsView From(LibraryPatternsInfo p) =>
            new(p.File, p.Created, p.Targets?.Targets ?? [], p.Ignored, p.Note);
    }

    public sealed record ModelView(string Status, string? Model, string? Error, int Dropped)
    {
        public static ModelView From(LibraryModelPart m) => new(m.Status, m.Model, m.Error, m.Dropped);
    }

    public sealed record ShowResult(
        JobView Job,
        PatternsView Patterns,
        long JobId,
        int Total,
        int Limit,
        int Offset,
        int? NextOffset,
        IReadOnlyList<MapEntryView> Items,
        ModelView Advice);

    public sealed record SignalView(string Signal, double Value, double Weight, bool Agrees, bool Contradicts, string Detail);

    public sealed record WeakPlaceView(IReadOnlyList<FindingRef> Findings, string Note);

    public sealed record CandidateView(
        int Rank,
        string Placement,
        string Target,
        string TargetRelative,
        string TargetRole,
        string TargetPurpose,
        string Destination,
        string DestinationRelative,
        bool KeepsName,
        double Score,
        string Certainty,
        string Reason,
        IReadOnlyList<SignalView> Signals,
        WeakPlaceView? WeakPlace,
        long? LibraryJobId = null,
        ContentView? Content = null);

    /// <summary>What a folder or the unit shows: fact code to values with counts, and the same as one line.</summary>
    public sealed record DescriptionView(int Sampled, int Described, IReadOnlyDictionary<string, IReadOnlyList<DescriptionValue>> Values, string Text)
    {
        public static DescriptionView? From(ContentDescription? d) =>
            d is null ? null : new DescriptionView(d.Sampled, d.Described, d.ByCode(), d.Text());
    }

    /// <summary>The content check of one candidate: fits, contradicts or unclear, why, and what the folder shows.</summary>
    /// <param name="Shared">The concrete feature both share (Issue 85: <c>fits</c> needs one); null when none was named.</param>
    /// <param name="SharedKind">A <c>SharedFeatureKind</c> code: occasion, person, place, topic, issuer or none.</param>
    public sealed record ContentView(string Fit, string Reason, DescriptionView? Folder, string? Shared = null, string? SharedKind = null);

    /// <summary>Whether the content was checked; <c>status</c> is a <see cref="ContentCheckStatus"/> code.</summary>
    public sealed record ContentCheckView(string Status, string? Model, string? Note);

    /// <summary>A folder time or names chose, dropped because its content contradicts the unit.</summary>
    public sealed record RejectedView(string TargetRelative, string Reason, DescriptionView? Folder);


    public sealed record NewFolderView(
        string Placement,
        string Parent,
        string ParentRelative,
        string Name,
        string NameSource,
        bool NameComplete,
        string? Pattern,
        string Destination,
        string DestinationRelative,
        string Certainty,
        string Reason,
        WeakPlaceView? WeakPlace,
        long? LibraryJobId = null);

    /// <summary>One library root: free space and what the unit needs there; <c>free_bytes</c> null and <c>space</c> unknown when it cannot be told.</summary>
    public sealed record SpaceView(long LibraryJobId, string RootPath, long? FreeBytes, long NeededBytes, string Space);

    public sealed record UnitView(
        string Kind,
        string? Path,
        long? BundleId,
        string? BundleKind,
        string Name,
        bool KeepWhole,
        string KeepReason,
        string Role,
        long Files,
        IReadOnlyDictionary<string, long> ByFileType,
        PeriodView Period,
        IReadOnlyList<string> Words,
        IReadOnlyDictionary<string, long>? Comparison,
        DescriptionView? Description = null);

    public sealed record ComparisonView(long ComparisonId, DateTime ComparedAt, bool Complete);

    public sealed record MatchView(
        JobView Source,
        JobView? Library,
        string? LibraryFrom,
        string Status,
        string? Message,
        UnitView Unit,
        ComparisonView? Comparison,
        int Considered,
        IReadOnlyList<CandidateView> Candidates,
        NewFolderView? NewFolder,
        PatternsView? Patterns,
        ModelView Advice,
        IReadOnlyList<string> Notes,
        IReadOnlyList<JobView>? Libraries = null,
        IReadOnlyList<SpaceView>? Space = null,
        ContentCheckView? ContentCheck = null,
        IReadOnlyList<RejectedView>? Rejected = null,
        IReadOnlyList<CandidateView>? Unconfirmed = null,
        string? NoTarget = null,
        string? Excluded = null,
        IReadOnlyList<PartView>? Parts = null);

    /// <summary>
    /// One part of a unit whose files do not belong together (Issue 85), with its own answer: <c>status</c> is
    /// <c>existing_folder</c>, <c>new_folder</c> or <c>no_target</c>; <c>destination_relative</c> null without a place.
    /// </summary>
    public sealed record PartView(int Files, string Label, IReadOnlyList<string> Examples, string Status, long? LibraryJobId,
        string? Destination, string? DestinationRelative, string? Certainty, string Reason);

    // ------------------------------------------------------------------ show

    public static Command CreateShow(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Library job (scanned with --role library) to describe.");
        var under = new Option<string?>("--under")
        {
            Description = "Only this folder and its subtree: relative to the library root, or a full path.",
            HelpName = "path"
        };
        var depth = new Option<int?>("--depth")
        {
            Description = "Levels below --under (or the root); 0 lists only that folder.",
            HelpName = "n"
        };
        depth.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int?>() is < 0)
                r.AddError("--depth must not be negative.");
        });
        var search = new Option<string?>("--search")
        {
            Description = "Only folders whose name contains this text (case and umlauts aside).",
            HelpName = "text"
        };
        var from = DateOption("--from", "Only folders whose period reaches this date or later: 2015, 2015-07 or 2015-07-12.");
        var to = DateOption("--to", "Only folders whose period starts on or before this date: 2015, 2015-07 or 2015-07-12.");
        var roles = VerdictCodes.All<FolderRole>().ToArray();
        var role = new Option<string[]>("--role")
        {
            Description = $"Only folders with this role: {string.Join(", ", roles)}. Repeatable.",
            HelpName = "code"
        };
        role.AcceptOnlyFromAmong(roles);
        var limit = new Option<int>("--limit")
        {
            Description = "Folders per page, 1 to 1000.",
            DefaultValueFactory = _ => DefaultShowLimit
        };
        limit.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 1 or > 1000)
                r.AddError("--limit must be between 1 and 1000.");
        });
        var offset = new Option<int>("--offset")
        {
            Description = "Folders to skip (see next_offset in the result).",
            DefaultValueFactory = _ => 0
        };
        offset.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() < 0)
                r.AddError("--offset must not be negative.");
        });
        var reset = new Option<bool>("--reset-patterns")
        {
            Description = "Write the patterns file of this library anew from its folders, replacing hand edits."
        };
        var noModel = LibraryCommand.NoModelOption();
        var model = LibraryCommand.ModelOption();
        var refresh = LibraryCommand.RefreshOption();
        var words = LibraryCommand.WordsOption();

        var command = new Command("show", "The target map of a library: each folder with role, purpose, period, kinds of files and how its subfolders are named.")
        {
            job, under, depth, search, from, to, role, limit, offset, reset, noModel, model, refresh, words
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var query = new LibraryShowQuery
            {
                JobId = p.GetValue(job),
                Words = LibraryCommand.LoadWordsFile(p.GetValue(words)),
                Under = p.GetValue(under),
                Depth = p.GetValue(depth),
                Search = p.GetValue(search),
                From = ParseDate(p.GetValue(from), end: false),
                To = ParseDate(p.GetValue(to), end: true),
                Roles = (p.GetValue(role) ?? []).Select(r => { VerdictCodes.TryParse<FolderRole>(r, out var v); return v; }).ToList(),
                Limit = p.GetValue(limit),
                Offset = p.GetValue(offset),
                UseModel = !p.GetValue(noModel),
                Refresh = p.GetValue(refresh),
                Model = p.GetValue(model),
                ResetPatterns = p.GetValue(reset)
            };

            LibraryShow show;
            try
            {
                show = await services.GetRequiredService<LibraryTargetService>().ShowAsync(query, ct);
            }
            catch (LibraryServiceException ex)
            {
                throw LibraryCommand.ToFailure(ex);
            }

            if (show.Patterns.Note is { } note) ctx.Note(note);
            var result = new ShowResult(JobView.From(show.Job), PatternsView.From(show.Patterns), show.Job.Id, show.Total, show.Limit,
                show.Offset, show.NextOffset, show.Items.Select(MapEntryView.From).ToList(), ModelView.From(show.Advice));

            return show.Advice.Status == AdviceStatus.Unavailable
                ? CommandOutcome.Fail(ExitCodes.OllamaUnavailable, ErrorCodes.OllamaUnavailable,
                    "Ollama is not reachable; the map of the rules is in the result. Use --no-model for the rules alone.",
                    result, () => RenderShow(result))
                : CommandOutcome.Success(result, () => RenderShow(result));
        });
        return command;
    }

    // ------------------------------------------------------------------ match

    public static Command CreateMatch(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Source job (see sfo jobs) that holds what needs a place.");
        var path = new Argument<string?>("path")
        {
            Description = "File or folder of the source: relative to its root, or a full path. Leave out with --bundle.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var bundle = new Option<long?>("--bundle")
        {
            Description = "Match a bundle as a whole (see sfo bundles list) instead of a path.",
            HelpName = "id"
        };
        var against = new Option<long[]>("--against")
        {
            Description = "Library job. Repeatable: each is matched and the best places of all are listed. Default: the libraries of the latest sfo compare, else the newest library job.",
            HelpName = "job"
        };
        var limit = new Option<int?>("--limit")
        {
            Description = "Proposals to list, 1 to 50. Default: LibraryMatch.MaxCandidates (3).",
            HelpName = "n"
        };
        limit.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int?>() is < 1 or > 50)
                r.AddError("--limit must be between 1 and 50.");
        });
        var noModel = LibraryCommand.NoModelOption();
        var model = LibraryCommand.ModelOption();
        var refresh = LibraryCommand.RefreshOption();
        var words = LibraryCommand.WordsOption();

        var command = new Command("match", "The best existing folders of the library for a file, folder or bundle of a source, each with its reasons; or a new folder in the style of the library.")
        {
            job, path, bundle, against, limit, noModel, model, refresh, words
        };
        command.Validators.Add(r =>
        {
            var hasPath = r.GetValue(path) is not null;
            var hasBundle = r.GetValue(bundle) is not null;
            if (hasPath == hasBundle)
                r.AddError("Give either a path or --bundle <id>.");
        });

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var query = new LibraryMatchQuery
            {
                SourceJobId = p.GetValue(job),
                Path = p.GetValue(path),
                BundleId = p.GetValue(bundle),
                Libraries = (p.GetValue(against) ?? []).Distinct().ToList(),
                Words = LibraryCommand.LoadWordsFile(p.GetValue(words)),
                Limit = p.GetValue(limit),
                UseModel = !p.GetValue(noModel),
                Refresh = p.GetValue(refresh),
                Model = p.GetValue(model)
            };

            LibraryMatchAnswer answer;
            try
            {
                answer = await services.GetRequiredService<LibraryTargetService>().MatchAsync(query, ct);
            }
            catch (LibraryServiceException ex)
            {
                throw LibraryCommand.ToFailure(ex);
            }

            foreach (var note in answer.Notes)
                ctx.Note(note);
            if (answer.Message is { } message)
                ctx.Note(message);
            if (answer.Advice.Status == AdviceStatus.Unavailable)
                ctx.Note("Ollama is not reachable; the new folder keeps the name of the rules.");
            else if (answer.Advice.Status == AdviceStatus.Failed)
                ctx.Note($"The model gave no usable name ({answer.Advice.Error}); the new folder keeps the name of the rules.");

            var view = ToView(answer);
            return CommandOutcome.Success(view, () => RenderMatch(view));
        });
        return command;
    }

    private static MatchView ToView(LibraryMatchAnswer a)
    {
        static string Full(string? root, string relative) => root is null ? relative
            : relative == "." ? root
            : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        ScanJob? LibraryOf(int candidate) =>
            a.CandidateLibraries is { } libs && candidate < libs.Count ? libs[candidate] : a.Library;

        var u = a.Unit;
        var unit = new UnitView(u.Kind.ToCode(), u.FullPath, u.BundleId, u.BundleKind.ToCode(), u.Name, u.KeepWhole, u.KeepReason,
            u.Role.ToCode(), u.Files, ByType(u.Types),
            new PeriodView(BundlesCommand.Local(u.From), BundlesCommand.Local(u.To), u.TimeSource.ToCode()),
            u.Words, u.States, DescriptionView.From(a.Result?.Content?.Unit));

        CandidateView View(MatchCandidate c, int i, ScanJob? library) => new(
            i + 1, c.Placement.ToCode(), c.Target.FullPath, c.Target.RelativePath, c.Target.Role.ToCode(), c.Target.Purpose,
            Full(library?.RootPath, c.Destination), c.Destination, u.KeepWhole, c.Score, c.Certainty.ToCode(), c.Reason,
            c.Signals.Select(s => new SignalView(s.Signal.ToCode(), Math.Round(s.Value, 3), s.Weight, s.Agrees, s.Contradicts, s.Detail)).ToList(),
            Weak(c.Weak), library?.Id,
            c.Content is { } cc ? new ContentView(cc.Fit.ToCode(), cc.Reason, DescriptionView.From(cc.Folder), cc.Shared, cc.SharedKind.ToCode()) : null);

        var candidates = (a.Result?.Candidates ?? []).Select((c, i) => View(c, i, LibraryOf(i))).ToList();
        var unconfirmed = (a.Result?.Unconfirmed ?? []).Select((c, i) =>
            View(c, i, a.UnconfirmedLibraries is { } libs && i < libs.Count ? libs[i] : a.Library)).ToList();

        NewFolderView? newFolder = null;
        if (a.Result?.NewFolder is { } n)
        {
            var library = a.NewFolderLibrary ?? a.Library;
            newFolder = new NewFolderView(MatchPlacement.NewFolder.ToCode(), n.Parent.FullPath, n.Parent.RelativePath, n.Name,
                n.NameSource.ToCode(), n.NameComplete, n.Pattern, Full(library?.RootPath, n.Destination), n.Destination, n.Certainty.ToCode(), n.Reason,
                Weak(n.Weak), library?.Id);
        }

        return new MatchView(
            JobView.From(a.Source),
            a.Library is null ? null : JobView.From(a.Library),
            a.Choice.ToCode(),
            a.Status.ToCode(),
            a.Message,
            unit,
            a.Comparison is { } c ? new ComparisonView(c.Id, c.ComparedAt, c.Complete) : null,
            a.Result?.Considered ?? 0,
            candidates,
            newFolder,
            a.Patterns is null ? null : PatternsView.From(a.Patterns),
            ModelView.From(a.Advice),
            a.Notes,
            (a.Libraries ?? []).Select(JobView.From).ToList(),
            (a.Space ?? []).Select(s => new SpaceView(s.LibraryJobId, s.RootPath, s.FreeBytes, s.NeededBytes, s.Space.ToCode())).ToList(),
            a.Result?.Content is { } check
                ? new ContentCheckView(check.Status.ToCode(), check.Status == ContentCheckStatus.Checked ? check.Note : null,
                    check.Status == ContentCheckStatus.Checked ? null : check.Note)
                : null,
            (a.Result?.Rejected ?? []).Select(r => new RejectedView(r.Target.RelativePath,
                r.Content?.Reason ?? "the content does not fit", DescriptionView.From(r.Content?.Folder))).ToList(),
            unconfirmed,
            a.Result?.NoTarget,
            a.Excluded,
            a.Parts?.Select(PartOf).ToList());
    }

    private static PartView PartOf(LibraryMatchPart p)
    {
        var examples = p.Files.Take(3).Select(f => f.RelativePath.Replace('\\', '/')).ToList();
        var r = p.Answer?.Result;
        if (r?.Candidates is { Count: > 0 } c && p.Answer!.CandidateLibraries is { Count: > 0 } libs)
            return new PartView(p.Files.Count, p.Label, examples, "existing_folder", libs[0].Id,
                Path.Combine(libs[0].RootPath, c[0].Destination.Replace('/', Path.DirectorySeparatorChar)), c[0].Destination,
                c[0].Certainty.ToCode(), c[0].Reason);
        if (r?.NewFolder is { } n && p.Answer!.NewFolderLibrary is { } home)
            return new PartView(p.Files.Count, p.Label, examples, "new_folder", home.Id,
                Path.Combine(home.RootPath, n.Destination.Replace('/', Path.DirectorySeparatorChar)), n.Destination, n.Certainty.ToCode(), n.Reason);
        return new PartView(p.Files.Count, p.Label, examples, "no_target", null, null, null, null,
            p.NotMatched ?? r?.NoTarget ?? "No folder of the libraries fits these files.");
    }

    private static WeakPlaceView? Weak(WeakPlace? w) => w is null ? null : new WeakPlaceView(Refs(w.Findings), w.Note);

    private static IReadOnlyList<FindingRef> Refs(IEnumerable<LibraryFinding> findings) =>
        findings.Select(f => new FindingRef(f.Kind.ToCode(), f.Impact.ToCode(), f.RelativePath)).ToList();

    private static IReadOnlyDictionary<string, long> ByType(long[] types) =>
        Enumerable.Range(0, types.Length)
            .Where(t => types[t] > 0)
            .OrderByDescending(t => types[t]).ThenBy(t => t)
            .ToDictionary(t => ((FileType)t).ToString().ToLowerInvariant(), t => types[t]);

    // ------------------------------------------------------------------ dates

    private static Option<string?> DateOption(string name, string description)
    {
        var option = new Option<string?>(name) { Description = description, HelpName = "date" };
        option.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<string?>() is { } v && ParseDate(v, end: false) is null)
                r.AddError($"{name} must be a year (2015), a month (2015-07) or a day (2015-07-12).");
        });
        return option;
    }

    /// <summary>A year, month or day as the start (or, with <paramref name="end"/>, the last moment) of that period.</summary>
    public static DateTime? ParseDate(string? text, bool end)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return end ? day.AddDays(1).AddTicks(-1) : day;
        if (DateTime.TryParseExact(t, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
            return end ? month.AddMonths(1).AddTicks(-1) : month;
        if (DateTime.TryParseExact(t, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var year))
            return end ? year.AddYears(1).AddTicks(-1) : year;
        return null;
    }

    // ------------------------------------------------------------------ text

    private static string RenderShow(ShowResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Library job {r.JobId}: {r.Total} folder(s)" + (r.NextOffset is { } next ? $", more with --offset {next}" : string.Empty));
        foreach (var e in r.Items)
            sb.AppendLine(e.ToLine());
        sb.AppendLine();
        sb.AppendLine(r.Patterns.File is null
            ? "Patterns: not written."
            : $"Patterns: {r.Patterns.File}{(r.Patterns.Created ? " (written now)" : string.Empty)}");
        foreach (var t in r.Patterns.Targets)
            sb.AppendLine($"  {t.Path}: {t.Pattern}");
        sb.AppendLine($"Model: {r.Advice.Status}" + (r.Advice.Error is null ? string.Empty : $": {r.Advice.Error}"));
        return sb.ToString();
    }

    private static string RenderMatch(MatchView r)
    {
        var sb = new StringBuilder();
        if (r.Status == MatchStatus.NoLibrary.ToCode())
        {
            sb.AppendLine(r.Message);
            return sb.ToString();
        }
        var u = r.Unit;
        sb.AppendLine($"{u.Kind} '{u.Name}': {u.Files} files" + (u.KeepWhole ? ", kept whole under its own name" : string.Empty));
        if (u.Description is { } d)
            sb.AppendLine($"   shows: {d.Text}");
        sb.AppendLine(r.Libraries is { Count: > 1 } libraries
            ? $"Library jobs {string.Join(", ", libraries.Select(l => l.Id))} ({r.LibraryFrom})"
            : $"Library job {r.Library!.Id} ({r.LibraryFrom})");
        foreach (var c in r.Candidates)
        {
            sb.AppendLine($"{c.Rank}. {c.Placement,-13} {c.Destination}  [{c.Certainty}, {c.Score.ToString("0.00", CultureInfo.InvariantCulture)}]");
            sb.AppendLine($"   {c.Reason}");
            if (c.WeakPlace is { } w) sb.AppendLine($"   {w.Note}");
        }
        foreach (var x in r.Rejected ?? [])
            sb.AppendLine($"   not {x.TargetRelative}: {x.Reason}");
        foreach (var x in r.Unconfirmed ?? [])
            sb.AppendLine($"   unconfirmed {x.DestinationRelative}: {x.Reason}");
        if (r.NoTarget is { } none)
            sb.AppendLine($"no_target     {none}");
        foreach (var p in r.Parts ?? [])
        {
            sb.AppendLine($"  part: {p.Files} files, {p.Label} -> {p.DestinationRelative ?? "none"} ({p.Status}{(p.Certainty is { } pc ? ", " + pc : string.Empty)})");
            sb.AppendLine($"     {p.Reason}");
        }
        if (r.NewFolder is { } n)
        {
            sb.AppendLine($"new_folder    {n.Destination}  [{n.Certainty}, name by {n.NameSource}]");
            sb.AppendLine($"   {n.Reason}");
            if (n.WeakPlace is { } w) sb.AppendLine($"   {w.Note}");
        }
        foreach (var s in r.Space ?? [])
            sb.AppendLine($"Space at {s.RootPath}: {(s.FreeBytes is { } free ? ReadFormat.Size(free) : "unknown")} free, {ReadFormat.Size(s.NeededBytes)} needed ({s.Space})");
        foreach (var note in r.Notes)
            sb.AppendLine($"Note: {note}");
        return sb.ToString();
    }
}
