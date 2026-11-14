using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>
/// The one question the plan view asks per action (https://github.com/crack666/smart_file_organizer/issues/102): what
/// happens to the file. Each of the five kinds of the data belongs to one outcome, and a rejected action stays where it
/// is whatever its kind, so it shows as <see cref="Stay"/>.
/// </summary>
public enum PlanOutcome
{
    /// <summary><c>move</c> or <c>copy</c>: to a target in a library.</summary>
    Move,

    /// <summary><c>quarantine</c> or <c>delete_source</c>: the original goes into the hold folder (the "Papierkorb").</summary>
    Bin,

    /// <summary><c>leave</c>, or any rejected action: nothing happens to the file.</summary>
    Stay
}

/// <summary>
/// Words and colours of the plan view. One fixed colour per outcome (filled badge) and per state (text), chosen to stay
/// apart from each other on the dark theme; the codes of the CLI stand in the tooltips.
/// </summary>
public static class PlanDisplay
{
    // ------------------------------------------------------------------ outcome

    public static PlanOutcome OutcomeOf(PlanAction a) =>
        a.State == PlanActionState.Rejected ? PlanOutcome.Stay : OutcomeOf(a.Kind);

    public static PlanOutcome OutcomeOf(PlanActionKind kind) => kind switch
    {
        PlanActionKind.Move or PlanActionKind.Copy => PlanOutcome.Move,
        PlanActionKind.Quarantine or PlanActionKind.DeleteSource => PlanOutcome.Bin,
        _ => PlanOutcome.Stay
    };

    /// <summary>The word on a badge and in the summary.</summary>
    public static string OutcomeLabel(PlanOutcome outcome) => outcome switch
    {
        PlanOutcome.Move => "Umziehen",
        PlanOutcome.Bin => "Papierkorb",
        _ => "Hier lassen"
    };

    /// <summary>The word on the button that sets the outcome.</summary>
    public static string OutcomeButton(PlanOutcome outcome) => outcome switch
    {
        PlanOutcome.Move => "Umziehen",
        PlanOutcome.Bin => "In den Papierkorb",
        _ => "Hier lassen"
    };

    public static string OutcomeColor(PlanOutcome outcome) => outcome switch
    {
        PlanOutcome.Move => "#2F6FD6",
        PlanOutcome.Bin => "#C26A12",
        _ => "#5A626B"
    };

    /// <summary>Darker tile behind the counts of an outcome in the summary.</summary>
    public static string OutcomeTile(PlanOutcome outcome) => outcome switch
    {
        PlanOutcome.Move => "#1C3A6B",
        PlanOutcome.Bin => "#6B3A0E",
        _ => "#3A3F45"
    };

    /// <summary>What a button of an outcome does, for its tooltip.</summary>
    public static string OutcomeHint(PlanOutcome outcome) => outcome switch
    {
        PlanOutcome.Move => "In die Bibliothek: kopieren, prüfen, dann das Original in den Papierkorb. Ziel siehe Zeile.",
        PlanOutcome.Bin => $"Das Original kommt in den Papierkorb ({PlanExecutor.HoldFolderName} im Job-Ordner); endgültig gelöscht "
                           + "erst beim Leeren (sfo plan purge). Bis dahin legt „Zurücknehmen“ es zurück.",
        _ => "Die Datei bleibt, wo sie ist. Beim Ausführen geschieht nichts."
    };

    public const string HoldText = "Papierkorb (" + PlanExecutor.HoldFolderName + " im Job-Ordner)";

    /// <summary>
    /// What executing the action does, in the words of the view: where the file is afterwards and whether it can be
    /// taken back. <paramref name="shortTarget"/> turns a target into "library/rest".
    /// </summary>
    public static string Consequence(PlanAction a, bool keepSource, Func<string, string> shortTarget)
    {
        var target = a.TargetPath is { } t ? shortTarget(t) : "kein Ziel";
        switch (a.State)
        {
            case PlanActionState.Rejected:
                return "Abgelehnt" + (a.DecidedBy is { } by ? $" ({OriginLabel(by)})" : string.Empty)
                       + ": bleibt, wo es ist. Beim Ausführen geschieht nichts. Einen Ausgang wählen, um neu zu entscheiden.";
            case PlanActionState.Verified:
                return a.Kind == PlanActionKind.DeleteSource
                    ? $"Schritt 1 ist erledigt: die Datei in {target} ist mit dem Original verglichen. Das Original wartet auf Schritt 2 (Papierkorb)."
                    : $"Schritt 1 ist erledigt: die Kopie in {target} ist geprüft. Das Original wartet auf Schritt 2 (Papierkorb).";
            case PlanActionState.Executed:
                return a.Kind == PlanActionKind.Copy || keepSource
                    ? $"Ausgeführt: kopiert nach {target}; das Original ist an seinem Platz. „Zurücknehmen“ entfernt die Kopie."
                    : $"Ausgeführt: das Original liegt im {HoldText}" + (a.Kind == PlanActionKind.DeleteSource ? string.Empty : $", die geprüfte Kopie in {target}")
                      + ". „Zurücknehmen“ legt es zurück; endgültig gelöscht wird es erst beim Leeren (sfo plan purge).";
            case PlanActionState.Failed:
                return "Fehlgeschlagen: das Original ist an seinem Platz; die Notiz sagt, warum.";
            case PlanActionState.Undone:
                return "Zurückgenommen: das Original ist wieder an seinem Platz, die Kopie dieses Plans entfernt.";
        }

        string text;
        switch (a.Kind)
        {
            case PlanActionKind.Move when keepSource:
                text = $"Wird nach {target} kopiert und geprüft. „Quelle nicht anfassen“ ist an: das Original bleibt. "
                       + "„Zurücknehmen“ entfernt die Kopie wieder.";
                break;
            case PlanActionKind.Move:
                text = $"Wird nach {target} kopiert und geprüft (Schritt 1); danach kommt das Original in den {HoldText} (Schritt 2). "
                       + "„Zurücknehmen“ legt es zurück; endgültig gelöscht wird es erst beim Leeren (sfo plan purge).";
                break;
            case PlanActionKind.Copy:
                text = $"Wird nach {target} kopiert und geprüft; das Original bleibt, wo es ist. „Zurücknehmen“ entfernt die Kopie wieder.";
                break;
            case PlanActionKind.Quarantine when keepSource:
                text = $"Wird in den Quarantäneordner der Bibliothek kopiert ({target}). „Quelle nicht anfassen“ ist an: das Original bleibt.";
                break;
            case PlanActionKind.Quarantine:
                text = $"Eine geprüfte Kopie kommt in den Quarantäneordner der Bibliothek ({target}), das Original in den {HoldText}. "
                       + "„Zurücknehmen“ legt es zurück; endgültig gelöscht wird es erst beim Leeren (sfo plan purge).";
                break;
            case PlanActionKind.DeleteSource when keepSource:
                text = $"Die Bibliothek hat den Inhalt schon ({target}). „Quelle nicht anfassen“ ist an: es geschieht nichts.";
                break;
            case PlanActionKind.DeleteSource:
                text = $"Die Bibliothek hat den Inhalt schon ({target}); beide werden verglichen, dann kommt das Original in den {HoldText}. "
                       + "„Zurücknehmen“ legt es zurück; endgültig gelöscht wird es erst beim Leeren (sfo plan purge).";
                break;
            default:
                text = "Bleibt, wo es ist. Beim Ausführen geschieht nichts.";
                break;
        }
        if (a.Kind != PlanActionKind.Leave && a.State == PlanActionState.Proposed)
            text += " Noch nicht freigegeben: läuft erst nach der Freigabe.";
        return text;
    }

    // ------------------------------------------------------------------ kind (the finer choice of the detail)

    public static string KindLabel(PlanActionKind kind) => kind switch
    {
        PlanActionKind.Move => "Umziehen",
        PlanActionKind.Copy => "Kopieren (Original bleibt)",
        PlanActionKind.Quarantine => "Papierkorb, Kopie in Quarantäne",
        PlanActionKind.DeleteSource => "Papierkorb, Bibliothek hat es",
        _ => "Hier lassen"
    };

    public static string KindColor(PlanActionKind kind) => OutcomeColor(OutcomeOf(kind));

    // ------------------------------------------------------------------ state

    public static string StateLabel(PlanActionState state) => state switch
    {
        PlanActionState.Proposed => "Offen",
        PlanActionState.Approved => "Freigegeben",
        PlanActionState.Rejected => "Abgelehnt",
        PlanActionState.Verified => "Kopiert, geprüft",
        PlanActionState.Executed => "Ausgeführt",
        PlanActionState.Failed => "Fehlgeschlagen",
        _ => "Zurückgenommen"
    };

    public static string StateColor(PlanActionState state) => state switch
    {
        PlanActionState.Proposed => "#A0A7B0",
        PlanActionState.Approved => "#4CC35A",
        PlanActionState.Rejected => "#E5534B",
        PlanActionState.Verified => "#58A6FF",
        PlanActionState.Executed => "#B48CF2",
        PlanActionState.Failed => "#FF7B72",
        _ => "#8B949E"
    };

    public static string OriginLabel(PlanActionOrigin? origin) => origin switch
    {
        PlanActionOrigin.Rule => "Regel",
        PlanActionOrigin.Model => "Modell",
        PlanActionOrigin.Agent => "Agent",
        PlanActionOrigin.Human => "Mensch",
        _ => "–"
    };

    /// <summary>A problem of the plan's checks in a few words, with its code.</summary>
    public static string ProblemLabel(PlanProblem problem) => problem switch
    {
        PlanProblem.NoTarget => "kein Ziel",
        PlanProblem.TargetOutsideLibrary => "Ziel liegt in keiner Bibliothek des Plans",
        PlanProblem.TargetInSource => "Ziel liegt in der Quelle",
        PlanProblem.TargetProtected => "Ziel liegt an einem geschützten Ort",
        PlanProblem.SourceInLibrary => "Quelle liegt in einer Bibliothek",
        PlanProblem.SourceInManagedArea => "Quelle liegt in einem verwalteten Bereich",
        PlanProblem.AlreadyInLibrary => "die Bibliothek hat den Inhalt schon",
        PlanProblem.SensitiveKind => "sensible Datei: nicht in Quarantäne",
        PlanProblem.NameCollision => "am Ziel liegt schon etwas",
        _ => problem.ToCode()
    } + $" ({problem.ToCode()})";

    /// <summary>Colour of the badge that marks a decision of a steering agent.</summary>
    public const string AgentColor = "#8957E5";

    public const string ProblemColor = "#FF7B72";

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    public static string Files(long files) => files == 1 ? "1 Datei" : $"{files} Dateien";

    /// <summary>The kinds the finer switch offers, in the order of the CLI.</summary>
    public static IReadOnlyList<PlanActionKind> Kinds { get; } = Enum.GetValues<PlanActionKind>();

    public static IReadOnlyList<PlanOutcome> Outcomes { get; } = Enum.GetValues<PlanOutcome>();
}
