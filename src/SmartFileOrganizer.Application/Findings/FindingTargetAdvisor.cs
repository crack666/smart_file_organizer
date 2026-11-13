using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Findings;

/// <summary>
/// Proposes a place in the library for groups of source files that do not fit their folder. The seam where the content
/// check of https://github.com/crack666/smart_file_organizer/issues/56 plugs in: an advisor that compares the content
/// returns <see cref="TargetContentCheck.Fits"/> (or <see cref="TargetContentCheck.Contradicts"/>), and the findings
/// read it without any other change.
/// </summary>
public interface IFindingTargetAdvisor
{
    /// <summary>
    /// One answer per group, in order; null where no existing folder of any of the libraries fits. Writes nothing. A
    /// target lies inside one of the library roots (<see cref="FindingTarget.LibraryJobId"/>); findings drop any other.
    /// </summary>
    /// <param name="libraryJobIds">The libraries to look in; the best place of all of them wins.</param>
    /// <param name="comparison">Comparison rows of the source against these libraries by file id (may be empty).</param>
    Task<IReadOnlyList<FindingTarget?>> ProposeAsync(
        long sourceJobId,
        IReadOnlyList<long> libraryJobIds,
        IReadOnlyList<IReadOnlyList<FileNode>> groups,
        IReadOnlyDictionary<long, FileComparison> comparison,
        CancellationToken ct = default);
}

/// <summary>
/// Today's advisor: the rules of <c>sfo library match</c> (period, names, file types, files already there). It does
/// not compare content, so every target says <see cref="TargetContentCheck.NotChecked"/>.
/// </summary>
public sealed class LibraryFindingTargetAdvisor(LibraryTargetService targets) : IFindingTargetAdvisor
{
    public async Task<IReadOnlyList<FindingTarget?>> ProposeAsync(
        long sourceJobId,
        IReadOnlyList<long> libraryJobIds,
        IReadOnlyList<IReadOnlyList<FileNode>> groups,
        IReadOnlyDictionary<long, FileComparison> comparison,
        CancellationToken ct = default)
    {
        var best = await targets.BestPlacesAsync(sourceJobId, libraryJobIds, groups, ct);
        var result = new List<FindingTarget?>(best.Count);
        for (var i = 0; i < best.Count; i++)
        {
            if (best[i] is not { } place)
            {
                result.Add(null);
                continue;
            }

            var (candidate, library) = place;
            var root = candidate.Target.FullPath;
            var same = groups[i].Count(f =>
                comparison.TryGetValue(f.Id, out var row)
                && row.State == ContentState.BackedUp
                && row.MatchPath is { } match
                && IsBelow(match, root));
            result.Add(new FindingTarget(root, candidate.Destination, candidate.Certainty, candidate.Reason, same,
                TargetContentCheck.NotChecked, library.Id, library.RootPath));
        }
        return result;
    }

    private static bool IsBelow(string path, string dir)
    {
        var d = Path.TrimEndingDirectorySeparator(dir);
        return path.StartsWith(d, StringComparison.OrdinalIgnoreCase)
               && path.Length > d.Length
               && (path[d.Length] == '/' || path[d.Length] == '\\');
    }
}
