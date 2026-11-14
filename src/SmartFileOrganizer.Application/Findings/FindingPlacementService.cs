using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Findings;

/// <summary>One decision of the controller about where a finding, or one part of it, goes.</summary>
/// <param name="Part">The part of a finding split by content (index as <c>sfo findings candidates</c> lists it); null for the whole.</param>
/// <param name="Status">A <see cref="FindingTargetStatus"/> code: <c>existing_folder</c>, <c>new_folder</c>, <c>inbox</c> or <c>no_target</c>.</param>
/// <param name="LibraryJobId">The library; may be left out when there is one, or when <paramref name="Folder"/> is a full path.</param>
/// <param name="Folder">The folder: relative to the library root (with <c>/</c>) or a full path below it.</param>
/// <param name="Withdraw">Remove the stored target of the finding instead; it is <c>not_matched</c> again.</param>
public sealed record PlacementRequest(
    string Key,
    int? Part = null,
    string? Status = null,
    long? LibraryJobId = null,
    string? Folder = null,
    string? Reason = null,
    bool Withdraw = false);

/// <summary>Why one decision was refused, and with it the whole call.</summary>
/// <param name="Index">Position of the decision in the call (0 for a single one).</param>
public sealed record PlacementRefusal(int Index, string Key, int? Part, IReadOnlyList<PlacementProblem> Problems, string Message);

/// <summary>At least one decision was refused; nothing of the call was stored.</summary>
public sealed class PlacementRefusedException(IReadOnlyList<PlacementRefusal> refused)
    : Exception($"{refused.Count} decision(s) refused; nothing was stored: "
                + string.Join("; ", refused.Select(r => $"{r.Key}{(r.Part is { } p ? $" part {p}" : string.Empty)}: {r.Message}")))
{
    public IReadOnlyList<PlacementRefusal> Refused { get; } = refused;
}

/// <summary>A stored decision.</summary>
public sealed record PlacementResult(
    string Key, int? Part, FindingTargetStatus? Status, long? LibraryJobId, string? Path, string? RelativePath, string? Reason, bool Withdrawn);

/// <summary>The answer of <c>sfo findings place</c>.</summary>
public sealed record PlacementRun(ScanJob Job, IReadOnlyList<PlacementResult> Items);

/// <summary>
/// Stores the decisions of the controller (https://github.com/crack666/smart_file_organizer/issues/95): per finding, or per
/// part of a finding split by content, an existing folder, a new folder below an existing one, the inbox of a library, or
/// no target, with the reason; <c>decided_by</c> <c>controller</c>. Every decision is checked as a plan target is: it lies
/// below a root of the libraries of the comparison, never in a protected place, and the path can be a folder. One refused
/// decision refuses the whole call: nothing is stored. A later <c>sfo findings targets</c> keeps the decisions. Nothing on
/// disk is changed.
/// </summary>
public sealed class FindingPlacementService
{
    /// <summary>The statuses a decision can set.</summary>
    public static readonly IReadOnlyList<FindingTargetStatus> Settable =
        [FindingTargetStatus.ExistingFolder, FindingTargetStatus.NewFolder, FindingTargetStatus.Inbox, FindingTargetStatus.NoTarget];

    private readonly FindingService _findings;
    private readonly FindingTargetService _matcher;
    private readonly LibraryTargetService _libraries;
    private readonly IFindingTargetRepository _store;
    private readonly IContentComparisonRepository _comparisons;
    private readonly IProtectedPlaces _protected;
    private readonly PlanOptions _planOptions;
    private readonly LibraryMatchOptions _matchOptions;
    private readonly TimeProvider _time;

    public FindingPlacementService(
        FindingService findings,
        FindingTargetService matcher,
        LibraryTargetService libraries,
        IFindingTargetRepository store,
        IContentComparisonRepository comparisons,
        IProtectedPlaces protectedPlaces,
        PlanOptions planOptions,
        LibraryMatchOptions matchOptions,
        TimeProvider? time = null)
    {
        _findings = findings;
        _matcher = matcher;
        _libraries = libraries;
        _store = store;
        _comparisons = comparisons;
        _protected = protectedPlaces;
        _planOptions = planOptions;
        _matchOptions = matchOptions;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Checks every decision, then stores all of them in one transaction, or none.</summary>
    /// <exception cref="PlacementRefusedException">At least one decision was refused; nothing was stored.</exception>
    public async Task<PlacementRun> PlaceAsync(long jobId, IReadOnlyList<long> libraryJobIds, IReadOnlyList<PlacementRequest> requests, CancellationToken ct = default)
    {
        var (report, files) = await _findings.ComputeWithFilesAsync(jobId, libraryJobIds, ct);
        var libraryIds = report.Inputs.LibraryJobIds ?? [];
        var libraries = new List<(ScanJob Job, LibraryMap Map)>();
        foreach (var id in libraryIds)
            libraries.Add(await _libraries.MapAsync(id, ct));
        var stored = await _store.GetAllAsync(jobId, ct);
        var protectedPlaces = _protected.Get().Concat(_planOptions.ProtectedPlaces).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        IReadOnlyDictionary<long, FileComparison>? rows = null;

        var now = _time.GetUtcNow().UtcDateTime;
        var pending = new Dictionary<string, FindingTargetRecord>(StringComparer.Ordinal);
        var remove = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<PlacementResult>();
        var refused = new List<PlacementRefusal>();

        for (var index = 0; index < requests.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var r = requests[index];
            var problems = new List<(PlacementProblem Problem, string Text)>();
            void Refuse(PlacementProblem p, string text) => problems.Add((p, text));

            var finding = report.Findings.FirstOrDefault(f => string.Equals(f.Key, r.Key, StringComparison.Ordinal));
            if (finding is null)
                Refuse(PlacementProblem.FindingNotFound, $"{r.Key} is not a finding of job {jobId} (or the data it rested on changed)");
            else if (!FindingRules.TargetKinds.Contains(finding.Kind) || finding.TargetInput is null)
                Refuse(PlacementProblem.NotPlaceable, $"a {finding.Kind.ToCode()} finding gets no target");

            if (problems.Count == 0 && r.Withdraw)
            {
                remove.Add(r.Key);
                pending.Remove(r.Key);
                results.Add(new PlacementResult(r.Key, null, null, null, null, null, r.Reason, Withdrawn: true));
                continue;
            }

            FindingTargetStatus status = default;
            if (string.IsNullOrWhiteSpace(r.Reason))
                Refuse(PlacementProblem.ReasonMissing, "every decision needs a reason");
            if (!VerdictCodes.TryParse<FindingTargetStatus>(r.Status, out status) || !Settable.Contains(status))
                Refuse(PlacementProblem.StatusInvalid, $"'{r.Status}' is not one of {string.Join(", ", Settable.Select(s => s.ToCode()))}");

            // Where it goes.
            (ScanJob Job, LibraryMap Map)? library = null;
            string? relative = null;
            string? path = null;
            if (problems.Count == 0 && status != FindingTargetStatus.NoTarget)
            {
                if (libraries.Count == 0)
                    Refuse(PlacementProblem.LibraryNotFound, $"job {jobId} was compared with no library; there is no permanent storage");
                else
                    (library, relative) = Resolve(r, status, libraries, report.Job, Refuse);
                if (library is { } lib && relative is not null)
                {
                    path = PlanChecks.Join(lib.Job.RootPath, relative);
                    if (protectedPlaces.FirstOrDefault(place => PlanChecks.IsAtOrBelow(path, place)) is { } place)
                        Refuse(PlacementProblem.TargetProtected, $"{path} lies in the protected place {place}");
                    CheckFolder(status, lib, relative, path, Refuse);
                }
            }

            // Which part.
            FindingTargetRecord? split = null;
            if (problems.Count == 0 && r.Part is { } part)
            {
                split = pending.GetValueOrDefault(r.Key);
                if (split is null && stored.TryGetValue(r.Key, out var known) && known.Status == FindingTargetStatus.Split
                    && (known.InputHash == finding!.TargetInput || known.DecidedBy == TargetDecidedBy.Controller))
                    split = Clone(known);
                if (split is null)
                {
                    // No stored split: the parts as the rules see them now, without a model, not stored by themselves.
                    rows ??= (await LibraryComparisonSet.LoadAsync(_comparisons, jobId, libraryIds, ct)).Set?.Rows ?? new Dictionary<long, FileComparison>();
                    var nodes = finding!.FileIds.Where(files.ContainsKey).Select(id => files[id]).ToList();
                    var preview = nodes.Count == 0 ? null
                        : await _matcher.MatchOneAsync(new FindingTargetQuery { JobId = jobId, Libraries = libraryIds }, finding, nodes, libraryIds, rows, ct);
                    if (preview?.Status == FindingTargetStatus.Split) split = preview;
                }
                if (split is null || split.Parts.All(p => p.Index != part))
                    Refuse(PlacementProblem.PartNotFound, split is null
                        ? "the finding is not split by content; decide it as a whole"
                        : $"the finding has the parts {string.Join(", ", split.Parts.Select(p => p.Index))}");
            }

            if (problems.Count > 0)
            {
                refused.Add(new PlacementRefusal(index, r.Key, r.Part, problems.Select(p => p.Problem).Distinct().ToList(),
                    string.Join("; ", problems.Select(p => p.Text))));
                continue;
            }

            var reason = r.Reason!.Trim();
            if (r.Part is { } index2 && split is not null)
            {
                var p = split.Parts.First(x => x.Index == index2);
                p.Status = status;
                p.LibraryJobId = library?.Job.Id;
                p.Path = path;
                p.RelativePath = relative;
                p.Certainty = status == FindingTargetStatus.NoTarget ? null : ResultCertainty.Likely;
                p.ContentCheck = TargetContentCheck.NotChecked;
                p.Reason = reason;
                p.DecidedBy = TargetDecidedBy.Controller;
                split.InputHash = finding!.TargetInput!;
                split.DecidedBy = TargetDecidedBy.Controller;
                split.MatchedAt = now;
                pending[r.Key] = split;
            }
            else
            {
                pending[r.Key] = new FindingTargetRecord
                {
                    JobId = jobId,
                    FindingKey = r.Key,
                    InputHash = finding!.TargetInput!,
                    Status = status,
                    LibraryJobId = library?.Job.Id,
                    Path = path,
                    RelativePath = relative,
                    Certainty = status == FindingTargetStatus.NoTarget ? null : ResultCertainty.Likely,
                    ContentCheck = TargetContentCheck.NotChecked,
                    ContentStatus = ContentCheckStatus.Controller,
                    Reason = reason,
                    WithModel = false,
                    MatchedAt = now,
                    DecidedBy = TargetDecidedBy.Controller
                };
            }
            remove.Remove(r.Key);
            results.Add(new PlacementResult(r.Key, r.Part, status, library?.Job.Id, path, relative, reason, Withdrawn: false));
        }

        if (refused.Count > 0)
            throw new PlacementRefusedException(refused);
        await _store.ReplaceAsync(jobId, pending.Values.ToList(), remove.ToList(), ct);
        return new PlacementRun(report.Job, results);
    }

    /// <summary>The library and the folder below its root (with <c>/</c>) a decision names; null when it cannot be told.</summary>
    private (ScanJob, LibraryMap)? FindLibrary(long? id, IReadOnlyList<(ScanJob Job, LibraryMap Map)> libraries) =>
        id is { } wanted ? libraries.FirstOrDefault(l => l.Job.Id == wanted) is { Job: not null } found ? found : null
        : libraries.Count == 1 ? libraries[0] : null;

    private ((ScanJob Job, LibraryMap Map)?, string?) Resolve(
        PlacementRequest r, FindingTargetStatus status, IReadOnlyList<(ScanJob Job, LibraryMap Map)> libraries, ScanJob source,
        Action<PlacementProblem, string> refuse)
    {
        if (status == FindingTargetStatus.Inbox)
        {
            var lib = FindLibrary(r.LibraryJobId, libraries);
            if (lib is null)
            {
                Unresolved(r, libraries, refuse);
                return (null, null);
            }
            return (lib, $"{_matchOptions.InboxFolderOf(lib.Value.Item1.RootPath)}/{SourceFolderName(source)}");
        }

        if (string.IsNullOrWhiteSpace(r.Folder))
        {
            refuse(PlacementProblem.FolderMissing, $"{status.ToCode()} needs --folder");
            return (null, null);
        }
        var folder = r.Folder.Trim();
        if (Path.IsPathRooted(folder))
        {
            string full;
            try
            {
                full = PlanChecks.Normalize(folder);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                refuse(PlacementProblem.InvalidPath, $"'{folder}' is no valid path");
                return (null, null);
            }
            var inside = libraries.Where(l => PlanChecks.IsAtOrBelow(full, l.Job.RootPath) && (r.LibraryJobId is null || r.LibraryJobId == l.Job.Id)).ToList();
            if (inside.Count == 0)
            {
                refuse(PlacementProblem.OutsideLibrary, $"{full} lies outside every library root ({string.Join(", ", libraries.Select(l => l.Job.RootPath))})");
                return (null, null);
            }
            var home = inside.OrderByDescending(l => l.Job.RootPath.Length).First();
            var relative = Path.GetRelativePath(PlanChecks.Normalize(home.Job.RootPath), full).Replace('\\', '/');
            return Checked(home, relative == "." ? string.Empty : relative, refuse);
        }

        var named = FindLibrary(r.LibraryJobId, libraries);
        if (named is null)
        {
            Unresolved(r, libraries, refuse);
            return (null, null);
        }
        return Checked(named.Value, folder.Replace('\\', '/'), refuse);
    }

    private static void Unresolved(PlacementRequest r, IReadOnlyList<(ScanJob Job, LibraryMap Map)> libraries, Action<PlacementProblem, string> refuse)
    {
        if (r.LibraryJobId is { } id)
            refuse(PlacementProblem.LibraryNotFound, $"job {id} is not one of the libraries of the comparison ({string.Join(", ", libraries.Select(l => l.Job.Id))})");
        else
            refuse(PlacementProblem.LibraryAmbiguous, $"there are {libraries.Count} libraries ({string.Join(", ", libraries.Select(l => l.Job.Id))}); name one with --library");
    }

    /// <summary>A relative folder: every level a valid folder name, no <c>.</c> or <c>..</c>, not the root itself.</summary>
    private static ((ScanJob Job, LibraryMap Map)?, string?) Checked(
        (ScanJob Job, LibraryMap Map) library, string relative, Action<PlacementProblem, string> refuse)
    {
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            refuse(PlacementProblem.OutsideLibrary, $"the root of library {library.Job.Id} itself is no target; name a folder below it");
            return (null, null);
        }
        foreach (var part in parts)
        {
            if (part.Trim().Trim('.').Length == 0 || part != part.Trim() || part.EndsWith('.')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.IndexOfAny(['*', '?', '"', '<', '>', '|', ':']) >= 0)
            {
                refuse(PlacementProblem.InvalidPath, $"'{part}' cannot be a folder name");
                return (null, null);
            }
        }
        return (library, string.Join('/', parts));
    }

    /// <summary>An existing folder must be one of the scan; a new one must not exist yet and must lie directly below one.</summary>
    private static void CheckFolder(FindingTargetStatus status, (ScanJob Job, LibraryMap Map) library, string relative, string path,
        Action<PlacementProblem, string> refuse)
    {
        var map = library.Map;
        switch (status)
        {
            case FindingTargetStatus.ExistingFolder when !map.ByRelativePath.ContainsKey(relative):
                refuse(PlacementProblem.FolderNotFound, $"{relative} is no folder of the scan of library {library.Job.Id}");
                break;
            case FindingTargetStatus.NewFolder:
            {
                if (map.ByRelativePath.ContainsKey(relative) || Directory.Exists(path))
                    refuse(PlacementProblem.FolderExists, $"{relative} exists already in library {library.Job.Id}; decide existing_folder");
                var cut = relative.LastIndexOf('/');
                if (cut > 0 && !map.ByRelativePath.ContainsKey(relative[..cut]))
                    refuse(PlacementProblem.ParentNotFound, $"{relative[..cut]} is no folder of library {library.Job.Id}; a new folder goes directly below an existing one");
                break;
            }
        }
    }

    /// <summary>The subfolder of a source in an inbox: the name of its root folder, or <c>source-&lt;job&gt;</c> for a drive root.</summary>
    public static string SourceFolderName(ScanJob source)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source.RootPath));
        var cleaned = new string((name ?? string.Empty).Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '*' or '?' ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? $"source-{source.Id}" : cleaned;
    }

    private static FindingTargetRecord Clone(FindingTargetRecord r) => new()
    {
        JobId = r.JobId, FindingKey = r.FindingKey, InputHash = r.InputHash, Status = r.Status, LibraryJobId = r.LibraryJobId,
        Path = r.Path, RelativePath = r.RelativePath, Certainty = r.Certainty, ContentCheck = r.ContentCheck,
        ContentStatus = r.ContentStatus, Reason = r.Reason, SameContentFiles = r.SameContentFiles, WithModel = r.WithModel,
        Model = r.Model, MatchedAt = r.MatchedAt, DecidedBy = r.DecidedBy,
        Parts = r.Parts.Select(p => new FindingTargetPart
        {
            Index = p.Index, FileIds = [.. p.FileIds], Label = p.Label, Status = p.Status, LibraryJobId = p.LibraryJobId, Path = p.Path,
            RelativePath = p.RelativePath, Certainty = p.Certainty, ContentCheck = p.ContentCheck, Reason = p.Reason, DecidedBy = p.DecidedBy
        }).ToList()
    };
}
