using System.Globalization;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Media;

/// <summary>
/// Kind and origin of the videos of a finding, a part or a library folder, condensed
/// (https://github.com/crack666/smart_file_organizer/issues/103): how many videos of each kind, from which kind of device,
/// and the works they belong to (a film with its year, a series with its seasons). Built from stored facts, no model.
/// </summary>
/// <param name="Videos">Videos with a stored kind.</param>
/// <param name="Kinds">Videos per video kind code, most first.</param>
/// <param name="Devices">Own recordings per recording device code, most first.</param>
/// <param name="Works">Films, series and music videos by title: <c>Example Show S01-S02</c>, <c>Some Film (2009)</c>; at most <see cref="MaxWorks"/>.</param>
/// <param name="Certain">Videos whose kind is certain.</param>
/// <param name="Disputed">Videos where the model named another kind than the file says.</param>
public sealed record VideoOriginMix(
    int Videos,
    IReadOnlyDictionary<string, int> Kinds,
    IReadOnlyDictionary<string, int> Devices,
    IReadOnlyList<string> Works,
    int Certain,
    int Disputed)
{
    public const int MaxWorks = 5;

    /// <summary>
    /// The mix of the given videos' facts, or null when none has a kind. The caller passes the facts as they may be shown:
    /// for a marked file without its title.
    /// </summary>
    public static VideoOriginMix? From(IEnumerable<IReadOnlyList<FileFact>> facts)
    {
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var devices = new Dictionary<string, int>(StringComparer.Ordinal);
        var works = new Dictionary<string, (VideoKind Kind, int? Year, SortedSet<int> Seasons)>(StringComparer.OrdinalIgnoreCase);
        int videos = 0, certain = 0, disputed = 0;
        foreach (var list in facts)
        {
            var origin = VideoOriginSummary.From(list);
            if (origin is null) continue;
            videos++;
            kinds[origin.Kind] = kinds.GetValueOrDefault(origin.Kind) + 1;
            if (origin.Device is { } device) devices[device] = devices.GetValueOrDefault(device) + 1;
            if (origin.Certainty == ResultCertainty.Certain.ToCode()) certain++;
            if (origin.Disputed is not null) disputed++;
            if (origin.Title is { Length: > 0 } title && VerdictCodes.ParseNullable<VideoKind>(origin.Kind) is { } kind
                && kind is VideoKind.Movie or VideoKind.SeriesEpisode or VideoKind.MusicVideo)
            {
                if (!works.TryGetValue(title, out var work)) work = (kind, origin.Year, []);
                if (origin.Season is { } s) work.Seasons.Add(s);
                works[title] = work with { Year = work.Year ?? origin.Year };
            }
        }
        if (videos == 0) return null;

        return new VideoOriginMix(videos, Ordered(kinds), Ordered(devices),
            works.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase).Take(MaxWorks).Select(w => Work(w.Key, w.Value.Kind, w.Value.Year, w.Value.Seasons)).ToList(),
            certain, disputed);
    }

    /// <summary>The dominant kind: more than half of the videos; null when they are mixed or unknown.</summary>
    public string? Dominant => Kinds.FirstOrDefault() is { Key: var k, Value: var n } && n * 2 > Videos && k != VideoKind.Unknown.ToCode() ? k : null;

    /// <summary>One line: <c>4 videos: 3 series_episode, 1 own_recording (action_cam 1); Example Show S01-S02</c>.</summary>
    public string Gist()
    {
        var text = $"{Videos.ToString(CultureInfo.InvariantCulture)} video(s): " + string.Join(", ", Kinds.Select(k => $"{k.Value.ToString(CultureInfo.InvariantCulture)} {k.Key}"));
        if (Devices.Count > 0) text += " (" + string.Join(", ", Devices.Select(d => $"{d.Key} {d.Value.ToString(CultureInfo.InvariantCulture)}")) + ")";
        if (Works.Count > 0) text += "; " + string.Join(", ", Works);
        if (Disputed > 0) text += $"; {Disputed.ToString(CultureInfo.InvariantCulture)} disputed by the model";
        return text;
    }

    private static string Work(string title, VideoKind kind, int? year, SortedSet<int> seasons)
    {
        if (kind == VideoKind.SeriesEpisode && seasons.Count > 0)
            return seasons.Count == 1 ? $"{title} S{seasons.Min:00}" : $"{title} S{seasons.Min:00}-S{seasons.Max:00}";
        return year is { } y ? $"{title} ({y.ToString(CultureInfo.InvariantCulture)})" : title;
    }

    private static IReadOnlyDictionary<string, int> Ordered(Dictionary<string, int> counts) =>
        counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => c.Value);
}
