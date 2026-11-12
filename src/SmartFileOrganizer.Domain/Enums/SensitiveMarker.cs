namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// Why a file is sensitive. A file can carry several markers.
/// Only the local model may read the content of a marked file; everything that leaves the
/// machine sees path, type, marker and a neutral summary (see
/// <c>SmartFileOrganizer.Application.Sensitivity.SensitiveOutputGuard</c>).
/// </summary>
/// <remarks>
/// The member names are C# names. Database and JSON use the fixed German tokens from
/// <see cref="SensitiveMarkerNames"/> (<c>zugangsdaten</c>, <c>schluessel</c>, ...), never
/// the numeric value and never <c>ToString()</c>.
/// </remarks>
[Flags]
public enum SensitiveMarker
{
    None = 0,

    /// <summary><c>zugangsdaten</c>: passwords, password stores, browser logins, .env files.</summary>
    Zugangsdaten = 1 << 0,

    /// <summary><c>schluessel</c>: private keys, certificates with key, crypto wallets.</summary>
    Schluessel = 1 << 1,

    /// <summary><c>private_nachricht</c>: mail archives, chat databases, private letters.</summary>
    PrivateNachricht = 1 << 2,

    /// <summary><c>ausweis</c>: identity cards, passports, driving licences.</summary>
    Ausweis = 1 << 3,

    /// <summary><c>finanzen</c>: bank statements, tax returns, payslips, wallets.</summary>
    Finanzen = 1 << 4,

    /// <summary><c>gesundheit</c>: medical findings, doctor letters, health insurance.</summary>
    Gesundheit = 1 << 5
}

/// <summary>
/// Fixed string tokens of <see cref="SensitiveMarker"/> for database columns and JSON.
/// A stored value is the comma-separated list of tokens in enum order, for example
/// <c>zugangsdaten,schluessel</c>; no marker is stored as <c>NULL</c>.
/// </summary>
public static class SensitiveMarkerNames
{
    public const string Zugangsdaten = "zugangsdaten";
    public const string Schluessel = "schluessel";
    public const string PrivateNachricht = "private_nachricht";
    public const string Ausweis = "ausweis";
    public const string Finanzen = "finanzen";
    public const string Gesundheit = "gesundheit";

    private static readonly (SensitiveMarker Marker, string Token)[] Map =
    [
        (SensitiveMarker.Zugangsdaten, Zugangsdaten),
        (SensitiveMarker.Schluessel, Schluessel),
        (SensitiveMarker.PrivateNachricht, PrivateNachricht),
        (SensitiveMarker.Ausweis, Ausweis),
        (SensitiveMarker.Finanzen, Finanzen),
        (SensitiveMarker.Gesundheit, Gesundheit)
    ];

    /// <summary>All tokens in enum order. Used for the model schema.</summary>
    public static IReadOnlyList<string> AllTokens { get; } = Map.Select(m => m.Token).ToArray();

    /// <summary>The tokens of every flag set in <paramref name="markers"/>, in enum order.</summary>
    public static IReadOnlyList<string> ToTokens(SensitiveMarker markers)
        => Map.Where(m => (markers & m.Marker) != 0).Select(m => m.Token).ToArray();

    /// <summary>Database form: comma-separated tokens, or <c>null</c> for no marker.</summary>
    public static string? Format(SensitiveMarker markers)
    {
        var tokens = ToTokens(markers);
        return tokens.Count == 0 ? null : string.Join(',', tokens);
    }

    /// <summary>Parses one token; case and surrounding whitespace are ignored, a hyphen counts as underscore.</summary>
    public static bool TryParseToken(string? token, out SensitiveMarker marker)
    {
        marker = SensitiveMarker.None;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var normalized = token.Trim().Replace('-', '_');
        foreach (var (m, t) in Map)
        {
            if (string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase))
            {
                marker = m;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Parses a stored or model-supplied list (comma, semicolon or whitespace separated).
    /// Unknown tokens are ignored rather than rejected: a model that invents a marker must
    /// not lose the ones it got right.
    /// </summary>
    public static SensitiveMarker Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return SensitiveMarker.None;

        var result = SensitiveMarker.None;
        foreach (var token in value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseToken(token, out var m)) result |= m;
        }
        return result;
    }
}
