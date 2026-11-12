namespace SmartFileOrganizer.Application.Sensitivity;

/// <summary>
/// Model text about a directory (verdict purpose and reason, Phase 3 summary, error texts) may name
/// files the model saw. When the directory holds marked files, such a text leaves the process only
/// if it names none of them; otherwise it is replaced by <see cref="NeutralHint"/>.
/// </summary>
/// <remarks>
/// This is a name check, not a content check: a model summary can still paraphrase a marked file
/// without naming it. The thorough solution is tracked in
/// https://github.com/crack666/smart_file_organizer/issues/33.
/// </remarks>
public static class DirectoryTextGuard
{
    /// <summary>Stems shorter than this are not matched on their own (too many false hits such as "a" or "db").</summary>
    public const int MinStemLength = 4;

    public const string NeutralHint = "Withheld: this text may name a sensitive file in this directory. Review locally.";

    /// <summary>
    /// Returns <paramref name="text"/> unchanged when it names none of <paramref name="markedNames"/>,
    /// otherwise <see cref="NeutralHint"/>. <paramref name="withheld"/> tells which case applied.
    /// </summary>
    public static string? Protect(
        string? text, IReadOnlyCollection<string> markedNames, SensitiveOutputOptions? options, out bool withheld)
    {
        withheld = false;
        if (string.IsNullOrEmpty(text) || markedNames.Count == 0 || options?.IncludeSensitive == true)
            return text;

        if (!NamesAny(text, markedNames))
            return text;

        withheld = true;
        return NeutralHint;
    }

    /// <summary>True when <paramref name="text"/> contains a file name or its stem (case-insensitive).</summary>
    public static bool NamesAny(string text, IEnumerable<string> fileNames)
    {
        foreach (var name in fileNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase)) return true;

            var stem = Path.GetFileNameWithoutExtension(name);
            if (stem.Length >= MinStemLength && text.Contains(stem, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
