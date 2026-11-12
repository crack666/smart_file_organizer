namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// Result of comparing one source file with the library (<see cref="ScanRole.Library"/>).
/// Stored as a fixed English code (<see cref="ContentStateValues"/>) in <c>comparison_files.state</c>
/// and written the same way in JSON and on the command line.
/// </summary>
public enum ContentState
{
    /// <summary>
    /// <c>backed_up</c>: a file with exactly the same content is in the library, established by the full
    /// SHA-256 of both files. Deleting the source file may follow from this, so nothing weaker counts.
    /// </summary>
    BackedUp = 0,

    /// <summary><c>new</c>: no library file has this content, and none has the same name in a folder of the same name.</summary>
    New,

    /// <summary><c>differs</c>: not in the library by content, but a file with the same name lies in a folder of the same name there.</summary>
    Differs,

    /// <summary><c>error</c>: the content could not be determined (read error, file gone, library path unreachable). Never counts as <c>new</c> or <c>backed_up</c>.</summary>
    Error
}

/// <summary>The stable codes for <see cref="ContentState"/> in the database, JSON and CLI.</summary>
public static class ContentStateValues
{
    public const string BackedUp = "backed_up";
    public const string New = "new";
    public const string Differs = "differs";
    public const string Error = "error";

    public static IReadOnlyList<string> All { get; } = [BackedUp, New, Differs, Error];

    public static string ToValue(this ContentState state) => state switch
    {
        ContentState.BackedUp => BackedUp,
        ContentState.New => New,
        ContentState.Differs => Differs,
        ContentState.Error => Error,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    public static ContentState Parse(string value) => value switch
    {
        BackedUp => ContentState.BackedUp,
        New => ContentState.New,
        Differs => ContentState.Differs,
        Error => ContentState.Error,
        _ => throw new ArgumentException($"Unknown content state '{value}'.", nameof(value))
    };
}
