using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Decisions;

/// <summary>
/// Where the targets of a target structure lie (https://github.com/crack666/smart_file_organizer/issues/71): in the root of
/// one library, as every other target does (https://github.com/crack666/smart_file_organizer/issues/74), or bound to none.
/// </summary>
/// <param name="Library">The library; null unless <see cref="TargetBinding.Bound"/>.</param>
/// <param name="Under">The folder of the structure's root below the library root, with <c>/</c>; empty when it is the root itself.</param>
/// <param name="Message">What to do when the structure is bound to none; null when bound.</param>
/// <param name="Chosen">A person chose the library, by the structure's root or <c>--library</c>. Otherwise it is the only one
/// known, which holds only until a second one is: such a target is stored without its library, and the plan asks again.</param>
public sealed record TargetRoot(TargetBinding Status, ScanJob? Library, string Under, string? Message, bool Chosen = false)
{
    /// <summary>A target of the structure as a target in this root; null where the root lies outside every library.</summary>
    public TargetProposal? Place(TargetProposal? target) => target switch
    {
        null => null,
        _ when Status == TargetBinding.OutsideLibrary => null,
        _ when Library is { } library => target with
        {
            Path = Under.Length == 0 ? target.Path : $"{Under}/{target.Path}",
            LibraryJobId = Chosen ? library.Id : null
        },
        _ => target
    };

    /// <summary>
    /// The root of <paramref name="structure"/>. Its own <c>root</c> must lie in a library; without one, the library named
    /// (<paramref name="named"/>), else the only library of the comparison, else the only library there is.
    /// </summary>
    /// <param name="libraries">Every completed library job.</param>
    /// <param name="compared">The libraries of the newest comparison of the source.</param>
    /// <exception cref="TargetRootException">The structure's root does not lie in the library named.</exception>
    public static TargetRoot Of(TargetStructure structure, IReadOnlyList<ScanJob> libraries, IReadOnlyList<long> compared, ScanJob? named)
    {
        if (structure.Root is { Length: > 0 } root)
        {
            if (named is not null)
            {
                if (!PlanChecks.IsAtOrBelow(root, named.RootPath))
                    throw new TargetRootException($"The root of the target structure ({root}) does not lie in library job {named.Id} ({named.RootPath}).");
                return Bound(named, root, chosen: true);
            }
            // The innermost library; a root scanned several times, the job of the comparison, else the newest.
            var home = libraries.Where(l => PlanChecks.IsAtOrBelow(root, l.RootPath))
                .OrderByDescending(l => PlanChecks.Normalize(l.RootPath).Length)
                .ThenByDescending(l => compared.Contains(l.Id)).ThenByDescending(l => l.Id)
                .FirstOrDefault();
            return home is not null
                ? Bound(home, root, chosen: true)
                : new TargetRoot(TargetBinding.OutsideLibrary, null, string.Empty,
                    $"The root of the target structure ({root}) lies in no library: no target is proposed. Scan it with "
                    + "'sfo scan <path> --role library', or set root to null to place the targets in a known library.");
        }

        if (named is not null) return Bound(named, named.RootPath, chosen: true);
        var known = libraries.Where(l => compared.Contains(l.Id)).ToList();
        if (known.Count == 0)
            known = libraries.GroupBy(l => PlanChecks.Normalize(l.RootPath), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(l => l.Id).First()).ToList();
        return known.Count switch
        {
            1 => Bound(known[0], known[0].RootPath, chosen: false),
            0 => new TargetRoot(TargetBinding.NoLibrary, null, string.Empty,
                "No permanent storage is known: the targets are relative, without a full path. Scan it with 'sfo scan <path> --role library'."),
            _ => new TargetRoot(TargetBinding.SeveralLibraries, null, string.Empty,
                $"Several libraries are known ({string.Join(", ", known.Select(l => $"job {l.Id}: {l.RootPath}"))}): the targets are relative, "
                + "without a full path. Name one with --library, or set root in the target structure.")
        };
    }

    private static TargetRoot Bound(ScanJob library, string root, bool chosen)
    {
        var under = Path.GetRelativePath(PlanChecks.Normalize(library.RootPath), PlanChecks.Normalize(root)).Replace('\\', '/').Trim('/');
        return new TargetRoot(TargetBinding.Bound, library, under == "." ? string.Empty : under, null, chosen);
    }
}

/// <summary>The target structure cannot lie in the library named with <c>--library</c>, or that job is no library.</summary>
public sealed class TargetRootException(string message) : Exception(message);

/// <summary>Free space at the root of the targets and what the targets there need.</summary>
/// <param name="Files">Kept files with a target that no library holds yet.</param>
/// <param name="FreeBytes">Null when it cannot be told, or when the targets are bound to no library.</param>
/// <param name="Space">Null when the targets are bound to no library.</param>
public sealed record TargetRoom(long Files, long NeededBytes, long? FreeBytes, TargetSpace? Space)
{
    public static TargetRoom Of(TargetRoot root, long files, long needed, Func<string, long?> freeBytes)
    {
        if (root.Library is not { } library) return new TargetRoom(files, needed, null, null);
        var free = freeBytes(library.RootPath);
        return new TargetRoom(files, needed, free,
            free is not { } bytes ? TargetSpace.Unknown : needed <= bytes ? TargetSpace.Enough : TargetSpace.NotEnough);
    }

    /// <summary>The warning for a root without enough room; null otherwise.</summary>
    public string? Warning(TargetRoot root) => Space == TargetSpace.NotEnough && root.Library is { } library
        ? $"Library job {library.Id} ({library.RootPath}): the targets need {FindingRules.Size(NeededBytes)}, only {FindingRules.Size(FreeBytes!.Value)} are free."
        : null;
}
