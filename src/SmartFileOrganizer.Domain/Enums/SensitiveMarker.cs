namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// Why a file is sensitive. A file can carry several markers.
/// Only the local model may read the content of a marked file; everything that leaves the
/// machine sees path, type, marker and a neutral summary (see
/// <c>SmartFileOrganizer.Application.Sensitivity.SensitiveOutputGuard</c>).
/// </summary>
/// <remarks>
/// The member names are C# names. Database and JSON use the fixed codes from
/// <see cref="SensitiveMarkerNames"/> (<c>credentials</c>, <c>keys</c>, ...), never
/// the numeric value and never <c>ToString()</c>. Words for people come from <see cref="CodeLabels"/>.
/// </remarks>
[Flags]
public enum SensitiveMarker
{
    None = 0,

    /// <summary><c>credentials</c>: passwords, password stores, browser logins, .env files.</summary>
    Credentials = 1 << 0,

    /// <summary><c>keys</c>: private keys, certificates with key, crypto wallets.</summary>
    Keys = 1 << 1,

    /// <summary><c>private_message</c>: mail archives, chat databases and exports, personal letters; not a formal
    /// letter to a company or an authority (docs/glossary.md draws the line).</summary>
    PrivateMessage = 1 << 2,

    /// <summary><c>identity</c>: identity cards, passports, driving licences.</summary>
    Identity = 1 << 3,

    /// <summary><c>finance</c>: bank statements, tax returns, payslips, depot and loan papers, wallets; not an
    /// ordinary invoice or a rental contract (docs/glossary.md draws the line).</summary>
    Finance = 1 << 4,

    /// <summary><c>health</c>: medical findings, doctor letters, health insurance.</summary>
    Health = 1 << 5
}

/// <summary>
/// Fixed string codes of <see cref="SensitiveMarker"/> for database columns and JSON.
/// A stored value is the comma-separated list of codes in enum order, for example
/// <c>credentials,keys</c>; no marker is stored as <c>NULL</c>.
/// </summary>
public static class SensitiveMarkerNames
{
    public const string Credentials = "credentials";
    public const string Keys = "keys";
    public const string PrivateMessage = "private_message";
    public const string Identity = "identity";
    public const string Finance = "finance";
    public const string Health = "health";

    private static readonly (SensitiveMarker Marker, string Token)[] Map =
    [
        (SensitiveMarker.Credentials, Credentials),
        (SensitiveMarker.Keys, Keys),
        (SensitiveMarker.PrivateMessage, PrivateMessage),
        (SensitiveMarker.Identity, Identity),
        (SensitiveMarker.Finance, Finance),
        (SensitiveMarker.Health, Health)
    ];

    /// <summary>All codes in enum order. Used for the model schema.</summary>
    public static IReadOnlyList<string> AllTokens { get; } = Map.Select(m => m.Token).ToArray();

    /// <summary>The codes of every flag set in <paramref name="markers"/>, in enum order.</summary>
    public static IReadOnlyList<string> ToTokens(SensitiveMarker markers)
        => Map.Where(m => (markers & m.Marker) != 0).Select(m => m.Token).ToArray();

    /// <summary>Database form: comma-separated codes, or <c>null</c> for no marker.</summary>
    public static string? Format(SensitiveMarker markers)
    {
        var tokens = ToTokens(markers);
        return tokens.Count == 0 ? null : string.Join(',', tokens);
    }

    /// <summary>Parses one code; case and surrounding whitespace are ignored, a hyphen counts as underscore.</summary>
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
    /// Parses a stored list (comma, semicolon or whitespace separated).
    /// Unknown tokens are ignored rather than rejected: a model that invents a marker must
    /// not lose the ones it got right.
    /// </summary>
    public static SensitiveMarker Parse(string? value) => Parse(value, acceptWords: false);

    /// <summary>
    /// Parses a list from a model answer: the codes, and also the German word for a marker as
    /// <see cref="CodeLabels"/> shows it (a model does not always keep to the codes it was given).
    /// Only the marker is kept, so whatever the model wrote is stored as the code.
    /// </summary>
    public static SensitiveMarker ParseAnswer(string? value) => Parse(value, acceptWords: true);

    private static SensitiveMarker Parse(string? value, bool acceptWords)
    {
        if (string.IsNullOrWhiteSpace(value)) return SensitiveMarker.None;

        var result = SensitiveMarker.None;
        foreach (var token in value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseToken(token, out var m) || (acceptWords && CodeLabels.TryParseWord(token, out m)))
                result |= m;
        }
        return result;
    }
}
