using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// What a stored file fact says. Facts are read from the file itself (metadata, page count,
/// archive listing) or derived from them by a rule, never guessed by the model. The code in
/// <see cref="JsonStringEnumMemberNameAttribute"/> is the fixed value in <c>file_facts.fact</c>;
/// read and write it with <see cref="VerdictCodes"/>.
/// </summary>
public enum FileFactKey
{
    /// <summary>Format recognised from the first bytes (<c>jpeg</c>, <c>pdf</c>, <c>zip</c>, <c>pe</c>, ...).</summary>
    [JsonStringEnumMemberName("detected_format")] DetectedFormat,

    /// <summary>Width in pixels (image or video).</summary>
    [JsonStringEnumMemberName("width")] Width,

    /// <summary>Height in pixels (image or video).</summary>
    [JsonStringEnumMemberName("height")] Height,

    /// <summary>When the photo or video was taken, from the metadata (ISO 8601, local time as recorded).</summary>
    [JsonStringEnumMemberName("taken_at")] TakenAt,

    /// <summary>Camera or recording device (make and model) from the metadata.</summary>
    [JsonStringEnumMemberName("device")] Device,

    /// <summary><c>true</c> when the metadata contain a location. The coordinates are never stored.</summary>
    [JsonStringEnumMemberName("has_location")] HasLocation,

    /// <summary>Where an image comes from, as a <see cref="ImageOrigin"/> code.</summary>
    [JsonStringEnumMemberName("image_origin")] ImageOrigin,

    /// <summary>Number of pages of a document.</summary>
    [JsonStringEnumMemberName("page_count")] PageCount,

    /// <summary><c>true</c> when a PDF carries extractable text, <c>false</c> for an image-only (scanned) PDF.</summary>
    [JsonStringEnumMemberName("has_text")] HasText,

    /// <summary>Document or track title from the file properties or tags.</summary>
    [JsonStringEnumMemberName("title")] Title,

    /// <summary>Document author from the file properties.</summary>
    [JsonStringEnumMemberName("author")] Author,

    /// <summary>Creation date from the document properties (ISO 8601).</summary>
    [JsonStringEnumMemberName("created_at")] CreatedAt,

    /// <summary>Playing time in whole seconds (audio or video).</summary>
    [JsonStringEnumMemberName("duration_seconds")] DurationSeconds,

    /// <summary>Artist from the audio tags.</summary>
    [JsonStringEnumMemberName("artist")] Artist,

    /// <summary>Album from the audio tags.</summary>
    [JsonStringEnumMemberName("album")] Album,

    /// <summary>Speech recording or music, as an <see cref="AudioKind"/> code.</summary>
    [JsonStringEnumMemberName("audio_kind")] AudioKind,

    /// <summary>Number of file entries listed in an archive (directories not counted).</summary>
    [JsonStringEnumMemberName("entry_count")] EntryCount,

    /// <summary>Sum of the uncompressed sizes of the listed archive entries, in bytes.</summary>
    [JsonStringEnumMemberName("unpacked_bytes")] UnpackedBytes,

    /// <summary>What an archive mostly holds, as an <see cref="ArchiveContent"/> code.</summary>
    [JsonStringEnumMemberName("archive_content")] ArchiveContent,

    /// <summary><c>true</c> when an archive or document is password protected.</summary>
    [JsonStringEnumMemberName("encrypted")] Encrypted,

    // ── Kind and origin of a video (https://github.com/crack666/smart_file_organizer/issues/103) ──
    // Read from the container, the name and the files next to it; the model decides the kind only where these do not.
    // Each carries its source (metadata, name, companion, model) and a certainty. Not a description: a video with only
    // these facts still gets described.

    /// <summary>What kind of video a file is, as a <see cref="Enums.VideoKind"/> code.</summary>
    [JsonStringEnumMemberName("video_kind")] VideoKind,

    /// <summary>What kind of device recorded an own recording, as a <see cref="Enums.RecordingDevice"/> code.</summary>
    [JsonStringEnumMemberName("recording_device")] RecordingDevice,

    /// <summary>Title of a film, a series or a music video, from the name, a companion file or the container (free text).</summary>
    [JsonStringEnumMemberName("video_title")] VideoTitle,

    /// <summary>Release year of a film or series, from the name or a companion file (four digits).</summary>
    [JsonStringEnumMemberName("release_year")] ReleaseYear,

    /// <summary>Season number of a series episode.</summary>
    [JsonStringEnumMemberName("season_number")] SeasonNumber,

    /// <summary>Episode number of a series episode (the first one for a double episode).</summary>
    [JsonStringEnumMemberName("episode_number")] EpisodeNumber,

    /// <summary>Program that recorded, encoded or wrote the container, from the metadata (<c>OBS 30.1</c>, <c>GoPro AVC</c>).</summary>
    [JsonStringEnumMemberName("encoder")] Encoder,

    /// <summary>Frames per second of the video track, rounded to two decimals.</summary>
    [JsonStringEnumMemberName("frame_rate")] FrameRate,

    /// <summary>Average bitrate of the whole file in kbit/s (size over duration).</summary>
    [JsonStringEnumMemberName("bitrate_kbps")] BitrateKbps,

    /// <summary>Languages of the audio tracks, as written in the container, comma separated (<c>de,en</c>).</summary>
    [JsonStringEnumMemberName("audio_languages")] AudioLanguages,

    /// <summary>Languages of the subtitle tracks and of subtitle files next to the video, comma separated.</summary>
    [JsonStringEnumMemberName("subtitle_languages")] SubtitleLanguages,

    /// <summary>Number of chapters in the container.</summary>
    [JsonStringEnumMemberName("chapter_count")] ChapterCount,

    /// <summary>The kind the losing side named when the model and a sure fact disagree: the sure fact stays in <c>video_kind</c>.</summary>
    [JsonStringEnumMemberName("video_kind_disputed")] VideoKindDisputed,

    /// <summary>
    /// Why the rules read the kind as they did, one sentence with the deciding field and value (<c>make and model 'Example
    /// X1' in the container</c>), so that a wrong conclusion can be traced (free text, at most 200 characters).
    /// </summary>
    [JsonStringEnumMemberName("video_kind_reason")] VideoKindReason,

    // ── Description by the model (stage 1, https://github.com/crack666/smart_file_organizer/issues/37) ──
    // Asked in the same call as the category, stored with source model, never for a marked file.

    /// <summary>What a picture shows, in a few words (free text, at most 40 characters).</summary>
    [JsonStringEnumMemberName("scene")] Scene,

    /// <summary>What the people in a picture do, in a few words (free text, at most 40 characters).</summary>
    [JsonStringEnumMemberName("activity")] Activity,

    /// <summary>Indoors or outdoors, as a <see cref="SceneSetting"/> code.</summary>
    [JsonStringEnumMemberName("setting")] Setting,

    /// <summary>The season a picture shows, as a <see cref="Enums.Season"/> code; only when the picture shows it.</summary>
    [JsonStringEnumMemberName("season")] Season,

    /// <summary>The kind of occasion of a picture, as an <see cref="OccasionKind"/> code.</summary>
    [JsonStringEnumMemberName("occasion")] Occasion,

    /// <summary><c>true</c> when a person is visible in a picture.</summary>
    [JsonStringEnumMemberName("people")] People,

    /// <summary>The kind of a document, as a <see cref="Enums.DocumentKind"/> code.</summary>
    [JsonStringEnumMemberName("document_kind")] DocumentKind,

    /// <summary>Who issued a document: company, authority or person, as written on it (free text, at most 60 characters).</summary>
    [JsonStringEnumMemberName("issuer")] Issuer,

    /// <summary>The year a document is dated, as read from its content (four digits).</summary>
    [JsonStringEnumMemberName("year")] Year
}

/// <summary>Where a stored fact comes from.</summary>
public enum FactSource
{
    /// <summary>Read directly from the file: metadata, tags, properties, page count, archive listing, first bytes.</summary>
    [JsonStringEnumMemberName("metadata")] Metadata,

    /// <summary>Derived by a deterministic rule from metadata, name and path.</summary>
    [JsonStringEnumMemberName("rule")] Rule,

    /// <summary>Reported by the local model.</summary>
    [JsonStringEnumMemberName("model")] Model,

    /// <summary>Read from the file name or its folder: a camera or program naming scheme, a release name, season and episode.</summary>
    [JsonStringEnumMemberName("name")] Name,

    /// <summary>Read from a file next to it: an info file, subtitles, a preview or side file of a camera.</summary>
    [JsonStringEnumMemberName("companion")] Companion
}

/// <summary>What kind of video a file is (https://github.com/crack666/smart_file_organizer/issues/103).</summary>
public enum VideoKind
{
    /// <summary>A feature film: a release name with title and year, or an info file of a media library.</summary>
    [JsonStringEnumMemberName("movie")] Movie,

    /// <summary>An episode of a series: season and episode in the name, or an info file of an episode.</summary>
    [JsonStringEnumMemberName("series_episode")] SeriesEpisode,

    /// <summary>Recorded by the person with a camera, phone, action cam, drone or 360-degree camera.</summary>
    [JsonStringEnumMemberName("own_recording")] OwnRecording,

    /// <summary>A recording of a screen or a game, by a recording program.</summary>
    [JsonStringEnumMemberName("screen_recording")] ScreenRecording,

    /// <summary>A clip downloaded from a website, a video platform or a social network.</summary>
    [JsonStringEnumMemberName("web_clip")] WebClip,

    /// <summary>The video of a piece of music.</summary>
    [JsonStringEnumMemberName("music_video")] MusicVideo,

    /// <summary>
    /// A clip received or forwarded through a messenger, by the naming scheme of the messenger: neither an own camera file
    /// nor a download. Only the name tells this, never the frames, so the model is not asked for it.
    /// </summary>
    [JsonStringEnumMemberName("received")] Received,

    /// <summary>Neither the facts nor the model could tell.</summary>
    [JsonStringEnumMemberName("unknown")] Unknown
}

/// <summary>What kind of device recorded an own recording.</summary>
public enum RecordingDevice
{
    /// <summary>A mobile phone.</summary>
    [JsonStringEnumMemberName("phone")] Phone,

    /// <summary>An action cam (helmet or body camera).</summary>
    [JsonStringEnumMemberName("action_cam")] ActionCam,

    /// <summary>A 360-degree camera.</summary>
    [JsonStringEnumMemberName("camera_360")] Camera360,

    /// <summary>A drone.</summary>
    [JsonStringEnumMemberName("drone")] Drone,

    /// <summary>A photo or video camera, a camcorder.</summary>
    [JsonStringEnumMemberName("camera")] Camera,

    /// <summary>A dashboard camera of a car.</summary>
    [JsonStringEnumMemberName("dashcam")] Dashcam
}

/// <summary>Where an image comes from, decided from its metadata and name without the model.</summary>
public enum ImageOrigin
{
    /// <summary>Taken with a camera or phone: the metadata name a device or a capture time.</summary>
    [JsonStringEnumMemberName("camera")] Camera,

    /// <summary>Screen capture: no camera metadata and a screenshot name or a screen-sized PNG.</summary>
    [JsonStringEnumMemberName("screenshot")] Screenshot,

    /// <summary>Scanned page: no camera metadata, scanner software or a print resolution of 200 dpi or more.</summary>
    [JsonStringEnumMemberName("scan")] Scan
}

/// <summary>Whether an audio file is a speech recording or music, decided from tags, format and path.</summary>
public enum AudioKind
{
    [JsonStringEnumMemberName("speech")] Speech,
    [JsonStringEnumMemberName("music")] Music
}

/// <summary>What an archive mostly holds, decided from its listing without unpacking it.</summary>
public enum ArchiveContent
{
    /// <summary>At least 60 % of the entries are images.</summary>
    [JsonStringEnumMemberName("photos")] Photos,

    /// <summary>At least 60 % are documents (PDF, Office, text).</summary>
    [JsonStringEnumMemberName("documents")] Documents,

    /// <summary>At least 60 % are audio or video files.</summary>
    [JsonStringEnumMemberName("media")] Media,

    /// <summary>Contains a setup program, an installer package, or programs with their libraries.</summary>
    [JsonStringEnumMemberName("software")] Software,

    /// <summary>At least 60 % are source code, or the listing has a project marker.</summary>
    [JsonStringEnumMemberName("code")] Code,

    /// <summary>None of the above dominates.</summary>
    [JsonStringEnumMemberName("mixed")] Mixed
}

/// <summary>Whether a picture was taken indoors or outdoors.</summary>
public enum SceneSetting
{
    [JsonStringEnumMemberName("indoor")] Indoor,
    [JsonStringEnumMemberName("outdoor")] Outdoor
}

/// <summary>The season a picture shows (snow, blossom, foliage, summer light); never guessed from a date.</summary>
public enum Season
{
    [JsonStringEnumMemberName("spring")] Spring,
    [JsonStringEnumMemberName("summer")] Summer,
    [JsonStringEnumMemberName("autumn")] Autumn,
    [JsonStringEnumMemberName("winter")] Winter
}

/// <summary>The kind of occasion a picture belongs to, as far as the picture shows it.</summary>
public enum OccasionKind
{
    /// <summary>A journey or holiday away from home.</summary>
    [JsonStringEnumMemberName("holiday")] Holiday,

    /// <summary>A birthday, wedding, party, family feast or festival.</summary>
    [JsonStringEnumMemberName("celebration")] Celebration,

    /// <summary>A day trip or excursion: a hike, a zoo, a city walk.</summary>
    [JsonStringEnumMemberName("outing")] Outing,

    /// <summary>Doing or watching sport.</summary>
    [JsonStringEnumMemberName("sport")] Sport,

    /// <summary>Everyday life at home or around it.</summary>
    [JsonStringEnumMemberName("everyday")] Everyday,

    /// <summary>Work, school or study.</summary>
    [JsonStringEnumMemberName("work")] Work,

    /// <summary>No occasion: a product photo, a picture of a thing, a meme, a picture of a document.</summary>
    [JsonStringEnumMemberName("none")] None
}

/// <summary>The kind of a document, finer than its category, read from its content.</summary>
public enum DocumentKind
{
    [JsonStringEnumMemberName("invoice")] Invoice,
    [JsonStringEnumMemberName("receipt")] Receipt,
    [JsonStringEnumMemberName("contract")] Contract,
    [JsonStringEnumMemberName("statement")] Statement,
    [JsonStringEnumMemberName("tax")] Tax,
    [JsonStringEnumMemberName("payslip")] Payslip,
    [JsonStringEnumMemberName("certificate")] Certificate,
    [JsonStringEnumMemberName("insurance")] Insurance,
    [JsonStringEnumMemberName("medical")] Medical,
    [JsonStringEnumMemberName("official")] Official,
    [JsonStringEnumMemberName("correspondence")] Correspondence,
    [JsonStringEnumMemberName("manual")] Manual,
    [JsonStringEnumMemberName("form")] Form,
    [JsonStringEnumMemberName("notes")] Notes,
    [JsonStringEnumMemberName("other")] Other
}

public static class FileFactKeys
{
    /// <summary>
    /// Facts that carry free text (title, author, artist, album; the scene, activity and issuer the model
    /// describes). For a file marked sensitive only the other facts are stored, so that the marker stays the
    /// only statement about its content.
    /// </summary>
    public static bool IsFreeText(this FileFactKey key) =>
        key is FileFactKey.Title or FileFactKey.Author or FileFactKey.Artist or FileFactKey.Album
            or FileFactKey.Scene or FileFactKey.Activity or FileFactKey.Issuer or FileFactKey.VideoTitle or FileFactKey.VideoKindReason;

    /// <summary>Facts the model describes in stage 1 (scene, occasion, document kind, ...), always with source model.</summary>
    public static bool IsDescription(this FileFactKey key) => key >= FileFactKey.Scene;

    /// <summary>
    /// The facts about kind and origin of a video (Issue 103), read again by <c>sfo facts refresh</c> without the model; a
    /// refresh replaces exactly these and keeps every other fact. <c>video_kind</c> and <c>video_kind_disputed</c> from the
    /// model are kept by a refresh as long as the metadata still leave the kind open.
    /// </summary>
    public static bool IsVideoOrigin(this FileFactKey key) => key is >= FileFactKey.VideoKind and <= FileFactKey.VideoKindReason;
}
