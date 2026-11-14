using System.CommandLine;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Duplicates;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo duplicates &lt;job&gt; [&lt;job&gt; ...]</c>: files and folders with the same content over one or more jobs of
/// any role, read in stages (https://github.com/crack666/smart_file_organizer/issues/87). Reports only.
/// </summary>
public static class DuplicatesCommand
{
    /// <param name="Share">Bytes read against the bytes of all files of the jobs.</param>
    public sealed record ReadView(int Files, long Bytes, double Share);

    public sealed record StagesView(int SizeCandidates, long SizeCandidateBytes, int SampleGroups, int SampleComputed, int FullComputed, int Reused);

    public sealed record SummaryView(int Groups, int FileGroups, int FolderGroups, int Copies, long WastedBytes, int Certain, int Likely,
        int AcrossJobs, int InLibrary);

    /// <param name="Sensitive">Marker tokens of a marked file; for such a file only path and markers are shown.</param>
    public sealed record FileView(string Path, long JobId, ScanRole Role, bool Managed, IReadOnlyList<string> Sensitive, string? SensitiveRule);

    public sealed record FolderView(string Path, long JobId, ScanRole Role, bool Managed, int Files, int MatchedFiles);

    /// <param name="Hash">Full SHA-256 of a certain file group; null otherwise and when a file of the group is marked.</param>
    /// <param name="FilesTotal">Files of the group; a folder group lists its folders, not its files.</param>
    public sealed record GroupView(
        int Group,
        DuplicateScope Scope,
        DuplicateCertainty Certainty,
        DuplicateAdvice Advice,
        long Size,
        int Copies,
        long WastedBytes,
        string? Hash,
        IReadOnlyList<long> Jobs,
        bool AcrossJobs,
        bool InLibrary,
        int FilesTotal,
        IReadOnlyList<FileView> Files,
        IReadOnlyList<FolderView> Folders);

    public sealed record ErrorView(long JobId, string Path, string Message);

    public sealed record DuplicatesResult(
        IReadOnlyList<JobView> Jobs,
        bool Verified,
        long MinSize,
        bool Complete,
        long Files,
        long Bytes,
        ReadView Read,
        StagesView Stages,
        SummaryView Summary,
        int GroupsTotal,
        IReadOnlyList<GroupView> Groups,
        int ErrorsTotal,
        IReadOnlyList<ErrorView> Errors);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var jobs = new Argument<long[]>("job")
        {
            Description = "Job ids of completed scans, any role (source or library). Several: duplicates across them.",
            HelpName = "job",
            Arity = ArgumentArity.OneOrMore
        };
        var verify = new Option<bool>("--verify")
        {
            Description = "Read the whole of every file of a group that agrees after the sample, so the group is certain or falls apart."
        };
        var summary = new Option<bool>("--summary")
        {
            Description = "Counts only, no list of groups."
        };
        var limit = new Option<int>("--limit")
        {
            Description = "Maximum groups and errors listed, largest waste first. 0 lists everything.",
            DefaultValueFactory = _ => 20,
            HelpName = "n"
        };
        limit.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() < 0)
                r.AddError("--limit must not be negative.");
        });
        var minSize = new Option<string?>("--min-size")
        {
            Description = "Leave out smaller files: bytes or a number with K, M, G, T (for example 1M). Empty files are always left out.",
            HelpName = "size"
        };
        minSize.Validators.Add(r =>
        {
            var value = r.GetValueOrDefault<string?>();
            if (value is not null && !ReadFormat.TryParseSize(value, out _))
                r.AddError($"--min-size: cannot read '{value}' as a size.");
        });

        var command = new Command("duplicates",
            "Files and folders with the same content over one or more jobs: size, then a sample checksum, the full checksum with --verify. Changes nothing.")
        {
            jobs, verify, summary, limit, minSize
        };

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            var minBytes = 1L;
            if (p.GetValue(minSize) is { } text && ReadFormat.TryParseSize(text, out var parsed))
                minBytes = Math.Max(1, parsed);
            var progress = new SynchronousProgress<DuplicateProgress>(x =>
                ctx.Progress.Report(x.Stage, x.Processed, x.Total, 0, x.CurrentPath));

            DuplicateReport report;
            try
            {
                report = await services.GetRequiredService<DuplicateService>().ReportAsync(
                    p.GetValue(jobs) ?? [], new DuplicateOptions(minBytes, p.GetValue(verify)), progress, ct);
            }
            catch (ComparisonException ex)
            {
                var code = ex.Failure switch
                {
                    ComparisonFailure.JobNotFound => ErrorCodes.JobNotFound,
                    ComparisonFailure.ScanNotCompleted => ErrorCodes.ScanNotCompleted,
                    ComparisonFailure.RootUnreachable => ErrorCodes.PathNotFound,
                    _ => ErrorCodes.InternalError
                };
                return CommandOutcome.Fail(ExitCodes.Failure, code, ex.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return CommandOutcome.Cancelled(
                    "Interrupted. Checksums computed so far are saved; run the command again to continue where it stopped.");
            }

            // A marked file shows its path and markers only; a group with one gets no checksum.
            var reads = services.GetRequiredService<JobReadService>();
            var marked = new Dictionary<long, SensitiveMatch>();
            if (report.Groups.Count > 0)
            {
                foreach (var job in report.Jobs)
                    foreach (var (id, match) in await reads.GetMarkedFilesAsync(job.Id, ct))
                        marked[id] = match;
            }

            var result = Map(report, p.GetValue(summary), p.GetValue(limit), marked);
            return CommandOutcome.Success(result, () => Render(result));
        });

        return command;
    }

    public static DuplicatesResult Map(
        DuplicateReport report, bool summaryOnly, int limit, IReadOnlyDictionary<long, SensitiveMatch> marked)
    {
        IReadOnlyList<T> Take<T>(IEnumerable<T> items) =>
            summaryOnly ? [] : (limit == 0 ? items : items.Take(limit)).ToList();

        var groups = report.Groups.Select((g, i) =>
        {
            var anyMarked = g.Files.Any(f => marked.ContainsKey(f.FileNodeId));
            var files = g.Scope == DuplicateScope.Files
                ? g.Files.Select(f =>
                {
                    var match = marked.GetValueOrDefault(f.FileNodeId);
                    return new FileView(f.FullPath, f.JobId, f.Role, f.Managed, SensitiveMarkerNames.ToTokens(match.Markers), match.Rule);
                }).ToList()
                : [];
            var folders = g.Folders.Select(d => new FolderView(d.Path, d.JobId, d.Role, d.Managed, d.Files, d.MatchedFiles)).ToList();
            return new GroupView(i + 1, g.Scope, g.Certainty, g.Advice, g.Size, g.Copies, g.WastedBytes,
                anyMarked ? null : g.Hash, g.JobIds, g.AcrossJobs, g.InLibrary, g.Files.Count, files, folders);
        });

        var s = report.Stages;
        return new DuplicatesResult(
            report.Jobs.Select(JobView.From).ToList(),
            report.Verified,
            report.MinSize,
            report.Complete,
            report.TotalFiles,
            report.TotalBytes,
            new ReadView(s.FilesRead, s.BytesRead, report.TotalBytes == 0 ? 0 : Math.Round((double)s.BytesRead / report.TotalBytes, 6)),
            new StagesView(s.SizeCandidates, s.SizeCandidateBytes, s.SampleGroups, s.SampleComputed, s.FullComputed, s.Reused),
            new SummaryView(report.Groups.Count,
                report.Groups.Count(g => g.Scope == DuplicateScope.Files),
                report.Groups.Count(g => g.Scope == DuplicateScope.Folders),
                report.Groups.Sum(g => g.Files.Count),
                report.WastedBytes,
                report.Groups.Count(g => g.Certainty == DuplicateCertainty.Certain),
                report.Groups.Count(g => g.Certainty == DuplicateCertainty.Likely),
                report.Groups.Count(g => g.AcrossJobs),
                report.Groups.Count(g => g.InLibrary)),
            report.Groups.Count,
            Take(groups),
            report.Errors.Count,
            Take(report.Errors.Select(e => new ErrorView(e.JobId, e.Path, e.Message))));
    }

    private static string Render(DuplicatesResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Jobs.Count == 1
            ? $"Duplicates in job {r.Jobs[0].Id} ({r.Jobs[0].Role.ToValue()}, {r.Jobs[0].RootPath})"
            : $"Duplicates over {r.Jobs.Count} jobs: {string.Join(", ", r.Jobs.Select(j => $"{j.Id} {j.Role.ToValue()} {j.RootPath}"))}");
        var sm = r.Summary;
        sb.AppendLine($"  {sm.Groups} groups ({sm.FolderGroups} of folders), {sm.Copies} copies, {ReadFormat.Size(sm.WastedBytes)} redundant; "
                      + $"{sm.Certain} certain, {sm.Likely} likely{(r.Verified ? string.Empty : " (--verify to be sure)")}");
        if (r.Jobs.Count > 1)
            sb.AppendLine($"  {sm.AcrossJobs} across jobs, {sm.InLibrary} with a copy in a library");
        sb.AppendLine($"  read {ReadFormat.Size(r.Read.Bytes)} of {ReadFormat.Size(r.Bytes)} ({(r.Read.Share * 100).ToString("0.###", CultureInfo.InvariantCulture)} %) "
                      + $"from {r.Read.Files} files; {r.Stages.Reused} files from earlier checksums");
        sb.AppendLine($"  stages: {r.Stages.SizeCandidates} files share a size, {r.Stages.SampleGroups} still agree after the sample");
        foreach (var g in r.Groups)
        {
            sb.AppendLine($"  [{g.Certainty.ToString().ToLowerInvariant()}] {ReadFormat.Size(g.WastedBytes)} redundant, "
                          + $"{g.Copies} {(g.Scope == DuplicateScope.Folders ? "folders" : "copies")} of {ReadFormat.Size(g.Size)}");
            if (g.Scope == DuplicateScope.Folders)
            {
                foreach (var d in g.Folders)
                    sb.AppendLine($"      {d.Path}  ({d.MatchedFiles} of {d.Files} files, job {d.JobId} {d.Role.ToValue()}{(d.Managed ? ", managed" : string.Empty)})");
            }
            else
            {
                foreach (var f in g.Files)
                {
                    var marks = f.Sensitive.Count == 0 ? string.Empty : $" [sensitive: {string.Join(", ", f.Sensitive)}]";
                    sb.AppendLine($"      {f.Path}  (job {f.JobId} {f.Role.ToValue()}{(f.Managed ? ", managed" : string.Empty)}){marks}");
                }
            }
        }
        if (r.GroupsTotal > r.Groups.Count && r.Groups.Count > 0)
            sb.AppendLine($"  ... {r.GroupsTotal - r.Groups.Count} more group(s); --limit 0 lists all");
        foreach (var e in r.Errors)
            sb.AppendLine($"  ! {e.Path}: {e.Message}");
        if (r.ErrorsTotal > r.Errors.Count)
            sb.AppendLine($"  ! ... {r.ErrorsTotal - r.Errors.Count} more error(s)");
        return sb.ToString().TrimEnd();
    }
}
