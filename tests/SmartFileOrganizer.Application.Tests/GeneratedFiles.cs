using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Builds small, synthetic test files byte by byte: PDFs, a JPEG with EXIF, an MP3 with ID3 tags,
/// an MP4 header, ZIP and Office packages. Nothing is copied from real files.
/// </summary>
internal static class GeneratedFiles
{
    // ─── PDF ──────────────────────────────────────────────────────────────────

    /// <summary>A PDF with one page per text, each page showing its text in Helvetica.</summary>
    public static byte[] PdfWithText(params string[] pageTexts)
    {
        var pageCount = pageTexts.Length;
        var fontObj = 3 + pageCount * 2;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pageCount).Select(i => $"{3 + i * 2} 0 R"))}] /Count {pageCount} >>"
        };
        foreach (var text in pageTexts)
        {
            var contentObj = objects.Count + 2;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Contents {contentObj} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>");
            var stream = $"BT /F1 14 Tf 20 350 Td ({text}) Tj ET";
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        return Pdf(objects);
    }

    /// <summary>A PDF whose pages hold only a filled rectangle: no text, like a scan.</summary>
    public static byte[] PdfImageOnly(int pageCount)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pageCount).Select(i => $"{3 + i * 2} 0 R"))}] /Count {pageCount} >>"
        };
        for (var i = 0; i < pageCount; i++)
        {
            var contentObj = objects.Count + 2;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Contents {contentObj} 0 R >>");
            var stream = $"0 0 1 rg {20 + i} 20 100 100 re f";
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        }
        return Pdf(objects);
    }

    private static byte[] Pdf(IReadOnlyList<string> objects)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ─── JPEG with EXIF ───────────────────────────────────────────────────────

    private sealed record IfdEntry(ushort Tag, ushort Type, uint Count, byte[] Value);

    private const ushort Ascii = 2, Long = 4, Rational = 5;

    private static IfdEntry Text(ushort tag, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value + "\0");
        return new IfdEntry(tag, Ascii, (uint)bytes.Length, bytes);
    }

    private static IfdEntry Pointer(ushort tag, int offset)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)offset);
        return new IfdEntry(tag, Long, 1, bytes);
    }

    private static IfdEntry Rationals(ushort tag, params (uint Num, uint Den)[] values)
    {
        var bytes = new byte[values.Length * 8];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 8), values[i].Num);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 8 + 4), values[i].Den);
        }
        return new IfdEntry(tag, Rational, (uint)values.Length, bytes);
    }

    /// <summary>One little-endian IFD at <paramref name="offset"/>, followed by its out-of-line values.</summary>
    private static byte[] Ifd(IReadOnlyList<IfdEntry> entries, int offset)
    {
        var head = new MemoryStream();
        var data = new MemoryStream();
        var dataStart = offset + 2 + 12 * entries.Count + 4;
        var buf = new byte[4];

        void U16(Stream s, int v) { BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)v); s.Write(buf, 0, 2); }
        void U32(Stream s, long v) { BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)v); s.Write(buf, 0, 4); }

        U16(head, entries.Count);
        foreach (var e in entries.OrderBy(e => e.Tag))
        {
            U16(head, e.Tag);
            U16(head, e.Type);
            U32(head, e.Count);
            if (e.Value.Length <= 4)
            {
                head.Write(e.Value);
                for (var i = e.Value.Length; i < 4; i++) head.WriteByte(0);
            }
            else
            {
                U32(head, dataStart + data.Length);
                data.Write(e.Value);
                if (data.Length % 2 == 1) data.WriteByte(0);
            }
        }
        U32(head, 0);
        head.Write(data.ToArray());
        return head.ToArray();
    }

    /// <summary>
    /// A JPEG header with EXIF (make, model, capture time, optional GPS) and a SOF0 frame of the
    /// given size. No scan data: metadata readers stop before it; nothing decodes the pixels here.
    /// </summary>
    public static byte[] JpegWithExif(DateTime takenAt, string make, string model, bool withGps, int width = 640, int height = 480)
    {
        // Layout inside the TIFF block: header (8) | IFD0 | Exif IFD | GPS IFD
        List<IfdEntry> Ifd0(int exifAt, int gpsAt)
        {
            var list = new List<IfdEntry> { Text(0x010F, make), Text(0x0110, model), Pointer(0x8769, exifAt) };
            if (withGps) list.Add(Pointer(0x8825, gpsAt));
            return list;
        }
        var exifEntries = new List<IfdEntry> { Text(0x9003, takenAt.ToString("yyyy:MM:dd HH:mm:ss")) };
        var gpsEntries = new List<IfdEntry> { Text(0x0001, "N"), Rationals(0x0002, (52, 1), (31, 1), (0, 1)) };

        var ifd0Length = Ifd(Ifd0(0, 0), 8).Length;
        var exifAt = 8 + ifd0Length;
        var exifLength = Ifd(exifEntries, exifAt).Length;
        var gpsAt = exifAt + exifLength;

        var tiff = new MemoryStream();
        tiff.Write("II"u8);
        tiff.Write([0x2A, 0x00, 0x08, 0x00, 0x00, 0x00]);
        tiff.Write(Ifd(Ifd0(exifAt, gpsAt), 8));
        tiff.Write(Ifd(exifEntries, exifAt));
        if (withGps) tiff.Write(Ifd(gpsEntries, gpsAt));

        var app1 = new MemoryStream();
        app1.Write("Exif\0\0"u8);
        app1.Write(tiff.ToArray());

        var jpeg = new MemoryStream();
        jpeg.Write([0xFF, 0xD8]);
        WriteSegment(jpeg, 0xE1, app1.ToArray());
        WriteSegment(jpeg, 0xC0, [8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0]);
        jpeg.Write([0xFF, 0xD9]);
        return jpeg.ToArray();
    }

    private static void WriteSegment(Stream s, byte marker, byte[] payload)
    {
        s.WriteByte(0xFF);
        s.WriteByte(marker);
        var length = payload.Length + 2;
        s.WriteByte((byte)(length >> 8));
        s.WriteByte((byte)length);
        s.Write(payload);
    }

    // ─── MP3 with ID3v2.3 ─────────────────────────────────────────────────────

    /// <summary>
    /// ID3v2.3 tag with title, artist and album, followed by silent MPEG-1 Layer III frames
    /// (128 kbit/s, 44.1 kHz, 1152 samples each).
    /// </summary>
    public static byte[] Mp3WithTags(string title, string artist, string album, int frames = 200)
    {
        var tagFrames = new MemoryStream();
        foreach (var (id, value) in new[] { ("TIT2", title), ("TPE1", artist), ("TALB", album) })
        {
            var text = Encoding.Latin1.GetBytes(value);
            tagFrames.Write(Encoding.ASCII.GetBytes(id));
            var size = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(size, text.Length + 1);
            tagFrames.Write(size);
            tagFrames.Write([0, 0, 0]); // flags (2) + encoding ISO-8859-1
            tagFrames.Write(text);
        }

        var body = tagFrames.ToArray();
        var mp3 = new MemoryStream();
        mp3.Write("ID3"u8);
        mp3.Write([3, 0, 0]);
        var n = body.Length; // syncsafe size
        mp3.Write([(byte)((n >> 21) & 0x7F), (byte)((n >> 14) & 0x7F), (byte)((n >> 7) & 0x7F), (byte)(n & 0x7F)]);
        mp3.Write(body);

        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        for (var i = 0; i < frames; i++) mp3.Write(frame);
        return mp3.ToArray();
    }

    // ─── MP4 header ───────────────────────────────────────────────────────────

    /// <summary>
    /// An MP4 with ftyp and a moov box (movie header and one video track header): duration,
    /// creation time and frame size, no media data.
    /// </summary>
    public static byte[] Mp4Header(DateTime createdUtc, int durationSeconds, int width, int height)
    {
        const int timescale = 1000;
        var secondsSince1904 = (uint)(createdUtc - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

        var mvhd = new MemoryStream();
        WriteU32(mvhd, 0);                              // version + flags
        WriteU32(mvhd, secondsSince1904);               // creation
        WriteU32(mvhd, secondsSince1904);               // modification
        WriteU32(mvhd, timescale);
        WriteU32(mvhd, (uint)(durationSeconds * timescale));
        WriteU32(mvhd, 0x00010000);                     // rate 1.0
        mvhd.Write([0x01, 0x00]);                       // volume 1.0
        mvhd.Write(new byte[10]);                       // reserved
        WriteMatrix(mvhd);
        mvhd.Write(new byte[24]);                       // pre-defined
        WriteU32(mvhd, 2);                              // next track id

        var tkhd = new MemoryStream();
        WriteU32(tkhd, 0x00000003);                     // version 0, flags enabled|in movie
        WriteU32(tkhd, secondsSince1904);
        WriteU32(tkhd, secondsSince1904);
        WriteU32(tkhd, 1);                              // track id
        WriteU32(tkhd, 0);                              // reserved
        WriteU32(tkhd, (uint)(durationSeconds * timescale));
        tkhd.Write(new byte[8]);                        // reserved
        tkhd.Write([0, 0, 0, 0, 0, 0, 0, 0]);           // layer, alternate group, volume, reserved
        WriteMatrix(tkhd);
        WriteU32(tkhd, (uint)width << 16);
        WriteU32(tkhd, (uint)height << 16);

        var trak = Box("trak", Box("tkhd", tkhd.ToArray()));
        var moov = Box("moov", [.. Box("mvhd", mvhd.ToArray()), .. trak]);

        var ftyp = new MemoryStream();
        ftyp.Write("isom"u8);
        WriteU32(ftyp, 0x200);
        ftyp.Write("isomiso2mp41"u8);

        return [.. Box("ftyp", ftyp.ToArray()), .. moov];
    }

    private static void WriteMatrix(Stream s)
    {
        foreach (var v in new uint[] { 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000 })
            WriteU32(s, v);
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    // ─── ZIP and Office ──────────────────────────────────────────────────────

    /// <summary>A ZIP whose entries hold the given number of bytes each (zeros, stored compressed).</summary>
    public static void Zip(string path, params (string Name, int Bytes)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            using var stream = entry.Open();
            stream.Write(new byte[bytes]);
        }
    }

    /// <summary>A .docx with one paragraph and core properties (title, creator, created).</summary>
    public static void Docx(string path, string title, string author, DateTime created, string paragraph)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(zip, "word/document.xml", $"""
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body><w:p><w:r><w:t>{paragraph}</w:t></w:r></w:p></w:body>
            </w:document>
            """);
        Write(zip, "docProps/core.xml", $"""
            <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/"
                xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <dc:title>{title}</dc:title>
              <dc:creator>{author}</dc:creator>
              <dcterms:created xsi:type="dcterms:W3CDTF">{created:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created>
            </cp:coreProperties>
            """);
        Write(zip, "docProps/app.xml", """
            <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Pages>2</Pages></Properties>
            """);
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
