using System.Text.Json;

namespace SmartFileOrganizer.Application.Sensitivity;

/// <summary>
/// Keeps a model-written directory summary from carrying content of a file that is marked now but was
/// not when the summary was written (rules added later, header check caught up, older version).
/// </summary>
/// <remarks>
/// <para>Phase 3 gives a marked file to the model as its neutral line only and records the names of those
/// files with the summary (<c>ai_directory_results.marked_inputs</c>). A summary is current when every file
/// marked now in its directory is in that list. A stale summary is withheld by the read commands and written
/// again by the next analysis, so the protection works at the source instead of guessing from the text.</para>
/// <para>A summary written without a model (<c>model_used</c> empty) is built from the verdict, whose model
/// sees names and the profile only, so it is always current.</para>
/// </remarks>
public static class DirectorySummaryGuard
{
    /// <summary>Shown instead of a stale summary.</summary>
    public const string StaleHint =
        "(withheld: a file in this directory was marked after this summary was written; the next analysis writes it again)";

    /// <summary>Stored form of the marked inputs: sorted JSON list of file names, <c>[]</c> when none.</summary>
    public static string FormatInputs(IEnumerable<string> markedNames) =>
        JsonSerializer.Serialize(markedNames.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList());

    /// <summary>
    /// True when the summary may be shown: written without a model, or every file marked now
    /// (<paramref name="markedNow"/>, direct files of the directory) entered it as its neutral line.
    /// </summary>
    public static bool IsCurrent(string? modelUsed, string? markedInputs, IReadOnlyCollection<string> markedNow)
    {
        if (string.IsNullOrEmpty(modelUsed) || markedNow.Count == 0) return true;
        var inputs = ParseInputs(markedInputs);
        return inputs is not null && markedNow.All(inputs.Contains);
    }

    /// <summary>Names from <see cref="FormatInputs"/>; null when nothing was recorded or the value is unreadable.</summary>
    private static HashSet<string>? ParseInputs(string? markedInputs)
    {
        if (string.IsNullOrWhiteSpace(markedInputs)) return null;
        try
        {
            var names = JsonSerializer.Deserialize<List<string>>(markedInputs);
            return names is null ? null : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
