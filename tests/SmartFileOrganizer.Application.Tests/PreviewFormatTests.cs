using System.IO.Compression;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Content;
using SmartFileOrganizer.Scanning.Engine;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Which preview a file gets and what the readers behind it return
/// (https://github.com/crack666/smart_file_organizer/issues/109): the format of the first bytes before the extension,
/// the text of a Word 97-2003 document and of an RTF file, the names in an archive, and an honest message for the rest.
/// Every file is generated here; nothing is copied from a real document.
/// </summary>
public class PreviewFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sfo_preview_{Guid.NewGuid():N}");
    private readonly FileContentExtractor _extractor = new();

    public PreviewFormatTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private FileNode Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return new FileNode { FullPath = path, Name = name, ParentPath = _dir, Extension = ext, FileType = FileTypeDetector.Detect(ext), Size = bytes.Length };
    }

    [Theory]
    [InlineData(".jfif", "jpeg", PreviewKind.Image)]
    [InlineData(".jpe", "jpeg", PreviewKind.Image)]
    [InlineData(".png", "jpeg", PreviewKind.Image)] // misnamed: the bytes decide
    [InlineData(".bin", "png", PreviewKind.Image)]
    [InlineData(".webp", "webp", PreviewKind.Image)]
    [InlineData(".tif", "tiff", PreviewKind.ImageNotDecoded)]
    [InlineData(".heic", "mp4", PreviewKind.ImageNotDecoded)] // HEIF is an ISO media file, not a video
    [InlineData(".heic", "heif", PreviewKind.ImageNotDecoded)]
    [InlineData(".mp4", "heif", PreviewKind.ImageNotDecoded)] // misnamed: the brand decides
    [InlineData(".bin", "avif", PreviewKind.ImageNotDecoded)]
    [InlineData(".bin", "quicktime", PreviewKind.Video)]
    [InlineData(".bin", "m4a", PreviewKind.Audio)]
    [InlineData(".jpg", "text", PreviewKind.ImageNotDecoded)]
    [InlineData(".docx", "zip", PreviewKind.OfficeText)]
    [InlineData(".odp", "zip", PreviewKind.OfficeText)]
    [InlineData(".doc", "ole", PreviewKind.LegacyWord)]
    [InlineData(".doc", "rtf", PreviewKind.Rtf)] // Word saves RTF under .doc when asked
    [InlineData(".xls", "ole", PreviewKind.LegacyOffice)]
    [InlineData(".ppt", "ole", PreviewKind.LegacyOffice)]
    [InlineData(".rtf", "rtf", PreviewKind.Rtf)]
    [InlineData(".pdf", "pdf", PreviewKind.Pdf)]
    [InlineData(".dat", "pdf", PreviewKind.Pdf)]
    [InlineData(".nfo", "text", PreviewKind.Text)]
    [InlineData(".tsv", null, PreviewKind.Text)]
    [InlineData(".ts", "text", PreviewKind.Text)]
    [InlineData(".ts", null, PreviewKind.Video)]
    [InlineData(".zip", "zip", PreviewKind.Archive)]
    [InlineData(".cbz", "zip", PreviewKind.Archive)]
    [InlineData(".gz", "gzip", PreviewKind.CompressedStream)]
    [InlineData(".mkv", "matroska", PreviewKind.Video)]
    [InlineData(".bin", "matroska", PreviewKind.Video)]
    [InlineData(".m4a", "mp4", PreviewKind.Audio)]
    [InlineData(".bin", "text", PreviewKind.Text)]
    [InlineData(".bin", "pe", PreviewKind.None)]
    [InlineData(".xyz", null, PreviewKind.None)]
    public void Decide_GoesByTheFirstBytesWhereTheyDecide_ElseByTheExtension(string ext, string? format, PreviewKind expected) =>
        Assert.Equal(expected, PreviewFormats.Decide(ext, format));

    [Fact]
    public void JfifAndJpe_AreImagesForTheScan()
    {
        Assert.Equal(FileType.Image, FileTypeDetector.Detect(".jfif"));
        Assert.Equal(FileType.Image, FileTypeDetector.Detect(".JPE"));
    }

    [Fact]
    public async Task Jfif_GivesTheImageBytes()
    {
        var jpeg = GeneratedFiles.JpegWithExif(new DateTime(2021, 7, 3, 10, 0, 0), "Make", "Model", withGps: false, 64, 48);
        var content = await _extractor.ExtractAsync(Write("photo.jfif", jpeg));
        Assert.Equal(PreviewKind.Image, content.Kind);
        Assert.Equal(jpeg, content.ImageBytes);
    }

    [Fact]
    public async Task LegacyDoc_GivesItsText_FromBothKindsOfPieces_WithTheFieldResultOnly()
    {
        var doc = GeneratedFiles.LegacyDoc(
            "Mietvertrag für die Wohnung\rKaution: 900 € – Seite \u0013 PAGE \u00141\u0015\r",
            "Übergabe am 1. März\u0007Schlüssel: 2\u0007\r");
        var content = await _extractor.ExtractAsync(Write("vertrag.doc", doc));

        Assert.Equal(PreviewKind.LegacyWord, content.Kind);
        Assert.True(content.HasText, content.Message);
        var lines = content.TextPreview.Split('\n');
        Assert.Equal("Mietvertrag für die Wohnung", lines[0]);
        Assert.Equal("Kaution: 900 € – Seite 1", lines[1]);
        Assert.Equal("Übergabe am 1. März\tSchlüssel: 2", lines[2]);
        Assert.DoesNotContain("PAGE", content.TextPreview);
    }

    [Fact]
    public void LegacyDoc_StopsAtTheLimit_AndSaysSo()
    {
        var doc = GeneratedFiles.LegacyDoc(new string('a', 300) + "\r", "b\r");
        var text = LegacyWordReader.Read(doc, maxChars: 100);
        Assert.Null(text.Problem);
        Assert.True(text.Truncated);
        Assert.Equal(new string('a', 100), text.Text);
    }

    [Fact]
    public async Task LegacyDoc_Damaged_OrNoWord_SaysWhy_InsteadOfNothing()
    {
        var doc = GeneratedFiles.LegacyDoc("Text\r", "mehr\r");
        var cut = doc[..1024];
        var damaged = await _extractor.ExtractAsync(Write("kaputt.doc", cut));
        Assert.False(damaged.HasText);
        Assert.StartsWith("Kein Textauszug aus dem Word-Dokument: ", damaged.Message);

        var other = (byte[])doc.Clone();
        // Rename the stream: a compound file without WordDocument, as an old Excel file is.
        var at = other.AsSpan().IndexOf("W\0o\0r\0d\0D\0o\0c\0"u8);
        "X"u8.CopyTo(other.AsSpan(at));
        var xls = await _extractor.ExtractAsync(Write("tabelle.doc", other));
        Assert.Contains("kein Word-97-2003-Dokument", xls.Message);

        var legacyExcel = await _extractor.ExtractAsync(Write("tabelle.xls", other));
        Assert.Equal(PreviewKind.LegacyOffice, legacyExcel.Kind);
        Assert.Contains("altes Office-Format", legacyExcel.Message);
    }

    [Fact]
    public async Task Rtf_GivesTheTextWithoutControlWordsAndTables()
    {
        const string rtf = """
            {\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Arial;}}{\colortbl;\red0\green0\blue0;}
            {\info{\title Geheimer Titel}{\author Niemand}}
            {\*\generator Some Writer;}\viewkind4\uc1\pard\f0\fs22 Rechnung Nr. 42\par
            Betrag: 19,99 \'80, f\'e4llig am Montag\par
            Gr\u252?\u223?e \{ok\}\par}
            """;
        var content = await _extractor.ExtractAsync(Write("brief.rtf", System.Text.Encoding.ASCII.GetBytes(rtf)));

        Assert.Equal(PreviewKind.Rtf, content.Kind);
        Assert.Equal("Rechnung Nr. 42\nBetrag: 19,99 €, fällig am Montag\nGrüße {ok}", content.TextPreview.Replace("\r", string.Empty));
        Assert.DoesNotContain("Arial", content.TextPreview);
        Assert.DoesNotContain("Geheimer", content.TextPreview);
    }

    [Fact]
    public async Task Odp_GivesTheTextOfItsPages()
    {
        var path = Path.Combine(_dir, "vortrag.odp");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(zip.CreateEntry("content.xml").Open());
            w.Write("""
                <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                    xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
                  <office:body><office:presentation><draw:page><draw:frame><draw:text-box>
                    <text:p>Erste Folie</text:p></draw:text-box></draw:frame></draw:page></office:presentation></office:body>
                </office:document-content>
                """);
        }
        var content = await _extractor.ExtractAsync(new FileNode { FullPath = path, Name = "vortrag.odp", Extension = ".odp", FileType = FileType.Other });
        Assert.Contains("Erste Folie", content.TextPreview);
    }

    [Fact]
    public async Task Archive_ListsItsNames_WithoutUnpacking()
    {
        var path = Path.Combine(_dir, "fotos.zip");
        GeneratedFiles.Zip(path, ("Urlaub/IMG_0001.jpg", 2048), ("Urlaub/IMG_0002.jpg", 1024), ("liesmich.txt", 10));
        var before = Directory.GetFileSystemEntries(_dir).Length;

        var content = await _extractor.ExtractAsync(new FileNode { FullPath = path, Name = "fotos.zip", Extension = ".zip", FileType = FileType.Archive });

        Assert.Equal(PreviewKind.Archive, content.Kind);
        Assert.Contains("Urlaub/IMG_0001.jpg  (2 KB)", content.TextPreview);
        Assert.Contains("liesmich.txt  (10 B)", content.TextPreview);
        Assert.StartsWith("Archiv: 3 Dateien", content.Message);
        Assert.Equal(before, Directory.GetFileSystemEntries(_dir).Length);
    }

    [Fact]
    public async Task UnsupportedAndUndecodable_SayWhy_InsteadOfAnEmptyArea()
    {
        var unknown = await _extractor.ExtractAsync(Write("daten.xyz", [0x00, 0x13, 0x37, 0x00, 0xFF, 0x00, 0x01, 0x02]));
        Assert.Equal(PreviewKind.None, unknown.Kind);
        Assert.StartsWith("Keine Vorschau für XYZ", unknown.Message);

        var tiff = await _extractor.ExtractAsync(Write("scan.tif", [0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0, 0, 0]));
        Assert.Equal(PreviewKind.ImageNotDecoded, tiff.Kind);
        Assert.False(tiff.HasImage);
        Assert.StartsWith("Kein Bild: TIF", tiff.Message);
    }

    [Fact]
    public async Task UnknownExtensionWithTextBytes_IsReadAsText()
    {
        var content = await _extractor.ExtractAsync(Write("notiz.unbekannt", "Einkaufsliste: Milch, Brot\n"u8.ToArray()));
        Assert.Equal(PreviewKind.Text, content.Kind);
        Assert.StartsWith("Einkaufsliste", content.TextPreview);
    }
}
