namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// Result of comparing one source file with the archive (<see cref="ScanRole.Bestand"/>).
/// Stored as a fixed string (<see cref="ContentStateValues"/>) in <c>comparison_files.state</c>
/// and written the same way in JSON.
/// </summary>
public enum ContentState
{
    /// <summary><c>gesichert</c>: a file with exactly the same content (full checksum) is in the archive.</summary>
    Gesichert = 0,

    /// <summary><c>neu</c>: no file in the archive has this content, and none has the same name in a folder of the same name.</summary>
    Neu,

    /// <summary><c>abweichend</c>: not in the archive by content, but a file with the same name lies in a folder of the same name there.</summary>
    Abweichend,

    /// <summary><c>fehler</c>: the content could not be determined (read error, file gone, archive path unreachable). Never counts as <c>neu</c> or <c>gesichert</c>.</summary>
    Fehler
}

/// <summary>The stable strings for <see cref="ContentState"/> in the database and in JSON.</summary>
public static class ContentStateValues
{
    public const string Gesichert = "gesichert";
    public const string Neu = "neu";
    public const string Abweichend = "abweichend";
    public const string Fehler = "fehler";

    public static IReadOnlyList<string> All { get; } = [Gesichert, Neu, Abweichend, Fehler];

    public static string ToValue(this ContentState state) => state switch
    {
        ContentState.Gesichert => Gesichert,
        ContentState.Neu => Neu,
        ContentState.Abweichend => Abweichend,
        ContentState.Fehler => Fehler,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    public static ContentState Parse(string value) => value switch
    {
        Gesichert => ContentState.Gesichert,
        Neu => ContentState.Neu,
        Abweichend => ContentState.Abweichend,
        Fehler => ContentState.Fehler,
        _ => throw new ArgumentException($"Unknown content state '{value}'.", nameof(value))
    };
}
