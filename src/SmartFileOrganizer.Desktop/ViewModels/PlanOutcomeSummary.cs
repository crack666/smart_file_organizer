using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>
/// The summary before a step that changes files (https://github.com/crack666/smart_file_organizer/issues/102), in the
/// words of the outcomes: how many move where, how many go into the hold folder ("Papierkorb", with its place and how it
/// is emptied), how many stay and why. Which actions run is the executor's answer to a dry run; this only counts it.
/// </summary>
public static class PlanOutcomeSummary
{
    /// <summary>Destination folders listed by name before the rest is counted.</summary>
    public const int MaxFolders = 6;

    /// <summary>Lines before step 1. <paramref name="willRun"/> are the actions the dry run of step 1 would run.</summary>
    public static IReadOnlyList<string> BeforeApply(
        IReadOnlyList<PlanAction> all, IReadOnlySet<long> willRun, IReadOnlyCollection<string> libraryRoots, string jobRoot, bool keepSource)
    {
        var run = all.Where(a => willRun.Contains(a.Id)).ToList();
        var lines = new List<string>();

        var moves = run.Where(a => PlanDisplay.OutcomeOf(a.Kind) == PlanOutcome.Move).ToList();
        if (moves.Count > 0)
        {
            var copies = moves.Count(a => a.Kind == PlanActionKind.Copy);
            var after = keepSource || copies == moves.Count
                ? "die Originale bleiben"
                : "die Originale kommen in Schritt 2 in den Papierkorb" + (copies > 0 ? $" (außer {copies} Kopien)" : string.Empty);
            lines.Add($"Umziehen: {Count(moves)}, kopiert und geprüft, {after}. Nach:");
            lines.AddRange(Destinations(moves, libraryRoots));
        }

        var bin = run.Where(a => PlanDisplay.OutcomeOf(a.Kind) == PlanOutcome.Bin).ToList();
        if (bin.Count > 0)
        {
            var quarantine = bin.Count(a => a.Kind == PlanActionKind.Quarantine);
            var discard = bin.Where(a => a.Kind == PlanActionKind.Discard).ToList();
            lines.Add(keepSource
                ? $"Papierkorb: {Count(bin)}; „Quelle nicht anfassen“ ist an, die Originale bleiben"
                  + (quarantine > 0 ? $" ({quarantine} bekommen eine Kopie im Quarantäneordner der Bibliothek)." : ".")
                : $"In den Papierkorb: {Count(bin)}. Die Originale kommen in Schritt 2 nach {HoldOf(jobRoot)}; "
                  + "endgültig gelöscht erst beim Leeren („Papierkorb leeren“, sfo plan purge), bis dahin legt „Zurücknehmen“ sie zurück."
                  + (discard.Count > 0 ? $" {Count(discard)} davon ohne Kopie: Schritt 1 prüft sie nur, nichts wird in eine Bibliothek geschrieben." : string.Empty)
                  + (quarantine > 0 ? $" {quarantine} davon bekommen vorher eine geprüfte Kopie im Quarantäneordner der Bibliothek." : string.Empty));
        }

        var stay = all.Where(a => !willRun.Contains(a.Id) && a.State is PlanActionState.Proposed or PlanActionState.Approved or PlanActionState.Rejected).ToList();
        if (stay.Count > 0)
        {
            var parts = new List<string>();
            Add(parts, stay.Count(a => a.Kind == PlanActionKind.Leave && a.State != PlanActionState.Rejected), "hier lassen");
            Add(parts, stay.Count(a => a.State == PlanActionState.Rejected), "abgelehnt");
            Add(parts, stay.Count(a => a.Kind != PlanActionKind.Leave && a.State == PlanActionState.Proposed), "nicht freigegeben");
            Add(parts, stay.Count(a => a.Kind != PlanActionKind.Leave && a.State == PlanActionState.Approved), "übersprungen, siehe Liste");
            lines.Add($"Bleiben, wo sie sind: {Count(stay)} ({string.Join(", ", parts)}).");
        }

        var done = all.Count(a => a.State is PlanActionState.Verified or PlanActionState.Executed or PlanActionState.Failed or PlanActionState.Undone);
        if (done > 0)
            lines.Add($"Schon ausgeführt, fehlgeschlagen oder zurückgenommen: {done} (laufen nicht noch einmal).");
        return lines;
    }

    /// <summary>Lines before step 2. <paramref name="willRun"/> are the actions whose originals the dry run of step 2 would take away.</summary>
    public static IReadOnlyList<string> BeforeRemove(IReadOnlyList<PlanAction> all, IReadOnlySet<long> willRun, string jobRoot)
    {
        var run = all.Where(a => willRun.Contains(a.Id)).ToList();
        var moved = run.Count(a => PlanDisplay.OutcomeOf(a.Kind) == PlanOutcome.Move);
        var discard = run.Count(a => a.Kind == PlanActionKind.Discard);
        var bin = run.Count - moved;
        var lines = new List<string>
        {
            $"In den Papierkorb ({HoldOf(jobRoot)}): die Originale von {Count(run)}"
            + $" – {moved} umgezogen (Kopie in der Bibliothek geprüft), {bin} „In den Papierkorb“"
            + (discard > 0 ? $", davon {discard} ohne Kopie." : "."),
            "Unmittelbar vorher wird jedes Original noch einmal mit seiner Kopie verglichen"
            + (discard > 0 ? "; was ohne Kopie in den Papierkorb kommt, muss so sein, wie der Scan es sah." : "."),
            "Endgültig gelöscht wird nichts: „Zurücknehmen“ legt die Originale zurück; geleert wird der Papierkorb nur mit „Papierkorb leeren“.",
        };
        return lines;
    }

    /// <summary>
    /// Lines before emptying the bin (<c>sfo plan purge</c>). <paramref name="willPurge"/> are the items of the purge's dry
    /// run that would run; <paramref name="held"/> is what lies in the bin now.
    /// </summary>
    public static IReadOnlyList<string> BeforePurge(IReadOnlyList<PlanRunItem> willPurge, PlanHoldSummary held, string jobRoot)
    {
        var files = willPurge.Sum(i => i.Files);
        var bytes = willPurge.Sum(i => i.Bytes);
        var lines = new List<string>
        {
            $"Endgültig gelöscht werden {PlanDisplay.Files(files)} ({PlanDisplay.Size(bytes)}) aus {HoldOf(jobRoot)}. Das lässt sich nicht zurücknehmen.",
        };
        if (held.DiscardedFiles > 0)
            lines.Add($"{PlanDisplay.Files(held.DiscardedFiles)} ({PlanDisplay.Size(held.DiscardedBytes)}) davon kamen ohne Kopie in den Papierkorb: "
                      + "sie sind danach nirgends mehr.");
        if (held.Files > held.DiscardedFiles)
            lines.Add("Bei Umgezogenem bleibt die geprüfte Kopie in der Bibliothek; ein Original, dessen Kopie sich seitdem verändert hat, "
                      + "bleibt im Papierkorb.");
        lines.Add($"Frei wird: {PlanDisplay.Size(bytes)} auf dem Laufwerk der Quelle"
                  + (held.Files > held.DiscardedFiles ? " (weniger, wo ein Original bleiben muss)." : "."));
        return lines;
    }

    /// <summary>The hold folder of the job as the person sees it: job folder name and folder name.</summary>
    public static string HoldOf(string jobRoot) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(jobRoot)) is { Length: > 0 } name
            ? $"{name}/{PlanExecutor.HoldFolderName}"
            : PlanExecutor.HoldFolderName;

    private static IEnumerable<string> Destinations(IReadOnlyList<PlanAction> moves, IReadOnlyCollection<string> roots)
    {
        var folders = moves
            .GroupBy(a => a.TargetPath is { } t && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(t)) is { } dir
                ? PlanActionRowViewModel.ShortTarget(dir, roots)
                : "kein Ziel", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var g in folders.Take(MaxFolders))
            yield return $"   → {g.Key}: {Count(g.ToList())}";
        if (folders.Count > MaxFolders)
            yield return $"   … und {folders.Count - MaxFolders} weitere Ordner ({Count(folders.Skip(MaxFolders).SelectMany(g => g).ToList())})";
    }

    private static string Count(IReadOnlyCollection<PlanAction> actions) =>
        $"{(actions.Count == 1 ? "1 Aktion" : $"{actions.Count} Aktionen")} ({PlanDisplay.Files(actions.Sum(a => a.Files))}, {PlanDisplay.Size(actions.Sum(a => a.Bytes))})";

    private static void Add(List<string> parts, int n, string what)
    {
        if (n > 0) parts.Add($"{n} {what}");
    }
}
