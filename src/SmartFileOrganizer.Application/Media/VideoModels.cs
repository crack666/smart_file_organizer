using System.Globalization;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Media;

/// <summary>One track of a video container.</summary>
/// <param name="Type"><c>video</c>, <c>audio</c> or <c>subtitle</c>.</param>
/// <param name="Language">The language as written in the container (<c>ger</c>, <c>de</c>, <c>en-US</c>); null for undetermined.</param>
/// <param name="Name">The track or handler name (<c>GoPro AVC</c>, <c>Director's commentary</c>).</param>
public sealed record VideoTrack(string Type, string? Language, string? Name);

/// <summary>
/// What the container of a video says, read without decoding a frame (https://github.com/crack666/smart_file_organizer/issues/103).
/// Every member is optional: a container tells what it tells. Coordinates are never kept, only <see cref="HasLocation"/>.
/// </summary>
public sealed record VideoContainerInfo
{
    /// <summary><c>mp4</c>, <c>quicktime</c>, <c>matroska</c>, <c>webm</c> or another format name.</summary>
    public string? Format { get; init; }

    public string? Make { get; init; }
    public string? Model { get; init; }

    /// <summary>The recording or encoding program (<c>©too</c>, <c>©swr</c>, software key, Matroska writing app).</summary>
    public string? Software { get; init; }

    /// <summary>The muxing library of a Matroska file (<c>libebml</c>, <c>Lavf</c>); never a recording program by itself.</summary>
    public string? MuxingApp { get; init; }

    /// <summary>The title stored in the container.</summary>
    public string? Title { get; init; }

    public DateTime? CreatedAt { get; init; }
    public bool HasLocation { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public double? DurationSeconds { get; init; }
    public long? BitrateKbps { get; init; }
    public int ChapterCount { get; init; }
    public IReadOnlyList<VideoTrack> Tracks { get; init; } = [];

    /// <summary>The device as one text, make and model without repeating the make.</summary>
    public string? Device =>
        Make is null ? Model
        : Model is null ? Make
        : Model.StartsWith(Make, StringComparison.OrdinalIgnoreCase) ? Model
        : $"{Make} {Model}";
}

/// <summary>What the files next to a video say (https://github.com/crack666/smart_file_organizer/issues/103).</summary>
/// <param name="SiblingNames">Names of the other files in the folder (a capped list).</param>
/// <param name="InfoRoot">The root element of an info file of a media library for this video: <c>movie</c>, <c>episodedetails</c>, <c>musicvideo</c>.</param>
/// <param name="FolderHasShowInfo">A <c>tvshow.nfo</c> lies in the folder or the one above.</param>
/// <param name="TelemetrySubtitles">A subtitle file of the same name holds camera telemetry (as drones and action cams write), not text.</param>
/// <param name="DownloadInfo">A <c>.info.json</c> of the same name, as download programs write.</param>
/// <param name="Artwork">Poster, fanart or folder pictures of a media library next to it.</param>
/// <param name="CameraSideFiles">Preview or side files a camera writes next to a clip (<c>.THM</c>, <c>.LRV</c>, an XML of the same number).</param>
/// <param name="SubtitleLanguages">Languages of subtitle files of the same name (<c>Film.de.srt</c>: <c>de</c>); <c>und</c> when none is named.</param>
public sealed record VideoCompanions(
    IReadOnlyList<string> SiblingNames,
    string? InfoRoot = null,
    string? InfoTitle = null,
    int? InfoYear = null,
    int? InfoSeason = null,
    int? InfoEpisode = null,
    bool FolderHasShowInfo = false,
    bool TelemetrySubtitles = false,
    bool DownloadInfo = false,
    bool Artwork = false,
    bool CameraSideFiles = false,
    IReadOnlyList<string>? SubtitleLanguages = null)
{
    public static VideoCompanions None { get; } = new([]);
}

/// <summary>
/// One thing the rules looked at for the kind of a video, and what it did to the decision
/// (https://github.com/crack666/smart_file_organizer/issues/103): <c>track_name 'ISO Media file produced by ...'</c>,
/// <c>ignored: platforms and encoders write it</c>.
/// </summary>
/// <param name="Field">A video evidence field code: <c>make</c>, <c>model</c>, <c>track_name</c>, <c>software</c>,
/// <c>location</c>, <c>name</c>, <c>folder</c>, <c>info_file</c> or <c>companion</c>.</param>
/// <param name="Value">The value read; for <c>name</c> and <c>folder</c> the scheme that matched, never the file name itself.</param>
/// <param name="Effect">What the rules made of it, in a few words.</param>
public sealed record VideoEvidence(string Field, string? Value, string Effect);

/// <summary>
/// The kind and origin of one video as the rules decided them, with where each statement came from and how sure it is.
/// <see cref="Remaining"/> are the kinds the model may still choose from when the rules are not sure; <see cref="Evidence"/>
/// lists what was looked at, also what was ignored or lowered the certainty.
/// </summary>
public sealed record VideoOrigin(
    VideoKind Kind,
    FactSource KindSource,
    ResultCertainty KindCertainty,
    string Reason,
    RecordingDevice? Device = null,
    FactSource? DeviceSource = null,
    ResultCertainty? DeviceCertainty = null,
    string? Title = null,
    int? Year = null,
    int? Season = null,
    int? Episode = null,
    FactSource? WorkSource = null,
    IReadOnlyList<VideoKind>? Remaining = null,
    IReadOnlyList<VideoEvidence>? Evidence = null)
{
    /// <summary>The kind needs no model: known, and at least likely.</summary>
    public bool IsSettled => Kind != VideoKind.Unknown && KindCertainty != ResultCertainty.Uncertain;

    /// <summary>
    /// The facts to store: kind and its evidence, device kind, title, year, season and episode, and what the container
    /// says (encoder, frame rate, bitrate, languages, chapters). Make and model stay in <c>device</c>, as for photos.
    /// </summary>
    public IReadOnlyList<FileFact> ToFacts(VideoContainerInfo? info, VideoCompanions? companions = null)
    {
        var facts = new List<FileFact>
        {
            new(FileFactKey.VideoKind, Kind.ToCode(), KindSource, KindCertainty)
        };
        if (Device is { } device)
            facts.Add(new FileFact(FileFactKey.RecordingDevice, device.ToCode(), DeviceSource ?? KindSource, DeviceCertainty ?? KindCertainty));
        var work = WorkSource ?? KindSource;
        if (!string.IsNullOrWhiteSpace(Title)) facts.Add(new FileFact(FileFactKey.VideoTitle, Clip(Title, 80), work, KindCertainty));
        if (Year is { } y) facts.Add(new FileFact(FileFactKey.ReleaseYear, Int(y), work, KindCertainty));
        if (Season is { } s) facts.Add(new FileFact(FileFactKey.SeasonNumber, Int(s), work, KindCertainty));
        if (Episode is { } e) facts.Add(new FileFact(FileFactKey.EpisodeNumber, Int(e), work, KindCertainty));
        if (!string.IsNullOrWhiteSpace(Reason)) facts.Add(new FileFact(FileFactKey.VideoKindReason, Clip(Reason, 200), KindSource));

        if (info is not null)
        {
            if (!string.IsNullOrWhiteSpace(info.Software)) facts.Add(new FileFact(FileFactKey.Encoder, Clip(info.Software, 60)));
            if (info.FrameRate is > 0 and var fps) facts.Add(new FileFact(FileFactKey.FrameRate, Math.Round(fps, 2).ToString(CultureInfo.InvariantCulture)));
            if (info.BitrateKbps is > 0 and var kbps) facts.Add(new FileFact(FileFactKey.BitrateKbps, kbps.ToString(CultureInfo.InvariantCulture)));
            var audio = Languages(info.Tracks.Where(t => t.Type == "audio"));
            if (audio is not null) facts.Add(new FileFact(FileFactKey.AudioLanguages, audio));
            if (info.ChapterCount > 0) facts.Add(new FileFact(FileFactKey.ChapterCount, Int(info.ChapterCount)));
        }
        var subs = (info?.Tracks.Where(t => t.Type == "subtitle").Select(t => VideoKindRules.Language(t.Language)) ?? [])
            .Concat(companions?.SubtitleLanguages ?? [])
            .Where(l => l is not null).Select(l => l!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (subs.Count > 0)
            facts.Add(new FileFact(FileFactKey.SubtitleLanguages, string.Join(",", subs),
                info?.Tracks.Any(t => t.Type == "subtitle") == true ? FactSource.Metadata : FactSource.Companion));
        return facts;
    }

    private static string? Languages(IEnumerable<VideoTrack> tracks)
    {
        var list = tracks.Select(t => VideoKindRules.Language(t.Language)).Where(l => l is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? null : string.Join(",", list);
    }

    private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Clip(string s, int max) => s.Length > max ? s[..max].TrimEnd() : s.Trim();
}

/// <summary>Reads the kind and origin of a video from its container, its name and the files next to it, without a model.</summary>
public interface IVideoOriginReader
{
    /// <summary>The container facts, the companions and the decision; null when the file cannot be read at all.</summary>
    VideoOriginReading? Read(FileNode file);
}

/// <summary>One reading of a video: what the container and the companions said, and what the rules decided.</summary>
public sealed record VideoOriginReading(VideoContainerInfo? Container, VideoCompanions Companions, VideoOrigin Origin)
{
    public IReadOnlyList<FileFact> Facts => Origin.ToFacts(Container, Companions);
}
