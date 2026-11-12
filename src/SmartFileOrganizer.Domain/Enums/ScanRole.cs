namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// What a scan stands for. Stored as a fixed string (<see cref="ScanRoleValues"/>) in
/// <c>scan_jobs.role</c> and written the same way in JSON.
/// </summary>
public enum ScanRole
{
    /// <summary><c>quelle</c>: a medium to be cleaned up (the default).</summary>
    Quelle = 0,

    /// <summary><c>bestand</c>: the user's organised archive. Only read, never changed, never analysed by a model.</summary>
    Bestand
}

/// <summary>The stable strings for <see cref="ScanRole"/> in the database and in JSON.</summary>
public static class ScanRoleValues
{
    public const string Quelle = "quelle";
    public const string Bestand = "bestand";

    public static IReadOnlyList<string> All { get; } = [Quelle, Bestand];

    public static string ToValue(this ScanRole role) => role switch
    {
        ScanRole.Quelle => Quelle,
        ScanRole.Bestand => Bestand,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
    };

    public static ScanRole Parse(string value) => value switch
    {
        Quelle => ScanRole.Quelle,
        Bestand => ScanRole.Bestand,
        _ => throw new ArgumentException($"Unknown scan role '{value}'.", nameof(value))
    };
}
