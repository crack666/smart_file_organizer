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
    [JsonStringEnumMemberName("model")] Model
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
            or FileFactKey.Scene or FileFactKey.Activity or FileFactKey.Issuer;

    /// <summary>Facts the model describes in stage 1 (scene, occasion, document kind, ...), always with source model.</summary>
    public static bool IsDescription(this FileFactKey key) => key >= FileFactKey.Scene;
}
