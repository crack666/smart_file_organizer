using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>
/// Words and colours of the plan view. One fixed colour per kind (filled badge) and per state (text), chosen to stay
/// apart from each other on the dark theme; the codes of the CLI stand in the tooltips.
/// </summary>
public static class PlanDisplay
{
    public static string KindLabel(PlanActionKind kind) => kind switch
    {
        PlanActionKind.Move => "Umziehen",
        PlanActionKind.Copy => "Kopieren",
        PlanActionKind.Quarantine => "Quarantäne",
        PlanActionKind.DeleteSource => "Löschen",
        _ => "Belassen"
    };

    public static string KindColor(PlanActionKind kind) => kind switch
    {
        PlanActionKind.Move => "#2F6FD6",
        PlanActionKind.Copy => "#17877F",
        PlanActionKind.Quarantine => "#C26A12",
        PlanActionKind.DeleteSource => "#C0392B",
        _ => "#5A626B"
    };

    public static string StateLabel(PlanActionState state) => state switch
    {
        PlanActionState.Proposed => "Vorgeschlagen",
        PlanActionState.Approved => "Bestätigt",
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

    /// <summary>The kinds the switch offers, in the order of the CLI.</summary>
    public static IReadOnlyList<PlanActionKind> Kinds { get; } = Enum.GetValues<PlanActionKind>();
}
