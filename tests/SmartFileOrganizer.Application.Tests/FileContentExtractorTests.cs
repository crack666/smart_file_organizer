using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Content;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// The UI-free evidence path: text, Office text, PDF page rendering to PNG, and the
/// classification input built from it. Uses real files in a temp directory.
/// </summary>
public class FileContentExtractorTests : IDisposable
{
    private readonly string _dir;
    private readonly FileContentExtractor _extractor = new();

    public FileContentExtractorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_content_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private FileNode Node(string name, FileType type)
    {
        var path = Path.Combine(_dir, name);
        return new FileNode
        {
            FullPath = path,
            Name = name,
            ParentPath = _dir,
            Extension = Path.GetExtension(name).ToLowerInvariant(),
            FileType = type,
            Size = File.Exists(path) ? new FileInfo(path).Length : 0
        };
    }

    [Fact]
    public async Task MissingFile_ReturnsMessageOnly()
    {
        var content = await _extractor.ExtractAsync(Node("absent.txt", FileType.Document));

        Assert.False(content.HasImage);
        Assert.False(content.HasText);
        Assert.Contains("does not exist", content.Message);
    }

    [Fact]
    public async Task TextFile_IsTruncatedToLimit()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "long.txt"), new string('a', FileContentExtractor.MaxTextChars + 500));

        var content = await _extractor.ExtractAsync(Node("long.txt", FileType.Document));

        Assert.StartsWith(new string('a', 100), content.TextPreview);
        Assert.EndsWith("preview truncated …", content.TextPreview);
    }

    [Fact]
    public async Task Docx_ParagraphTextIsExtracted()
    {
        var path = Path.Combine(_dir, "letter.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("word/document.xml");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("""
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                    <w:p><w:r><w:t>Dear </w:t></w:r><w:r><w:t>tax office</w:t></w:r></w:p>
                    <w:p><w:r><w:t>Second line</w:t></w:r></w:p>
                  </w:body>
                </w:document>
                """);
        }

        var content = await _extractor.ExtractAsync(Node("letter.docx", FileType.Document));

        Assert.Contains("Dear tax office", content.TextPreview);
        Assert.Contains("Second line", content.TextPreview);
    }

    [Fact]
    public async Task Pdf_FirstPageIsRenderedAsValidPng()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "page.pdf"), MinimalPdf());

        var content = await _extractor.ExtractAsync(Node("page.pdf", FileType.Document));

        Assert.True(content.HasImage, content.Message);
        Assert.True(content.IsRenderedPage);
        AssertValidRgbaPng(content.ImageBytes!);
    }

    [Fact]
    public async Task InputPreparer_SendsRenderedPdfPageAsBase64()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "page.pdf"), MinimalPdf());
        var preparer = new AiClassificationInputPreparer(_extractor, new OllamaOptions());

        var input = await preparer.PrepareAsync(Node("page.pdf", FileType.Document));

        var image = Assert.Single(input.Base64Images);
        AssertValidRgbaPng(Convert.FromBase64String(image));
    }

    [Fact]
    public async Task InputPreparer_SkipsRenderedPageAboveDocumentLimit()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "page.pdf"), MinimalPdf());
        var preparer = new AiClassificationInputPreparer(_extractor, new OllamaOptions { MaxDocumentUploadBytes = 10 });

        var input = await preparer.PrepareAsync(Node("page.pdf", FileType.Document));

        Assert.Empty(input.Base64Images);
    }

    [Fact]
    public async Task InputPreparer_NeverUploadsVideoBytes()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "clip.mp4"), [1, 2, 3]);
        var preparer = new AiClassificationInputPreparer(_extractor, new OllamaOptions());

        var input = await preparer.PrepareAsync(Node("clip.mp4", FileType.Video));

        Assert.Empty(input.Base64Images);
        Assert.Contains("Video file", input.ExtractedText);
    }

    /// <summary>Checks signature, chunk CRCs, IHDR (8-bit RGBA) and that IDAT inflates to the expected size.</summary>
    private static void AssertValidRgbaPng(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

        int width = 0, height = 0;
        using var idat = new MemoryStream();
        var pos = 8;
        var sawEnd = false;
        while (pos < png.Length)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, len);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + len));
            Assert.Equal(Crc32(png.AsSpan(pos + 4, len + 4)), crc);

            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                Assert.Equal(8, data[8]);
                Assert.Equal(6, data[9]);
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }
            else if (type == "IEND")
            {
                sawEnd = true;
            }

            pos += 12 + len;
        }

        Assert.True(sawEnd);
        Assert.True(width > 0 && height > 0);

        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.CopyTo(inflated);
        Assert.Equal((long)height * (width * 4 + 1), inflated.Length);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>A one-page PDF with a filled rectangle and correct xref offsets.</summary>
    private static byte[] MinimalPdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Contents 4 0 R >>",
            "<< /Length 31 >>\nstream\n0 0 1 rg 20 20 100 100 re f\n\nendstream"
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
