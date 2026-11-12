using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>
/// Minimal PNG writer for 32-bit BGRA pixel buffers (as produced by PDFium).
/// Exists so the core can hand rendered PDF pages to the model without a UI imaging library.
/// </summary>
internal static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes tightly packed BGRA rows (stride = width * 4) as an 8-bit RGBA PNG.</summary>
    public static byte[] EncodeBgra(byte[] bgra, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var stride = width * 4;
        if (bgra.Length < stride * height)
            throw new ArgumentException("Pixel buffer is smaller than width * height * 4.", nameof(bgra));

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[0..4], width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..8], height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // colour type: RGBA
        header[10] = 0;  // compression
        header[11] = 0;  // filter method
        header[12] = 0;  // no interlace
        WriteChunk(output, "IHDR", header);

        using (var idat = new MemoryStream())
        {
            using (var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            {
                var row = new byte[stride + 1]; // leading filter byte 0 = None
                for (var y = 0; y < height; y++)
                {
                    var src = y * stride;
                    for (var x = 0; x < stride; x += 4)
                    {
                        row[x + 1] = bgra[src + x + 2]; // R
                        row[x + 2] = bgra[src + x + 1]; // G
                        row[x + 3] = bgra[src + x];     // B
                        row[x + 4] = bgra[src + x + 3]; // A
                    }
                    zlib.Write(row);
                }
            }
            WriteChunk(output, "IDAT", idat.GetBuffer().AsSpan(0, (int)idat.Length));
        }

        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer4, data.Length);
        output.Write(buffer4);

        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        output.Write(typeBytes);
        output.Write(data);

        var crc = UpdateCrc(0xFFFFFFFFu, typeBytes);
        crc = UpdateCrc(crc, data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer4, crc);
        output.Write(buffer4);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
