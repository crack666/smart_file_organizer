using System.Text.Json;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Evaluation;

/// <summary>
/// Reads and writes the codes an evaluation compares. Every comparison goes through the enums;
/// the codes come from the central places (<see cref="VerdictCodes"/>, <see cref="SensitiveMarkerNames"/>)
/// or, for categories, from the enum names in the same <c>snake_case</c> form the CLI JSON uses.
/// Truth files use the same codes as the database and the CLI JSON (see <c>docs/glossary.md</c>).
/// </summary>
public static class EvalCodes
{
    // ── Directory verdict ────────────────────────────────────────────────

    public static bool TryParseVerdict(string? code, out DirectoryVerdictKind verdict) =>
        VerdictCodes.TryParse(code, out verdict);

    public static string Verdict(DirectoryVerdictKind verdict) => verdict.ToCode();

    // ── Sensitive markers ────────────────────────────────────────────────

    public static bool TryParseMarker(string? code, out SensitiveMarker marker) =>
        SensitiveMarkerNames.TryParseToken(code, out marker);

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
}
