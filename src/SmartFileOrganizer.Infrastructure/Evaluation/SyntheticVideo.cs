using System.Buffers.Binary;
using System.Text;

namespace SmartFileOrganizer.Infrastructure.Evaluation;

/// <summary>What a synthetic video container carries; everything optional.</summary>
public sealed record SyntheticVideoSpec
{
    public DateTime CreatedUtc { get; init; } = new(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    public int DurationSeconds { get; init; } = 30;
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public double FrameRate { get; init; } = 30;
    public string? Make { get; init; }
    public string? Model { get; init; }

    /// <summary>The recording or encoding program (<c>©too</c> in MP4, writing app in Matroska).</summary>
    public string? Software { get; init; }

    public string? Title { get; init; }

    /// <summary>Name of the video track (the handler name in MP4).</summary>
    public string? VideoTrackName { get; init; }

    public IReadOnlyList<string> AudioLanguages { get; init; } = [];
    public IReadOnlyList<string> SubtitleLanguages { get; init; } = [];
    public int Chapters { get; init; }
    public bool Location { get; init; }

    /// <summary>MP4: write make and model as phone metadata keys (<c>meta</c>/<c>keys</c>) instead of user data texts.</summary>
    public bool AsMetadataKeys { get; init; }

    /// <summary>Bytes of media data after the headers (zeros).</summary>
    public int MediaBytes { get; init; } = 2048;
}

/// <summary>
/// Writes small, structurally valid video containers with chosen metadata, for tests and the evaluation corpus
/// (https://github.com/crack666/smart_file_organizer/issues/103): MP4 with <c>moov</c> (movie and track headers, handler
/// names, languages, time-to-sample, user data texts, phone metadata keys, Nero chapters) and Matroska with segment info,
/// tracks, chapters and tags. The media data are zeros: the files play nowhere, but every header reader sees what a real
/// file of that kind carries.
/// </summary>
public static class SyntheticVideo
{
    // ================================================================== MP4

    public static byte[] Mp4(SyntheticVideoSpec spec)
    {
        const uint timescale = 1000;
        var created = Seconds1904(spec.CreatedUtc);
        var duration = (uint)(spec.DurationSeconds * timescale);
        var trackId = 1u;
        var tracks = new List<byte[]>
        {
            Trak(trackId++, "vide", spec.VideoTrackName ?? "VideoHandler", "und", created, duration, spec.Width, spec.Height,
                (uint)Math.Round(timescale / Math.Max(1, spec.FrameRate)), samples: (uint)(spec.DurationSeconds * spec.FrameRate))
        };
        foreach (var lang in spec.AudioLanguages)
            tracks.Add(Trak(trackId++, "soun", "SoundHandler", lang, created, duration, 0, 0, 1024, 0));
        foreach (var lang in spec.SubtitleLanguages)
            tracks.Add(Trak(trackId++, "sbtl", "SubtitleHandler", lang, created, duration, 0, 0, 1000, 0));

        var udta = new List<byte[]>();
        if (!spec.AsMetadataKeys)
        {
            if (spec.Make is not null) udta.Add(UserText("©mak", spec.Make));
            if (spec.Model is not null) udta.Add(UserText("©mod", spec.Model));
            if (spec.Location) udta.Add(UserText("©xyz", "+48.1000+011.5000/"));
        }
        if (spec.Title is not null) udta.Add(UserText("©nam", spec.Title));
        if (spec.Chapters > 0) udta.Add(Box("chpl", NeroChapters(spec.Chapters, spec.DurationSeconds)));
        if (spec.Software is not null)
            udta.Add(Box("meta", [.. U32(0), .. Hdlr("mdir", ""), .. Box("ilst", Box("©too", DataBox(spec.Software)))]));

        var moov = new List<byte[]> { Box("mvhd", Mvhd(created, timescale, duration)) };
        moov.AddRange(tracks);
        if (udta.Count > 0) moov.Add(Box("udta", Concat(udta)));
        if (spec.AsMetadataKeys)
        {
            var keys = new List<(string Key, string Value)>();
            if (spec.Make is not null) keys.Add(("com.apple.quicktime.make", spec.Make));
            if (spec.Model is not null) keys.Add(("com.apple.quicktime.model", spec.Model));
            if (spec.Location) keys.Add(("com.apple.quicktime.location.ISO6709", "+48.1000+011.5000+500.000/"));
            if (keys.Count > 0) moov.Add(MetaKeys(keys));
        }

        var ftyp = Concat(["isom"u8.ToArray(), U32(0x200), "isomiso2mp41"u8.ToArray()]);
        return Concat([Box("ftyp", ftyp), Box("moov", Concat(moov)), Box("mdat", new byte[spec.MediaBytes])]);
    }

    private static byte[] Trak(uint id, string handler, string handlerName, string language, uint created, uint duration,
        int width, int height, uint delta, uint samples)
    {
        var tkhd = Concat([U32(0x00000003), U32(created), U32(created), U32(id), U32(0), U32(duration), new byte[8],
            new byte[8], Matrix(), U32((uint)width << 16), U32((uint)height << 16)]);
        var mdhd = Concat([U32(0), U32(created), U32(created), U32(1000), U32(duration), U16(PackLanguage(language)), U16(0)]);
        var stts = Concat([U32(0), U32(1), U32(Math.Max(1, samples)), U32(delta)]);
        var stsz = Concat([U32(0), U32(0), U32(samples)]);
        var stbl = Box("stbl", Concat([Box("stts", stts), Box("stsz", stsz)]));
        var minf = Box("minf", stbl);
        var mdia = Box("mdia", Concat([Box("mdhd", mdhd), Hdlr(handler, handlerName), minf]));
        return Box("trak", Concat([Box("tkhd", tkhd), mdia]));
    }

    private static byte[] Mvhd(uint created, uint timescale, uint duration) =>
        Concat([U32(0), U32(created), U32(created), U32(timescale), U32(duration), U32(0x00010000), [0x01, 0x00], new byte[10],
            Matrix(), new byte[24], U32(2)]);

    private static byte[] Hdlr(string type, string name) =>
        Box("hdlr", Concat([U32(0), U32(0), Encoding.ASCII.GetBytes(type), new byte[12], Encoding.UTF8.GetBytes(name), [0]]));

    private static byte[] UserText(string type, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Box(type, Concat([U16((ushort)bytes.Length), U16(0x15C7), bytes]));
    }

    private static byte[] DataBox(string text) => Box("data", Concat([U32(1), U32(0), Encoding.UTF8.GetBytes(text)]));

    private static byte[] MetaKeys(IReadOnlyList<(string Key, string Value)> entries)
    {
        var keys = new List<byte[]> { U32(0), U32((uint)entries.Count) };
        foreach (var (key, _) in entries)
        {
            var name = Encoding.UTF8.GetBytes(key);
            keys.Add(Concat([U32((uint)(8 + name.Length)), "mdta"u8.ToArray(), name]));
        }
        var items = new List<byte[]>();
        for (var i = 0; i < entries.Count; i++)
        {
            var item = DataBox(entries[i].Value);
            items.Add(Concat([U32((uint)(8 + item.Length)), U32((uint)(i + 1)), item]));
        }
        return Box("meta", Concat([U32(0), Hdlr("mdta", ""), Box("keys", Concat(keys)), Box("ilst", Concat(items))]));
    }

    private static byte[] NeroChapters(int count, int durationSeconds)
    {
        var parts = new List<byte[]> { new byte[] { 0x01, 0, 0, 0 }, U32(0), new[] { (byte)count } };
        for (var i = 0; i < count; i++)
        {
            var start = (ulong)(durationSeconds * 10_000_000L * i / count);
            var title = Encoding.UTF8.GetBytes($"Chapter {i + 1}");
            var time = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(time, start);
            parts.Add(Concat([time, [(byte)title.Length], title]));
        }
        return Concat(parts);
    }

    private static ushort PackLanguage(string language)
    {
        var code = language.Length == 3 ? language.ToLowerInvariant() : "und";
        return (ushort)(((code[0] - 0x60) << 10) | ((code[1] - 0x60) << 5) | (code[2] - 0x60));
    }

    private static uint Seconds1904(DateTime utc) => (uint)(utc - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    private static byte[] Matrix() =>
        Concat(new uint[] { 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000 }.Select(U32).ToList());

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.Latin1.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    // ================================================================== Matroska

    public static byte[] Mkv(SyntheticVideoSpec spec)
    {
        var header = Element(0x1A45DFA3, Concat([
            Element(0x4286, UInt(1)), Element(0x42F7, UInt(1)), Element(0x42F2, UInt(4)), Element(0x42F3, UInt(8)),
            Element(0x4282, Encoding.ASCII.GetBytes("matroska")), Element(0x4287, UInt(4)), Element(0x4285, UInt(2))]));

        var info = new List<byte[]>
        {
            Element(0x2AD7B1, UInt(1_000_000)),
            Element(0x4489, Double(spec.DurationSeconds * 1000.0)),
            Element(0x4D80, Encoding.UTF8.GetBytes("libebml v1.4.4 + libmatroska v1.7.1")),
            Element(0x5741, Encoding.UTF8.GetBytes(spec.Software ?? "mkvmerge v80.0")),
            Element(0x4461, Int64((spec.CreatedUtc - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks * 100))
        };
        if (spec.Title is not null) info.Add(Element(0x7BA9, Encoding.UTF8.GetBytes(spec.Title)));

        var tracks = new List<byte[]>();
        var number = 1;
        var video = new List<byte[]>
        {
            Element(0xD7, UInt((ulong)number++)), Element(0x83, UInt(1)), Element(0x86, Encoding.ASCII.GetBytes("V_MPEG4/ISO/AVC")),
            Element(0x23E383, UInt((ulong)Math.Round(1e9 / Math.Max(1, spec.FrameRate)))),
            Element(0xE0, Concat([Element(0xB0, UInt((ulong)spec.Width)), Element(0xBA, UInt((ulong)spec.Height))]))
        };
        if (spec.VideoTrackName is not null) video.Add(Element(0x536E, Encoding.UTF8.GetBytes(spec.VideoTrackName)));
        tracks.Add(Element(0xAE, Concat(video)));
        foreach (var lang in spec.AudioLanguages)
            tracks.Add(Element(0xAE, Concat([Element(0xD7, UInt((ulong)number++)), Element(0x83, UInt(2)),
                Element(0x86, Encoding.ASCII.GetBytes("A_AAC")), Element(0x22B59C, Encoding.ASCII.GetBytes(lang))])));
        foreach (var lang in spec.SubtitleLanguages)
            tracks.Add(Element(0xAE, Concat([Element(0xD7, UInt((ulong)number++)), Element(0x83, UInt(17)),
                Element(0x86, Encoding.ASCII.GetBytes("S_TEXT/UTF8")), Element(0x22B59C, Encoding.ASCII.GetBytes(lang))])));

        var segment = new List<byte[]> { Element(0x1549A966, Concat(info)), Element(0x1654AE6B, Concat(tracks)) };
        if (spec.Chapters > 0)
        {
            var atoms = Enumerable.Range(0, spec.Chapters).Select(i => Element(0xB6, Concat([
                Element(0x73C4, UInt((ulong)(i + 1))), Element(0x91, UInt((ulong)(spec.DurationSeconds * 1_000_000_000L * i / spec.Chapters)))]))).ToList();
            segment.Add(Element(0x1043A770, Element(0x45B9, Concat(atoms))));
        }
        var tags = new List<(string, string)>();
        if (spec.Make is not null) tags.Add(("MAKE", spec.Make));
        if (spec.Model is not null) tags.Add(("MODEL", spec.Model));
        if (tags.Count > 0)
        {
            segment.Add(Element(0x1254C367, Element(0x7373, Concat(tags.Select(t => Element(0x67C8, Concat([
                Element(0x45A3, Encoding.UTF8.GetBytes(t.Item1)), Element(0x4487, Encoding.UTF8.GetBytes(t.Item2))]))).ToList()))));
        }
        segment.Add(Element(0x1F43B675, Concat([Element(0xE7, UInt(0)), Element(0xA3, new byte[spec.MediaBytes])])));
        return Concat([header, Element(0x18538067, Concat(segment))]);
    }

    private static byte[] Element(uint id, byte[] body)
    {
        var idBytes = id switch
        {
            <= 0xFF => new[] { (byte)id },
            <= 0xFFFF => new[] { (byte)(id >> 8), (byte)id },
            <= 0xFFFFFF => new[] { (byte)(id >> 16), (byte)(id >> 8), (byte)id },
            _ => new[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id }
        };
        // Sizes always as eight-byte vints: simple and valid.
        var size = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)body.Length);
        size[0] = 0x01;
        return Concat([idBytes, size, body]);
    }

    private static byte[] UInt(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        var skip = 0;
        while (skip < 7 && b[skip] == 0) skip++;
        return b[skip..];
    }

    private static byte[] Int64(long v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, v);
        return b;
    }

    private static byte[] Double(double v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, v);
        return b;
    }

    // ================================================================== bytes

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        return b;
    }

    private static byte[] Concat(IReadOnlyList<byte[]> parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }
        return result;
    }
}
