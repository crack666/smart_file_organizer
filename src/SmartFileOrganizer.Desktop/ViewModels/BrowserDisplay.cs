using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>
/// Words and colours of the browser (tree, folder banner, file table;
/// https://github.com/crack666/smart_file_organizer/issues/14). The words of the codes come from
/// <see cref="CodeLabels"/>; one fixed colour per verdict and per retention value.
/// </summary>
public static class BrowserDisplay
{
    private const DisplayLanguage German = DisplayLanguage.German;

    public static string VerdictLabel(string? code) => CodeLabels.LabelOfCode<DirectoryVerdictKind>(code, German) ?? string.Empty;

    public static string VerdictColor(string? code) => code switch
    {
        "unit" => "#6D4FC2",
        "uniform" => "#1F7A6B",
        "mixed" => "#A35A00",
        "system" => "#54606B",
        "dump" => "#8A3B6A",
        _ => "#4A4F55"
    };

    public static string RetentionLabel(string? code) => CodeLabels.LabelOfCode<RetentionValue>(code, German) ?? string.Empty;

    public static string RetentionColor(string? code) => code switch
    {
        "keep" => "#2E7D32",
        "review" => "#8A6D00",
        "disposable" => "#B23B3B",
        _ => "#4A4F55"
    };

    /// <summary>Who decided a verdict, as a sentence part: <c>Regel</c>, <c>Modell</c>, <c>von Hand</c>.</summary>
    public static string SourceLabel(string? code) => code == VerdictSource.Human.ToCode()
        ? "von Hand"
        : CodeLabels.LabelOfCode<VerdictSource>(code, German) ?? string.Empty;

    /// <summary>What a verdict means for the files of the folder; the hint beside the choice.</summary>
    public static string VerdictEffect(DirectoryVerdictKind kind) => kind switch
    {
        DirectoryVerdictKind.Unit => "Der Ordner bleibt als Ganzes zusammen. Dateien darin, die noch kein Ergebnis haben, werden übersprungen.",
        DirectoryVerdictKind.Uniform => "Gleichartiger Inhalt: der nächste KI-Lauf beurteilt eine Stichprobe, der Rest übernimmt ihr Ergebnis.",
        DirectoryVerdictKind.Mixed => "Ohne gemeinsamen Zweck: der nächste KI-Lauf beurteilt jede Datei einzeln.",
        DirectoryVerdictKind.System => "Kein eigener Inhalt: Dateien darin, die noch kein Ergebnis haben, werden übersprungen.",
        DirectoryVerdictKind.Dump => "Abzug eines Geräts: die Ordner darin werden einzeln beurteilt.",
        _ => "Der Ordner enthält keine Dateien."
    };

    public static string StatusLabel(FileNodeStatus status) => status switch
    {
        FileNodeStatus.Discovered => "offen",
        FileNodeStatus.Classified => "vorbeurteilt",
        FileNodeStatus.AiAnalyzed => "beurteilt",
        FileNodeStatus.ReviewNeeded => "zu prüfen",
        FileNodeStatus.Approved => "bestätigt",
        FileNodeStatus.Skipped => "übersprungen",
        FileNodeStatus.Error => "Fehler",
        FileNodeStatus.Processing => "in Arbeit",
        FileNodeStatus.Deferred => "wartet auf Stichprobe",
        FileNodeStatus.Inherited => "abgeleitet",
        _ => status.ToString()
    };

    /// <summary>The markers of a file in words, <c>Finanzen, Ausweis</c>; empty when it has none.</summary>
    public static string MarkerText(SensitiveMarker markers) =>
        markers == SensitiveMarker.None ? string.Empty : CodeLabels.Markers(SensitiveMarkerNames.ToTokens(markers), German);

    public static string SkipText(string? code) => code switch
    {
        null => string.Empty,
        "backed_up" => "Nicht analysiert: liegt schon in einer Bibliothek.",
        _ => $"Nicht analysiert ({code})."
    };

    /// <summary>The line in place of the content of a file with access data, in table, detail and preview.</summary>
    public const string AccessDataHint = "Inhalt wird nicht angezeigt: Zugangsdaten.";

    public const string SensitiveColor = "#B3261E";
    public const string TemporaryColor = "#6B5B1E";
}
