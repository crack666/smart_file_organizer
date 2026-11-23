using System.Globalization;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Media;

/// <summary>One reason of the rules for the kind of a video, with the number of videos it decided.</summary>
public sealed record VideoReasonCount(string Reason, int Videos);

/// <summary>
/// Kind and origin of the videos of a finding, a part or a library folder, condensed
/// (https://github.com/crack666/smart_file_organizer/issues/103): how many videos of each kind, from which kind of device,
/// and the works they belong to (a film with its year, a series with its seasons). Since 23.11.2026 also the images per
/// <c>image_origin</c> and the audio files per <c>audio_kind</c>, so that camera pictures, screenshots and downloads, or
/// music and audiobooks, read apart in the same place. Built from stored facts, no model.
/// </summary>
/// <param name="Videos">Videos with a stored kind.</param>
/// <param name="Kinds">Videos per video kind code, most first.</param>
/// <param name="Devices">Own recordings per recording device code, most first.</param>
/// <param name="Works">Films, series and music videos by title: <c>Example Show S01-S02</c>, <c>Some Film (2009)</c>; at most <see cref="MaxWorks"/>.</param>
/// <param name="Certain">Videos whose kind is certain.</param>
/// <param name="Disputed">Videos where the model named another kind than the file says.</param>
/// <param name="Reasons">Why the rules read the kinds as they did: each <c>video_kind_reason</c> with the number of videos,
/// most first, at most <see cref="MaxReasons"/>; empty for videos analysed before the reason was stored.</param>
/// <param name="Images">Images per image origin code, most first; empty when none has one.</param>
/// <param name="Audio">Audio files per audio kind code, most first; empty when none has one.</param>
public sealed record VideoOriginMix(
    int Videos,
    IReadOnlyDictionary<string, int> Kinds,
    IReadOnlyDictionary<string, int> Devices,
    IReadOnlyList<string> Works,
    int Certain,
    int Disputed,
    IReadOnlyList<VideoReasonCount>? Reasons = null,
    IReadOnlyDictionary<string, int>? Images = null,
    IReadOnlyDictionary<string, int>? Audio = null)
{
    public const int MaxWorks = 5;
    public const int MaxReasons = 5;

    /// <summary>
    /// The mix of the given files' facts, or null when no video has a kind, no image an origin and no audio file a kind.
    /// The caller passes the facts as they may be shown: for a marked file without its title.
    /// </summary>
    public static VideoOriginMix? From(IEnumerable<IReadOnlyList<FileFact>> facts)
    {
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var devices = new Dictionary<string, int>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var works = new Dictionary<string, (VideoKind Kind, int? Year, SortedSet<int> Seasons)>(StringComparer.OrdinalIgnoreCase);
        var images = new Dictionary<string, int>(StringComparer.Ordinal);
        var audio = new Dictionary<string, int>(StringComparer.Ordinal);
        int videos = 0, certain = 0, disputed = 0;
        foreach (var list in facts)
        {
            var origin = VideoOriginSummary.From(list);
            if (origin is null)
            {
                if (list.FirstOrDefault(f => f.Key == FileFactKey.ImageOrigin)?.Value is { Length: > 0 } image)
                    images[image] = images.GetValueOrDefault(image) + 1;
                else if (list.FirstOrDefault(f => f.Key == FileFactKey.AudioKind)?.Value is { Length: > 0 } sound)
                    audio[sound] = audio.GetValueOrDefault(sound) + 1;
                continue;
            }
            videos++;
            kinds[origin.Kind] = kinds.GetValueOrDefault(origin.Kind) + 1;
            if (origin.Device is { } device) devices[device] = devices.GetValueOrDefault(device) + 1;
            if (origin.Reason is { Length: > 0 } reason) reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
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
        if (videos == 0 && images.Count == 0 && audio.Count == 0) return null;

        return new VideoOriginMix(videos, Ordered(kinds), Ordered(devices),
            works.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase).Take(MaxWorks).Select(w => Work(w.Key, w.Value.Kind, w.Value.Year, w.Value.Seasons)).ToList(),
            certain, disputed, Ordered(reasons).Take(MaxReasons).Select(r => new VideoReasonCount(r.Key, r.Value)).ToList(),
            Ordered(images), Ordered(audio));
    }

    /// <summary>The dominant kind: more than half of the videos; null when they are mixed or unknown.</summary>
    public string? Dominant => Kinds.FirstOrDefault() is { Key: var k, Value: var n } && n * 2 > Videos && k != VideoKind.Unknown.ToCode() ? k : null;

    /// <summary>
    /// One line: <c>4 video(s): 3 series_episode, 1 own_recording (action_cam 1); Example Show S01-S02</c>, then
    /// <c>12 image(s): 9 camera, 3 download</c> and <c>5 audio file(s): 5 audiobook</c> where there are such.
    /// </summary>
    public string Gist()
    {
        var parts = new List<string>();
        if (Videos > 0)
        {
            var text = $"{Videos.ToString(CultureInfo.InvariantCulture)} video(s): " + Counts(Kinds);
            if (Devices.Count > 0) text += " (" + string.Join(", ", Devices.Select(d => $"{d.Key} {d.Value.ToString(CultureInfo.InvariantCulture)}")) + ")";
            if (Works.Count > 0) text += "; " + string.Join(", ", Works);
            if (Disputed > 0) text += $"; {Disputed.ToString(CultureInfo.InvariantCulture)} disputed by the model";
            parts.Add(text);
        }
        if (Images is { Count: > 0 } images) parts.Add($"{images.Values.Sum().ToString(CultureInfo.InvariantCulture)} image(s): {Counts(images)}");
        if (Audio is { Count: > 0 } audio) parts.Add($"{audio.Values.Sum().ToString(CultureInfo.InvariantCulture)} audio file(s): {Counts(audio)}");
        return string.Join("; ", parts);
    }

    private static string Counts(IReadOnlyDictionary<string, int> counts) =>
        string.Join(", ", counts.Select(k => $"{k.Value.ToString(CultureInfo.InvariantCulture)} {k.Key}"));

    private static string Work(string title, VideoKind kind, int? year, SortedSet<int> seasons)
    {
        if (kind == VideoKind.SeriesEpisode && seasons.Count > 0)
            return seasons.Count == 1 ? $"{title} S{seasons.Min:00}" : $"{title} S{seasons.Min:00}-S{seasons.Max:00}";
        return year is { } y ? $"{title} ({y.ToString(CultureInfo.InvariantCulture)})" : title;
    }

    private static IReadOnlyDictionary<string, int> Ordered(Dictionary<string, int> counts) =>
        counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => c.Value);
}
