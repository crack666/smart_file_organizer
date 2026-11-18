using System.Text;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>
/// Recognises common formats from the first bytes, independent of the extension, and tells
/// whether a file looks like text. Codes are lower-case English (<c>jpeg</c>, <c>pdf</c>, <c>pe</c>).
/// </summary>
public static class FileSignature
{
    public const int ProbeBytes = 4096;

    private static ReadOnlySpan<byte> Png => [0x89, 0x50, 0x4E, 0x47];
    private static ReadOnlySpan<byte> ZipLocal => [0x50, 0x4B, 0x03, 0x04];
    private static ReadOnlySpan<byte> ZipEmpty => [0x50, 0x4B, 0x05, 0x06];
    private static ReadOnlySpan<byte> SevenZip => [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static ReadOnlySpan<byte> Xz => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
    private static ReadOnlySpan<byte> Elf => [0x7F, 0x45, 0x4C, 0x46];

    public static byte[] ReadPrefix(string path, int count = ProbeBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
        var buffer = new byte[Math.Min(count, Math.Max(0, (int)Math.Min(int.MaxValue, stream.Length)))];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>The format code for the given first bytes, or null when none is recognised.</summary>
    public static string? Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4) return null;

        if ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)) return "text"; // UTF-16 BOM
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpeg";
        if (b.StartsWith(Png)) return "png";
        if (b.StartsWith("GIF8"u8)) return "gif";
        if (b.StartsWith("%PDF"u8)) return "pdf";
        if (b.StartsWith(ZipLocal) || b.StartsWith(ZipEmpty)) return "zip";
        if (b.StartsWith("Rar!"u8)) return "rar";
        if (b.StartsWith(SevenZip)) return "7z";
        if (b[0] == 0x1F && b[1] == 0x8B) return "gzip";
        if (b.StartsWith("BZh"u8)) return "bzip2";
        if (b.StartsWith(Xz)) return "xz";
        if (b.StartsWith("MZ"u8)) return "pe";
        if (b.StartsWith(Elf)) return "elf";
        if (b.StartsWith("SQLite format 3"u8)) return "sqlite";
        if (b.StartsWith("ID3"u8) || (b[0] == 0xFF && (b[1] & 0xE0) == 0xE0)) return "mp3";
        if (b.StartsWith("fLaC"u8)) return "flac";
        if (b.StartsWith("OggS"u8)) return "ogg";
        if (b.Length >= 12 && b.StartsWith("RIFF"u8))
        {
            var kind = b.Slice(8, 4);
            if (kind.SequenceEqual("WAVE"u8)) return "wav";
            if (kind.SequenceEqual("AVI "u8)) return "avi";
            if (kind.SequenceEqual("WEBP"u8)) return "webp";
            return "riff";
        }
        if (b.Length >= 12 && b.Slice(4, 4).SequenceEqual("ftyp"u8)) return IsoMediaFormat(b);
        if (b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3) return "matroska";
        if ((b[0] == 0x49 && b[1] == 0x49 && b[2] == 0x2A && b[3] == 0x00) ||
            (b[0] == 0x4D && b[1] == 0x4D && b[2] == 0x00 && b[3] == 0x2A)) return "tiff";
        if (b.StartsWith("BM"u8) && b.Length >= 14) return "bmp";
        if (b.Length >= 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0) return "ole";
        if (b.StartsWith("{\\rtf"u8)) return "rtf";
        if (b.StartsWith("OTTO"u8) || (b[0] == 0x00 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)) return "font";
        if (b.StartsWith("wOFF"u8) || b.StartsWith("wOF2"u8)) return "font";
        if (b.Length > 262 && b.Slice(257, 5).SequenceEqual("ustar"u8)) return "tar";
        if (LooksLikeText(b)) return "text";
        return null;
    }

    /// <summary>
    /// The format of an ISO media file (first box <c>ftyp</c>) by its brands: <c>heif</c> and <c>avif</c> are still
    /// images, <c>quicktime</c> a MOV, <c>m4a</c> audio; every other brand is <c>mp4</c>. Until 18.11.2026 all of
    /// them were <c>mp4</c> (https://github.com/crack666/smart_file_organizer/issues/111), and facts stored before
    /// still say so for a HEIC, so readers keep treating <c>mp4</c> as "the extension decides".
    /// </summary>
    private static string IsoMediaFormat(ReadOnlySpan<byte> b)
    {
        var major = b.Slice(8, 4);
        if (IsHeifBrand(major)) return "heif";
        if (major.SequenceEqual("avif"u8) || major.SequenceEqual("avis"u8)) return "avif";
        if (major.SequenceEqual("qt  "u8)) return "quicktime";
        if (major.SequenceEqual("M4A "u8) || major.SequenceEqual("M4B "u8) || major.SequenceEqual("M4P "u8)) return "m4a";

        // The generic image brands mif1 and msf1 name the codec only among the compatible brands (after the minor version).
        if (major.SequenceEqual("mif1"u8) || major.SequenceEqual("msf1"u8))
        {
            var boxSize = (int)Math.Min(b.Length, (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]));
            for (var i = 16; i + 4 <= boxSize; i += 4)
            {
                var brand = b.Slice(i, 4);
                if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return "avif";
            }
            return "heif";
        }
        return "mp4";
    }

    private static bool IsHeifBrand(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("heim"u8)
        || brand.SequenceEqual("heis"u8) || brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8)
        || brand.SequenceEqual("hevm"u8) || brand.SequenceEqual("hevs"u8);

    /// <summary>
    /// True when the bytes decode as text: no NUL byte (outside a UTF-16 BOM) and at most 5 %
    /// control characters other than tab and line breaks.
    /// </summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> b)
    {
        if (b.Length == 0) return false;
        if (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF))) return true;

        var control = 0;
        foreach (var c in b)
        {
            if (c == 0) return false;
            if (c < 0x20 && c is not (byte)'\t' and not (byte)'\n' and not (byte)'\r' and not 0x0C and not 0x1B)
                control++;
        }
        return control * 20 <= b.Length;
    }

    /// <summary>Decodes a text prefix (UTF-8 or UTF-16 with BOM) for an excerpt.</summary>
    public static string DecodeText(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
