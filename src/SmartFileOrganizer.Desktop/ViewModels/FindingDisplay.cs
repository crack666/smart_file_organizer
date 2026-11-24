using System.Globalization;
using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>
/// Words and colours of the findings view (https://github.com/crack666/smart_file_organizer/issues/27). The words of the
/// codes come from <see cref="CodeLabels"/> in the language chosen there; one fixed colour per kind and per state.
/// </summary>
public static class FindingDisplay
{
    public static string KindColor(FindingKind kind) => kind switch
    {
        FindingKind.NotBackedUp => "#B5480F",
        FindingKind.InNoise => "#7A5B12",
        FindingKind.Misplaced => "#2F6FD6",
        FindingKind.Sensitive => BrowserDisplay.SensitiveColor,
        FindingKind.Duplicate => "#54606B",
        _ => "#4A4F55"
    };

    public static string StateColor(FindingState state) => state switch
    {
        FindingState.Accepted => "#4CC35A",
        FindingState.Rejected => "#E5534B",
        FindingState.Wrong => "#B48CF2",
        _ => "#A0A7B0"
    };

    public static string CertaintyColor(ResultCertainty certainty) => certainty switch
    {
        ResultCertainty.Certain => "#4CC35A",
        ResultCertainty.Likely => "#8FBF6A",
        ResultCertainty.Uncertain => "#E3B341",
        _ => "#FF9E64"
    };

    /// <summary>"3 Bilder", "1 Dokument"; without a kind of file "3 Dateien".</summary>
    public static string Files(string? group, long n, DisplayLanguage language) => (language, group) switch
    {
        (DisplayLanguage.German, "documents") => Count(n, "Dokument", "Dokumente"),
        (DisplayLanguage.German, "images") => Count(n, "Bild", "Bilder"),
        (DisplayLanguage.German, "videos") => Count(n, "Video", "Videos"),
        (DisplayLanguage.German, "audio") => Count(n, "Tondatei", "Tondateien"),
        (DisplayLanguage.German, "archives") => Count(n, "Archiv", "Archive"),
        (DisplayLanguage.German, "installers") => Count(n, "Programm", "Programme"),
        (DisplayLanguage.German, _) => Count(n, "Datei", "Dateien"),
        (_, "documents") => Count(n, "document", "documents"),
        (_, "images") => Count(n, "image", "images"),
        (_, "videos") => Count(n, "video", "videos"),
        (_, "audio") => Count(n, "audio file", "audio files"),
        (_, "archives") => Count(n, "archive", "archives"),
        (_, "installers") => Count(n, "program", "programs"),
        _ => Count(n, "file", "files")
    };

    private static string Count(long n, string one, string many) =>
        n == 1 ? "1 " + one : n.ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + " " + many;

    /// <summary>"12.07.2015 – 19.07.2015", one day as one date; empty without a time.</summary>
    public static string Period(DateTime? from, DateTime? to)
    {
        if (from is not { } f) return string.Empty;
        var t = to ?? f;
        return f.Date == t.Date ? Date(f) : $"{Date(f)} – {Date(t)}";
    }

    public static string Date(DateTime d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    public static string Place(string? relativePath) =>
        relativePath is null or "" or "." ? "(Job-Ordner)" : relativePath.Replace('\\', '/');

    /// <summary>Why a placement was refused, in a few words with the code.</summary>
    public static string ProblemLabel(PlacementProblem problem) => problem switch
    {
        PlacementProblem.FindingNotFound => "das Fundstück gibt es nicht mehr, neu laden",
        PlacementProblem.NotPlaceable => "diese Art bekommt kein Ziel",
        PlacementProblem.PartNotFound => "diesen Teil gibt es nicht",
        PlacementProblem.LibraryNotFound => "der Job wurde mit keiner solchen Bibliothek verglichen",
        PlacementProblem.LibraryAmbiguous => "mehrere Bibliotheken, keine genannt",
        PlacementProblem.OutsideLibrary => "einen Ordner unter der Wurzel der Bibliothek wählen",
        PlacementProblem.TargetProtected => "Ziel liegt an einem geschützten Ort",
        PlacementProblem.InvalidPath => "kein gültiger Ordnername",
        PlacementProblem.FolderMissing => "kein Ordner gewählt",
        PlacementProblem.FolderNotFound => "den Ordner kennt der Scan der Bibliothek nicht",
        PlacementProblem.FolderExists => "den Ordner gibt es schon",
        PlacementProblem.ParentNotFound => "ein neuer Ordner geht nur direkt unter einen vorhandenen",
        PlacementProblem.ReasonMissing => "keine Begründung",
        _ => "nicht möglich"
    } + $" ({problem.ToCode()})";
}
