using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SmartFileOrganizer.Application.Media;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>
/// Reads what a video container says without decoding a frame (https://github.com/crack666/smart_file_organizer/issues/103):
/// MP4 and QuickTime boxes (<c>mvhd</c>, <c>tkhd</c>, <c>mdhd</c>, <c>hdlr</c>, <c>stts</c>, <c>udta</c> texts, <c>meta</c>
/// keys of phones and cameras, chapters) and Matroska/WebM elements (segment info, tracks, chapters, tags). Only the
/// headers are read; media data are skipped by seeking. Other formats (AVI, MPEG-TS) fall back to
/// <see cref="MediaMetadataReader"/>. No external program and no new library: the two formats are small to walk, and
/// MetadataExtractor, already used for photos, reads neither the track languages nor Matroska. Location data are reduced
/// to "present".
/// </summary>
public static class VideoContainerReader
{
    /// <summary>Largest leaf box or element read into memory; bigger ones are skipped (sample tables of long films).</summary>
    private const int MaxLeafBytes = 1 << 20;

    public static VideoContainerInfo? Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        Span<byte> head = stackalloc byte[12];
        var n = stream.Read(head);
        stream.Position = 0;
        if (n >= 4 && BinaryPrimitives.ReadUInt32BigEndian(head) == 0x1A45DFA3)
            return Matroska.Read(stream);
        if (n >= 8 && IsBoxType(head[4..8]))
            return Mp4.Read(stream);
        return FromMetadataExtractor(path);
    }

    private static bool IsBoxType(ReadOnlySpan<byte> t) =>
        t.SequenceEqual("ftyp"u8) || t.SequenceEqual("moov"u8) || t.SequenceEqual("mdat"u8) || t.SequenceEqual("wide"u8)
        || t.SequenceEqual("free"u8) || t.SequenceEqual("skip"u8) || t.SequenceEqual("pnot"u8);

    private static VideoContainerInfo? FromMetadataExtractor(string path)
    {
        var meta = MediaMetadataReader.Read(path);
        return new VideoContainerInfo
        {
            Make = meta.Make, Model = meta.Model, Software = meta.Software, CreatedAt = meta.TakenAt, HasLocation = meta.HasLocation,
            Width = meta.Width, Height = meta.Height, DurationSeconds = meta.DurationSeconds,
            BitrateKbps = Bitrate(new FileInfo(path).Length, meta.DurationSeconds)
        };
    }

    internal static long? Bitrate(long bytes, double? seconds) =>
        seconds is > 0 ? (long)Math.Round(bytes * 8 / seconds.Value / 1000) : null;

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Replace("\0", string.Empty).Trim();
        return s.Length == 0 ? null : s;
    }

    // ================================================================== MP4 / QuickTime

    private static class Mp4
    {
        private sealed class Track
        {
            public string? Handler;
            public string? HandlerName;
            public string? Language;
            public uint Timescale;
            public uint FirstDelta;
            public uint SampleCount;
            public int Width;
            public int Height;
            public bool ChapterReference;
        }

        private sealed class State
        {
            public uint Timescale;
            public ulong Duration;
            public DateTime? Created;
            public readonly List<Track> Tracks = [];
            public Track? Current;
            public readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal);
            public readonly List<string> MetaKeys = [];
            public int NeroChapters;
            public bool Location;
        }

        public static VideoContainerInfo Read(FileStream stream)
        {
            var s = new State();
            Walk(stream, 0, stream.Length, s, depth: 0);

            string? Text(params string[] keys) => keys.Select(k => s.Texts.GetValueOrDefault(k)).FirstOrDefault(v => v is not null);
            var video = s.Tracks.FirstOrDefault(t => t.Handler == "vide");
            double? seconds = s.Timescale > 0 && s.Duration > 0 ? s.Duration / (double)s.Timescale : null;
            double? fps = video is { Timescale: > 0, FirstDelta: > 0 } ? video.Timescale / (double)video.FirstDelta : null;
            if (Text("com.android.capture.fps") is { } captured && double.TryParse(captured, NumberStyles.Float, CultureInfo.InvariantCulture, out var cfps))
                fps ??= cfps;
            var chapters = s.NeroChapters;
            if (chapters == 0 && s.Tracks.Any(t => t.ChapterReference))
                chapters = (int)s.Tracks.Where(t => t.Handler == "text").Select(t => t.SampleCount).DefaultIfEmpty(0u).Max();

            return new VideoContainerInfo
            {
                Format = s.Texts.GetValueOrDefault("ftyp") is "qt  " ? "quicktime" : "mp4",
                Make = Clean(Text("com.apple.quicktime.make", "com.android.manufacturer", "©mak")),
                Model = Clean(Text("com.apple.quicktime.model", "com.android.model", "©mod")),
                Software = Clean(Text("com.apple.quicktime.software", "©swr", "©too", "©enc")),
                Title = Clean(Text("com.apple.quicktime.title", "©nam")),
                CreatedAt = s.Created,
                HasLocation = s.Location,
                Width = video?.Width is > 0 ? video.Width : null,
                Height = video?.Height is > 0 ? video.Height : null,
                FrameRate = fps is > 0 and < 1000 ? fps : null,
                DurationSeconds = seconds,
                BitrateKbps = Bitrate(stream.Length, seconds),
                ChapterCount = chapters,
                // A text track is the chapter list when another track refers to it as such; otherwise it is subtitles.
                Tracks = s.Tracks.Where(t => t.Handler is "vide" or "soun" or "sbtl" or "subt"
                                             || (t.Handler == "text" && !s.Tracks.Any(x => x.ChapterReference)))
                    .Select(t => new VideoTrack(t.Handler switch { "vide" => "video", "soun" => "audio", _ => "subtitle" }, t.Language, Clean(t.HandlerName)))
                    .ToList()
            };
        }

        private static readonly HashSet<string> Containers = ["moov", "trak", "mdia", "minf", "stbl", "udta", "edts", "tref", "ilst", "meta"];

        private static void Walk(FileStream stream, long start, long end, State s, int depth)
        {
            var pos = start;
            Span<byte> header = stackalloc byte[16];
            while (pos + 8 <= end && depth < 12)
            {
                stream.Position = pos;
                if (stream.Read(header[..8]) < 8) return;
                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = Encoding.Latin1.GetString(header[4..8]);
                var headerSize = 8;
                if (size == 1)
                {
                    if (stream.Read(header[8..16]) < 8) return;
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                    headerSize = 16;
                }
                else if (size == 0)
                {
                    size = end - pos;
                }
                if (size < headerSize || pos + size > end) return;
                var bodyStart = pos + headerSize;
                var bodyEnd = pos + size;

                if (type == "trak")
                {
                    s.Current = new Track();
                    s.Tracks.Add(s.Current);
                    Walk(stream, bodyStart, bodyEnd, s, depth + 1);
                    s.Current = null;
                }
                else if (type == "meta")
                {
                    // ISO meta is a full box (4 bytes version and flags), QuickTime meta is not: look for its hdlr.
                    var body = ReadBody(stream, bodyStart, Math.Min(bodyEnd, bodyStart + 12));
                    var skip = body.Length >= 8 && Encoding.Latin1.GetString(body, 4, 4) == "hdlr" ? 0 : 4;
                    Walk(stream, bodyStart + skip, bodyEnd, s, depth + 1);
                }
                else if (type == "ilst")
                {
                    WalkItems(stream, bodyStart, bodyEnd, s);
                }
                else if (Containers.Contains(type))
                {
                    if (type == "tref" && s.Current is not null) TrefChapter(stream, bodyStart, bodyEnd, s.Current);
                    else Walk(stream, bodyStart, bodyEnd, s, depth + 1);
                }
                else if (bodyEnd - bodyStart <= MaxLeafBytes)
                {
                    Leaf(type, ReadBody(stream, bodyStart, bodyEnd), s, depth);
                }
                pos = bodyEnd;
            }
        }

        private static void TrefChapter(FileStream stream, long start, long end, Track track)
        {
            var body = ReadBody(stream, start, Math.Min(end, start + 4096));
            for (var i = 0; i + 8 <= body.Length;)
            {
                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i));
                if (Encoding.Latin1.GetString(body, i + 4, 4) == "chap") track.ChapterReference = true;
                if (size < 8) break;
                i += size;
            }
        }

        private static byte[] ReadBody(FileStream stream, long start, long end)
        {
            var length = (int)Math.Max(0, Math.Min(end - start, MaxLeafBytes));
            var buffer = new byte[length];
            stream.Position = start;
            var read = 0;
            while (read < length)
            {
                var n = stream.Read(buffer, read, length - read);
                if (n <= 0) break;
                read += n;
            }
            return read == length ? buffer : buffer[..read];
        }

        private static void Leaf(string type, byte[] b, State s, int depth)
        {
            var t = s.Current;
            switch (type)
            {
                case "ftyp" when b.Length >= 4:
                    s.Texts["ftyp"] = Encoding.Latin1.GetString(b, 0, 4);
                    break;
                case "mvhd" when b.Length >= 20:
                    if (b[0] == 1 && b.Length >= 32)
                    {
                        s.Created = Mac(BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(4)));
                        s.Timescale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(20));
                        s.Duration = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(24));
                    }
                    else
                    {
                        s.Created = Mac(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4)));
                        s.Timescale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12));
                        s.Duration = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16));
                    }
                    break;
                case "tkhd" when t is not null && b.Length >= 84:
                    t.Width = (int)(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(b.Length - 8)) >> 16);
                    t.Height = (int)(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(b.Length - 4)) >> 16);
                    break;
                case "mdhd" when t is not null && b.Length >= 24:
                    var v1 = b[0] == 1 && b.Length >= 36;
                    t.Timescale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(v1 ? 20 : 12));
                    var lang = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(v1 ? 32 : 20));
                    t.Language = PackedLanguage(lang);
                    break;
                case "hdlr" when b.Length >= 24:
                    var handler = Encoding.Latin1.GetString(b, 8, 4);
                    // The handler of the media (mdia/hdlr), not that of a metadata box inside the track.
                    if (t is not null && t.Handler is null && handler is not ("mdir" or "mdta" or "ID32" or "dhlr" or "alis" or "url "))
                    {
                        t.Handler = handler;
                        t.HandlerName = HandlerName(b.AsSpan(24));
                    }
                    break;
                case "stts" when t is not null && b.Length >= 16:
                    if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4)) > 0)
                        t.FirstDelta = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12));
                    break;
                case "stsz" when t is not null && b.Length >= 12:
                    t.SampleCount = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8));
                    break;
                case "keys" when b.Length >= 8:
                    s.MetaKeys.Clear();
                    var count = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));
                    for (int i = 8, k = 0; k < count && i + 8 <= b.Length; k++)
                    {
                        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i));
                        if (size < 8 || i + size > b.Length) break;
                        s.MetaKeys.Add(Encoding.UTF8.GetString(b, i + 8, size - 8));
                        i += size;
                    }
                    break;
                case "chpl" when b.Length >= 9:
                    s.NeroChapters = b[0] == 1 ? b[8] : b[4];
                    break;
                default:
                    if (type.Length == 4 && type[0] == '©')
                        StoreText(s, type, QuickTimeText(b));
                    break;
            }
        }

        /// <summary>Items of an <c>ilst</c>: by four-letter name (<c>©too</c>) or by the index of a <c>keys</c> entry.</summary>
        private static void WalkItems(FileStream stream, long start, long end, State s)
        {
            var pos = start;
            Span<byte> header = stackalloc byte[8];
            while (pos + 8 <= end)
            {
                stream.Position = pos;
                if (stream.Read(header) < 8) return;
                var size = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (size < 8 || pos + size > end) return;
                var body = ReadBody(stream, pos + 8, pos + size);
                var value = DataBox(body);
                var index = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
                if (index >= 1 && index <= s.MetaKeys.Count)
                    StoreText(s, s.MetaKeys[(int)index - 1], value);
                else
                    StoreText(s, Encoding.Latin1.GetString(header[4..]), value);
                pos += size;
            }
        }

        private static void StoreText(State s, string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (key is "©xyz" or "com.apple.quicktime.location.ISO6709" or "com.android.location" || key.Contains("location", StringComparison.OrdinalIgnoreCase))
            {
                // Only that there is one; coordinates never leave this method.
                s.Location = true;
                return;
            }
            s.Texts.TryAdd(key, value.Trim());
        }

        /// <summary>A <c>data</c> box inside an item: type, locale, then the value.</summary>
        private static string? DataBox(byte[] b)
        {
            if (b.Length < 16 || Encoding.Latin1.GetString(b, 4, 4) != "data") return null;
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(b), (uint)b.Length);
            var wellKnown = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8)) & 0x00FFFFFF;
            var payload = b.AsSpan(16, Math.Max(0, size - 16));
            return wellKnown switch
            {
                1 => Encoding.UTF8.GetString(payload),
                2 => Encoding.BigEndianUnicode.GetString(payload),
                23 when payload.Length >= 4 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(payload)).ToString(CultureInfo.InvariantCulture),
                21 or 22 when payload.Length is > 0 and <= 8 => BigInteger(payload).ToString(CultureInfo.InvariantCulture),
                _ => null
            };
        }

        private static long BigInteger(ReadOnlySpan<byte> b)
        {
            long v = 0;
            foreach (var x in b) v = (v << 8) | x;
            return v;
        }

        /// <summary>A QuickTime user data text: 16-bit length, 16-bit language, text; or an iTunes-style <c>data</c> box.</summary>
        private static string? QuickTimeText(byte[] b)
        {
            if (b.Length >= 16 && Encoding.Latin1.GetString(b, 4, 4) == "data") return DataBox(b);
            if (b.Length < 4) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(b);
            return length > 0 && 4 + length <= b.Length ? Encoding.UTF8.GetString(b, 4, length) : null;
        }

        /// <summary>The name after the fixed part of <c>hdlr</c>: a C string (ISO) or a Pascal string (QuickTime).</summary>
        private static string? HandlerName(ReadOnlySpan<byte> rest)
        {
            if (rest.Length == 0) return null;
            if (rest[0] > 0 && rest[0] == rest.Length - 1 && rest[0] < 0x20)
                return Encoding.UTF8.GetString(rest[1..]);
            var zero = rest.IndexOf((byte)0);
            var text = Encoding.UTF8.GetString(zero >= 0 ? rest[..zero] : rest);
            return text.Length > 0 && text[0] < ' ' ? text[1..] : text;
        }

        private static string? PackedLanguage(ushort packed)
        {
            if (packed == 0 || packed == 0x7FFF || packed < 0x400) return null; // Macintosh language codes and none
            var chars = new[] { (char)(((packed >> 10) & 0x1F) + 0x60), (char)(((packed >> 5) & 0x1F) + 0x60), (char)((packed & 0x1F) + 0x60) };
            var code = new string(chars);
            return code.All(c => c is >= 'a' and <= 'z') ? code : null;
        }

        private static DateTime? Mac(ulong seconds) =>
            seconds is > 0 and < 8_000_000_000UL ? new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds) : null;
    }

    // ================================================================== Matroska / WebM

    private static class Matroska
    {
        private const uint Segment = 0x18538067, Info = 0x1549A966, Tracks = 0x1654AE6B, TrackEntry = 0xAE, Cluster = 0x1F43B675;
        private const uint Chapters = 0x1043A770, EditionEntry = 0x45B9, ChapterAtom = 0xB6, Tags = 0x1254C367, Tag = 0x7373, SimpleTag = 0x67C8;

        private sealed class State
        {
            public string? DocType;
            public ulong Scale = 1_000_000;
            public double? Duration;
            public string? Title, MuxingApp, WritingApp, Encoder;
            public DateTime? Date;
            public int Chapters;
            public readonly List<(int Type, string? Language, string? Name, ulong FrameNs, int W, int H)> Tracks = [];
            public readonly Dictionary<string, string> TagValues = new(StringComparer.OrdinalIgnoreCase);
        }

        public static VideoContainerInfo Read(FileStream stream)
        {
            var s = new State();
            long pos = 0;
            while (pos < stream.Length)
            {
                if (!Header(stream, pos, out var id, out var size, out var headerLength)) break;
                var bodyStart = pos + headerLength;
                if (id == 0x1A45DFA3) ReadEbmlHeader(stream, bodyStart, Math.Min(stream.Length, bodyStart + (long)Math.Min(size, 4096)), s);
                if (id == Segment)
                {
                    var end = size == ulong.MaxValue ? stream.Length : Math.Min(stream.Length, bodyStart + (long)size);
                    ReadSegment(stream, bodyStart, end, s);
                    break;
                }
                if (size == ulong.MaxValue) break;
                pos = bodyStart + (long)size;
            }

            var video = s.Tracks.FirstOrDefault(t => t.Type == 1);
            var seconds = s.Duration is > 0 ? s.Duration * s.Scale / 1e9 : null;
            return new VideoContainerInfo
            {
                Format = s.DocType == "webm" ? "webm" : "matroska",
                Software = Clean(s.Encoder ?? s.WritingApp),
                MuxingApp = Clean(s.MuxingApp),
                Title = Clean(s.Title),
                CreatedAt = s.Date,
                Make = Clean(s.TagValues.GetValueOrDefault("MAKE") ?? s.TagValues.GetValueOrDefault("MANUFACTURER")),
                Model = Clean(s.TagValues.GetValueOrDefault("MODEL")),
                HasLocation = s.TagValues.ContainsKey("LOCATION") || s.TagValues.ContainsKey("RECORDING_LOCATION"),
                Width = video.W > 0 ? video.W : null,
                Height = video.H > 0 ? video.H : null,
                FrameRate = video.FrameNs > 0 ? 1e9 / video.FrameNs : null,
                DurationSeconds = seconds,
                BitrateKbps = Bitrate(stream.Length, seconds),
                ChapterCount = s.Chapters,
                Tracks = s.Tracks.Where(t => t.Type is 1 or 2 or 17)
                    .Select(t => new VideoTrack(t.Type switch { 1 => "video", 2 => "audio", _ => "subtitle" }, t.Language, Clean(t.Name)))
                    .ToList()
            };
        }

        private static void ReadEbmlHeader(FileStream stream, long start, long end, State s)
        {
            foreach (var (id, body) in Children(stream, start, end))
                if (id == 0x4282) s.DocType = Text(body);
        }

        private static void ReadSegment(FileStream stream, long start, long end, State s)
        {
            var pos = start;
            while (pos < end)
            {
                if (!Header(stream, pos, out var id, out var size, out var headerLength)) return;
                var bodyStart = pos + headerLength;
                if (size == ulong.MaxValue) return; // a live stream: nothing after it can be found by size
                var bodyEnd = Math.Min(end, bodyStart + (long)size);
                switch (id)
                {
                    case Info: ReadInfo(stream, bodyStart, bodyEnd, s); break;
                    case Tracks: ReadTracks(stream, bodyStart, bodyEnd, s); break;
                    case Chapters: s.Chapters += CountAtoms(stream, bodyStart, bodyEnd); break;
                    case Tags: ReadTags(stream, bodyStart, bodyEnd, s); break;
                    case Cluster: break; // media data: skipped by its size
                }
                pos = bodyEnd;
            }
        }

        private static void ReadInfo(FileStream stream, long start, long end, State s)
        {
            foreach (var (id, body) in Children(stream, start, end))
            {
                switch (id)
                {
                    case 0x2AD7B1: s.Scale = UInt(body) is > 0 and var scale ? scale : s.Scale; break;
                    case 0x4489: s.Duration = Float(body); break;
                    case 0x7BA9: s.Title = Text(body); break;
                    case 0x4D80: s.MuxingApp = Text(body); break;
                    case 0x5741: s.WritingApp = Text(body); break;
                    case 0x4461 when body.Length == 8:
                        s.Date = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(BinaryPrimitives.ReadInt64BigEndian(body) / 100);
                        break;
                }
            }
        }

        private static void ReadTracks(FileStream stream, long start, long end, State s)
        {
            foreach (var (entryStart, entryEnd) in Ranges(stream, start, end, TrackEntry))
            {
                int type = 0, w = 0, h = 0;
                string? language = "eng", bcp = null, name = null; // Matroska's default language is English
                ulong frame = 0;
                foreach (var (id, body) in Children(stream, entryStart, entryEnd))
                {
                    switch (id)
                    {
                        case 0x83: type = (int)UInt(body); break;
                        case 0x22B59C: language = Text(body); break;
                        case 0x22B59D: bcp = Text(body); break;
                        case 0x536E: name = Text(body); break;
                        case 0x23E383: frame = UInt(body); break;
                        case 0xE0:
                            foreach (var (vid, vbody) in Children(body))
                            {
                                if (vid == 0xB0) w = (int)UInt(vbody);
                                else if (vid == 0xBA) h = (int)UInt(vbody);
                            }
                            break;
                    }
                }
                s.Tracks.Add((type, bcp ?? language, name, frame, w, h));
            }
        }

        private static void ReadTags(FileStream stream, long start, long end, State s)
        {
            foreach (var (tagStart, tagEnd) in Ranges(stream, start, end, Tag))
            {
                foreach (var (id, body) in Children(stream, tagStart, tagEnd))
                {
                    if (id != SimpleTag) continue;
                    string? key = null, value = null;
                    foreach (var (sid, sbody) in Children(body))
                    {
                        if (sid == 0x45A3) key = Text(sbody);
                        else if (sid == 0x4487) value = Text(sbody);
                    }
                    if (key is null || string.IsNullOrWhiteSpace(value)) continue;
                    if (key.Equals("ENCODER", StringComparison.OrdinalIgnoreCase) || key.Equals("ENCODED_BY", StringComparison.OrdinalIgnoreCase))
                        s.Encoder ??= value;
                    s.TagValues.TryAdd(key, value);
                }
            }
        }

        private static int CountAtoms(FileStream stream, long start, long end)
        {
            var count = 0;
            foreach (var (editionStart, editionEnd) in Ranges(stream, start, end, EditionEntry))
                count += Ranges(stream, editionStart, editionEnd, ChapterAtom).Count();
            return count;
        }

        /// <summary>The body ranges of the direct children with <paramref name="wanted"/> as id.</summary>
        private static IEnumerable<(long Start, long End)> Ranges(FileStream stream, long start, long end, uint wanted)
        {
            var pos = start;
            var list = new List<(long, long)>();
            while (pos < end)
            {
                if (!Header(stream, pos, out var id, out var size, out var headerLength) || size == ulong.MaxValue) break;
                var bodyStart = pos + headerLength;
                var bodyEnd = Math.Min(end, bodyStart + (long)size);
                if (id == wanted) list.Add((bodyStart, bodyEnd));
                pos = bodyEnd;
            }
            return list;
        }

        /// <summary>The direct children with their bodies; bodies above the leaf limit are left out.</summary>
        private static List<(uint Id, byte[] Body)> Children(FileStream stream, long start, long end)
        {
            var list = new List<(uint, byte[])>();
            var pos = start;
            while (pos < end)
            {
                if (!Header(stream, pos, out var id, out var size, out var headerLength) || size == ulong.MaxValue) break;
                var bodyStart = pos + headerLength;
                var bodyEnd = Math.Min(end, bodyStart + (long)size);
                if (bodyEnd - bodyStart <= MaxLeafBytes)
                {
                    var body = new byte[bodyEnd - bodyStart];
                    stream.Position = bodyStart;
                    stream.ReadExactly(body);
                    list.Add((id, body));
                }
                pos = bodyEnd;
            }
            return list;
        }

        private static List<(uint Id, byte[] Body)> Children(byte[] data)
        {
            var list = new List<(uint, byte[])>();
            var pos = 0;
            while (pos < data.Length)
            {
                if (!Header(data.AsSpan(pos), out var id, out var size, out var headerLength) || size == ulong.MaxValue) break;
                var length = (int)Math.Min((ulong)(data.Length - pos - headerLength), size);
                list.Add((id, data.AsSpan(pos + headerLength, length).ToArray()));
                pos += headerLength + length;
            }
            return list;
        }

        private static bool Header(FileStream stream, long pos, out uint id, out ulong size, out int headerLength)
        {
            Span<byte> b = stackalloc byte[12];
            stream.Position = pos;
            var n = stream.Read(b);
            return Header(b[..n], out id, out size, out headerLength);
        }

        private static bool Header(ReadOnlySpan<byte> b, out uint id, out ulong size, out int headerLength)
        {
            id = 0; size = 0; headerLength = 0;
            if (b.Length < 2) return false;
            var idLength = Length(b[0], 4);
            if (idLength == 0 || b.Length < idLength + 1) return false;
            for (var i = 0; i < idLength; i++) id = (id << 8) | b[i];
            var sizeLength = Length(b[idLength], 8);
            if (sizeLength == 0 || b.Length < idLength + sizeLength) return false;
            var allOnes = true;
            size = (ulong)(b[idLength] & (0xFF >> sizeLength));
            if (size != (ulong)(0xFF >> sizeLength)) allOnes = false;
            for (var i = 1; i < sizeLength; i++)
            {
                size = (size << 8) | b[idLength + i];
                if (b[idLength + i] != 0xFF) allOnes = false;
            }
            if (allOnes) size = ulong.MaxValue;
            headerLength = idLength + sizeLength;
            return true;
        }

        private static int Length(byte first, int max)
        {
            for (var i = 0; i < max; i++)
                if ((first & (0x80 >> i)) != 0) return i + 1;
            return 0;
        }

        private static ulong UInt(byte[] b)
        {
            ulong v = 0;
            foreach (var x in b.Take(8)) v = (v << 8) | x;
            return v;
        }

        private static double? Float(byte[] b) => b.Length switch
        {
            4 => BinaryPrimitives.ReadSingleBigEndian(b),
            8 => BinaryPrimitives.ReadDoubleBigEndian(b),
            _ => null
        };

        private static string? Text(byte[] b) => Clean(Encoding.UTF8.GetString(b));
    }
}
