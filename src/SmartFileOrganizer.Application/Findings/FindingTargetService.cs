using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Findings;

/// <summary>What <c>sfo findings targets</c> asks for.</summary>
public sealed record FindingTargetQuery
{
    public long JobId { get; init; }

    /// <summary>The library jobs whose comparisons count and whose folders are candidates; empty for those of the newest compare.</summary>
    public IReadOnlyList<long> Libraries { get; init; } = [];

    /// <summary>Only these findings; empty for every open or accepted finding that should move.</summary>
    public IReadOnlyCollection<string> Keys { get; init; } = [];

    /// <summary>The model may describe candidate folders, compare content and name new folders.</summary>
    public bool UseModel { get; init; } = true;

    /// <summary>Match again, and ask the model again, even where a stored match fits the finding.</summary>
    public bool Refresh { get; init; }

    public string? Model { get; init; }
}

/// <summary>The match of one finding: the place, or why there is none.</summary>
/// <param name="Reused">The stored match of an earlier run was kept: nothing was asked.</param>
public sealed record FindingTargetItem(
    string Key,
    FindingKind Kind,
    string Title,
    FindingTargetStatus Status,
    bool Reused,
    long? LibraryJobId,
    string? Path,
    string? RelativePath,
    ResultCertainty? Certainty,
    TargetContentCheck ContentCheck,
    ContentCheckStatus ContentStatus,
    string Reason,
    IReadOnlyList<FindingTargetPart>? Parts = null);

/// <summary>Progress of a run, one report per finding.</summary>
public sealed record FindingTargetProgress(int Done, int Total, string Key, FindingTargetStatus Status, bool Reused);

/// <summary>The answer of <c>sfo findings targets</c>.</summary>
/// <param name="Matched">Findings matched in this run.</param>
/// <param name="Reused">Findings whose stored match was kept.</param>
/// <param name="ByStatus">Findings per <see cref="FindingTargetStatus"/> code, matched and reused together.</param>
public sealed record FindingTargetRun(
    ScanJob Job,
    StorageStatus Status,
    string? Message,
    IReadOnlyList<long> LibraryJobIds,
    int Considered,
    int Matched,
    int Reused,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyList<FindingTargetItem> Items);

/// <summary>
/// The target match of the findings (https://github.com/crack666/smart_file_organizer/issues/83): for every open or
/// accepted finding that should move (<see cref="FindingRules.TargetKinds"/>), the match of <c>sfo library match</c>
/// (<see cref="LibraryTargetService.MatchAsync"/>: rules, content check, new folder in the style of the library) over all
/// libraries of the comparison, with exactly the files of the finding. The answer is stored per finding
/// (<c>finding_targets</c>) as soon as it is there, so a run cut off goes on where it stopped; a finding whose stored match
/// still rests on the same files, descriptions and libraries is not matched again. The findings read the stored match
/// (<see cref="FindingService"/>), and the plan takes the target from there. Nothing on disk is changed.
/// </summary>
public sealed class FindingTargetService
{
    private readonly FindingService _findings;
    private readonly LibraryTargetService _targets;
    private readonly IFindingTargetRepository _store;
    private readonly IContentComparisonRepository _comparisons;
    private readonly ILogger<FindingTargetService> _logger;
    private readonly TimeProvider _time;

    public FindingTargetService(
        FindingService findings,
        LibraryTargetService targets,
        IFindingTargetRepository store,
        IContentComparisonRepository comparisons,
        ILogger<FindingTargetService> logger,
        TimeProvider? time = null)
    {
        _findings = findings;
        _targets = targets;
        _store = store;
        _comparisons = comparisons;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<FindingTargetRun> MatchAsync(
        FindingTargetQuery query, IProgress<FindingTargetProgress>? progress = null, CancellationToken ct = default)
    {
        var (report, files) = await _findings.ComputeWithFilesAsync(query.JobId, query.Libraries, ct);
        var libraries = report.Inputs.LibraryJobIds ?? [];
        if (libraries.Count == 0)
        {
            return new FindingTargetRun(report.Job, StorageStatus.NoLibrary,
                "No permanent storage is known, so no target is proposed. Scan it with 'sfo scan <path> --role library' and compare with "
                + $"'sfo compare {query.JobId} --against <library>'.",
                [], 0, 0, 0, new Dictionary<string, int>(), []);
        }

        foreach (var key in query.Keys)
        {
            if (report.Findings.All(f => !string.Equals(f.Key, key, StringComparison.Ordinal)))
                throw new FindingException(FindingFailure.NotFound,
                    $"Finding {key} is not part of job {query.JobId}; the data it rested on may have changed. List the findings again.");
        }

        // A library that cannot be read ends the run with a named failure before anything is matched or stored
        // (https://github.com/crack666/smart_file_organizer/issues/85); never a run that looks successful.
        await _targets.EnsureReadableAsync(libraries, ct);

        var todo = report.Findings
            .Where(f => f.State is FindingState.Open or FindingState.Accepted)
            .Where(f => FindingRules.TargetKinds.Contains(f.Kind) && f.TargetInput is not null && f.FileIds.Count > 0)
            .Where(f => query.Keys.Count == 0 || query.Keys.Contains(f.Key))
            .ToList();

        var stored = await _store.GetAllAsync(query.JobId, ct);
        var (set, _) = await LibraryComparisonSet.LoadAsync(_comparisons, query.JobId, libraries, ct);
        var rows = set?.Rows ?? new Dictionary<long, FileComparison>();

        var items = new List<FindingTargetItem>(todo.Count);
        var matched = 0;
        var reused = 0;
        for (var i = 0; i < todo.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var finding = todo[i];
            FindingTargetRecord record;
            bool fromStore;
            if (!query.Refresh && stored.TryGetValue(finding.Key, out var known) && StillValid(known, finding, query.UseModel))
            {
                record = known;
                fromStore = true;
                reused++;
            }
            else
            {
                var nodes = finding.FileIds.Where(files.ContainsKey).Select(id => files[id]).ToList();
                if (nodes.Count == 0) continue;
                var answer = await _targets.MatchAsync(new LibraryMatchQuery
                {
                    SourceJobId = query.JobId,
                    Libraries = libraries,
                    Files = nodes,
                    // A named folder or a device copy keeps its name, as in sfo library match --bundle.
                    Path = finding.BundleKind is BundleKind.Folder or BundleKind.Dump ? finding.Path : null,
                    UseModel = query.UseModel,
                    Refresh = query.Refresh,
                    Model = query.Model
                }, ct);
                record = ToRecord(finding, answer, nodes, rows, query.UseModel, _time.GetUtcNow().UtcDateTime);
                await _store.UpsertAsync(record, ct);
                fromStore = false;
                matched++;
                _logger.LogInformation("Target match of {Key}: {Status} {Path}", finding.Key, record.Status.ToCode(), record.RelativePath);
            }

            items.Add(new FindingTargetItem(finding.Key, finding.Kind, finding.Title, record.Status, fromStore, record.LibraryJobId,
                record.Path, record.RelativePath, record.Certainty, record.ContentCheck, record.ContentStatus, record.Reason, record.Parts));
            progress?.Report(new FindingTargetProgress(i + 1, todo.Count, finding.Key, record.Status, fromStore));
        }

        var byStatus = items.GroupBy(x => x.Status.ToCode(), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new FindingTargetRun(report.Job, StorageStatus.Known, null, libraries, todo.Count, matched, reused, byStatus, items);
    }

    /// <summary>
    /// A stored match is kept while it rests on the same files, descriptions and libraries, unless this run may ask the
    /// model and the stored one could not (<c>--no-model</c>, model away, no usable answer).
    /// </summary>
    private static bool StillValid(FindingTargetRecord known, Finding finding, bool useModel) =>
        known.InputHash == finding.TargetInput
        && !(useModel && (!known.WithModel || known.ContentStatus is ContentCheckStatus.NoModel or ContentCheckStatus.Unavailable or ContentCheckStatus.Failed));

    /// <summary>The answer of the match as it is stored: the best existing folder, else a complete new folder, else why none.</summary>
    private static FindingTargetRecord ToRecord(
        Finding finding, LibraryMatchAnswer answer, IReadOnlyList<FileNode> nodes, IReadOnlyDictionary<long, FileComparison> rows,
        bool useModel, DateTime now)
    {
        var record = Place(answer, nodes, rows, useModel);
        record.JobId = answer.Source.Id;
        record.FindingKey = finding.Key;
        record.InputHash = finding.TargetInput!;
        record.WithModel = useModel;
        record.MatchedAt = now;
        if (answer.Parts is { Count: > 0 } parts)
        {
            // The files do not belong together: the mixture gets no target, each part its own (Issue 85).
            record.Status = FindingTargetStatus.Split;
            record.ContentCheck = TargetContentCheck.NotChecked;
            record.ContentStatus = parts.Select(p => p.Answer?.Result?.Content?.Status).FirstOrDefault(s => s is not null) ?? ContentCheckStatus.NoCandidates;
            record.Model = parts.Select(p => p.Answer?.Advice.Model).FirstOrDefault(m => m is not null);
            record.Reason = answer.Result?.NoTarget ?? "The files do not belong together by content; each part is matched on its own.";
            for (var i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                var placed = p.Answer is { } a ? Place(a, p.Files, rows, useModel) : null;
                record.Parts.Add(new FindingTargetPart
                {
                    Index = i,
                    FileIds = p.Files.Select(f => f.Id).ToList(),
                    Label = p.Label,
                    Status = placed?.Status ?? FindingTargetStatus.NoTarget,
                    LibraryJobId = placed?.LibraryJobId,
                    Path = placed?.Path,
                    RelativePath = placed?.RelativePath,
                    Certainty = placed?.Certainty,
                    ContentCheck = placed?.ContentCheck ?? TargetContentCheck.NotChecked,
                    Reason = placed?.Reason ?? p.NotMatched ?? "Not matched."
                });
            }
        }
        return record;
    }

    /// <summary>Where the answer of one match puts the files: the best existing folder, else a complete new folder, else why none.</summary>
    private static FindingTargetRecord Place(
        LibraryMatchAnswer answer, IReadOnlyList<FileNode> nodes, IReadOnlyDictionary<long, FileComparison> rows, bool useModel)
    {
        var content = answer.Result?.Content;
        var contentStatus = content?.Status ?? ContentCheckStatus.NoModel;
        var record = new FindingTargetRecord { ContentStatus = contentStatus, Model = answer.Advice.Model };
        if (answer.Excluded is { } excluded)
        {
            record.Status = FindingTargetStatus.Excluded;
            record.ContentCheck = TargetContentCheck.NotChecked;
            record.Reason = "Not matched on purpose: " + excluded;
            return record;
        }

        if (answer.Result is { Candidates.Count: > 0 } result && answer.CandidateLibraries is { Count: > 0 } candidateLibraries)
        {
            var best = result.Candidates[0];
            var library = candidateLibraries[0];
            var path = PlanChecks.Join(library.RootPath, best.Destination);
            record.Status = FindingTargetStatus.ExistingFolder;
            record.LibraryJobId = library.Id;
            record.Path = path;
            record.RelativePath = best.Destination;
            record.Certainty = best.Certainty;
            record.ContentCheck = best.Content?.Fit switch
            {
                ContentFit.Fits => TargetContentCheck.Fits,
                ContentFit.Contradicts => TargetContentCheck.Contradicts,
                ContentFit.Unclear => TargetContentCheck.Unclear,
                _ => TargetContentCheck.NotChecked
            };
            record.Reason = best.Reason;
            record.SameContentFiles = nodes.Count(f =>
                rows.TryGetValue(f.Id, out var row) && row.State == ContentState.BackedUp
                && row.MatchPath is { } match && PlanChecks.IsAtOrBelow(match, path));
            return record;
        }

        var proposal = answer.Result?.NewFolder;
        if (proposal is { NameComplete: true } && answer.NewFolderLibrary is { } home)
        {
            record.Status = FindingTargetStatus.NewFolder;
            record.LibraryJobId = home.Id;
            record.Path = PlanChecks.Join(home.RootPath, proposal.Destination);
            record.RelativePath = proposal.Destination;
            record.Certainty = proposal.Certainty;
            record.ContentCheck = TargetContentCheck.NotChecked;
            record.Reason = proposal.Reason;
            return record;
        }

        record.ContentCheck = TargetContentCheck.NotChecked;
        if (answer.Result?.NoTarget is { } why)
        {
            record.Status = FindingTargetStatus.NoTarget;
            record.Reason = why;
            return record;
        }
        var modelMissing =!useModel || contentStatus is ContentCheckStatus.NoModel or ContentCheckStatus.Unavailable or ContentCheckStatus.Failed;
        record.Status = proposal is { NameComplete: false } && modelMissing
            ? FindingTargetStatus.NoModel
            : contentStatus == ContentCheckStatus.NoDescription
                ? FindingTargetStatus.ContentUnclear
                : FindingTargetStatus.NoFittingFolder;
        record.Reason = record.Status switch
        {
            FindingTargetStatus.NoModel =>
                $"No folder of the libraries fits; a new folder {proposal!.Destination} needs a name the rules cannot complete, and no model was {(useModel ? "reachable" : "asked (--no-model)")}.",
            FindingTargetStatus.ContentUnclear =>
                "No folder of the libraries fits by time, name or kind of files, and the files are not described yet, so their content cannot choose one (sfo analyze describes them).",
            _ => proposal is not null
                ? $"No folder of the libraries fits, and no complete name for a new folder below {proposal.Parent.RelativePath} was found."
                : "No folder of the libraries fits these files."
        };
        return record;
    }
}
