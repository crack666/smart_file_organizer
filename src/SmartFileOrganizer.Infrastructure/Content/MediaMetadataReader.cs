using System.Globalization;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using SmartFileOrganizer.Domain.Enums;
using MetaDirectory = MetadataExtractor.Directory;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>What the metadata of an image or video say, as far as this tool uses them.</summary>
public sealed class MediaMetadata
{
    public int? Width { get; set; }
    public int? Height { get; set; }
    public DateTime? TakenAt { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public string? Software { get; set; }
    public bool HasLocation { get; set; }
    public double? DurationSeconds { get; set; }
    public double? DpiX { get; set; }

    public string? Device =>
        Make is null ? Model
        : Model is null ? Make
        : Model.StartsWith(Make, StringComparison.OrdinalIgnoreCase) ? Model
        : $"{Make} {Model}";
}

/// <summary>
/// Reads image and video metadata (EXIF, PNG, QuickTime/MP4, AVI, ...) through MetadataExtractor
/// without decoding pixels. Location data are reduced to "present": coordinates never leave this class.
/// </summary>
public static partial class MediaMetadataReader
{
    public static MediaMetadata Read(string path)
    {
        var directories = ImageMetadataReader.ReadMetadata(path);
        var meta = new MediaMetadata();

        foreach (var dir in directories)
        {
            if (dir is GpsDirectory gps)
            {
                if (gps.ContainsTag(GpsDirectory.TagLatitude) || gps.ContainsTag(GpsDirectory.TagLongitude))
                    meta.HasLocation = true;
                continue;
            }

            if (dir is ExifDirectoryBase exif)
            {
                meta.Make ??= Clean(exif.GetString(ExifDirectoryBase.TagMake));
                meta.Model ??= Clean(exif.GetString(ExifDirectoryBase.TagModel));
                meta.Software ??= Clean(exif.GetString(ExifDirectoryBase.TagSoftware));
                if (meta.TakenAt is null && exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var original))
                    meta.TakenAt = original;
                if (meta.DpiX is null && exif.TryGetDouble(ExifDirectoryBase.TagXResolution, out var dpi) && dpi > 0)
                {
                    // Resolution unit 3 = centimetres
                    meta.DpiX = exif.TryGetInt32(ExifDirectoryBase.TagResolutionUnit, out var unit) && unit == 3 ? dpi * 2.54 : dpi;
                }
            }

            foreach (var tag in dir.Tags)
                ReadGenericTag(dir, tag, meta);
        }

        return meta;
    }

    /// <summary>Tags that differ in id between formats but share a name (QuickTime, MP4, AVI, PNG, JPEG).</summary>
    private static void ReadGenericTag(MetaDirectory dir, Tag tag, MediaMetadata meta)
    {
        var name = tag.Name;

        if (name.Equals("Image Width", StringComparison.OrdinalIgnoreCase) || name.Equals("Width", StringComparison.OrdinalIgnoreCase))
        {
            if (dir.TryGetInt32(tag.Type, out var w) && w > 0 && w > (meta.Width ?? 0)) meta.Width = w;
            return;
        }
        if (name.Equals("Image Height", StringComparison.OrdinalIgnoreCase) || name.Equals("Height", StringComparison.OrdinalIgnoreCase))
        {
            if (dir.TryGetInt32(tag.Type, out var h) && h > 0 && h > (meta.Height ?? 0)) meta.Height = h;
            return;
        }

        if (name.Equals("Duration in Seconds", StringComparison.OrdinalIgnoreCase))
        {
            if (dir.TryGetDouble(tag.Type, out var secs) && secs > 0) meta.DurationSeconds = secs;
            return;
        }
        if (name.Equals("Duration", StringComparison.OrdinalIgnoreCase) && meta.DurationSeconds is null)
        {
            var value = dir.GetObject(tag.Type);
            if (value is TimeSpan span && span > TimeSpan.Zero) meta.DurationSeconds = span.TotalSeconds;
            else if (value is string s && TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var parsed) && parsed > TimeSpan.Zero)
                meta.DurationSeconds = parsed.TotalSeconds;
            return;
        }

        if (meta.TakenAt is null && IsCaptureDateTag(name) && dir.TryGetDateTime(tag.Type, out var date) && date.Year > 1970)
        {
            meta.TakenAt = date;
            return;
        }

        if (name.Equals("Make", StringComparison.OrdinalIgnoreCase)) meta.Make ??= Clean(dir.GetString(tag.Type));
        else if (name.Equals("Model", StringComparison.OrdinalIgnoreCase)) meta.Model ??= Clean(dir.GetString(tag.Type));
        else if (name.Equals("Software", StringComparison.OrdinalIgnoreCase)) meta.Software ??= Clean(dir.GetString(tag.Type));
        else if (name.Contains("GPS", StringComparison.OrdinalIgnoreCase) || name.Contains("Location", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(dir.GetString(tag.Type))) meta.HasLocation = true;
        }
    }

    private static bool IsCaptureDateTag(string name) =>
        name.Equals("Date/Time Original", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Datetime Original", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Creation Date", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Creation Time", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Created", StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().TrimEnd('\0').Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static readonly string[] ScannerWords = ["scan", "mfp", "perfection"];

    private static readonly (int W, int H)[] ScreenSizes =
    [
        (640, 480), (800, 600), (1024, 768), (1152, 864), (1280, 720), (1280, 768), (1280, 800), (1280, 1024),
        (1360, 768), (1366, 768), (1440, 900), (1536, 864), (1600, 900), (1600, 1200), (1680, 1050),
        (1920, 1080), (1920, 1200), (2048, 1152), (2560, 1080), (2560, 1440), (2560, 1600), (2880, 1800),
        (3440, 1440), (3840, 2160), (5120, 1440), (5120, 2880)
    ];

    [GeneratedRegex(@"(screen ?shot|screenshot|bildschirmfoto|bildschirmaufnahme|capture|snip|clip_image|^scr[_-]?\d)", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenshotName();

    [GeneratedRegex(@"(^|[^a-z])(scan|scanned|eingescannt|gescannt)", RegexOptions.IgnoreCase)]
    private static partial Regex ScanName();

    /// <summary>Names a messenger gives a picture it received or sent: WhatsApp, Telegram, Signal.</summary>
    [GeneratedRegex(@"^(IMG-\d{8}-WA\d{3,}|WhatsApp Image \d{4}-\d{2}-\d{2} at |photo_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}|signal-\d{4}-\d{2}-\d{2}-\d{4,})", RegexOptions.IgnoreCase)]
    private static partial Regex MessengerName();

    /// <summary>
    /// Names of saved web pictures: a social network's download (<c>FB_IMG_…</c>, <c>123_456_789_n</c>), a browser's
    /// default name for an image without one (<c>images (3)</c>, <c>unnamed</c>, <c>download</c>), a hash as name, a stock
    /// photo site's prefix.
    /// </summary>
    [GeneratedRegex(@"^(FB_IMG_\d{10,}|\d{6,}_\d{6,}_\d{6,}_[nobs]|(images|unnamed|download)( ?\(\d+\))?|[0-9a-f]{32}|(pexels|unsplash|pixabay|pinterest|shutterstock|istock|adobestock|depositphotos|freepik)[-_ ].*)$", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadName();

    /// <summary>
    /// Decides where an image comes from, without the model, and how sure the rule is
    /// (https://github.com/crack666/smart_file_organizer/issues/103). Order: scanner names in the metadata (certain), camera
    /// make or model (certain), a messenger's naming scheme (certain), a capture time alone (camera, likely), a screenshot
    /// name or a screen-sized PNG (likely), a download name (likely), a scan name (likely) or a print resolution
    /// (uncertain), and last a download folder on the way to the file (download, uncertain). Null when nothing tells.
    /// A messenger name wins over a capture time alone: messengers keep the date but strip make and model.
    /// </summary>
    public static (ImageOrigin Origin, ResultCertainty Certainty)? DecideOrigin(MediaMetadata meta, string fileName, string extension, string relativeDir = "")
    {
        var deviceText = $"{meta.Make} {meta.Model} {meta.Software}";
        if (ScannerWords.Any(w => deviceText.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return (ImageOrigin.Scan, ResultCertainty.Certain);

        if (meta.Make is not null || meta.Model is not null)
            return (ImageOrigin.Camera, ResultCertainty.Certain);

        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (MessengerName().IsMatch(stem))
            return (ImageOrigin.Received, ResultCertainty.Certain);

        if (meta.TakenAt is not null)
            return (ImageOrigin.Camera, ResultCertainty.Likely);

        if (ScreenshotName().IsMatch(fileName))
            return (ImageOrigin.Screenshot, ResultCertainty.Likely);

        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) && meta.Width is { } w && meta.Height is { } h
            && ScreenSizes.Any(s => (s.W == w && s.H == h) || (s.W == h && s.H == w)))
            return (ImageOrigin.Screenshot, ResultCertainty.Likely);

        if (DownloadName().IsMatch(stem))
            return (ImageOrigin.Download, ResultCertainty.Likely);

        if (ScanName().IsMatch(fileName))
            return (ImageOrigin.Scan, ResultCertainty.Likely);
        if (meta.DpiX >= 200)
            return (ImageOrigin.Scan, ResultCertainty.Uncertain);

        if (SmartFileOrganizer.Application.Media.VideoKindRules.InDownloadFolder(relativeDir))
            return (ImageOrigin.Download, ResultCertainty.Uncertain);

        return null;
    }
}
