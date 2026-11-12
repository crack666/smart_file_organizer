using System.Text;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>Language of the words shown to people. Codes in the database and in JSON never change with it.</summary>
public enum DisplayLanguage
{
    English,
    German
}

/// <summary>
/// The one place that turns fixed codes into words for people, in English and German.
/// GUI and the text output of the CLI show these words; the database, JSON and the model
/// prompts carry the codes (<see cref="VerdictCodes"/>, <see cref="SensitiveMarkerNames"/>,
/// <see cref="ScanRoleValues"/>, <see cref="ContentStateValues"/>). Meanings are in <c>docs/glossary.md</c>.
/// </summary>
/// <remarks>
/// The German words are also what a model sometimes answers with instead of a code, because
/// they were the codes before https://github.com/crack666/smart_file_organizer/issues/35.
/// <see cref="TryParseWord{TEnum}"/> accepts them for model answers; nothing stores them.
/// </remarks>
public static class CodeLabels
{
    private sealed record Entry(Type Enum, object Value, string Code, string English, string German);

    private static readonly Entry[] Entries =
    [
        Verdict(DirectoryVerdictKind.Unit, "unit", "Einheit"),
        Verdict(DirectoryVerdictKind.Uniform, "uniform", "einheitlich"),
        Verdict(DirectoryVerdictKind.Mixed, "mixed", "gemischt"),
        Verdict(DirectoryVerdictKind.Empty, "empty", "leer"),
        Verdict(DirectoryVerdictKind.System, "system", "System"),

        Verdict(RetentionValue.Keep, "keep", "behalten"),
        Verdict(RetentionValue.Review, "review", "prüfen"),
        Verdict(RetentionValue.Disposable, "disposable", "entbehrlich"),

        Verdict(VerdictSource.Rule, "rule", "Regel"),
        Verdict(VerdictSource.Model, "model", "Modell"),

        Verdict(ResultOrigin.Rule, "rule", "Regel"),
        Verdict(ResultOrigin.Model, "model", "Modell"),
        Verdict(ResultOrigin.Inherited, "inherited", "abgeleitet"),
        Verdict(ResultOrigin.Human, "human", "Mensch"),
        Verdict(ResultOrigin.Agent, "agent", "Agent"),
        Verdict(ResultOrigin.External, "external", "extern"),

        Verdict(KeepHint.Temporary, "temporary", "vorübergehend"),
        Verdict(KeepHint.WorthKeeping, "worth keeping", "aufhebenswert"),

        Marker(SensitiveMarker.Credentials, "credentials", "Zugangsdaten"),
        Marker(SensitiveMarker.Keys, "keys", "Schlüssel"),
        Marker(SensitiveMarker.PrivateMessage, "private message", "private Nachricht"),
        Marker(SensitiveMarker.Identity, "identity", "Ausweis"),
        Marker(SensitiveMarker.Finance, "finance", "Finanzen"),
        Marker(SensitiveMarker.Health, "health", "Gesundheit"),

        new(typeof(ScanRole), ScanRole.Source, ScanRoleValues.Source, "source", "Quelle"),
        new(typeof(ScanRole), ScanRole.Library, ScanRoleValues.Library, "library", "Bibliothek"),

        new(typeof(ContentState), ContentState.BackedUp, ContentStateValues.BackedUp, "backed up", "gesichert"),
        new(typeof(ContentState), ContentState.New, ContentStateValues.New, "new", "neu"),
        new(typeof(ContentState), ContentState.Differs, ContentStateValues.Differs, "differs", "abweichend"),
        new(typeof(ContentState), ContentState.Error, ContentStateValues.Error, "error", "Fehler")
    ];

    private static readonly Dictionary<(Type, object), Entry> ByValue = Entries.ToDictionary(e => (e.Enum, e.Value));
    private static readonly Dictionary<(Type, string), Entry> ByCode = Entries.ToDictionary(e => (e.Enum, e.Code));

    /// <summary>Enums whose codes have words here. Every value of each must have an entry (checked by a test).</summary>
    public static IReadOnlyList<Type> Enums { get; } = Entries.Select(e => e.Enum).Distinct().ToList();

    /// <summary>
    /// Display language from a language setting (the summary language of the settings, for example
    /// <c>Deutsch</c>). German for German, English for everything else.
    /// </summary>
    public static DisplayLanguage LanguageFor(string? setting) =>
        setting?.Trim().ToLowerInvariant() switch
        {
            "deutsch" or "german" or "de" => DisplayLanguage.German,
            _ => DisplayLanguage.English
        };

    /// <summary>The word for an enum value. Throws when the value has none (a missing entry is a bug).</summary>
    public static string Label<TEnum>(TEnum value, DisplayLanguage language) where TEnum : struct, Enum =>
        ByValue.TryGetValue((typeof(TEnum), value), out var e)
            ? Pick(e, language)
            : throw new InvalidOperationException($"{typeof(TEnum).Name}.{value} has no display label.");

    /// <summary>The word for a stored code of <typeparamref name="TEnum"/>; any other text (<c>none</c>, <c>error</c>, null) unchanged.</summary>
    public static string? LabelOfCode<TEnum>(string? code, DisplayLanguage language) where TEnum : struct, Enum =>
        code is not null && ByCode.TryGetValue((typeof(TEnum), code), out var e) ? Pick(e, language) : code;

    /// <summary>The words for a list of marker codes, comma-separated.</summary>
    public static string Markers(IEnumerable<string> codes, DisplayLanguage language) =>
        string.Join(", ", codes.Select(c => LabelOfCode<SensitiveMarker>(c, language)));

    /// <summary>
    /// Reads a word a person or a model wrote for <typeparamref name="TEnum"/> instead of its code: the German
    /// word above, in any case, with umlauts written out (<c>pruefen</c>) and spaces as underscores
    /// (<c>private_nachricht</c>). The English word is the code itself and is read by the code parsers.
    /// </summary>
    public static bool TryParseWord<TEnum>(string? word, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(word)) return false;
        var wanted = Normalize(word);
        foreach (var e in Entries)
        {
            if (e.Enum == typeof(TEnum) && Normalize(e.German) == wanted)
            {
                value = (TEnum)e.Value;
                return true;
            }
        }
        return false;
    }

    private static string Pick(Entry e, DisplayLanguage language) =>
        language == DisplayLanguage.German ? e.German : e.English;

    private static string Normalize(string word)
    {
        var sb = new StringBuilder(word.Length + 4);
        foreach (var ch in word.Trim().ToLowerInvariant())
        {
            switch (ch)
            {
                case 'ä': sb.Append("ae"); break;
                case 'ö': sb.Append("oe"); break;
                case 'ü': sb.Append("ue"); break;
                case 'ß': sb.Append("ss"); break;
                case ' ' or '-': sb.Append('_'); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static Entry Verdict<TEnum>(TEnum value, string english, string german) where TEnum : struct, Enum =>
        new(typeof(TEnum), value, value.ToCode(), english, german);

    private static Entry Marker(SensitiveMarker marker, string english, string german) =>
        new(typeof(SensitiveMarker), marker, SensitiveMarkerNames.Format(marker)!, english, german);
}
