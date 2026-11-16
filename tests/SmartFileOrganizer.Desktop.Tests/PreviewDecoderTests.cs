using System.Buffers.Binary;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using SkiaSharp;
using SmartFileOrganizer.Infrastructure.Content;

namespace SmartFileOrganizer.Desktop.Tests;

/// <summary>
/// What the decoder of the GUI (Avalonia over Skia) really draws, against what <see cref="PreviewFormats"/> calls an
/// image (https://github.com/crack666/smart_file_organizer/issues/109): JPEG, PNG, GIF, BMP, WebP and ICO decode; TIFF
/// does not, so it gets the hint instead of an empty area. Each image is generated here.
/// </summary>
public class PreviewDecoderTests
{
    [AvaloniaTheory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("gif")]
    [InlineData("bmp")]
    [InlineData("ico")]
    public void FormatsCalledImage_AreDrawnByTheGuiDecoder(string format)
    {
        var bytes = Encode(format);
        Assert.Equal(PreviewKind.Image, PreviewFormats.Decide("." + format, FileSignature.Detect(bytes)));
        using var bitmap = new Bitmap(new MemoryStream(bytes));
        Assert.True(bitmap.PixelSize.Width > 0);
    }

    [AvaloniaFact]
    public void Tiff_IsNotDrawn_SoItIsCalledNotDecoded()
    {
        var tiff = Tiff();
        Assert.Equal(PreviewKind.ImageNotDecoded, PreviewFormats.Decide(".tif", FileSignature.Detect(tiff)));
        Assert.ThrowsAny<Exception>(() => new Bitmap(new MemoryStream(tiff)));
    }

    private static byte[] Encode(string format)
    {
        using var bitmap = new SKBitmap(16, 12);
        bitmap.Erase(new SKColor(0x4A, 0x90, 0xD9));
        using var image = SKImage.FromBitmap(bitmap);
        return format switch
        {
            "jpeg" => image.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray(),
            "png" => image.Encode(SKEncodedImageFormat.Png, 100).ToArray(),
            "webp" => image.Encode(SKEncodedImageFormat.Webp, 80).ToArray(),
            "gif" => Gif(),
            "bmp" => Bmp(16, 12),
            "ico" => Ico(image.Encode(SKEncodedImageFormat.Png, 100).ToArray()),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    /// <summary>A 1×1 GIF89a with a two-colour palette.</summary>
    private static byte[] Gif() =>
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0x4A, 0x90, 0xD9, 0xFF, 0xFF, 0xFF,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B
    ];

    /// <summary>An uncompressed 24-bit BMP.</summary>
    private static byte[] Bmp(int width, int height)
    {
        var row = (width * 3 + 3) & ~3;
        var bytes = new byte[54 + row * height];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        for (var i = 54; i < bytes.Length; i += 3) bytes[i] = 0xD9;
        return bytes;
    }

    /// <summary>An icon file holding one PNG image, as Windows Vista and later write them.</summary>
    private static byte[] Ico(byte[] png)
    {
        var bytes = new byte[6 + 16 + png.Length];
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), 1);
        bytes[6] = 16;
        bytes[7] = 12;
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(10), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), png.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 22);
        png.CopyTo(bytes, 22);
        return bytes;
    }

    /// <summary>A 1×1 grey TIFF, little-endian, uncompressed.</summary>
    private static byte[] Tiff()
    {
        var b = new List<byte> { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00 };
        (ushort Tag, ushort Type, uint Value)[] entries =
        [
            (256, 3, 1), (257, 3, 1), (258, 3, 8), (259, 3, 1), (262, 3, 1), (273, 4, 8 + 2 + 8 * 12 + 4), (277, 3, 1), (279, 4, 1)
        ];
        b.AddRange(BitConverter.GetBytes((ushort)entries.Length));
        foreach (var (tag, type, value) in entries)
        {
            b.AddRange(BitConverter.GetBytes(tag));
            b.AddRange(BitConverter.GetBytes(type));
            b.AddRange(BitConverter.GetBytes(1u));
            b.AddRange(BitConverter.GetBytes(value));
        }
        b.AddRange(BitConverter.GetBytes(0u));
        b.Add(0x80);
        return b.ToArray();
    }
}
