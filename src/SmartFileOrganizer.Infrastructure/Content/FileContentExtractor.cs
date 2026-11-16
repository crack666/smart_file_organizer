using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>
/// What could be read from a file: an encoded image (original image bytes or a PNG of the
/// first PDF page), a truncated text excerpt, or a message explaining why neither exists.
/// Free of any UI types so both the GUI preview and the model input can be built from it.
/// </summary>
public sealed class FileContent
{
    /// <summary>Encoded image bytes (PNG/JPEG/... as stored on disk, or PNG for rendered PDF pages).</summary>
    public byte[]? ImageBytes { get; init; }

    /// <summary>Which reader ran (<see cref="PreviewFormats.Decide"/>); <see cref="PreviewKind.None"/> when none.</summary>
    public PreviewKind Kind { get; init; }

    /// <summary>True when <see cref="ImageBytes"/> is a PNG rendered from a document page.</summary>
    public bool IsRenderedPage { get; init; }

    public string TextPreview { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Set when the file was deliberately not read because it crosses a limit of <see cref="OfficePackage"/>
    /// (too many entries, a part or all parts unpacking to too much); says which limit and where.
    /// </summary>
    public string? NotReadCause { get; init; }

    public bool HasImage => ImageBytes is { Length: > 0 };
    public bool HasText => !string.IsNullOrWhiteSpace(TextPreview);
}

/// <summary>What <see cref="FileContentExtractor.ReadPdf"/> found in a PDF.</summary>
/// <param name="PageCount">Number of pages.</param>
/// <param name="Text">Embedded text of the first pages, each introduced by <c>[Page n]</c>.</param>
/// <param name="PagesRead">How many pages were read for text.</param>
/// <param name="TextTruncated">True when pages or characters were left out because of the limit.</param>
/// <param name="HasText">True when at least one read page carries text; false for an image-only PDF.</param>
/// <param name="RenderedPages">Rendered pages (1-based number and PNG).</param>
public sealed record PdfEvidence(
    int PageCount,
    string Text,
    int PagesRead,
    bool TextTruncated,
    bool HasText,
    IReadOnlyList<(int Page, byte[] Png)> RenderedPages);

/// <summary>
/// Reads file content for preview and AI evidence: images as bytes, PDF page 1 rendered via
/// PDFium (Docnet.Core), Office/OpenDocument text (within the limits of <see cref="OfficePackage"/>),
/// and text-like files as a truncated excerpt.
/// </summary>
public class FileContentExtractor
{
    public const int MaxTextChars = 12_000;
    private const int PdfTargetWidth = 1200;
    private const int PdfTargetHeight = 1600;

    private static readonly object PdfRenderLock = new();
    private static readonly Lazy<IDocLib> PdfLib = new(() => DocLib.Instance);

    /// <summary>Archive names shown in a preview; the listing itself stops at <see cref="ArchiveListingReader.MaxEntries"/>.</summary>
    public const int MaxListedArchiveEntries = 200;

    /// <summary>
    /// Reads what the preview of <paramref name="file"/> shows. Which reader runs is decided by
    /// <see cref="PreviewFormats.Decide"/> from the first bytes and the extension (until 16.11.2026 from the scan type and
    /// a short extension list, so <c>.jfif</c>, <c>.doc</c>, <c>.rtf</c>, <c>.odp</c> and archives had no preview).
    /// </summary>
    public async Task<FileContent> ExtractAsync(FileNode file, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(file.FullPath) || !File.Exists(file.FullPath))
        {
            return new FileContent { Message = "Keine Vorschau: die Datei ist nicht mehr da." };
        }

        var ext = file.Extension?.ToLowerInvariant() ?? string.Empty;
        var format = PreviewFormats.Sniff(file.FullPath);
        var kind = PreviewFormats.Decide(ext, format);
        try
        {
            return kind switch
            {
                PreviewKind.Image => new FileContent { Kind = kind, ImageBytes = await File.ReadAllBytesAsync(file.FullPath, ct) },
                PreviewKind.ImageNotDecoded => new FileContent
                {
                    Kind = kind,
                    Message = $"Kein Bild: {FormatName(ext, format)} kann die Vorschau nicht zeichnen. „Öffnen“ zeigt es im Standardprogramm."
                },
                PreviewKind.Pdf => RenderPdfFirstPage(file.FullPath),
                PreviewKind.OfficeText => await ExtractOfficeTextAsync(file.FullPath, ext, ct),
                PreviewKind.LegacyWord => await Task.Run(() => ReadLegacyWord(file.FullPath), ct),
                PreviewKind.LegacyOffice => new FileContent
                {
                    Kind = kind,
                    Message = $"Kein Textauszug: altes Office-Format ({FormatName(ext, format)}) wird nicht gelesen. „Öffnen“ zeigt die Datei."
                },
                PreviewKind.Rtf => await ReadRtfAsync(file.FullPath, ct),
                PreviewKind.Text => await ReadTextAsync(file.FullPath, ct),
                PreviewKind.Archive => await Task.Run(() => ListArchive(file.FullPath, format, ct), ct),
                PreviewKind.CompressedStream => new FileContent
                {
                    Kind = kind,
                    Message = "Gepackter Datenstrom: was darin liegt, zeigt erst das Entpacken, und das tut die Vorschau nie."
                },
                PreviewKind.Video or PreviewKind.Audio => new FileContent { Kind = kind },
                _ => new FileContent
                {
                    Kind = kind,
                    Message = $"Keine Vorschau für {FormatName(ext, format)}. „Öffnen“ zeigt die Datei im Standardprogramm."
                }
            };
        }
        catch (ContentLimitException ex)
        {
            return new FileContent { Message = $"Not read: {ex.Message}.", NotReadCause = ex.Message };
        }
        catch (Exception ex)
        {
            return new FileContent { Message = $"Preview unavailable: {ex.Message}" };
        }
    }

    private static async Task<FileContent> ExtractOfficeTextAsync(string path, string ext, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var text = ext switch
            {
                ".docx" or ".docm" => ExtractDocxText(path, ct),
                ".xlsx" or ".xlsm" => ExtractXlsxText(path, ct),
                ".pptx" or ".pptm" => ExtractPptxText(path, ct),
                // Text of a presentation lies in text:p of its pages, as in a text document.
                ".odt" or ".odp" => ExtractOdtText(path, ct),
                ".ods" => ExtractOdsText(path, ct),
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                return new FileContent
                {
                    Kind = PreviewKind.OfficeText,
                    Message = "Das Office-Dokument enthält keinen lesbaren Text."
                };
            }

            if (text.Length > MaxTextChars)
                text = text[..MaxTextChars] + "\n\n… preview truncated …";

            return new FileContent { Kind = PreviewKind.OfficeText, TextPreview = text };
        }, ct);
    }

    private static string ExtractDocxText(string path, CancellationToken ct)
    {
        using var archive = OfficePackage.Open(path);
        var entry = archive.GetEntry("word/document.xml");
        if (entry == null) return string.Empty;

        var doc = archive.Load(entry);

        // Word text lives in w:t, paragraphs in w:p
        var sb = new StringBuilder();
        var paragraphs = doc.Descendants().Where(e => e.Name.LocalName == "p");
        foreach (var p in paragraphs)
        {
            ct.ThrowIfCancellationRequested();
            var runs = p.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value);
            var line = string.Concat(runs).Trim();
            if (!string.IsNullOrWhiteSpace(line))
                sb.AppendLine(line);
            if (sb.Length >= MaxTextChars) break;
        }

        return sb.ToString();
    }

    private static string ExtractXlsxText(string path, CancellationToken ct)
    {
        using var archive = OfficePackage.Open(path);

        // shared strings table (optional)
        var sharedStrings = new List<string>();
        var sstEntry = archive.GetEntry("xl/sharedStrings.xml");
        if (sstEntry != null)
        {
            var sstDoc = archive.Load(sstEntry);
            foreach (var si in sstDoc.Descendants().Where(e => e.Name.LocalName == "si"))
            {
                ct.ThrowIfCancellationRequested();
                var text = string.Concat(si.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                sharedStrings.Add(text);
            }
        }

        var sb = new StringBuilder();
        var sheetEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName)
            .ToList();

        foreach (var sheetEntry in sheetEntries)
        {
            ct.ThrowIfCancellationRequested();
            var sheetDoc = archive.Load(sheetEntry);

            foreach (var row in sheetDoc.Descendants().Where(e => e.Name.LocalName == "row"))
            {
                ct.ThrowIfCancellationRequested();
                var cells = new List<string>();

                foreach (var c in row.Elements().Where(e => e.Name.LocalName == "c"))
                {
                    var cellType = c.Attribute("t")?.Value;
                    var v = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;

                    string cellText;
                    if (cellType == "s" && int.TryParse(v, out var idx) && idx >= 0 && idx < sharedStrings.Count)
                    {
                        cellText = sharedStrings[idx];
                    }
                    else if (cellType == "inlineStr")
                    {
                        cellText = string.Concat(c.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                    }
                    else
                    {
                        cellText = v ?? string.Empty;
                    }

                    if (!string.IsNullOrWhiteSpace(cellText))
                        cells.Add(cellText.Trim());
                }

                if (cells.Count > 0)
                    sb.AppendLine(string.Join(" | ", cells));

                if (sb.Length >= MaxTextChars)
                    return sb.ToString();
            }
        }

        return sb.ToString();
    }

    private static string ExtractOdtText(string path, CancellationToken ct)
    {
        using var archive = OfficePackage.Open(path);
        var entry = archive.GetEntry("content.xml");
        if (entry == null) return string.Empty;

        var doc = archive.Load(entry);

        var sb = new StringBuilder();
        var paragraphs = doc.Descendants().Where(e => e.Name.LocalName is "p" or "h");
        foreach (var p in paragraphs)
        {
            ct.ThrowIfCancellationRequested();
            var line = string.Concat(p.DescendantNodes().OfType<XText>().Select(t => t.Value)).Trim();
            if (!string.IsNullOrWhiteSpace(line))
                sb.AppendLine(line);
            if (sb.Length >= MaxTextChars) break;
        }

        return sb.ToString();
    }

    private static string ExtractOdsText(string path, CancellationToken ct)
    {
        using var archive = OfficePackage.Open(path);
        var entry = archive.GetEntry("content.xml");
        if (entry == null) return string.Empty;

        var doc = archive.Load(entry);

        var sb = new StringBuilder();
        var rows = doc.Descendants().Where(e => e.Name.LocalName == "table-row");
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var cells = row.Elements().Where(e => e.Name.LocalName == "table-cell")
                .Select(c => string.Concat(c.DescendantNodes().OfType<XText>().Select(t => t.Value)).Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            if (cells.Count > 0)
                sb.AppendLine(string.Join(" | ", cells));

            if (sb.Length >= MaxTextChars) break;
        }

        return sb.ToString();
    }

    private static string ExtractPptxText(string path, CancellationToken ct)
    {
        using var archive = OfficePackage.Open(path);
        var slides = archive.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Entry: e, Number: int.TryParse(Path.GetFileNameWithoutExtension(e.Name)[5..], out var n) ? n : int.MaxValue))
            .OrderBy(s => s.Number)
            .ToList();

        var sb = new StringBuilder();
        foreach (var (entry, number) in slides)
        {
            ct.ThrowIfCancellationRequested();
            var doc = archive.Load(entry);
            var lines = doc.Descendants().Where(e => e.Name.LocalName == "p")
                .Select(p => string.Concat(p.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value)).Trim())
                .Where(l => l.Length > 0)
                .ToList();
            if (lines.Count == 0) continue;
            sb.AppendLine($"[Slide {number}]");
            foreach (var line in lines) sb.AppendLine(line);
            if (sb.Length >= MaxTextChars) break;
        }
        return sb.ToString();
    }

    /// <summary>Renders page 1 with a white background (so it reads as a page on dark UIs) and encodes it as PNG.</summary>
    private static FileContent RenderPdfFirstPage(string path)
    {
        lock (PdfRenderLock)
        {
            using var docReader = PdfLib.Value.GetDocReader(path, new PageDimensions(PdfTargetWidth, PdfTargetHeight));
            return new FileContent
            {
                Kind = PreviewKind.Pdf,
                ImageBytes = RenderPage(docReader, 0),
                IsRenderedPage = true
            };
        }
    }

    private static byte[] RenderPage(IDocReader docReader, int index)
    {
        using var pageReader = docReader.GetPageReader(index);
        var width = pageReader.GetPageWidth();
        var height = pageReader.GetPageHeight();
        var bgra = pageReader.GetImage(new NaiveTransparencyRemover(255, 255, 255));
        return PngEncoder.EncodeBgra(bgra, width, height);
    }

    /// <summary>
    /// Reads a PDF for the model: page count, the embedded text of the first pages up to
    /// <paramref name="maxTextChars"/>, and rendered pages: page 1 always, plus a middle page
    /// when the document has no text (a scan), so the model sees more than the cover.
    /// PDFium is not thread-safe; all PDF work shares one lock.
    /// </summary>
    /// <param name="renderMorePages">For the second look of a check: also render the middle and the last
    /// page, whether or not the document has text.</param>
    public PdfEvidence ReadPdf(string path, int maxTextChars, CancellationToken ct, bool renderMorePages = false)
    {
        lock (PdfRenderLock)
        {
            using var docReader = PdfLib.Value.GetDocReader(path, new PageDimensions(PdfTargetWidth, PdfTargetHeight));
            var pageCount = docReader.GetPageCount();

            var text = new StringBuilder();
            var pagesWithText = 0;
            var pagesRead = 0;
            for (var i = 0; i < pageCount && text.Length < maxTextChars; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var pageReader = docReader.GetPageReader(i);
                var pageText = pageReader.GetText()?.Trim() ?? string.Empty;
                pagesRead++;
                if (pageText.Length == 0) continue;
                pagesWithText++;
                text.AppendLine($"[Page {i + 1}]");
                text.AppendLine(pageText);
            }

            var allText = text.ToString();
            var truncated = allText.Length > maxTextChars;
            if (truncated) allText = allText[..maxTextChars];

            var rendered = new List<(int Page, byte[] Png)>();
            if (pageCount > 0)
                rendered.Add((1, RenderPage(docReader, 0)));
            if ((pagesWithText == 0 || renderMorePages) && pageCount >= 3)
            {
                var middle = pageCount / 2;
                rendered.Add((middle + 1, RenderPage(docReader, middle)));
            }
            if (renderMorePages && pageCount >= 2)
                rendered.Add((pageCount, RenderPage(docReader, pageCount - 1)));

            return new PdfEvidence(pageCount, allText.TrimEnd(), pagesRead, truncated || pagesRead < pageCount, pagesWithText > 0, rendered);
        }
    }

    private static string FormatName(string ext, string? format) =>
        ext.Length > 1 ? ext.TrimStart('.').ToUpperInvariant() : format?.ToUpperInvariant() ?? "diese Datei";

    private static FileContent ReadLegacyWord(string path)
    {
        var doc = LegacyWordReader.Read(path, MaxTextChars);
        if (doc.Problem is { } problem)
            return new FileContent { Kind = PreviewKind.LegacyWord, Message = $"Kein Textauszug aus dem Word-Dokument: {problem}. „Öffnen“ zeigt es." };
        if (doc.Text.Length == 0)
            return new FileContent { Kind = PreviewKind.LegacyWord, Message = "Das Word-Dokument enthält keinen Text." };
        return new FileContent
        {
            Kind = PreviewKind.LegacyWord,
            TextPreview = doc.Text + (doc.Truncated ? "\n\n… preview truncated …" : string.Empty),
            Message = "Altes Word-Format: nur der Text, ohne Bilder und Layout."
        };
    }

    private static async Task<FileContent> ReadRtfAsync(string path, CancellationToken ct)
    {
        // RTF is 7-bit by its specification; characters beyond come as \'hh and \uN escapes.
        using var reader = new StreamReader(path, Encoding.Latin1);
        var buffer = new char[MaxTextChars * 8];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        var text = RtfText.Extract(new string(buffer, 0, read), MaxTextChars).Trim();
        if (text.Length == 0)
            return new FileContent { Kind = PreviewKind.Rtf, Message = "Das RTF-Dokument enthält keinen lesbaren Text am Anfang." };
        return new FileContent
        {
            Kind = PreviewKind.Rtf,
            TextPreview = text + (reader.Peek() >= 0 ? "\n\n… preview truncated …" : string.Empty)
        };
    }

    private static FileContent ListArchive(string path, string? format, CancellationToken ct)
    {
        if (!ArchiveListingReader.CanList(format))
            return new FileContent { Kind = PreviewKind.Archive, Message = $"Das Archiv ({format ?? "unbekanntes Format"}) lässt sich nicht auflisten." };
        ArchiveListing listing;
        try
        {
            listing = ArchiveListingReader.Read(path, format!, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FileContent { Kind = PreviewKind.Archive, Message = "Das Archiv lässt sich nicht auflisten: verschlüsselt oder beschädigt." };
        }
        var sb = new StringBuilder();
        foreach (var e in listing.Entries.Take(MaxListedArchiveEntries))
            sb.Append(e.Name).Append("  (").Append(SizeText(e.Size)).AppendLine(")");
        var more = listing.Entries.Count - MaxListedArchiveEntries;
        if (more > 0 || listing.Truncated)
            sb.AppendLine($"… und {(listing.Truncated ? "mehr als " : string.Empty)}{Math.Max(more, 0):N0} weitere");
        return new FileContent
        {
            Kind = PreviewKind.Archive,
            TextPreview = sb.ToString(),
            Message = $"Archiv: {listing.Entries.Count:N0}{(listing.Truncated ? "+" : string.Empty)} Dateien, entpackt {SizeText(listing.UnpackedBytes)}"
                      + (listing.Encrypted ? ", verschlüsselt" : string.Empty) + "; nichts wurde entpackt."
        };
    }

    private static string SizeText(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };

    private static async Task<FileContent> ReadTextAsync(string path, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var buffer = new char[MaxTextChars];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(0, MaxTextChars), ct);
        var text = new string(buffer, 0, read);

        var truncated = stream.Position < stream.Length;
        if (truncated)
            text += "\n\n… preview truncated …";

        return new FileContent { Kind = PreviewKind.Text, TextPreview = text };
    }
}
