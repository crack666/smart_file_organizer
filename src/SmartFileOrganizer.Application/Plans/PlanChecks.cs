using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Plans;

/// <summary>Settings of the plan, section <c>Plan</c> of the settings file.</summary>
public sealed class PlanOptions
{
    /// <summary>
    /// Folders another program manages (a photo manager with its own index, for example): nothing in them is moved,
    /// renamed or removed, because that program would lose track. They stay readable as a library. An action whose
    /// target lies in one is marked (<c>into_managed_area</c>), so the person knows the program must read it in again.
    /// </summary>
    public List<string> ManagedAreas { get; set; } = [];

    /// <summary>Further places no target may lie in, beside the user profile and the system drive.</summary>
    public List<string> ProtectedPlaces { get; set; } = [];
}

/// <summary>What the checks of a plan look at.</summary>
/// <param name="JobRoot">Root of the source job.</param>
/// <param name="LibraryRoots">Roots of the libraries of the plan, by job id: targets lie below one of them.</param>
/// <param name="AllLibraryRoots">Roots of every library job: no source lies in one.</param>
/// <param name="BackedUp">Full paths of the source files some library holds with the same content.</param>
/// <param name="Exists">Whether a file or folder exists at a path (only stat, nothing is opened).</param>
public sealed record PlanCheckContext(
    string JobRoot,
    IReadOnlyDictionary<long, string> LibraryRoots,
    IReadOnlyList<string> AllLibraryRoots,
    IReadOnlyList<string> ManagedAreas,
    IReadOnlyList<string> ProtectedPlaces,
    IReadOnlySet<string> BackedUp,
    Func<string, bool> Exists);

/// <summary>
/// The hard rules of a plan (https://github.com/crack666/smart_file_organizer/issues/11), as pure functions: every target
/// lies below a library root, never in the source, never in the user profile, on the system drive or in a protected
/// place; content a library holds already is not moved or copied; a sensitive file never goes to quarantine; a library
/// and a managed area are never the source of a move, a quarantine or a removal. <c>leave</c> needs no check.
/// </summary>
public static class PlanChecks
{
    /// <summary>Kinds that write a copy at the target.</summary>
    public static bool Writes(PlanActionKind kind) => kind is PlanActionKind.Move or PlanActionKind.Copy or PlanActionKind.Quarantine;

    /// <summary>Kinds that take the original away from its place.</summary>
    public static bool RemovesSource(PlanActionKind kind) => kind is PlanActionKind.Move or PlanActionKind.Quarantine or PlanActionKind.DeleteSource;

    /// <summary>
    /// The problems of one action. <paramref name="takenByOther"/> says whether another action already writes to a
    /// target (normalized path).
    /// </summary>
    public static IReadOnlyList<PlanProblem> Check(PlanAction a, PlanCheckContext ctx, Func<string, bool> takenByOther)
    {
        var problems = new List<PlanProblem>();
        if (a.Kind == PlanActionKind.Leave)
            return problems;

        if (ctx.AllLibraryRoots.Any(root => IsAtOrBelow(a.SourcePath, root)))
            problems.Add(PlanProblem.SourceInLibrary);
        if (RemovesSource(a.Kind) && ctx.ManagedAreas.Any(area => IsAtOrBelow(a.SourcePath, area)))
            problems.Add(PlanProblem.SourceInManagedArea);
        if (a.Sensitive && (a.Kind == PlanActionKind.Quarantine
                            || a.Kind == PlanActionKind.DeleteSource && a.Origin != PlanActionOrigin.Human))
            problems.Add(PlanProblem.SensitiveKind);
        if (Writes(a.Kind) && HoldsBackedUp(a, ctx))
            problems.Add(PlanProblem.AlreadyInLibrary);

        if (a.TargetPath is not { Length: > 0 } target)
        {
            problems.Add(PlanProblem.NoTarget);
            return problems;
        }

        if (LibraryOf(target, ctx) is null)
            problems.Add(PlanProblem.TargetOutsideLibrary);
        if (IsAtOrBelow(target, ctx.JobRoot) || IsAtOrBelow(target, a.SourcePath))
            problems.Add(PlanProblem.TargetInSource);
        if (ctx.ProtectedPlaces.Any(place => IsAtOrBelow(target, place)))
            problems.Add(PlanProblem.TargetProtected);
        if (Writes(a.Kind) && (takenByOther(Normalize(target)) || ctx.Exists(target)))
            problems.Add(PlanProblem.NameCollision);
        return problems;
    }

    /// <summary>The problems of every action of a plan; a target two actions write to collides for both.</summary>
    public static IReadOnlyDictionary<long, IReadOnlyList<PlanProblem>> CheckAll(IReadOnlyList<PlanAction> actions, PlanCheckContext ctx)
    {
        var writers = actions
            .Where(a => Writes(a.Kind) && a.TargetPath is { Length: > 0 })
            .GroupBy(a => Normalize(a.TargetPath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Id).ToList(), StringComparer.OrdinalIgnoreCase);
        return actions.ToDictionary(a => a.Id, a => Check(a, ctx,
            t => writers.TryGetValue(t, out var ids) && ids.Any(id => id != a.Id)));
    }

    /// <summary>The library of the plan whose root holds <paramref name="target"/> (strictly below it), else null.</summary>
    public static long? LibraryOf(string target, PlanCheckContext ctx)
    {
        foreach (var (id, root) in ctx.LibraryRoots)
        {
            if (IsAtOrBelow(target, root) && !string.Equals(Normalize(target), Normalize(root), StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return null;
    }

    /// <summary>Whether the target lies in a managed area: the program that manages it must read it in again.</summary>
    public static bool IntoManagedArea(PlanAction a, PlanCheckContext ctx) =>
        Writes(a.Kind) && a.TargetPath is { Length: > 0 } t && ctx.ManagedAreas.Any(area => IsAtOrBelow(t, area));

    /// <summary>A file some library holds already, or a folder with such a file below it.</summary>
    public static bool HoldsBackedUp(PlanAction a, PlanCheckContext ctx)
    {
        if (ctx.BackedUp.Count == 0) return false;
        return a.SourceKind == PlanSourceKind.File
            ? ctx.BackedUp.Contains(a.SourcePath)
            : ctx.BackedUp.Any(p => IsAtOrBelow(p, a.SourcePath));
    }

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Whether <paramref name="path"/> is <paramref name="dir"/> or lies below it; case is ignored, both separators count.</summary>
    public static bool IsAtOrBelow(string path, string dir)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(dir)) return false;
        var p = Normalize(path);
        var d = Normalize(dir);
        if (string.Equals(p, d, StringComparison.OrdinalIgnoreCase)) return true;
        // A root such as C:\ or / ends with its separator already.
        if (d.EndsWith('/') || d.EndsWith('\\'))
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        return p.Length > d.Length && p.StartsWith(d, StringComparison.OrdinalIgnoreCase) && (p[d.Length] == '/' || p[d.Length] == '\\');
    }

    /// <summary>Joins a folder and relative parts given with <c>/</c>, with the separator of the folder.</summary>
    public static string Join(string folder, params string[] parts)
    {
        var result = folder;
        foreach (var part in parts)
        {
            foreach (var piece in part.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
                result = Path.Combine(result, piece);
        }
        return result;
    }
}
