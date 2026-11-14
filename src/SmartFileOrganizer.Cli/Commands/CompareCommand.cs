using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo compare &lt;source-job&gt; --against &lt;library-job&gt;</c>: which files of a source are already
/// in the library, by content. https://github.com/crack666/smart_file_organizer/issues/24
/// </summary>
public static class CompareCommand
{
    public sealed record CountsView(int Files, int BackedUp, int New, int Differs, int Errors);

    public sealed record DuplicatesView(int GroupsTotal, int Files, IReadOnlyList<DuplicateGroup> Groups);

    public sealed record DirectoryView(string Path, int Files, int BackedUp, int New, int Differs, int Errors, double BackedUpShare);

    public sealed record ErrorView(ScanRole Role, ComparisonIssueKind Kind, string Path, string Message);

    public sealed record FileView(
        string Path,
        ContentState? State,
        string? MatchPath,
        int MatchCount,
        string? MatchHash,
        DateTime? MatchVerifiedAt,
        int? DuplicateGroup,
        string? Error,
        long? MatchLibraryJobId = null);

    /// <summary>One library of the run: its job, its stored comparison and the source files it holds by content.</summary>
    public sealed record LibraryView(JobView Job, long ComparisonId, DateTime ComparedAt, bool Complete, int BackedUp, long BackedUpBytes);

    public sealed record CompareResult(
        long ComparisonId,
        JobView Source,
        JobView? Against,
        DateTime ComparedAt,
        bool Complete,
        CountsView Counts,
        DuplicatesView Duplicates,
        int DirectoriesTotal,
        IReadOnlyList<DirectoryView> Directories,
        int ErrorsTotal,
        IReadOnlyList<ErrorView> Errors,
        int FilesTotal,
        IReadOnlyList<FileView> Files,
        HashingStats Hashing,
        IReadOnlyList<LibraryView>? Libraries = null);

    private const string AllStates = "all";

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id of a completed scan with role source.");
        var against = new Option<long[]>("--against")
        {
            Description = "Job id of a completed scan with role library. Repeatable: a file is backed_up as soon as one of them holds it. Without it, only duplicates within the source are found.",
            HelpName = "job"
        };
        var files = new Option<string[]>("--files")
        {
            Description = "List the files with this state: backed_up, new, differs, error or all. Repeatable. Default: no file list.",
            HelpName = "state",
            AllowMultipleArgumentsPerToken = true
        };
        files.AcceptOnlyFromAmong([.. ContentStateValues.All, AllStates]);
        var limit = new Option<int>("--limit")
        {
            Description = "Maximum entries per list (directories, errors, duplicate groups, files). 0 lists everything.",
            DefaultValueFactory = _ => 100,
            HelpName = "n"
        };
        limit.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() < 0)
                r.AddError("--limit must not be negative.");
        });

        var command = new Command("compare",
            "Compare a source scan with a library scan by content: backed_up, new, differs, plus duplicates within the source.")
        {
            job, against, files, limit
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var sourceId = ctx.ParseResult.GetValue(job);
            var againstIds = ctx.ParseResult.GetValue(against) ?? [];
            var progress = new SynchronousProgress<ComparisonProgress>(p =>
                ctx.Progress.Report("compare", p.Processed, p.Total, 0, p.CurrentPath));

            ComparisonReport report;
            try
            {
                report = await services.GetRequiredService<ContentComparisonService>()
                    .CompareWithLibrariesAsync(sourceId, againstIds, progress, ct);
            }
            catch (ComparisonException ex)
            {
                var code = ex.Failure switch
                {
                    ComparisonFailure.JobNotFound => ErrorCodes.JobNotFound,
                    ComparisonFailure.ScanNotCompleted => ErrorCodes.ScanNotCompleted,
                    ComparisonFailure.WrongRole => ErrorCodes.WrongRole,
                    ComparisonFailure.RootUnreachable => ErrorCodes.PathNotFound,
                    _ => ErrorCodes.InternalError
                };
                return CommandOutcome.Fail(ExitCodes.Failure, code, ex.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return CommandOutcome.Cancelled(
                    "Comparison interrupted. Checksums computed so far are saved; run the command again to continue.");
            }

            // The full checksum of a short secret file confirms a guess of its content: none for marked files.
            var reads = services.GetRequiredService<JobReadService>();
            var marked = new HashSet<long>(await reads.GetMarkedFileIdsAsync(report.Source.Id, ct));
            foreach (var library in report.Libraries?.Select(l => l.Library) ?? (report.Against is { } a ? [a] : []))
                marked.UnionWith(await reads.GetMarkedFileIdsAsync(library.Id, ct));

            var result = Map(report, ParseStates(ctx.ParseResult.GetValue(files)), ctx.ParseResult.GetValue(limit), marked);
            if (!result.Complete)
            {
                return CommandOutcome.Fail(ExitCodes.Failure, ErrorCodes.CompareIncomplete,
                    $"{result.ErrorsTotal} error(s): some content could not be read, so the affected files are marked error. The result is stored.",
                    result, () => Render(result));
            }
            return CommandOutcome.Success(result, () => Render(result));
        });

        return command;
    }

    public static IReadOnlySet<ContentState> ParseStates(string[]? names)
    {
        if (names == null || names.Length == 0)
            return new HashSet<ContentState>();
        if (names.Contains(AllStates))
            return Enum.GetValues<ContentState>().ToHashSet();
        return names.Select(ContentStateValues.Parse).ToHashSet();
    }

    /// <param name="markedFileIds">Marked files of both jobs; their match checksum is left out.</param>
    public static CompareResult Map(
        ComparisonReport report, IReadOnlySet<ContentState> listStates, int limit, IReadOnlySet<long>? markedFileIds = null)
    {
        string? Hash(FileComparison f) =>
            markedFileIds != null && (markedFileIds.Contains(f.FileNodeId) ||
                                      (f.MatchFileNodeId is { } m && markedFileIds.Contains(m)))
                ? null
                : f.MatchHash;

        IReadOnlyList<T> Take<T>(IEnumerable<T> items) => (limit == 0 ? items : items.Take(limit)).ToList();

        var listed = report.Files.Where(f => f.State is { } s && listStates.Contains(s)).ToList();
        return new CompareResult(
            report.Header.Id,
            JobView.From(report.Source),
            report.Against == null ? null : JobView.From(report.Against),
            report.Header.ComparedAt,
            report.Header.Complete,
            new CountsView(report.Files.Count,
                report.Count(ContentState.BackedUp), report.Count(ContentState.New),
                report.Count(ContentState.Differs), report.Count(ContentState.Error)),
            new DuplicatesView(report.DuplicateGroups.Count, report.DuplicateGroups.Sum(g => g.Paths.Count), Take(report.DuplicateGroups)),
            report.Directories.Count,
            Take(report.Directories.Select(d => new DirectoryView(d.Path, d.Files, d.BackedUp, d.New, d.Differs, d.Errors, d.BackedUpShare))),
            report.Errors.Count,
            Take(report.Errors.Select(e => new ErrorView(e.Role, e.Kind, e.Path, e.Message))),
            listed.Count,
            Take(listed.Select(f => new FileView(f.FullPath, f.State, f.MatchPath, f.MatchCount, Hash(f), f.MatchVerifiedAt, f.DuplicateGroup, f.Error,
                f.MatchJobId))),
            report.Hashing,
            report.Libraries?.Select(l => new LibraryView(JobView.From(l.Library), l.Header.Id, l.Header.ComparedAt, l.Header.Complete,
                l.BackedUpFiles, l.BackedUpBytes)).ToList() ?? []);
    }

    private static string Render(CompareResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Against == null
            ? $"Job {r.Source.Id} ({r.Source.RootPath}) without library: duplicates only"
            : r.Libraries is { Count: > 1 } libraries
                ? $"Job {r.Source.Id} ({r.Source.RootPath}) against {libraries.Count} library jobs"
                : $"Job {r.Source.Id} ({r.Source.RootPath}) against library job {r.Against.Id} ({r.Against.RootPath})");
        if (r.Libraries is { Count: > 1 })
        {
            foreach (var l in r.Libraries)
                sb.AppendLine($"  library job {l.Job.Id} ({l.Job.RootPath}): {l.BackedUp} backed_up ({ReadFormat.Size(l.BackedUpBytes)})");
        }
        var c = r.Counts;
        if (r.Against != null)
            sb.AppendLine($"  {c.Files} files: {c.BackedUp} backed_up, {c.New} new, {c.Differs} differs, {c.Errors} error");
        sb.AppendLine($"  {r.Duplicates.GroupsTotal} duplicate groups ({r.Duplicates.Files} files)");
        sb.AppendLine($"  checksums: {r.Hashing.HeadTailComputed} sample and {r.Hashing.FullComputed} full computed, {r.Hashing.Reused} files from cache");
        foreach (var f in r.Files)
            sb.AppendLine($"  {ContentStateLabel(f.State),-10} {f.Path}{(f.MatchPath == null ? string.Empty : $"  <- {f.MatchPath}")}");
        foreach (var e in r.Errors)
            sb.AppendLine($"  ! {e.Role.ToValue()} {e.Kind.ToString().ToLowerInvariant()}: {e.Path}: {e.Message}");
        if (r.ErrorsTotal > r.Errors.Count)
            sb.AppendLine($"  ! ... {r.ErrorsTotal - r.Errors.Count} more error(s)");
        return sb.ToString().TrimEnd();
    }

    private static string ContentStateLabel(ContentState? state) => state?.ToValue() ?? "-";
}
