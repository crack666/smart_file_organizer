using System.Text.Json;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Evaluation;

/// <summary>
/// Reads and writes the codes an evaluation compares. Every comparison goes through the enums;
/// the codes come from the central places (<see cref="VerdictCodes"/>, <see cref="SensitiveMarkerNames"/>)
/// or, for categories, from the enum names in the same <c>snake_case</c> form the CLI JSON uses.
/// </summary>
/// <remarks>
/// Truth files are written with the English codes of
/// https://github.com/crack666/smart_file_organizer/issues/35. Until that issue switches the
/// central codes, they are still German; <see cref="EnglishByDeclarationOrder"/> bridges the gap.
/// The central code always wins, so once issue 35 lands the bridge is never consulted and can be
/// deleted. Output always uses the central code, so expected and actual values read alike.
/// </remarks>
public static class EvalCodes
{
    /// <summary>
    /// English codes from the table in issue 35, listed in the declaration order of the enum they
    /// belong to (which is the order of that table). Matched by position, so the bridge holds no
    /// German strings and survives renamed members.
    /// </summary>
    private static readonly Dictionary<Type, string[]> EnglishByDeclarationOrder = new()
    {
        [typeof(DirectoryVerdictKind)] = ["unit", "uniform", "mixed", "empty", "system"],
        [typeof(RetentionValue)] = ["keep", "review", "disposable"],
        [typeof(SensitiveMarker)] = ["credentials", "keys", "private_message", "identity", "finance", "health"]
    };

    // ── Directory verdict ────────────────────────────────────────────────

    public static bool TryParseVerdict(string? code, out DirectoryVerdictKind verdict)
    {
        if (VerdictCodes.TryParse(code, out verdict))
            return true;
        return TryBridge(code, out verdict);
    }

    public static string Verdict(DirectoryVerdictKind verdict) => verdict.ToCode();

    // ── Sensitive markers ────────────────────────────────────────────────

    public static bool TryParseMarker(string? code, out SensitiveMarker marker)
    {
        if (SensitiveMarkerNames.TryParseToken(code, out marker))
            return true;
        var flags = Enum.GetValues<SensitiveMarker>().Where(m => m != SensitiveMarker.None).ToArray();
        var index = Array.FindIndex(EnglishByDeclarationOrder[typeof(SensitiveMarker)],
            c => string.Equals(c, code?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index >= flags.Length)
            return false;
        marker = flags[index];
        return true;
    }

    /// <summary>The marker codes in enum order; empty for none.</summary>
    public static IReadOnlyList<string> Markers(SensitiveMarker markers) => SensitiveMarkerNames.ToTokens(markers);

    /// <summary>Comma-separated codes, or <c>none</c>.</summary>
    public static string MarkerList(SensitiveMarker markers) =>
        markers == SensitiveMarker.None ? "none" : string.Join(',', Markers(markers));

    // ── File category ────────────────────────────────────────────────────

    private static readonly Dictionary<string, FileCategory> CategoryByCode =
        Enum.GetValues<FileCategory>().ToDictionary(Category, c => c, StringComparer.OrdinalIgnoreCase);

    /// <summary>The category code, for example <c>document_scan</c>: the enum name in snake_case, as in all CLI JSON.</summary>
    public static string Category(FileCategory category) => JsonNamingPolicy.SnakeCaseLower.ConvertName(category.ToString());

    public static bool TryParseCategory(string? code, out FileCategory category)
    {
        category = FileCategory.Unknown;
        return code is not null && CategoryByCode.TryGetValue(code.Trim(), out category);
    }

    // ── Bridge ───────────────────────────────────────────────────────────

    private static bool TryBridge<TEnum>(string? code, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(code) || !EnglishByDeclarationOrder.TryGetValue(typeof(TEnum), out var english))
            return false;
        var values = Enum.GetValues<TEnum>();
        var index = Array.FindIndex(english, c => string.Equals(c, code.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index >= values.Length)
            return false;
        value = values[index];
        return true;
    }

    /// <summary>For tests: the bridge lists must have exactly one code per enum member.</summary>
    public static IEnumerable<(Type Enum, int Codes, int Members)> BridgeShape() =>
        EnglishByDeclarationOrder.Select(kv => (kv.Key, kv.Value.Length,
            Enum.GetValues(kv.Key).Cast<object>().Count(v => Convert.ToInt64(v) != 0 || kv.Key != typeof(SensitiveMarker))));
}
