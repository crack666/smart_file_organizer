using System.Globalization;
using System.Text.RegularExpressions;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Media;

/// <summary>
/// Decides what kind of video a file is and what recorded it, from what is cheap and sure to read
/// (https://github.com/crack666/smart_file_organizer/issues/103): the container (make, model, recording program, handler
/// names, location), the name (naming schemes of cameras and recording programs, release names with title and year,
/// season and episode) and the files next to it (an info file of a media library, telemetry subtitles of a drone, the
/// side files of a camera, a download info file). The order is: info file, container, name, companions; the first sure
/// statement decides. What none of them tells stays <see cref="VideoKind.Unknown"/>, and the model chooses from
/// <see cref="VideoOrigin.Remaining"/>. Naming schemes are those of the makers (GoPro, DJI, Insta360, phones, camera
/// makers) and of common recording and download programs; no list of titles.
/// </summary>
public static partial class VideoKindRules
{
    /// <summary>Programs that record a screen or a game, as they write themselves into the container.</summary>
    private static readonly string[] ScreenRecorders =
    [
        "obs", "shadowplay", "geforce", "nvidia", "radeon relive", "amd relive", "game bar", "gamebar", "game dvr",
        "bandicam", "camtasia", "screenflow", "snagit", "fraps", "action!", "mirillis", "xsplit", "screencast",
        "screen recorder", "screenrecorder", "loom", "sharex", "kazam", "simplescreenrecorder", "vokoscreen",
        "quicktime screen", "screen recording", "steam game recording"
    ];

    private static readonly string[] ActionCamMakers = ["gopro", "akaso", "sjcam", "yi action", "osmo action", "drift", "garmin virb", "apeman"];
    private static readonly string[] Camera360Makers = ["insta360", "ricoh theta", "theta", "gopro max", "gopro fusion", "qoocam", "kandao", "samsung gear 360"];
    private static readonly string[] DroneWords = ["mavic", "phantom", "inspire", "spark", "air 2", "air 3", "mini 2", "mini 3", "mini 4", "fpv", "avata", "autel", "skydio", "parrot anafi"];
    private static readonly string[] DashcamMakers = ["viofo", "blackvue", "nextbase", "garmin dash", "thinkware", "70mai", "dashcam", "dash cam"];
    private static readonly string[] PhoneMakers =
    [
        "apple", "iphone", "samsung", "google", "pixel", "xiaomi", "redmi", "poco", "oneplus", "huawei", "honor", "oppo", "vivo",
        "realme", "motorola", "nokia", "hmd", "fairphone", "nothing", "asus", "zte", "lg electronics", "htc", "android"
    ];
    private static readonly string[] CameraMakers =
    [
        "canon", "nikon", "sony", "panasonic", "fujifilm", "olympus", "om digital", "leica", "pentax", "ricoh", "jvc", "sigma",
        "blackmagic", "hasselblad", "kodak", "casio", "samsung techwin"
    ];

    /// <summary>
    /// The decision for one video. <paramref name="info"/> is null when the container could not be read; <paramref name="relativeDir"/>
    /// is the folder relative to the scan root, used for a folder named like a film (<c>Title (2009)</c>).
    /// </summary>
    public static VideoOrigin Decide(VideoContainerInfo? info, string fileName, string relativeDir, VideoCompanions companions)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var folder = LastFolder(relativeDir);

        // 1. An info file of a media library names the kind.
        switch (companions.InfoRoot)
        {
            case "movie":
                return new VideoOrigin(VideoKind.Movie, FactSource.Companion, ResultCertainty.Certain, "info file <movie>",
                    Title: companions.InfoTitle ?? Release(stem)?.Title, Year: companions.InfoYear ?? Release(stem)?.Year,
                    WorkSource: FactSource.Companion);
            case "episodedetails":
                var ep = Episode(stem);
                return new VideoOrigin(VideoKind.SeriesEpisode, FactSource.Companion, ResultCertainty.Certain, "info file <episodedetails>",
                    Title: companions.InfoTitle ?? ep?.Title, Season: companions.InfoSeason ?? ep?.Season,
                    Episode: companions.InfoEpisode ?? ep?.Episode, WorkSource: FactSource.Companion);
            case "musicvideo":
                return new VideoOrigin(VideoKind.MusicVideo, FactSource.Companion, ResultCertainty.Certain, "info file <musicvideo>",
                    Title: companions.InfoTitle, Year: companions.InfoYear, WorkSource: FactSource.Companion);
        }

        // 2. The container: a recording program, a device.
        if (info is not null)
        {
            var programs = $"{info.Software} {info.MuxingApp} {string.Join(" ", info.Tracks.Select(t => t.Name))}";
            if (ScreenRecorders.FirstOrDefault(r => programs.Contains(r, StringComparison.OrdinalIgnoreCase)) is { } recorder)
                return new VideoOrigin(VideoKind.ScreenRecording, FactSource.Metadata, ResultCertainty.Certain, $"recording program '{recorder}' in the container");

            var trackNames = string.Join(" ", info.Tracks.Select(t => t.Name));
            if (info.Make is not null || info.Model is not null)
            {
                var device = DeviceOf($"{info.Make} {info.Model} {trackNames}", ext) ?? CameraName(stem, ext)?.Device;
                return new VideoOrigin(VideoKind.OwnRecording, FactSource.Metadata, ResultCertainty.Certain, $"device '{info.Device}' in the container",
                    device, FactSource.Metadata, device is null ? null : ResultCertainty.Certain);
            }
            // Some cameras name only their tracks (GoPro AVC, DJI.AVC, Insta360).
            if (DeviceOf(trackNames, ext) is { } byTrack)
                return new VideoOrigin(VideoKind.OwnRecording, FactSource.Metadata, ResultCertainty.Certain, "camera track name in the container",
                    byTrack, FactSource.Metadata, ResultCertainty.Certain);
        }

        // 3. The name.
        if (Episode(stem) is { } episode)
        {
            return new VideoOrigin(VideoKind.SeriesEpisode, FactSource.Name, ResultCertainty.Certain, "season and episode in the name",
                Title: episode.Title ?? FolderTitle(relativeDir), Year: episode.Year, Season: episode.Season, Episode: episode.Episode,
                WorkSource: FactSource.Name);
        }

        if (CameraName(stem, ext) is { } camera)
        {
            // A side file of the camera or telemetry next to it confirms the scheme.
            var confirmed = companions.CameraSideFiles || companions.TelemetrySubtitles;
            var device = camera.Device ?? (companions.TelemetrySubtitles ? RecordingDevice.Drone : null);
            return new VideoOrigin(VideoKind.OwnRecording, confirmed ? FactSource.Companion : FactSource.Name,
                camera.Sure || confirmed ? ResultCertainty.Certain : ResultCertainty.Likely,
                $"naming scheme of {camera.Scheme}{(confirmed ? " with side files of the camera" : string.Empty)}",
                device, confirmed ? FactSource.Companion : FactSource.Name,
                device is null ? null : camera.Sure || confirmed ? ResultCertainty.Certain : ResultCertainty.Likely);
        }

        if (ScreenRecordingName().IsMatch(stem))
            return new VideoOrigin(VideoKind.ScreenRecording, FactSource.Name, ResultCertainty.Likely, "naming scheme of a screen recording");
        if (GameRecordingName().IsMatch(stem))
            return new VideoOrigin(VideoKind.ScreenRecording, FactSource.Name, ResultCertainty.Likely, "naming scheme of a game recording program (date and time with dashes)");

        if (MessengerName().IsMatch(stem))
            return new VideoOrigin(VideoKind.WebClip, FactSource.Name, ResultCertainty.Likely, "naming scheme of a messenger");
        if (companions.DownloadInfo)
            return new VideoOrigin(VideoKind.WebClip, FactSource.Companion, ResultCertainty.Certain, "download info file next to it");
        if (DownloadName().IsMatch(stem))
            return new VideoOrigin(VideoKind.WebClip, FactSource.Name, ResultCertainty.Likely, "video id of a platform in brackets");

        if (MusicVideoName().IsMatch(stem))
            return new VideoOrigin(VideoKind.MusicVideo, FactSource.Name, ResultCertainty.Likely, "'official video' in the name",
                Title: Tidy(MusicVideoName().Replace(stem, " ")), WorkSource: FactSource.Name);

        var release = Release(stem);
        var folderRelease = release is null ? Release(folder) : null;
        if (release is { Tagged: true })
        {
            return new VideoOrigin(VideoKind.Movie, FactSource.Name, ResultCertainty.Certain, "release name with year and source or resolution",
                Title: release.Title, Year: release.Year, WorkSource: FactSource.Name);
        }
        if ((release ?? folderRelease) is { } named)
        {
            var library = companions.Artwork || (companions.SubtitleLanguages?.Count ?? 0) > 0;
            return new VideoOrigin(VideoKind.Movie, library ? FactSource.Companion : FactSource.Name,
                library ? ResultCertainty.Certain : ResultCertainty.Likely,
                $"title with year in {(release is null ? "the folder name" : "the name")}{(library ? ", artwork or subtitles of a media library next to it" : string.Empty)}",
                Title: named.Title, Year: named.Year, WorkSource: FactSource.Name);
        }
        if (companions.FolderHasShowInfo)
            return new VideoOrigin(VideoKind.SeriesEpisode, FactSource.Companion, ResultCertainty.Likely, "tvshow.nfo in the folder",
                Title: FolderTitle(relativeDir), WorkSource: FactSource.Companion);

        // 4. Weak signs: a location without a device.
        if (info?.HasLocation == true)
        {
            return new VideoOrigin(VideoKind.OwnRecording, FactSource.Metadata, ResultCertainty.Uncertain, "a location in the container, no device",
                Remaining: RemainingKinds(info));
        }

        return new VideoOrigin(VideoKind.Unknown, FactSource.Rule, ResultCertainty.Uncertain,
            "neither container, name nor the files next to it tell the kind", Remaining: RemainingKinds(info));
    }

    /// <summary>
    /// The kinds the model may still choose from: all known kinds, less those the duration rules out (no film under
    /// 20 minutes, no music video over 15 minutes, no episode under 2 minutes).
    /// </summary>
    public static IReadOnlyList<VideoKind> RemainingKinds(VideoContainerInfo? info)
    {
        var kinds = Enum.GetValues<VideoKind>().Where(k => k != VideoKind.Unknown).ToList();
        if (info?.DurationSeconds is { } d)
        {
            if (d < 20 * 60) kinds.Remove(VideoKind.Movie);
            if (d > 15 * 60) kinds.Remove(VideoKind.MusicVideo);
            if (d < 120) kinds.Remove(VideoKind.SeriesEpisode);
        }
        return kinds;
    }

    // ------------------------------------------------------------------ device

    /// <summary>The kind of device that make, model or track names name; null when they name none this table knows.</summary>
    public static RecordingDevice? DeviceOf(string text, string ext = "")
    {
        if (string.IsNullOrWhiteSpace(text)) return ext == ".insv" ? RecordingDevice.Camera360 : null;
        bool Has(IEnumerable<string> words) => words.Any(w => Word(text, w));
        if (ext is ".insv" || Has(Camera360Makers)) return RecordingDevice.Camera360;
        if (Has(ActionCamMakers)) return RecordingDevice.ActionCam;
        if (Has(DashcamMakers)) return RecordingDevice.Dashcam;
        if (Word(text, "dji") || Has(DroneWords))
            return Word(text, "osmo") || Word(text, "pocket") || Word(text, "action") ? RecordingDevice.ActionCam : RecordingDevice.Drone;
        if (Has(PhoneMakers) || IPhoneModel().IsMatch(text) || SamsungModel().IsMatch(text)) return RecordingDevice.Phone;
        if (Has(CameraMakers)) return RecordingDevice.Camera;
        return null;
    }

    /// <summary>The word stands alone (not inside another word), so <c>nothing</c> is a maker but not in <c>nothingness</c>.</summary>
    private static bool Word(string text, string word)
    {
        for (var i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var before = i == 0 || !char.IsLetter(text[i - 1]);
            var end = i + word.Length;
            var after = end >= text.Length || !char.IsLetter(text[end]);
            if (before && after) return true;
        }
        return false;
    }

    private sealed record CameraScheme(string Scheme, RecordingDevice? Device, bool Sure);

    /// <summary>The naming schemes of cameras and phones: <c>GX010123</c>, <c>DJI_0042</c>, <c>VID_20240501_183000</c>, <c>MVI_1234</c>, ...</summary>
    private static CameraScheme? CameraName(string stem, string ext)
    {
        if (ext == ".insv" || Insta360Name().IsMatch(stem)) return new CameraScheme("a 360-degree camera", RecordingDevice.Camera360, true);
        if (GoProName().IsMatch(stem)) return new CameraScheme("an action cam (GoPro)", RecordingDevice.ActionCam, true);
        if (DjiName().IsMatch(stem)) return new CameraScheme("a DJI drone or camera", RecordingDevice.Drone, false);
        if (PhoneName().IsMatch(stem)) return new CameraScheme("a phone", RecordingDevice.Phone, false);
        if (CameraMakerName().IsMatch(stem)) return new CameraScheme("a camera", RecordingDevice.Camera, false);
        if (ext is ".mts" or ".m2ts" && AvchdName().IsMatch(stem)) return new CameraScheme("a camcorder (AVCHD)", RecordingDevice.Camera, false);
        if (DashcamName().IsMatch(stem)) return new CameraScheme("a dashcam", RecordingDevice.Dashcam, false);
        return null;
    }

    // ------------------------------------------------------------------ release names

    /// <summary>Title, year and whether release tags (source, resolution, codec) follow.</summary>
    public sealed record ReleaseName(string Title, int Year, bool Tagged);

    /// <summary>
    /// A film name: <c>Title (2009)</c>, <c>Title [2009]</c> or <c>Title.2009.1080p.BluRay.x264</c>. A bare year without
    /// brackets counts only with a source or codec word after it, so <c>Birthday_2018</c> and <c>Holiday 2019 1080p</c> are no film.
    /// </summary>
    public static ReleaseName? Release(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return null;
        var tagged = TaggedRelease().Match(stem);
        if (tagged.Success && Year(tagged.Groups["year"].Value) is { } ty && Tidy(tagged.Groups["title"].Value) is { Length: > 0 } tt)
            return new ReleaseName(tt, ty, true);
        var bracketed = BracketedRelease().Match(stem);
        if (bracketed.Success && Year(bracketed.Groups["year"].Value) is { } by && Tidy(bracketed.Groups["title"].Value) is { Length: > 0 } bt)
            return new ReleaseName(bt, by, ReleaseTags().IsMatch(stem[(bracketed.Index + bracketed.Length)..]));
        return null;
    }

    public sealed record EpisodeName(string? Title, int Season, int Episode, int? Year);

    /// <summary>Season and episode: <c>S01E02</c>, <c>s1e2</c>, <c>1x02</c>, with the series title before it.</summary>
    public static EpisodeName? Episode(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return null;
        var m = EpisodePattern().Match(stem);
        if (!m.Success) return null;
        var season = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        var episode = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture);
        if (season > 99 || episode is 0 or > 999) return null;
        var before = stem[..m.Index];
        var yearMatch = TitleYear().Match(before);
        int? year = yearMatch.Success ? Year(yearMatch.Groups["year"].Value) : null;
        var title = Tidy(yearMatch.Success ? before[..yearMatch.Index] : before);
        return new EpisodeName(title is { Length: > 0 } ? title : null, season, episode, year);
    }

    private static int? Year(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1900 and <= 2100 ? y : null;

    /// <summary>Dots, underscores and dashes between words become spaces; brackets and spaces at the ends go.</summary>
    private static string Tidy(string text)
    {
        var s = Separators().Replace(text, " ");
        return s.Trim(' ', '-', '(', '[', '{', ')', ']', '}');
    }

    private static string LastFolder(string relativeDir) =>
        string.IsNullOrEmpty(relativeDir) ? string.Empty : Path.GetFileName(relativeDir.Replace('\\', '/').TrimEnd('/'));

    /// <summary>The series title from the folders: the folder above a season folder, else the folder itself.</summary>
    private static string? FolderTitle(string relativeDir)
    {
        var parts = relativeDir.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var last = parts[^1];
        if (SeasonFolder().IsMatch(last)) return parts.Length > 1 ? Tidy(parts[^2]) : null;
        return Tidy(last);
    }

    // ------------------------------------------------------------------ languages

    private static readonly Dictionary<string, string> ThreeLetter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ger"] = "de", ["deu"] = "de", ["eng"] = "en", ["fre"] = "fr", ["fra"] = "fr", ["spa"] = "es", ["ita"] = "it",
        ["jpn"] = "ja", ["dut"] = "nl", ["nld"] = "nl", ["por"] = "pt", ["rus"] = "ru", ["pol"] = "pl", ["tur"] = "tr",
        ["swe"] = "sv", ["dan"] = "da", ["nor"] = "no", ["fin"] = "fi", ["chi"] = "zh", ["zho"] = "zh", ["kor"] = "ko"
    };

    /// <summary>A language code as two letters where it is one of the common ones (<c>ger</c>: <c>de</c>); null for undetermined.</summary>
    public static string? Language(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim();
        if (c.Equals("und", StringComparison.OrdinalIgnoreCase) || c.Equals("zxx", StringComparison.OrdinalIgnoreCase)
            || c.Equals("mis", StringComparison.OrdinalIgnoreCase)) return null;
        var primary = c.Split('-', '_')[0];
        if (ThreeLetter.TryGetValue(primary, out var two)) return two;
        return primary.Length is 2 or 3 && primary.All(char.IsAsciiLetter) ? primary.ToLowerInvariant() : null;
    }

    // ------------------------------------------------------------------ patterns

    [GeneratedRegex(@"[._]+|\s{2,}")]
    private static partial Regex Separators();

    [GeneratedRegex(@"(?:^|[^a-z0-9])(?:s(?<s>\d{1,2})[ ._-]?e(?<e>\d{1,3})|(?<s>\d{1,2})x(?<e>\d{2,3}))(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodePattern();

    [GeneratedRegex(@"[ ._(\[]+(?<year>(19|20)\d{2})[)\]]?[ ._-]*$")]
    private static partial Regex TitleYear();

    [GeneratedRegex(@"^(?<title>.+?)[ ._(\[]+(?<year>(19|20)\d{2})[)\]]?[ ._-]+(?=.*" + Tags + ")", RegexOptions.IgnoreCase)]
    private static partial Regex TaggedRelease();

    [GeneratedRegex(@"^(?<title>.*?[^\s._-])[ ._]*[(\[](?<year>(19|20)\d{2})[)\]]")]
    private static partial Regex BracketedRelease();

    private const string Tags =
        // Source and codec words of release names only: no word a person adds to an own export (a resolution, web, hdr, dvd).
        @"(?:\b|_)(?:bluray|blu-ray|bdrip|brrip|bdremux|remux|web-?dl|webrip|hdtv|dvdrip|hdrip|x264|x265|h\.?264|h\.?265|hevc|xvid|divx|dts|ac3|dd5\.?1|atmos|proper|repack|unrated|directors\.?cut)(?:\b|_)";

    [GeneratedRegex(Tags, RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseTags();

    [GeneratedRegex(@"^(?:staffel|season|series|saison|temporada)[ ._-]*\d{1,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"^(?:G[HXP]\d{6}|GOPR\d{4}|GP\d{6}|GH[A-Z]{2}\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex GoProName();

    [GeneratedRegex(@"^(?:DJI_\d{4}|DJI_\d{14}_\d{4}(?:_[A-Z])?)$", RegexOptions.IgnoreCase)]
    private static partial Regex DjiName();

    [GeneratedRegex(@"^(?:VID_\d{8}_\d{6}_\d{2}_\d{3}|LRV_\d{8}_\d{6}_\d{2}_\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex Insta360Name();

    [GeneratedRegex(@"^(?:VID_\d{8}_\d{6}(?:_\d+)?|PXL_\d{8}_\d{9}(?:\.[A-Z_]+)?|\d{8}_\d{6}|IMG_\d{4}|IMG_\d{8}_\d{6}|video_\d{8}_\d{6}|MOV_\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex PhoneName();

    [GeneratedRegex(@"^(?:MVI_\d{4}|DSC[_N]?\d{4,5}|_DSC\d{4}|C\d{4}|MAH\d{5}|P\d{7}|DSCF\d{4}|P\d{3}\d{4}|M2U\d{5}|MOV\d{5})$", RegexOptions.IgnoreCase)]
    private static partial Regex CameraMakerName();

    [GeneratedRegex(@"^\d{5}$")]
    private static partial Regex AvchdName();

    [GeneratedRegex(@"^(?:\d{8}_\d{6}_(?:NF|EF|PF|NR|ER)|FILE\d{6}-\d{6}[FR]?)$", RegexOptions.IgnoreCase)]
    private static partial Regex DashcamName();

    [GeneratedRegex(@"(?:screen ?recording|bildschirmaufnahme|screencast|screen[ _-]?capture|bildschirmvideo|enregistrement de l.écran)", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenRecordingName();

    [GeneratedRegex(@"(?:^|\s)\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}$|\d{4}\.\d{2}\.\d{2} - \d{2}\.\d{2}\.\d{2}(?:\.\d{2})?(?:\.DVR)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GameRecordingName();

    [GeneratedRegex(@"^(?:VID-\d{8}-WA\d{4}|signal-\d{4}-\d{2}-\d{2}-\d{6}|telegram-cloud-|video_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex MessengerName();

    [GeneratedRegex(@"\[[A-Za-z0-9_-]{11}\]$")]
    private static partial Regex DownloadName();

    [GeneratedRegex(@"[(\[]?\b(?:official\s+(?:music\s+)?video|official\s+clip|music\s+video|lyric\s+video|offizielles\s+musikvideo)\b[)\]]?", RegexOptions.IgnoreCase)]
    private static partial Regex MusicVideoName();

    [GeneratedRegex(@"iphone\s*\d|ipad", RegexOptions.IgnoreCase)]
    private static partial Regex IPhoneModel();

    [GeneratedRegex(@"\bSM-[A-Z]\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex SamsungModel();
}
