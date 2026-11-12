using System.IO.Compression;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Content;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Evidence per file type, built from generated files in a temp directory: what the model sees,
/// which facts are read without it, and what the evidence names as missing.
/// No video tool is used (the frame extractor is told there is none).
/// </summary>
public class FileEvidenceTests : IDisposable
{
    private readonly string _dir;
    private readonly AiClassificationInputPreparer _preparer =
        new(new FileContentExtractor(), new OllamaOptions(), new VideoFrameExtractor(() => null));

    public FileEvidenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_evidence_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private FileNode Node(string name, FileType type, string relativeDir = "")
    {
        var path = Path.Combine(_dir, name);
        return new FileNode
        {
            Id = 1,
            FullPath = path,
            Name = name,
            ParentPath = _dir,
            RelativeDir = relativeDir,
            Extension = Path.GetExtension(name).ToLowerInvariant(),
            FileType = type,
            Size = File.Exists(path) ? new FileInfo(path).Length : 0
        };
    }

    private static string? Fact(OllamaClassificationInput input, FileFactKey key) =>
        input.Facts.FirstOrDefault(f => f.Key == key)?.Value;

    private static FactSource? Source(OllamaClassificationInput input, FileFactKey key) =>
        input.Facts.FirstOrDefault(f => f.Key == key)?.Source;

    // ─── PDF ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pdf_WithThreePages_GivesPageCountTextOfAllPagesAndFirstPageImage()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "report.pdf"),
            GeneratedFiles.PdfWithText("Invoice 2019 page one", "Terms page two", "Signature page three"));

        var input = await _preparer.PrepareAsync(Node("report.pdf", FileType.Document));

        Assert.Equal("3", Fact(input, FileFactKey.PageCount));
        Assert.Equal(FactSource.Metadata, Source(input, FileFactKey.PageCount));
        Assert.Equal("true", Fact(input, FileFactKey.HasText));
        Assert.Equal("pdf", Fact(input, FileFactKey.DetectedFormat));
        Assert.Contains("Invoice 2019 page one", input.ExtractedText);
        Assert.Contains("Terms page two", input.ExtractedText);
        Assert.Contains("[Page 3]", input.ExtractedText);
        Assert.Single(input.Base64Images); // text present: page 1 only
        Assert.Contains(input.Contains, c => c.Contains("all 3 pages"));
        Assert.Contains(input.Contains, c => c.Contains("page 1 of 3 rendered"));
        Assert.Null(input.RuleResult);
    }

    [Fact]
    public async Task Pdf_WithoutText_RendersFirstAndMiddlePage_AndSaysTextIsMissing()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "scan.pdf"), GeneratedFiles.PdfImageOnly(5));

        var input = await _preparer.PrepareAsync(Node("scan.pdf", FileType.Document));

        Assert.Equal("5", Fact(input, FileFactKey.PageCount));
        Assert.Equal("false", Fact(input, FileFactKey.HasText));
        Assert.Equal(2, input.Base64Images.Count);
        Assert.Contains(input.Contains, c => c.Contains("page 3 of 5"));
        Assert.Contains(input.Missing, m => m.Contains("embedded text: none"));
    }

    // ─── Archives ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Zip_ListsEntriesWithoutUnpacking_AndRecognisesPhotos()
    {
        var path = Path.Combine(_dir, "holiday.zip");
        GeneratedFiles.Zip(path, ("2019/IMG_0001.JPG", 1000), ("2019/IMG_0002.JPG", 2000), ("2019/IMG_0003.jpg", 3000), ("notes.txt", 10));

        var input = await _preparer.PrepareAsync(Node("holiday.zip", FileType.Archive));

        Assert.Equal("4", Fact(input, FileFactKey.EntryCount));
        Assert.Equal("6010", Fact(input, FileFactKey.UnpackedBytes));
        Assert.Equal("photos", Fact(input, FileFactKey.ArchiveContent));
        Assert.Equal(FactSource.Rule, Source(input, FileFactKey.ArchiveContent));
        Assert.Contains("2019/IMG_0002.JPG | 2000", input.ExtractedText);
        Assert.Contains("notes.txt | 10", input.ExtractedText);
        Assert.Contains(input.Missing, m => m.Contains("never unpacked"));
        Assert.Empty(input.Base64Images);
        Assert.Null(input.RuleResult);
    }

    [Fact]
    public async Task Zip_WithSetupProgram_IsSoftware()
    {
        var path = Path.Combine(_dir, "tool.zip");
        GeneratedFiles.Zip(path, ("setup.exe", 50), ("readme.txt", 5), ("data/a.bin", 5), ("data/b.bin", 5));

        var input = await _preparer.PrepareAsync(Node("tool.zip", FileType.Archive));

        Assert.Equal("software", Fact(input, FileFactKey.ArchiveContent));
    }

    [Fact]
    public void ArchiveListing_StopsAtEntryLimit()
    {
        var path = Path.Combine(_dir, "many.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (var i = 0; i <= ArchiveListingReader.MaxEntries + 5; i++)
                zip.CreateEntry($"f{i}.txt", CompressionLevel.NoCompression);
        }

        var listing = ArchiveListingReader.Read(path, "zip", CancellationToken.None);

        Assert.True(listing.Truncated);
        Assert.Equal(ArchiveListingReader.MaxEntries, listing.Entries.Count);
    }

    [Fact]
    public async Task GzipTarball_IsNotListed_AndTheEvidenceSaysWhy()
    {
        var path = Path.Combine(_dir, "backup.tar.gz");
        await using (var file = File.Create(path))
        await using (var gz = new GZipStream(file, CompressionLevel.Fastest))
            gz.Write(new byte[1024]);

        var input = await _preparer.PrepareAsync(Node("backup.tar.gz", FileType.Archive));

        Assert.Equal("gzip", Fact(input, FileFactKey.DetectedFormat));
        Assert.Null(Fact(input, FileFactKey.EntryCount));
        Assert.Contains(input.Missing, m => m.Contains("only known after unpacking"));
    }

    // ─── Images ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Jpeg_WithExif_GivesCaptureDateDeviceLocationFlagAndCameraOrigin()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "IMG_0042.jpg"),
            GeneratedFiles.JpegWithExif(new DateTime(2019, 7, 14, 10, 22, 33), "Canon", "Canon EOS 600D", withGps: true));

        var input = await _preparer.PrepareAsync(Node("IMG_0042.jpg", FileType.Image));

        Assert.Equal("2019-07-14T10:22:33", Fact(input, FileFactKey.TakenAt));
        Assert.Equal("Canon EOS 600D", Fact(input, FileFactKey.Device));
        Assert.Equal("true", Fact(input, FileFactKey.HasLocation));
        Assert.Equal("640", Fact(input, FileFactKey.Width));
        Assert.Equal("480", Fact(input, FileFactKey.Height));
        Assert.Equal("camera", Fact(input, FileFactKey.ImageOrigin));
        Assert.Equal(FactSource.Rule, Source(input, FileFactKey.ImageOrigin));
        Assert.Single(input.Base64Images);
        // Only "present" is kept: no coordinate appears anywhere in the evidence.
        Assert.DoesNotContain(input.Facts, f => f.Value.Contains("52"));
        Assert.DoesNotContain("52", input.ExtractedText);
    }

    [Fact]
    public async Task Jpeg_WithoutGps_HasNoLocationFact()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "p.jpg"),
            GeneratedFiles.JpegWithExif(new DateTime(2008, 1, 2, 3, 4, 5), "NIKON", "E4500", withGps: false));

        var input = await _preparer.PrepareAsync(Node("p.jpg", FileType.Image));

        Assert.Null(Fact(input, FileFactKey.HasLocation));
        Assert.Equal("NIKON E4500", Fact(input, FileFactKey.Device));
    }

    [Theory]
    [InlineData("Screenshot 2021-03-04 101112.png", 800, 1000, null, "screenshot")]
    [InlineData("Bildschirmfoto vom 2020-01-01.png", 333, 444, null, "screenshot")]
    [InlineData("image.png", 1920, 1080, null, "screenshot")]
    [InlineData("image.png", 1000, 1000, null, null)]
    [InlineData("vertrag_scan.png", 1000, 1400, null, "scan")]
    [InlineData("page.tif", 2480, 3508, 300.0, "scan")]
    public void ImageOrigin_IsDecidedFromNameSizeAndResolution(string name, int width, int height, double? dpi, string? expected)
    {
        var meta = new MediaMetadata { Width = width, Height = height, DpiX = dpi };

        var origin = MediaMetadataReader.DecideOrigin(meta, name, Path.GetExtension(name));

        Assert.Equal(expected, origin?.ToCode());
    }

    [Fact]
    public void ImageOrigin_ScannerSoftwareWinsOverCameraMake()
    {
        var meta = new MediaMetadata { Make = "Canon", Model = "CanoScan LiDE 220" };
        Assert.Equal(ImageOrigin.Scan, MediaMetadataReader.DecideOrigin(meta, "x.jpg", ".jpg"));
    }

    // ─── Audio ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mp3_WithTags_GivesTitleArtistAlbumDurationAndMusic()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "track01.mp3"),
            GeneratedFiles.Mp3WithTags("Morning Song", "Test Band", "First Album"));

        var input = await _preparer.PrepareAsync(Node("track01.mp3", FileType.Audio));

        Assert.Equal("Morning Song", Fact(input, FileFactKey.Title));
        Assert.Equal("Test Band", Fact(input, FileFactKey.Artist));
        Assert.Equal("First Album", Fact(input, FileFactKey.Album));
        Assert.Equal("music", Fact(input, FileFactKey.AudioKind));
        Assert.True(int.TryParse(Fact(input, FileFactKey.DurationSeconds), out var seconds) && seconds is >= 4 and <= 6,
            $"duration {Fact(input, FileFactKey.DurationSeconds)}");
        Assert.Contains(input.Missing, m => m.Contains("not transcribed"));
        Assert.Empty(input.Base64Images);
    }

    [Theory]
    [InlineData("PTT-20190312-WA0003.opus", "", "speech")]
    [InlineData("track.mp3", "Sprachmemos", "speech")]
    [InlineData("call.amr", "", "speech")]
    [InlineData("track.mp3", "Music/Rock", "music")]
    [InlineData("track.mp3", "Downloads", null)]
    public void AudioKind_IsDecidedFromFormatAndPath_WhenTagsAreMissing(string name, string dir, string? expected)
    {
        var kind = AudioMetadataReader.DecideKind(new AudioMetadata(), name, Path.GetExtension(name), dir);
        Assert.Equal(expected, kind?.ToCode());
    }

    // ─── Video ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mp4_GivesDurationSizeAndDate_AndSaysFramesAreMissingWithoutTool()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "clip.mp4"),
            GeneratedFiles.Mp4Header(new DateTime(2016, 8, 1, 12, 0, 0, DateTimeKind.Utc), 95, 1280, 720));

        var input = await _preparer.PrepareAsync(Node("clip.mp4", FileType.Video));

        Assert.Equal("95", Fact(input, FileFactKey.DurationSeconds));
        Assert.Equal("1280", Fact(input, FileFactKey.Width));
        Assert.Equal("720", Fact(input, FileFactKey.Height));
        Assert.StartsWith("2016-08-01", Fact(input, FileFactKey.TakenAt));
        Assert.Empty(input.Base64Images);
        Assert.Contains(input.Missing, m => m.Contains("no video tool"));
        Assert.Contains(input.Missing, m => m.Contains("never uploaded"));
    }

    // ─── Office and text ──────────────────────────────────────────────────────

    [Fact]
    public async Task Docx_GivesTextAndDocumentProperties()
    {
        GeneratedFiles.Docx(Path.Combine(_dir, "brief.docx"), "Rental agreement", "A. Writer", new DateTime(2012, 5, 6), "Dear landlord");

        var input = await _preparer.PrepareAsync(Node("brief.docx", FileType.Document));

        Assert.Equal("Rental agreement", Fact(input, FileFactKey.Title));
        Assert.Equal("A. Writer", Fact(input, FileFactKey.Author));
        Assert.Equal("2012-05-06", Fact(input, FileFactKey.CreatedAt));
        Assert.Equal("2", Fact(input, FileFactKey.PageCount));
        Assert.Contains("Dear landlord", input.ExtractedText);
        Assert.Contains(input.Contains, c => c == "document properties");
    }

    [Fact]
    public async Task UnknownExtensionWithText_GetsAnExcerpt()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "release.nfo"), "Release notes of an old program");

        var input = await _preparer.PrepareAsync(Node("release.nfo", FileType.Other));

        Assert.Contains("Release notes of an old program", input.ExtractedText);
        Assert.Null(input.RuleResult);
    }

    // ─── Rules ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownBinary_GetsRuleResult_WithoutContentForTheModel()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "blob.dat"), [0x00, 0x13, 0x37, 0x00, 0xFF, 0x00]);

        var input = await _preparer.PrepareAsync(Node("blob.dat", FileType.Other));

        var rule = Assert.IsType<FileClassification>(input.RuleResult);
        Assert.Equal(FileCategory.Unknown, rule.Category);
        Assert.False(rule.IsAiResult);
        Assert.Equal("rule:unknown-binary", rule.ModelUsed);
        Assert.Contains(input.Missing, m => m.Contains("decided by rule"));
    }

    [Theory]
    [InlineData("setup.exe", FileType.Executable, new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, FileCategory.SoftwareInstaller)]
    [InlineData("helper.dll", FileType.Executable, new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, FileCategory.SystemFile)]
    [InlineData("arial.ttf", FileType.Font, new byte[] { 0x00, 0x01, 0x00, 0x00 }, FileCategory.SystemFile)]
    [InlineData("Thumbs.db", FileType.Database, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0, 0, 0, 0 }, FileCategory.TrashCandidate)]
    [InlineData("addresses.sqlite", FileType.Database, new byte[] { 0x53, 0x51, 0x4C, 0x69 }, FileCategory.ReviewNeeded)]
    public async Task ProgramsFontsAndDatabases_AreDecidedByRule(string name, FileType type, byte[] content, FileCategory expected)
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, name), content);

        var input = await _preparer.PrepareAsync(Node(name, type));

        Assert.Equal(expected, Assert.IsType<FileClassification>(input.RuleResult).Category);
    }

    // ─── Prompt ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Prompt_ShowsFactsContainsAndMissing()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "IMG_0042.jpg"),
            GeneratedFiles.JpegWithExif(new DateTime(2019, 7, 14, 10, 22, 33), "Canon", "Canon EOS 600D", withGps: false));
        var input = await _preparer.PrepareAsync(Node("IMG_0042.jpg", FileType.Image));

        var prompt = OllamaService.BuildClassificationPrompt(input);

        Assert.Contains("Facts read from the file without a model", prompt);
        Assert.Contains("taken_at: 2019-07-14T10:22:33 (metadata)", prompt);
        Assert.Contains("image_origin: camera (rule)", prompt);
        Assert.Contains("The evidence contains:", prompt);
        Assert.Contains("  - the image itself", prompt);
        Assert.Contains("Attached images: 1", prompt);
    }

    /// <summary>
    /// Issue 50: three real models marked the same ordinary invoices and a rental contract as finance and the
    /// same formal letters to companies and authorities as private messages, because the prompt called invoices
    /// financial records and named letters without saying which. The prompt now draws the line with examples on
    /// both sides, and no longer files invoices next to bank statements.
    /// </summary>
    [Fact]
    public void Prompt_DrawsTheLineOfFinanceAndPrivateMessage_WithExamplesOnBothSides()
    {
        var prompt = OllamaService.BuildClassificationPrompt(new OllamaClassificationInput
        {
            File = new FileNode { Name = "Rechnung.pdf", Extension = ".pdf", FileType = FileType.Document }
        });

        Assert.DoesNotContain("invoices, bank statements", prompt);
        var finance = prompt.Split('\n').Single(l => l.StartsWith("- finance:", StringComparison.Ordinal));
        Assert.Contains("bank", finance);
        Assert.Contains("payslips", finance);
        Assert.Contains("Not:", finance);
        Assert.Contains("invoice", finance[finance.IndexOf("Not:", StringComparison.Ordinal)..]);
        Assert.Contains("rental", finance[finance.IndexOf("Not:", StringComparison.Ordinal)..]);

        var message = prompt.Split('\n').Single(l => l.StartsWith("- private_message:", StringComparison.Ordinal));
        Assert.Contains("personal letters", message);
        Assert.Contains("chat", message);
        var notMessage = message[message.IndexOf("Not:", StringComparison.Ordinal)..];
        Assert.Contains("company", notMessage);
        Assert.Contains("authority", notMessage);

        // One category per kind of paper: the old "PersonalDocument or Letter", "Invoice or Document" are gone.
        Assert.DoesNotContain("PersonalDocument or Letter", prompt);
        Assert.DoesNotContain("Invoice or Document", prompt);
        foreach (var category in new[] { "- Invoice:", "- Letter:", "- PersonalDocument:", "- Document:", "- DocumentScan:" })
            Assert.Contains(category, prompt);
    }

    [Fact]
    public async Task Prompt_ListsWhatIsMissing()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "clip.mp4"), [1, 2, 3, 4, 5, 6, 7, 8]);
        var input = await _preparer.PrepareAsync(Node("clip.mp4", FileType.Video));

        var prompt = OllamaService.BuildClassificationPrompt(input);

        Assert.Contains("Not available (do not guess these", prompt);
        Assert.Contains("no video tool", prompt);
    }

    // ─── Signatures ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "png")]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 }, "7z")]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0 }, "zip")]
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46, 2, 1 }, "elf")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, "text")]
    [InlineData(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x0A }, "text")]
    [InlineData(new byte[] { 0x00, 0x13, 0x37, 0x00 }, null)]
    public void FileSignature_RecognisesFormats(byte[] prefix, string? expected)
    {
        Assert.Equal(expected, FileSignature.Detect(prefix));
    }
}
