using System.Buffers.Binary;
using System.Text;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>What <see cref="LegacyWordReader.Read"/> found.</summary>
/// <param name="Text">The beginning of the main text, at most the requested characters; empty when none.</param>
/// <param name="Truncated">True when the document has more text than was read.</param>
/// <param name="Problem">Why no text was read (encrypted, not a Word 97-2003 document, damaged), or null.</param>
public sealed record LegacyWordText(string Text, bool Truncated, string? Problem);

/// <summary>
/// The text of a Word 97-2003 document (<c>.doc</c>) without Word and without a library
/// (https://github.com/crack666/smart_file_organizer/issues/109): the compound file (MS-CFB) is read for the streams
/// <c>WordDocument</c> and <c>0Table</c>/<c>1Table</c>, and the piece table of the file information block (MS-DOC,
/// <c>Clx</c>) says where each run of the main text lies, as 8-bit Windows-1252 or as UTF-16. Field codes, pictures and
/// formatting are dropped. Files over <see cref="MaxFileBytes"/> are not read; every chain and count is bounded, so a
/// damaged file ends in <see cref="LegacyWordText.Problem"/>, not in a loop.
/// </summary>
public static class LegacyWordReader
{
    /// <summary>Larger files are not read for a preview.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private static ReadOnlySpan<byte> Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static LegacyWordText Read(string path, int maxChars)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
            return new LegacyWordText(string.Empty, false, $"größer als {MaxFileBytes / (1024 * 1024)} MB");
        return Read(File.ReadAllBytes(path), maxChars);
    }

    public static LegacyWordText Read(byte[] file, int maxChars)
    {
        try
        {
            var cfb = new CompoundFile(file);
            var word = cfb.Stream("WordDocument");
            if (word is null || word.Length < 0x1AA || BinaryPrimitives.ReadUInt16LittleEndian(word) != 0xA5EC)
                return new LegacyWordText(string.Empty, false, "kein Word-97-2003-Dokument");

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(0x0A));
            if ((flags & 0x0100) != 0)
                return new LegacyWordText(string.Empty, false, "verschlüsselt");
            var table = cfb.Stream((flags & 0x0200) != 0 ? "1Table" : "0Table");
            if (table is null)
                return new LegacyWordText(string.Empty, false, "Tabellenstrom fehlt");

            // FibBase (32 bytes), then the variable parts: csw + fibRgW, cslw + fibRgLw, cbRgFcLcb + fibRgFcLcbBlob.
            var pos = 32;
            int csw = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(pos));
            pos += 2 + csw * 2;
            int cslw = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(pos));
            var rgLw = pos + 2;
            pos = rgLw + cslw * 4;
            int cbRgFcLcb = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(pos));
            var blob = pos + 2;
            if (cslw < 4 || cbRgFcLcb < 34 || blob + 34 * 8 > word.Length)
                return new LegacyWordText(string.Empty, false, "Dateikopf unvollständig");

            var ccpText = BinaryPrimitives.ReadInt32LittleEndian(word.AsSpan(rgLw + 3 * 4));
            // fcClx and lcbClx are the 34th pair of FibRgFcLcb97.
            var fcClx = BinaryPrimitives.ReadUInt32LittleEndian(word.AsSpan(blob + 33 * 8));
            var lcbClx = BinaryPrimitives.ReadUInt32LittleEndian(word.AsSpan(blob + 33 * 8 + 4));
            if (ccpText <= 0) return new LegacyWordText(string.Empty, false, null);
            if (lcbClx == 0 || fcClx + (long)lcbClx > table.Length)
                return new LegacyWordText(string.Empty, false, "Stückliste fehlt");

            return Pieces(word, table.AsSpan((int)fcClx, (int)lcbClx), ccpText, maxChars);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException or OverflowException)
        {
            return new LegacyWordText(string.Empty, false, "beschädigt");
        }
    }

    private static LegacyWordText Pieces(byte[] word, ReadOnlySpan<byte> clx, int ccpText, int maxChars)
    {
        var i = 0;
        while (i < clx.Length && clx[i] == 0x01) // Prc: formatting, skipped
            i += 3 + BinaryPrimitives.ReadUInt16LittleEndian(clx[(i + 1)..]);
        if (i >= clx.Length || clx[i] != 0x02) throw new InvalidDataException("no Pcdt");
        var lcb = BinaryPrimitives.ReadInt32LittleEndian(clx[(i + 1)..]);
        var plc = clx.Slice(i + 5, lcb);
        var n = (lcb - 4) / 12;
        if (n <= 0) throw new InvalidDataException("empty piece table");

        var raw = new StringBuilder();
        var limit = Math.Min(ccpText, maxChars * 2); // room for the field codes that are dropped below
        for (var p = 0; p < n && raw.Length < limit; p++)
        {
            var cpStart = BinaryPrimitives.ReadInt32LittleEndian(plc[(p * 4)..]);
            var cpEnd = BinaryPrimitives.ReadInt32LittleEndian(plc[((p + 1) * 4)..]);
            if (cpStart >= ccpText) break;
            var count = Math.Min(cpEnd, ccpText) - cpStart;
            count = Math.Min(count, limit - raw.Length);
            if (count <= 0) continue;
            var pcd = plc.Slice((n + 1) * 4 + p * 8, 8);
            var fc = BinaryPrimitives.ReadUInt32LittleEndian(pcd[2..]);
            if ((fc & 0x40000000) != 0)
            {
                var offset = (int)((fc & 0x3FFFFFFF) / 2);
                foreach (var b in word.AsSpan(offset, count)) raw.Append(Cp1252(b));
            }
            else
            {
                raw.Append(Encoding.Unicode.GetString(word, (int)fc, count * 2));
            }
        }

        var text = Clean(raw.ToString());
        var truncated = ccpText > limit || text.Length > maxChars;
        if (text.Length > maxChars) text = text[..maxChars];
        return new LegacyWordText(text.Trim(), truncated, null);
    }

    /// <summary>Paragraph, cell and page marks become line breaks; of a field only its result stays.</summary>
    private static string Clean(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        // One entry per open field: true while in its code, false once its result begins.
        var inCode = new Stack<bool>();
        foreach (var c in raw)
        {
            switch (c)
            {
                case '\u0013': inCode.Push(true); continue;
                case '\u0014': if (inCode.Count > 0) { inCode.Pop(); inCode.Push(false); } continue;
                case '\u0015': if (inCode.Count > 0) inCode.Pop(); continue;
            }
            if (inCode.Count > 0 && inCode.Peek()) continue;
            switch (c)
            {
                case '\r' or '\u000B' or '\u000C': sb.Append('\n'); break;
                case '\u0007': sb.Append('\t'); break;
                case '\u001E': sb.Append('-'); break;
                case '\t': sb.Append('\t'); break;
                case < ' ': break; // pictures, footnote marks, optional hyphens
                default: sb.Append(c); break;
            }
        }
        return sb.ToString().Replace("\t\n", "\n");
    }

    /// <summary>Windows-1252 without the code pages provider: Latin-1 except 0x80-0x9F.</summary>
    internal static char Cp1252(byte b) => b is >= 0x80 and <= 0x9F ? Cp1252High[b - 0x80] : (char)b;

    private static readonly char[] Cp1252High =
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ'
    ];

    /// <summary>The streams of a compound file (MS-CFB, versions 3 and 4), read from memory.</summary>
    private sealed class CompoundFile
    {
        private readonly byte[] _file;
        private readonly int _sectorSize;
        private readonly int _miniSectorSize;
        private readonly uint _miniCutoff;
        private readonly uint[] _fat;
        private readonly List<(string Name, int Type, uint Start, long Size)> _entries = [];
        private uint[]? _miniFat;
        private byte[]? _miniStream;
        private readonly uint _miniFatStart;

        public CompoundFile(byte[] file)
        {
            _file = file;
            if (file.Length < 512 || !file.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("no compound file");
            var shift = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x1E));
            var miniShift = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0x20));
            if (shift is not (9 or 12) || miniShift != 6) throw new InvalidDataException("sector size");
            _sectorSize = 1 << shift;
            _miniSectorSize = 1 << miniShift;
            var fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x2C));
            var dirStart = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x30));
            _miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x38));
            _miniFatStart = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x3C));
            var difatStart = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x44));
            var maxSectors = (uint)(file.Length / _sectorSize + 1);
            if (fatSectors > maxSectors) throw new InvalidDataException("fat");

            // The FAT sectors: 109 in the header, the rest in the DIFAT chain.
            var fatList = new List<uint>();
            for (var i = 0; i < 109 && fatList.Count < fatSectors; i++)
                fatList.Add(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x4C + i * 4)));
            var difat = difatStart;
            var perDifat = _sectorSize / 4 - 1;
            for (var guard = 0; fatList.Count < fatSectors && difat < EndOfChain - 2 && guard < maxSectors; guard++)
            {
                var sector = SectorSpan(difat);
                for (var i = 0; i < perDifat && fatList.Count < fatSectors; i++)
                    fatList.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector[(i * 4)..]));
                difat = BinaryPrimitives.ReadUInt32LittleEndian(sector[(perDifat * 4)..]);
            }
            var perSector = _sectorSize / 4;
            _fat = new uint[fatList.Count * perSector];
            for (var s = 0; s < fatList.Count; s++)
            {
                var sector = SectorSpan(fatList[s]);
                for (var i = 0; i < perSector; i++)
                    _fat[s * perSector + i] = BinaryPrimitives.ReadUInt32LittleEndian(sector[(i * 4)..]);
            }

            var dir = ReadChain(dirStart, long.MaxValue);
            for (var off = 0; off + 128 <= dir.Length; off += 128)
            {
                var e = dir.AsSpan(off, 128);
                int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(e[0x40..]);
                var type = e[0x42];
                if (type == 0 || nameLen < 2 || nameLen > 64) continue;
                var name = Encoding.Unicode.GetString(e[..(nameLen - 2)]);
                var start = BinaryPrimitives.ReadUInt32LittleEndian(e[0x74..]);
                var size = _sectorSize == 512
                    ? BinaryPrimitives.ReadUInt32LittleEndian(e[0x78..])
                    : (long)BinaryPrimitives.ReadUInt64LittleEndian(e[0x78..]);
                _entries.Add((name, type, start, size));
            }
        }

        public byte[]? Stream(string name)
        {
            var entry = _entries.FirstOrDefault(e => e.Type == 2 && e.Name == name);
            if (entry.Name is null) return null;
            if (entry.Size > _file.Length) throw new InvalidDataException("stream size");
            if (entry.Size >= _miniCutoff) return ReadChain(entry.Start, entry.Size);

            if (_miniStream is null)
            {
                var root = _entries.FirstOrDefault(e => e.Type == 5);
                _miniStream = root.Name is null ? [] : ReadChain(root.Start, root.Size);
                var miniFatBytes = ReadChain(_miniFatStart, long.MaxValue);
                _miniFat = new uint[miniFatBytes.Length / 4];
                for (var i = 0; i < _miniFat.Length; i++)
                    _miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i * 4));
            }
            var result = new byte[entry.Size];
            var written = 0;
            var sector = entry.Start;
            for (var guard = 0; written < entry.Size && sector < _miniFat!.Length && guard <= _miniFat.Length; guard++)
            {
                var off = (long)sector * _miniSectorSize;
                var n = (int)Math.Min(_miniSectorSize, entry.Size - written);
                if (off + n > _miniStream.Length) break;
                Array.Copy(_miniStream, off, result, written, n);
                written += n;
                sector = _miniFat[sector];
            }
            return written == entry.Size ? result : throw new InvalidDataException("mini chain");
        }

        private ReadOnlySpan<byte> SectorSpan(uint sector)
        {
            var off = ((long)sector + 1) * _sectorSize;
            if (off + _sectorSize > _file.Length) throw new InvalidDataException("sector beyond the end");
            return _file.AsSpan((int)off, _sectorSize);
        }

        private byte[] ReadChain(uint start, long size)
        {
            if (start is EndOfChain or FreeSector) return [];
            using var ms = new MemoryStream();
            var sector = start;
            for (var guard = 0; sector < _fat.Length && guard <= _fat.Length && ms.Length < size; guard++)
            {
                var span = SectorSpan(sector);
                ms.Write(size == long.MaxValue ? span : span[..(int)Math.Min(_sectorSize, size - ms.Length)]);
                sector = _fat[sector];
            }
            return ms.ToArray();
        }
    }
}
