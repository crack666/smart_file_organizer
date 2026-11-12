namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// What a scan stands for. Stored as a fixed English code (<see cref="ScanRoleValues"/>) in
/// <c>scan_jobs.role</c> and written the same way in JSON and on the command line.
/// </summary>
public enum ScanRole
{
    /// <summary><c>source</c>: a medium to be cleaned up (the default).</summary>
    Source = 0,

    /// <summary><c>library</c>: the user's organised archive. Only read, never changed, never analysed by a model.</summary>
    Library
}

/// <summary>The stable codes for <see cref="ScanRole"/> in the database, JSON and CLI.</summary>
public static class ScanRoleValues
{
    public const string Source = "source";
    public const string Library = "library";

    public static IReadOnlyList<string> All { get; } = [Source, Library];

    public static string ToValue(this ScanRole role) => role switch
    {
        ScanRole.Source => Source,
        ScanRole.Library => Library,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
    };

    public static ScanRole Parse(string value) => value switch
    {
        Source => ScanRole.Source,
        Library => ScanRole.Library,
        _ => throw new ArgumentException($"Unknown scan role '{value}'.", nameof(value))
    };
}
