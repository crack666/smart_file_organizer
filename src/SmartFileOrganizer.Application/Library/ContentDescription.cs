using System.Globalization;
using System.Text.Json;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Library;

/// <summary>One value of a description and on how many described files it was seen.</summary>
public sealed record DescriptionValue(string Value, int Count);

/// <summary>
/// What a group of files shows, built from the stored description facts of its files
/// (https://github.com/crack666/smart_file_organizer/issues/56): for pictures the occasion, scene, activity, setting,
/// season and whether people are seen; for documents the kind, the issuer and the year. Every value carries how many of
/// the described files show it, so a group of ski photos with one picture of a cake still reads as a ski trip. Fixed
/// codes and short texts of the model, never a free summary: two descriptions are compared value by value.
/// </summary>
/// <param name="Sampled">Files looked at (a sample of the group).</param>
/// <param name="Described">Of these, files with at least one description fact.</param>
/// <param name="Values">Per fact, its values with counts, most frequent first.</param>
public sealed record ContentDescription(int Sampled, int Described, IReadOnlyDictionary<FileFactKey, IReadOnlyList<DescriptionValue>> Values)
{
    /// <summary>The order in which facts are listed: what a person names first.</summary>
    public static readonly FileFactKey[] Order =
    [
        FileFactKey.Occasion, FileFactKey.Scene, FileFactKey.Activity, FileFactKey.Setting, FileFactKey.Season, FileFactKey.People,
        FileFactKey.DocumentKind, FileFactKey.Issuer, FileFactKey.Year
    ];

    public static ContentDescription Empty { get; } = new(0, 0, new Dictionary<FileFactKey, IReadOnlyList<DescriptionValue>>());

    /// <summary>True when no file of the group carries a description.</summary>
    public bool IsEmpty => Described == 0;

    /// <summary>The values of one fact, most frequent first; empty when none.</summary>
    public IReadOnlyList<DescriptionValue> Of(FileFactKey key) => Values.TryGetValue(key, out var v) ? v : [];

    /// <summary>The most frequent value of a fact, or null.</summary>
    public string? Top(FileFactKey key) => Of(key) is { Count: > 0 } v ? v[0].Value : null;

    /// <summary>Pictures or documents, by which facts were described more often.</summary>
    public bool IsDocuments =>
        Of(FileFactKey.DocumentKind).Sum(v => v.Count) + Of(FileFactKey.Issuer).Sum(v => v.Count)
        > Of(FileFactKey.Occasion).Sum(v => v.Count) + Of(FileFactKey.Scene).Sum(v => v.Count);

    /// <summary>
    /// Builds the description from the facts of each sampled file. Only description facts count (source model);
    /// texts are compared case-insensitively and listed as first written.
    /// </summary>
    public static ContentDescription From(IReadOnlyList<IReadOnlyList<FileFact>> files)
    {
        var counts = new Dictionary<FileFactKey, Dictionary<string, (string Shown, int Count)>>();
        var described = 0;
        foreach (var facts in files)
        {
            var any = false;
            foreach (var f in facts.Where(f => f.Key.IsDescription() && !string.IsNullOrWhiteSpace(f.Value)))
            {
                any = true;
                if (!counts.TryGetValue(f.Key, out var byValue))
                    counts[f.Key] = byValue = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
                var value = f.Value.Trim();
                byValue[value] = byValue.TryGetValue(value, out var c) ? (c.Shown, c.Count + 1) : (value, 1);
            }
            if (any) described++;
        }

        var values = counts.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<DescriptionValue>)kv.Value.Values
                .OrderByDescending(v => v.Count).ThenBy(v => v.Shown, StringComparer.OrdinalIgnoreCase)
                .Select(v => new DescriptionValue(v.Shown, v.Count)).ToList());
        return new ContentDescription(files.Count, described, values);
    }

    /// <summary>
    /// The words a person would use for it, for a reason: the scenes and activities (or kinds and issuers) seen most,
    /// at most <paramref name="max"/> ("snowy slope, skiing, mountain hut").
    /// </summary>
    public string Gist(int max = 3)
    {
        IEnumerable<string> words = IsDocuments
            ? Of(FileFactKey.DocumentKind).Select(v => v.Value).Take(1).Concat(Of(FileFactKey.Issuer).Select(v => v.Value).Take(1))
                .Concat(Of(FileFactKey.Year).Select(v => v.Value).Take(1))
            : Of(FileFactKey.Scene).Concat(Of(FileFactKey.Activity)).OrderByDescending(v => v.Count).Select(v => v.Value)
                .Concat(Of(FileFactKey.Occasion).Select(v => v.Value).Take(1));
        var list = words.Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        return list.Count == 0 ? "nothing described" : string.Join(", ", list);
    }

    /// <summary>
    /// One line per fact as the model sees it: <c>occasion: holiday 4/4; scene: snowy slope 3, mountain hut 1</c>.
    /// At most <paramref name="perFact"/> values per fact.
    /// </summary>
    public string Text(int perFact = 3)
    {
        if (IsEmpty) return "nothing described";
        var parts = new List<string>();
        foreach (var key in Order)
        {
            var values = Of(key);
            if (values.Count == 0) continue;
            var shown = key == FileFactKey.People
                ? $"yes {values.Where(v => v.Value == "true").Sum(v => v.Count)} of {values.Sum(v => v.Count)}"
                : string.Join(", ", values.Take(perFact).Select(v => $"{v.Value} {v.Count.ToString(CultureInfo.InvariantCulture)}"));
            parts.Add($"{key.ToCode()}: {shown}");
        }
        return $"{string.Join("; ", parts)} ({Described.ToString(CultureInfo.InvariantCulture)} of {Sampled.ToString(CultureInfo.InvariantCulture)} files described)";
    }

    // ------------------------------------------------------------------ storage

    private sealed record Stored(int Sampled, int Described, Dictionary<string, List<DescriptionValue>> Values);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string ToJson() => JsonSerializer.Serialize(new Stored(Sampled, Described,
        Values.ToDictionary(kv => kv.Key.ToCode(), kv => kv.Value.ToList())), Json);

    public static ContentDescription FromJson(string json)
    {
        var stored = JsonSerializer.Deserialize<Stored>(json, Json);
        if (stored is null) return Empty;
        var values = new Dictionary<FileFactKey, IReadOnlyList<DescriptionValue>>();
        foreach (var (code, list) in stored.Values ?? [])
        {
            if (VerdictCodes.TryParse<FileFactKey>(code, out var key))
                values[key] = list;
        }
        return new ContentDescription(stored.Sampled, stored.Described, values);
    }

    /// <summary>The description as a JSON-friendly map: fact code to values with counts.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DescriptionValue>> ByCode() =>
        Order.Where(Values.ContainsKey).ToDictionary(k => k.ToCode(), k => Values[k]);

    public override string ToString() => Text();
}
