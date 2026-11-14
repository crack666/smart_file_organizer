using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Findings;

/// <summary>What <c>sfo findings candidates</c> asks for.</summary>
public sealed record FindingCandidateQuery
{
    public long JobId { get; init; }

    /// <summary>Library jobs whose comparisons count and whose folders are candidates; empty for those of the newest compare.</summary>
    public IReadOnlyList<long> Libraries { get; init; } = [];

    /// <summary>Only these findings; empty for every open or accepted finding that should move.</summary>
    public IReadOnlyCollection<string> Keys { get; init; } = [];

    /// <summary>Candidate folders per finding (or per part of a mixed one), best first.</summary>
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>File names listed per finding, part and candidate folder.</summary>
    public int Names { get; init; } = DefaultNames;

    public const int DefaultLimit = 5;
    public const int MaxLimit = 20;
    public const int DefaultNames = 5;
}

/// <summary>A marked file of a finding: path and markers, never content (Issue 95).</summary>
public sealed record SensitiveFileRef(string RelativePath, SensitiveMarker Markers);

/// <summary>One finding with the facts a controlling model decides from.</summary>
/// <param name="Examples">A few unmarked files of the finding (relative paths).</param>
/// <param name="SensitiveFiles">The marked files of the finding (marked themselves or in a marked folder): path and markers only.</param>
/// <param name="Marked">Ids of the marked files; their names are never listed as examples.</param>
public sealed record FindingCandidateItem(
    Finding Finding,
    UnitCandidates Candidates,
    IReadOnlyList<string> Examples,
    IReadOnlyList<SensitiveFileRef> SensitiveFiles,
    IReadOnlySet<long> Marked);

/// <summary>The answer of <c>sfo findings candidates</c>.</summary>
public sealed record FindingCandidateRun(
    ScanJob Job,
    StorageStatus Status,
    string? Message,
    IReadOnlyList<LibraryRootFacts> Libraries,
    IReadOnlyList<FindingCandidateItem> Items);

/// <summary>
/// Facts instead of a verdict (https://github.com/crack666/smart_file_organizer/issues/95): for every finding that should
/// move, what it is (kind, size, period, kinds of files, what its files show by their stored descriptions, the parts of a
/// mixture) and the candidate folders of the rules in every library, with what they hold and their stored description.
/// The controlling model decides from this and stores its decision with <see cref="FindingPlacementService"/>. No model
/// is asked; a marked file shows path and markers only.
/// </summary>
public sealed class FindingCandidateService
{
    private readonly FindingService _findings;
    private readonly LibraryTargetService _targets;

    public FindingCandidateService(FindingService findings, LibraryTargetService targets)
    {
        _findings = findings;
        _targets = targets;
    }

    public async Task<FindingCandidateRun> CandidatesAsync(FindingCandidateQuery query, CancellationToken ct = default)
    {
        var (report, files) = await _findings.ComputeWithFilesAsync(query.JobId, query.Libraries, ct);
        var libraries = report.Inputs.LibraryJobIds ?? [];
        if (libraries.Count == 0)
        {
            return new FindingCandidateRun(report.Job, StorageStatus.NoLibrary,
                "No permanent storage is known, so there is no candidate folder. Scan it with 'sfo scan <path> --role library' and compare with "
                + $"'sfo compare {query.JobId} --against <library>'.", [], []);
        }
        foreach (var key in query.Keys)
        {
            if (report.Findings.All(f => !string.Equals(f.Key, key, StringComparison.Ordinal)))
                throw new FindingException(FindingFailure.NotFound,
                    $"Finding {key} is not part of job {query.JobId}; the data it rested on may have changed. List the findings again.");
        }

        var todo = report.Findings
            .Where(f => f.State is FindingState.Open or FindingState.Accepted)
            .Where(f => FindingRules.TargetKinds.Contains(f.Kind) && f.FileIds.Count > 0)
            .Where(f => query.Keys.Count == 0 || query.Keys.Contains(f.Key))
            .Select(f => (Finding: f, Files: f.FileIds.Where(files.ContainsKey).Select(id => files[id]).ToList()))
            .Where(x => x.Files.Count > 0)
            .ToList();
        var answer = await _targets.CandidatesAsync(new CandidateQuery(query.JobId, libraries,
            todo.Select(x => new CandidateUnit(x.Finding.Key, x.Files,
                x.Finding.BundleKind is BundleKind.Folder or BundleKind.Dump ? x.Finding.Path : null)).ToList(),
            Math.Clamp(query.Limit, 0, FindingCandidateQuery.MaxLimit), Math.Clamp(query.Names, 0, 20)), ct);

        var items = new List<FindingCandidateItem>(todo.Count);
        for (var i = 0; i < todo.Count; i++)
        {
            var (finding, nodes) = todo[i];
            var marked = nodes.Select(n => (Node: n, Markers: answer.Marked.GetValueOrDefault(n.Id) | SensitiveOutputGuard.GetMarkers(n))).ToList();
            items.Add(new FindingCandidateItem(finding, answer.Units[i],
                marked.Where(m => m.Markers == SensitiveMarker.None).Select(m => Rel(m.Node)).Take(Math.Max(0, query.Names)).ToList(),
                marked.Where(m => m.Markers != SensitiveMarker.None).Select(m => new SensitiveFileRef(Rel(m.Node), m.Markers)).ToList(),
                answer.Marked.Keys.ToHashSet()));
        }
        return new FindingCandidateRun(report.Job, StorageStatus.Known, null, answer.Libraries, items);
    }

    private static string Rel(FileNode f) => f.RelativePath.Replace('\\', '/');
}
