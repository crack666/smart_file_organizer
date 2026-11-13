using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>
/// An Office or OpenDocument file that is not read because reading it would cost more than the limits of
/// <see cref="OfficePackage"/> allow. <see cref="Exception.Message"/> names the limit and the part.
/// </summary>
public sealed class ContentLimitException(string message) : IOException(message);

/// <summary>
/// Reads the XML parts of an Office (OOXML) or OpenDocument file, which are ZIP packages, within fixed limits, so a
/// prepared or very large file cannot fill the memory: the package may hold at most <see cref="MaxEntries"/>
/// entries (counted from the end record before the directory is loaded), one part may unpack to at most
/// <see cref="MaxPartBytes"/>, and all parts read from one file together to at most <see cref="MaxTotalBytes"/>.
/// The unpacked bytes are counted while reading, so a size in the directory that lies does not help. Document type
/// definitions are refused, so entity expansion cannot multiply the text either. Crossing a limit throws
/// <see cref="ContentLimitException"/>.
/// </summary>
public sealed class OfficePackage : IDisposable
{
    public const int MaxEntries = 10_000;
    public const long MaxPartBytes = 16L * 1024 * 1024;
    public const long MaxTotalBytes = 32L * 1024 * 1024;

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0
    };

    private readonly ZipArchive _archive;
    private long _readBytes;

    private OfficePackage(ZipArchive archive) => _archive = archive;

    public static OfficePackage Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            if (DeclaredEntryCount(stream) is { } count && count > MaxEntries)
                throw new ContentLimitException($"package has {count:N0} entries, more than the limit of {MaxEntries:N0}");
            stream.Position = 0;
            return new OfficePackage(new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public IReadOnlyCollection<ZipArchiveEntry> Entries => _archive.Entries;

    public ZipArchiveEntry? GetEntry(string name) => _archive.GetEntry(name);

    /// <summary>Loads one part as XML, counting its unpacked bytes against both limits.</summary>
    public XDocument Load(ZipArchiveEntry entry, long maxPartBytes = MaxPartBytes)
    {
        if (entry.Length > maxPartBytes)
            throw new ContentLimitException($"{entry.FullName} unpacks to {Size(entry.Length)}, more than the limit of {Size(maxPartBytes)}");
        if (_readBytes + entry.Length > MaxTotalBytes)
            throw new ContentLimitException($"the parts read unpack to more than the limit of {Size(MaxTotalBytes)} (at {entry.FullName})");

        using var stream = new LimitedReadStream(entry.Open(), entry.FullName, maxPartBytes, MaxTotalBytes - _readBytes);
        using var reader = XmlReader.Create(stream, XmlSettings);
        var doc = XDocument.Load(reader);
        _readBytes += stream.BytesRead;
        return doc;
    }

    public void Dispose() => _archive.Dispose();

    private static string Size(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MiB";

    /// <summary>
    /// The number of entries the end record of the ZIP declares (ZIP64 included), or null when it cannot be found;
    /// <see cref="ZipArchive"/> then reports the damage itself.
    /// </summary>
    public static long? DeclaredEntryCount(Stream stream)
    {
        const int EndRecordSize = 22;
        const int Zip64LocatorSize = 20;
        if (stream.Length < EndRecordSize) return null;

        var tailLength = (int)Math.Min(stream.Length, EndRecordSize + ushort.MaxValue + Zip64LocatorSize);
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        stream.ReadExactly(tail);

        for (var i = tailLength - EndRecordSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != 0x06054b50) continue;

            var total = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 10));
            if (total != ushort.MaxValue) return total;

            // ZIP64: the locator right before the end record points at the ZIP64 end record with the real count.
            var locator = i - Zip64LocatorSize;
            if (locator < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator)) != 0x07064b50) return total;
            var offset = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(locator + 8));
            if (offset < 0 || offset > stream.Length - 56) return null;
            var record = new byte[56];
            stream.Position = offset;
            stream.ReadExactly(record);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06064b50) return null;
            var count = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32));
            return count > long.MaxValue ? long.MaxValue : (long)count;
        }

        return null;
    }

    /// <summary>Passes reads through and throws once more bytes come out than the part or the package may unpack to.</summary>
    private sealed class LimitedReadStream(Stream inner, string part, long maxPartBytes, long remainingTotal) : Stream
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        private int Count(int read)
        {
            BytesRead += read;
            if (BytesRead > maxPartBytes)
                throw new ContentLimitException($"{part} unpacks to more than the limit of {Size(maxPartBytes)}");
            if (BytesRead > remainingTotal)
                throw new ContentLimitException($"the parts read unpack to more than the limit of {Size(MaxTotalBytes)} (at {part})");
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
