namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>What the preview shows for a file (https://github.com/crack666/smart_file_organizer/issues/109).</summary>
public enum PreviewKind
{
    /// <summary>Nothing: no reader for the format; the preview says so and points to "Öffnen".</summary>
    None,

    /// <summary>The image itself, decoded by the GUI (Skia): JPEG, PNG, GIF (first frame), BMP, WebP, ICO.</summary>
    Image,

    /// <summary>An image format the GUI decoder cannot draw (TIFF, HEIC, camera raw): a hint instead of an empty area.</summary>
    ImageNotDecoded,

    /// <summary>Page 1 rendered through PDFium.</summary>
    Pdf,

    /// <summary>Text from the XML of an Office Open XML or OpenDocument package.</summary>
    OfficeText,

    /// <summary>Text of a Word 97-2003 document from its piece table (<see cref="LegacyWordReader"/>).</summary>
    LegacyWord,

    /// <summary>A legacy binary Office file other than Word (<c>.xls</c>, <c>.ppt</c>): a hint.</summary>
    LegacyOffice,

    /// <summary>The text of an RTF file without its control words (<see cref="RtfText"/>).</summary>
    Rtf,

    /// <summary>The beginning of a text file.</summary>
    Text,

    /// <summary>The names in the table of contents of an archive, without unpacking (<see cref="ArchiveListingReader"/>).</summary>
    Archive,

    /// <summary>A compressed stream (<c>.gz</c>, <c>.bz2</c>, <c>.xz</c>, <c>.tar.gz</c>) whose contents exist only after unpacking.</summary>
    CompressedStream,

    /// <summary>A strip of frames through the optional <c>ffmpeg</c>, plus "Abspielen".</summary>
    Video,

    /// <summary>No picture; "Abspielen" and the tags.</summary>
    Audio
}

/// <summary>
/// Decides the preview of a file from the format its first bytes show (<see cref="FileSignature"/>, the same code that
/// stores <c>detected_format</c>) and, where those do not decide, from a complete list of extensions. Until 16.11.2026
/// the preview went by the scan type of the extension only: <c>.jfif</c> was no image type, so it got no picture, and
/// <c>.doc</c>, <c>.rtf</c>, <c>.odp</c> and archives had no reader, so they got the generic "no preview" line.
/// </summary>
public static class PreviewFormats
{
    /// <summary>Formats (codes of <see cref="FileSignature"/>) the GUI decoder draws; checked by a test against Skia.</summary>
    private static readonly HashSet<string> DecodableImageFormats = ["jpeg", "png", "gif", "bmp", "webp"];

    private static readonly HashSet<string> ImageExtensions = Set(
        ".jpg", ".jpeg", ".jpe", ".jfif", ".pjpeg", ".pjp", ".png", ".apng", ".gif", ".bmp", ".dib", ".webp", ".ico");

    private static readonly HashSet<string> NotDecodedImageExtensions = Set(
        ".tif", ".tiff", ".heic", ".heif", ".avif", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".raf", ".psd", ".jxl");

    private static readonly HashSet<string> OfficeTextExtensions = Set(".docx", ".docm", ".xlsx", ".xlsm", ".pptx", ".pptm", ".odt", ".ods", ".odp");

    private static readonly HashSet<string> LegacyOfficeExtensions = Set(".xls", ".ppt", ".pps", ".pot", ".xlt", ".msg");

    /// <summary>Extensions read as text; files with another extension are read as text when their first bytes are text.</summary>
    public static readonly IReadOnlySet<string> TextExtensions = Set(
        ".txt", ".text", ".md", ".markdown", ".rst", ".nfo", ".diz", ".log", ".csv", ".tsv", ".json", ".jsonl", ".xml",
        ".ini", ".cfg", ".conf", ".config", ".properties", ".toml", ".env", ".yml", ".yaml", ".sql", ".srt", ".vtt", ".ass",
        ".ssa", ".sub", ".m3u", ".m3u8", ".cue", ".svg", ".html", ".htm", ".css", ".cs", ".js", ".ts", ".tsx", ".jsx", ".py",
        ".java", ".c", ".h", ".cpp", ".hpp", ".go", ".rs", ".rb", ".php", ".kt", ".swift", ".ps1", ".psm1", ".sh", ".bash",
        ".bat", ".cmd", ".reg", ".vbs", ".tex", ".bib");

    private static readonly HashSet<string> ArchiveExtensions = Set(".zip", ".rar", ".7z", ".tar", ".cbz", ".cbr");
    private static readonly HashSet<string> CompressedExtensions = Set(".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz");

    private static readonly HashSet<string> VideoExtensions = Set(
        ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".mpg", ".mpeg", ".m2ts", ".mts", ".3gp", ".ogv", ".vob");

    private static readonly HashSet<string> AudioExtensions = Set(
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".oga", ".m4a", ".wma", ".opus", ".aiff", ".aif", ".mid", ".midi", ".amr");

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The preview of a file with extension <paramref name="extension"/> (with dot) and the format code its first bytes
    /// show (null when unknown). A decodable image format wins over the extension (a <c>.jfif</c>, a misnamed <c>.png</c>);
    /// a ZIP or OLE container goes by its extension (a <c>.docx</c> is a ZIP); an unknown extension goes by the format.
    /// </summary>
    public static PreviewKind Decide(string? extension, string? detectedFormat)
    {
        var ext = extension ?? string.Empty;
        if (detectedFormat is { } f && DecodableImageFormats.Contains(f)) return PreviewKind.Image;
        if (detectedFormat == "pdf") return PreviewKind.Pdf;
        if (detectedFormat == "rtf") return PreviewKind.Rtf;

        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return PreviewKind.Pdf;
        if (ImageExtensions.Contains(ext))
            // An image extension over bytes that are no image the decoder knows: say so instead of failing to decode.
            return detectedFormat is null || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) ? PreviewKind.Image : PreviewKind.ImageNotDecoded;
        if (NotDecodedImageExtensions.Contains(ext) || detectedFormat == "tiff") return PreviewKind.ImageNotDecoded;
        if (OfficeTextExtensions.Contains(ext)) return PreviewKind.OfficeText;
        if (ext.Equals(".doc", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dot", StringComparison.OrdinalIgnoreCase))
            return detectedFormat == "ole" ? PreviewKind.LegacyWord : detectedFormat == "text" ? PreviewKind.Text : PreviewKind.LegacyWord;
        if (ext.Equals(".rtf", StringComparison.OrdinalIgnoreCase)) return PreviewKind.Rtf;
        if (LegacyOfficeExtensions.Contains(ext)) return PreviewKind.LegacyOffice;
        if (VideoExtensions.Contains(ext)) return PreviewKind.Video;
        if (AudioExtensions.Contains(ext)) return PreviewKind.Audio;
        if (ArchiveExtensions.Contains(ext)) return PreviewKind.Archive;
        if (CompressedExtensions.Contains(ext)) return PreviewKind.CompressedStream;
        // .ts is TypeScript as text and an MPEG transport stream as binary.
        if (ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) && detectedFormat != "text") return PreviewKind.Video;
        if (TextExtensions.Contains(ext) && detectedFormat is null or "text") return PreviewKind.Text;

        return detectedFormat switch
        {
            "zip" or "rar" or "7z" or "tar" => PreviewKind.Archive,
            "gzip" or "bzip2" or "xz" => PreviewKind.CompressedStream,
            "mp4" or "avi" or "matroska" => PreviewKind.Video,
            "mp3" or "flac" or "ogg" or "wav" => PreviewKind.Audio,
            "ole" => PreviewKind.LegacyOffice,
            "text" => PreviewKind.Text,
            _ => PreviewKind.None
        };
    }

    /// <summary>The format code of the first bytes of <paramref name="path"/>, or null when unknown or unreadable.</summary>
    public static string? Sniff(string path)
    {
        try
        {
            return FileSignature.Detect(FileSignature.ReadPrefix(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
