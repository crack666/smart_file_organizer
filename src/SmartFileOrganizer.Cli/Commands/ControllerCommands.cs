using System.CommandLine;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// The commands of a controlling model (https://github.com/crack666/smart_file_organizer/issues/95): sfo gives the facts,
/// the controller decides. <c>sfo findings candidates</c> lists every finding that should move with the candidate folders
/// of the rules; <c>sfo library folder</c> looks into one folder; <c>sfo library describe</c> lets the local model describe
/// one folder on demand; <c>sfo findings place</c> records the decision. Only <c>library describe</c> calls a model.
/// docs/controller-guide.md describes the workflow.
/// </summary>
public static class ControllerCommands
{
    public const int DefaultMaxKb = 256;
    public const int MaxMaxKb = 4096;

    // ------------------------------------------------------------------ views

    public sealed record PeriodView(string? From, string? To, string? TimeSource);

    /// <summary>What files show by their stored descriptions; <c>described</c> files of <c>sampled</c> carried one.</summary>
    public sealed record DescribedView(int Sampled, int Described, IReadOnlyDictionary<string, IReadOnlyList<DescriptionValue>> Values, string Text)
    {
        public static DescribedView From(ContentDescription d) => new(d.Sampled, d.Described, d.ByCode(), d.Text());
    }

    public sealed record SensitiveFileView(string RelativePath, IReadOnlyList<string> Markers);

    /// <summary>A library folder as the scan holds it. A sensitive folder has no names and no description.</summary>
    /// <param name="Described">Whether a description of the folder is stored (<c>folder_descriptions</c>).</param>
    public sealed record FolderView(
        long LibraryJobId,
        string Path,
        string RelativePath,
        string Role,
        string Purpose,
        long Files,
        long DirectFiles,
        int Subdirs,
        long Bytes,
        IReadOnlyDictionary<string, long> ByFileType,
        PeriodView Period,
        string? Pattern,
        bool Sensitive,
        IReadOnlyList<string> Markers,
        IReadOnlyList<string> FileNames,
        IReadOnlyList<string> Subfolders,
        bool Described,
        DescribedView? Description,
        DateTime? DescribedAt)
    {
        public static FolderView From(FolderFacts f) => new(
            f.Library.Id, f.Entry.FullPath, f.Entry.RelativePath, f.Entry.Role.ToCode(), f.Sensitive ? string.Empty : f.Entry.Purpose,
            f.Entry.Files, f.Entry.DirectFiles, f.Entry.Subdirs, f.Entry.Bytes, ByType(f.Entry.Types),
            new PeriodView(BundlesCommand.Local(f.Entry.From), BundlesCommand.Local(f.Entry.To), f.Entry.TimeSource.ToCode()),
            f.Entry.Pattern, f.Sensitive, SensitiveMarkerNames.ToTokens(f.Markers), f.FileNames, f.Subfolders,
            f.Description is not null, f.Description is { } d ? DescribedView.From(d) : null, f.DescribedAt);
    }

    /// <summary>One candidate folder of the rules: where the files would end up, the score and reasons of the rules, and the folder.</summary>
    public sealed record CandidateFolderView(
        int Rank, string Placement, string DestinationRelative, double Score, string Certainty, string Reason,
        IReadOnlyList<string> Agrees, IReadOnlyList<string> Contradicts, FolderView Folder);

    public sealed record NewFolderHintView(long LibraryJobId, string ParentRelative, string Name, bool NameComplete, string? Pattern);

    /// <summary>The files of a finding, or one part of a mixed finding (<c>part</c> as <c>sfo findings place --part</c> takes it).</summary>
    public sealed record GroupView(
        int? Part,
        int Files,
        long Bytes,
        string? Label,
        IReadOnlyDictionary<string, long> ByFileType,
        PeriodView Period,
        IReadOnlyList<string> Examples,
        DescribedView Described,
        IReadOnlyList<CandidateFolderView> Candidates,
        NewFolderHintView? NewFolder,
        string? NotMatched);

    public sealed record StoredTargetView(string Status, string? DecidedBy, long? LibraryJobId, string? RelativePath);

    public sealed record CandidateItemView(
        string Key,
        string Kind,
        string State,
        string Title,
        string? RelativePath,
        string? BundleKind,
        long Files,
        long Bytes,
        IReadOnlyDictionary<string, long> ByFileType,
        PeriodView? Period,
        string UnitName,
        bool KeepWhole,
        IReadOnlyList<string> Examples,
        IReadOnlyList<SensitiveFileView> SensitiveFiles,
        StoredTargetView? Stored,
        string? Excluded,
        bool Split,
        IReadOnlyList<GroupView> Groups);

    public sealed record LibraryView(long LibraryJobId, string RootPath, bool Reachable, long? FreeBytes, string InboxFolder);

    public sealed record CandidatesResult(
        long JobId,
        string Status,
        string? Message,
        IReadOnlyList<LibraryView> Libraries,
        int Total,
        int Offset,
        int? NextOffset,
        bool Truncated,
        IReadOnlyList<CandidateItemView> Items);

    public sealed record PlaceItemView(
        string Key, int? Part, string? Status, long? LibraryJobId, string? Path, string? RelativePath, string? Reason, bool Withdrawn, string DecidedBy);

    public sealed record PlaceResult(long JobId, int Stored, int Withdrawn, IReadOnlyList<PlaceItemView> Items);

    public sealed record RefusalView(int Index, string Key, int? Part, IReadOnlyList<string> Problems, string Message);

    public sealed record RefusedResult(long JobId, int Stored, IReadOnlyList<RefusalView> Refused);

    public sealed record FolderFileView(string Name, string RelativePath, long SizeBytes, string FileType, DateTime? Modified, IReadOnlyList<string> Markers);

    public sealed record FolderResult(
        FolderView Folder, IReadOnlyList<string> Subfolders, int Total, int Limit, int Offset, int? NextOffset, IReadOnlyList<FolderFileView> Files);

    public sealed record DescribeResult(FolderView Folder, bool Described, bool Stored, IReadOnlyList<string> Sample, string? Model, string? Refused);

    // ------------------------------------------------------------------ findings candidates

    public static Command CreateCandidates(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id of a source (see sfo jobs).");
        var against = new Option<long[]>("--against")
        {
            Description = "Library job whose folders are candidates. Repeatable. Default: the libraries of the newest sfo compare.",
            HelpName = "job"
        };
        var key = new Option<string[]>("--key")
        {
            Description = "Only this finding. Repeatable. Default: every open or accepted finding of the kinds not_backed_up, in_noise and misplaced.",
            HelpName = "key"
        };
        var limit = new Option<int>("--limit")
        {
            Description = $"Candidate folders per finding (or per part of a mixed one), 0 to {FindingCandidateQuery.MaxLimit}.",
            DefaultValueFactory = _ => FindingCandidateQuery.DefaultLimit,
            HelpName = "n"
        };
        limit.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 0 or > FindingCandidateQuery.MaxLimit)
                r.AddError($"--limit must be between 0 and {FindingCandidateQuery.MaxLimit}.");
        });
        var names = new Option<int>("--names")
        {
            Description = "File names listed per finding, part and folder, 0 to 20.",
            DefaultValueFactory = _ => FindingCandidateQuery.DefaultNames,
            HelpName = "n"
        };
        names.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 0 or > 20)
                r.AddError("--names must be between 0 and 20.");
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
        var maxKb = new Option<int>("--max-kb")
        {
            Description = $"Cap of the listed findings in kB of JSON, 1 to {MaxMaxKb}; the rest follows with --offset. At least one finding is listed.",
            DefaultValueFactory = _ => DefaultMaxKb,
            HelpName = "kb"
        };
        maxKb.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 1 or > MaxMaxKb)
                r.AddError($"--max-kb must be between 1 and {MaxMaxKb}.");
        });

        var command = new Command("candidates",
            "Facts for a controlling model: every finding that should move with what it holds, and the candidate folders of the rules in every library with what they hold. No model call; marked files show path and marker only.")
        {
            job, against, key, limit, names, offset, maxKb
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var query = new FindingCandidateQuery
            {
                JobId = p.GetValue(job),
                Libraries = (p.GetValue(against) ?? []).Distinct().ToList(),
                Keys = (p.GetValue(key) ?? []).Distinct(StringComparer.Ordinal).ToList(),
                Limit = p.GetValue(limit),
                Names = p.GetValue(names)
            };

            FindingCandidateRun run;
            try
            {
                run = await RunFindings(() => services.GetRequiredService<FindingCandidateService>().CandidatesAsync(query, ct));
            }
            catch (LibraryServiceException ex)
            {
                throw LibraryCommand.ToFailure(ex);
            }

            var all = run.Items.Select(i => ItemView(i, query.Names)).ToList();
            var start = Math.Min(p.GetValue(offset), all.Count);
            var cap = p.GetValue(maxKb) * 1024L;
            var page = new List<CandidateItemView>();
            long size = 0;
            for (var i = start; i < all.Count; i++)
            {
                var bytes = Encoding.UTF8.GetByteCount(CliJson.Serialize(all[i], indented: false));
                if (page.Count > 0 && size + bytes > cap) break;
                page.Add(all[i]);
                size += bytes;
            }
            int? next = start + page.Count < all.Count ? start + page.Count : null;
            var result = new CandidatesResult(run.Job.Id, run.Status.ToCode(), run.Message,
                run.Libraries.Select(l => new LibraryView(l.Library.Id, l.Library.RootPath, Directory.Exists(l.Library.RootPath), l.FreeBytes, l.InboxFolder)).ToList(),
                all.Count, start, next, next is not null, page);
            return CommandOutcome.Success(result, () => RenderCandidates(result));
        });
        return command;
    }

    private static CandidateItemView ItemView(FindingCandidateItem item, int names)
    {
        var f = item.Finding;
        var u = item.Candidates;
        var groups = u.Groups.Select(g =>
        {
            var unmarked = g.Files.Where(x => SensitiveOutputGuard.GetMarkers(x) == SensitiveMarker.None).ToList();
            return new GroupView(
                g.Index, g.Files.Count, g.Files.Sum(x => x.Size), g.Label, ByType(g.Unit.Types),
                new PeriodView(BundlesCommand.Local(g.Unit.From), BundlesCommand.Local(g.Unit.To), g.Unit.TimeSource.ToCode()),
                g.Index is null ? [] : unmarked.Take(names).Select(x => x.RelativePath.Replace('\\', '/')).ToList(),
                DescribedView.From(g.Described),
                g.Candidates.Select((c, i) => new CandidateFolderView(i + 1, c.Candidate.Placement.ToCode(), c.Candidate.Destination,
                    Math.Round(c.Candidate.Score, 3), c.Candidate.Certainty.ToCode(), c.Candidate.Reason,
                    c.Candidate.Signals.Where(s => s.Agrees).Select(s => s.Signal.ToCode()).ToList(),
                    c.Candidate.Signals.Where(s => s.Contradicts).Select(s => s.Signal.ToCode()).ToList(),
                    FolderView.From(c.Folder))).ToList(),
                g.NewFolder is { } n ? new NewFolderHintView(n.Library.Id, n.Parent.RelativePath, n.Name, n.NameComplete, n.Pattern) : null,
                g.NotMatched);
        }).ToList();

        return new CandidateItemView(
            f.Key, f.Kind.ToCode(), f.State.ToCode(), f.Title, f.RelativePath, f.BundleKind.ToCode(), f.Files, f.Bytes, f.ByFileType,
            f.Period is { } period ? new PeriodView(BundlesCommand.Local(period.From), BundlesCommand.Local(period.To), period.Source.ToCode()) : null,
            u.Unit.Name, u.Unit.KeepWhole, item.Examples,
            item.SensitiveFiles.Select(s => new SensitiveFileView(s.RelativePath, SensitiveMarkerNames.ToTokens(s.Markers))).ToList(),
            f.TargetStatus is { } status && status != FindingTargetStatus.NotMatched
                ? new StoredTargetView(status.ToCode(), f.TargetDecidedBy.ToCode(), f.Target?.LibraryJobId, f.Target?.RelativePath)
                : null,
            u.Excluded, u.Split, groups);
    }

    private static string RenderCandidates(CandidatesResult r)
    {
        if (r.Message is { } message) return message;
        var sb = new StringBuilder();
        sb.AppendLine($"Job {r.JobId}: {r.Total} finding(s) to place" + (r.NextOffset is { } next ? $", more with --offset {next}" : string.Empty));
        foreach (var l in r.Libraries)
            sb.AppendLine($"  library job {l.LibraryJobId}: {l.RootPath} ({(l.FreeBytes is { } free ? ReadFormat.Size(free) : "unknown")} free, inbox {l.InboxFolder})");
        foreach (var i in r.Items)
        {
            sb.AppendLine($"{i.Key}  {i.Kind}  {i.Files} files {ReadFormat.Size(i.Bytes)}  {i.Title}");
            if (i.Stored is { } s)
                sb.AppendLine($"   stored: {s.Status}{(s.RelativePath is { } rel ? " -> " + rel : string.Empty)} ({s.DecidedBy})");
            if (i.Excluded is { } excluded)
                sb.AppendLine($"   excluded: {excluded}");
            foreach (var marked in i.SensitiveFiles)
                sb.AppendLine($"   marked: {marked.RelativePath} [{string.Join(",", marked.Markers)}]");
            foreach (var g in i.Groups)
            {
                var prefix = g.Part is { } part ? $"   part {part}: {g.Files} files, {g.Label}" : "   ";
                if (g.Part is not null) sb.AppendLine(prefix);
                if (!g.Described.Text.Equals(string.Empty)) sb.AppendLine($"     shows: {g.Described.Text}");
                if (g.NotMatched is { } nm) sb.AppendLine($"     {nm}");
                foreach (var c in g.Candidates)
                    sb.AppendLine($"     {c.Rank}. {c.DestinationRelative} (library job {c.Folder.LibraryJobId}, {c.Certainty}, {c.Folder.Files} files"
                                  + (c.Folder.Sensitive ? ", sensitive" : c.Folder.Described ? ", described" : string.Empty) + ")");
                if (g.NewFolder is { } n)
                    sb.AppendLine($"     new folder: {n.ParentRelative}/{n.Name} (library job {n.LibraryJobId})");
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ findings place

    public static Command CreatePlace(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id of a source (see sfo jobs).");
        var key = new Argument<string?>("key")
        {
            Description = "Finding key (see sfo findings candidates). Leave out with --file.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var statuses = FindingPlacementService.Settable.Select(s => s.ToCode()).ToArray();
        var status = new Option<string?>("--status")
        {
            Description = $"The decision: {string.Join(", ", statuses)}.",
            HelpName = "status"
        };
        status.AcceptOnlyFromAmong(statuses);
        var library = new Option<long?>("--library")
        {
            Description = "Library job of the target. Needed when there are several and --folder is relative.",
            HelpName = "job"
        };
        var folder = new Option<string?>("--folder")
        {
            Description = "existing_folder: a folder of the library; new_folder: the folder to create, directly below an existing one. Relative to the library root (with /) or a full path.",
            HelpName = "path"
        };
        var part = new Option<int?>("--part")
        {
            Description = "Only this part of a mixed finding (part in sfo findings candidates).",
            HelpName = "n"
        };
        var reason = new Option<string?>("--reason")
        {
            Description = "Why this target: kept with the decision. Required.",
            HelpName = "text"
        };
        var withdraw = new Option<bool>("--withdraw")
        {
            Description = "Remove the stored target of the finding; the next sfo findings targets matches it by the rules again."
        };
        var file = new Option<string?>("--file")
        {
            Description = "Decisions from a JSON file: {\"decisions\": [{\"key\", \"part\", \"status\", \"library_job_id\", \"folder\", \"reason\", \"withdraw\"}]}. All or nothing.",
            HelpName = "path"
        };
        var against = new Option<long[]>("--against")
        {
            Description = "Library jobs a target may lie in. Repeatable. Default: the libraries of the newest sfo compare.",
            HelpName = "job"
        };
        var command = new Command("place",
            "Record where findings go, as decided by a controlling model: an existing folder, a new folder, the inbox of a library, or no target. Checked like a plan target; nothing on disk changes.")
        {
            job, key, status, library, folder, part, reason, withdraw, file, against
        };
        command.Validators.Add(r =>
        {
            var hasFile = r.GetValue(file) is not null;
            var hasKey = r.GetValue(key) is not null;
            if (hasFile == hasKey)
                r.AddError("Give either a finding key or --file.");
            else if (hasFile && (r.GetValue(status) is not null || r.GetValue(folder) is not null || r.GetValue(part) is not null
                                 || r.GetValue(reason) is not null || r.GetValue(withdraw) || r.GetValue(library) is not null))
                r.AddError("--file takes every decision from the file; leave out --status, --library, --folder, --part, --reason and --withdraw.");
            else if (hasKey && !r.GetValue(withdraw) && r.GetValue(status) is null)
                r.AddError("Give --status (or --withdraw).");
        });

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var jobId = p.GetValue(job);
            var requests = p.GetValue(file) is { } path
                ? ParseDecisions(await ReadFileAsync(path, ct))
                : [new PlacementRequest(p.GetValue(key)!, p.GetValue(part), p.GetValue(status), p.GetValue(library), p.GetValue(folder),
                    p.GetValue(reason), p.GetValue(withdraw))];

            PlacementRun run;
            try
            {
                run = await RunFindings(() => services.GetRequiredService<FindingPlacementService>()
                    .PlaceAsync(jobId, (p.GetValue(against) ?? []).Distinct().ToList(), requests, ct));
            }
            catch (LibraryServiceException ex)
            {
                throw LibraryCommand.ToFailure(ex);
            }
            catch (PlacementRefusedException ex)
            {
                var refused = new RefusedResult(jobId, 0, ex.Refused.Select(r =>
                    new RefusalView(r.Index, r.Key, r.Part, r.Problems.Select(x => x.ToCode()).ToList(), r.Message)).ToList());
                return CommandOutcome.Fail(ExitCodes.Failure, ErrorCodes.PlacementRefused, ex.Message, refused,
                    () => string.Join(Environment.NewLine, refused.Refused.Select(r => $"refused {r.Key}: {r.Message}")));
            }

            var result = new PlaceResult(run.Job.Id, run.Items.Count(i => !i.Withdrawn), run.Items.Count(i => i.Withdrawn),
                run.Items.Select(i => new PlaceItemView(i.Key, i.Part, i.Status.ToCode(), i.LibraryJobId, i.Path, i.RelativePath, i.Reason,
                    i.Withdrawn, TargetDecidedBy.Controller.ToCode())).ToList());
            return CommandOutcome.Success(result, () => string.Join(Environment.NewLine, result.Items.Select(i =>
                i.Withdrawn ? $"{i.Key}: withdrawn"
                : $"{i.Key}{(i.Part is { } n ? $" part {n}" : string.Empty)}: {i.Status}{(i.Path is { } target ? " -> " + target : string.Empty)}")));
        });
        return command;
    }

    private static async Task<string> ReadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ImportInvalid, $"Decisions file cannot be read: {ex.Message}");
        }
    }

    /// <summary>Reads a decisions file; every problem of the file itself refuses the whole file.</summary>
    public static IReadOnlyList<PlacementRequest> ParseDecisions(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ImportInvalid, $"Decisions file is not JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("decisions", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ImportInvalid, "Decisions file needs an object with a list \"decisions\".");

            var requests = new List<PlacementRequest>();
            var problems = new List<string>();
            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                index++;
                if (item.ValueKind != JsonValueKind.Object || Text(item, "key") is not { Length: > 0 } key)
                {
                    problems.Add($"entry {index}: \"key\" (text) is missing");
                    continue;
                }
                int? part = null;
                if (item.TryGetProperty("part", out var pt) && pt.ValueKind != JsonValueKind.Null)
                {
                    if (!pt.TryGetInt32(out var n) || n < 0)
                    {
                        problems.Add($"entry {index}: \"part\" must be a number of 0 or more");
                        continue;
                    }
                    part = n;
                }
                long? library = null;
                if (item.TryGetProperty("library_job_id", out var lj) && lj.ValueKind != JsonValueKind.Null)
                {
                    if (!lj.TryGetInt64(out var id))
                    {
                        problems.Add($"entry {index}: \"library_job_id\" must be a number");
                        continue;
                    }
                    library = id;
                }
                var withdraw = item.TryGetProperty("withdraw", out var w) && w.ValueKind == JsonValueKind.True;
                requests.Add(new PlacementRequest(key, part, Text(item, "status"), library, Text(item, "folder"), Text(item, "reason"), withdraw));
            }
            if (problems.Count > 0)
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ImportInvalid, "Decisions file: " + string.Join("; ", problems));
            if (requests.Count == 0)
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.ImportInvalid, "Decisions file holds no decision.");
            return requests;
        }

        static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    // ------------------------------------------------------------------ library folder

    public static Command CreateFolder(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Library job (scanned with --role library).");
        var path = new Argument<string>("path") { Description = "Folder of the library: relative to its root (with /), or a full path." };
        var limit = new Option<int>("--limit")
        {
            Description = "Files per page, 1 to 1000.",
            DefaultValueFactory = _ => 50
        };
        limit.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 1 or > 1000)
                r.AddError("--limit must be between 1 and 1000.");
        });
        var offset = new Option<int>("--offset")
        {
            Description = "Files to skip (see next_offset in the result).",
            DefaultValueFactory = _ => 0
        };
        offset.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() < 0)
                r.AddError("--offset must not be negative.");
        });
        var command = new Command("folder",
            "One folder of a library as the scan holds it: counts, period, kinds of files, its subfolders and its own files, and its stored description. No model call; a sensitive folder lists no names.")
        {
            job, path, limit, offset
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            FolderDetail detail;
            try
            {
                detail = await services.GetRequiredService<LibraryTargetService>()
                    .FolderAsync(p.GetValue(job), p.GetValue(path)!, p.GetValue(limit), p.GetValue(offset), ct);
            }
            catch (LibraryServiceException ex)
            {
                throw LibraryCommand.ToFailure(ex);
            }

            var root = detail.Facts.Library.RootPath;
            var result = new FolderResult(FolderView.From(detail.Facts), detail.Subfolders.Select(s => s.Name).ToList(), detail.TotalFiles,
                detail.Limit, detail.Offset, detail.NextOffset,
                detail.Files.Select(f => new FolderFileView(f.Name, Path.GetRelativePath(root, f.FullPath).Replace('\\', '/'), f.Size,
                    f.FileType.ToString().ToLowerInvariant(), f.LastWriteTime, SensitiveMarkerNames.ToTokens(SensitiveOutputGuard.GetMarkers(f)))).ToList());
            return CommandOutcome.Success(result, () =>
            {
                var sb = new StringBuilder();
                var f = result.Folder;
                sb.AppendLine($"{f.RelativePath} (library job {f.LibraryJobId}): {f.Files} files, {f.Subdirs} subfolders, {ReadFormat.Size(f.Bytes)}"
                              + (f.Sensitive ? $", sensitive [{string.Join(",", f.Markers)}]" : string.Empty));
                if (f.Description is { } d) sb.AppendLine($"  shows: {d.Text}");
                foreach (var s in result.Subfolders) sb.AppendLine($"  {s}/");
                foreach (var x in result.Files)
                    sb.AppendLine($"  {ReadFormat.Size(x.SizeBytes),10}  {x.Name}" + (x.Markers.Count > 0 ? $" [{string.Join(",", x.Markers)}]" : string.Empty));
                if (result.NextOffset is { } next) sb.AppendLine($"more with --offset {next}");
                return sb.ToString().TrimEnd();
            });
        });
        return command;
    }

    // ------------------------------------------------------------------ library describe

    public static Command CreateDescribe(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Library job (scanned with --role library).");
        var path = new Argument<string>("path") { Description = "Folder of the library: relative to its root (with /), or a full path." };
        var refresh = new Option<bool>("--refresh") { Description = "Describe anew instead of returning the stored description." };
        var command = new Command("describe",
            "Let the local model describe this one folder from a small sample of its own files, and store it (folder_descriptions). No other folder is described; a sensitive folder is refused.")
        {
            job, path, refresh
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            FolderDescribeAnswer answer;
            try
            {
                answer = await services.GetRequiredService<LibraryTargetService>().DescribeFolderAsync(p.GetValue(job), p.GetValue(path)!, p.GetValue(refresh), ct);
            }
            catch (LibraryServiceException ex)
            {
                throw LibraryCommand.ToFailure(ex);
            }

            var d = answer.Description;
            var result = new DescribeResult(FolderView.From(answer.Folder), answer.Refused is null, d?.Stored ?? false,
                answer.Folder.Sensitive ? [] : d?.Sample ?? [], d?.Model, answer.Refused);
            string Render() => result.Refused ?? $"{result.Folder.RelativePath}: {result.Folder.Description?.Text}";
            return answer.ModelUnavailable
                ? CommandOutcome.Fail(ExitCodes.OllamaUnavailable, ErrorCodes.OllamaUnavailable, answer.Refused!, result, Render)
                : CommandOutcome.Success(result, Render);
        });
        return command;
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<T> RunFindings<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (JobReadException ex)
        {
            throw ReadFormat.ToFailure(ex);
        }
        catch (FindingException ex)
        {
            throw ex.Failure switch
            {
                FindingFailure.WrongRole => new CliFailureException(ExitCodes.Failure, ErrorCodes.WrongRole, ex.Message),
                FindingFailure.ComparisonNotFound => new CliFailureException(ExitCodes.Failure, ErrorCodes.ComparisonNotFound, ex.Message),
                _ => new CliFailureException(ExitCodes.Failure, ErrorCodes.FindingNotFound, ex.Message)
            };
        }
        catch (ArgumentException ex)
        {
            throw new CliFailureException(ExitCodes.Failure, ErrorCodes.SettingsInvalid, ex.Message);
        }
    }

    private static IReadOnlyDictionary<string, long> ByType(long[] types) =>
        Enumerable.Range(0, types.Length)
            .Where(t => types[t] > 0)
            .OrderByDescending(t => types[t]).ThenBy(t => t)
            .ToDictionary(t => ((FileType)t).ToString().ToLowerInvariant(), t => types[t]);
}
