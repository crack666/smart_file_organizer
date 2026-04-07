using System.Runtime.InteropServices;
using System.Text;
using System.IO.Compression;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.Services;

public sealed class FilePreviewData
{
    public Bitmap? ImagePreview { get; init; }
    public string TextPreview { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    public bool HasImagePreview => ImagePreview != null;
    public bool HasTextPreview => !string.IsNullOrWhiteSpace(TextPreview);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
}

public class FilePreviewService
{
    private const int MaxTextChars = 12_000;
    private static readonly object PdfRenderLock = new();
    private static readonly Lazy<IDocLib> PdfLib = new(() => DocLib.Instance);

    private static readonly HashSet<string> TextExtensions =
    [
        ".txt", ".md", ".json", ".xml", ".csv", ".log", ".ini", ".cfg",
        ".yml", ".yaml", ".sql", ".cs", ".js", ".ts", ".tsx", ".jsx",
        ".py", ".html", ".htm", ".css", ".ps1", ".sh", ".bat"
    ];

    public async Task<FilePreviewData> BuildAsync(FileNode file, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(file.FullPath) || !File.Exists(file.FullPath))
        {
            return new FilePreviewData { Message = "Preview unavailable: file does not exist." };
        }

        try
        {
            var ext = file.Extension?.ToLowerInvariant() ?? string.Empty;

            if (file.FileType == FileType.Image)
                return BuildImagePreview(file.FullPath);

            if (ext == ".pdf")
                return BuildPdfPreview(file.FullPath);

            if (ext is ".docx" or ".xlsx" or ".odt" or ".ods")
                return await BuildOfficePreviewAsync(file.FullPath, ext, ct);

            if (IsTextLike(file))
                return await BuildTextPreviewAsync(file.FullPath, ct);

            return new FilePreviewData
            {
                Message = "No built-in preview for this file type yet. Images and text-like files are previewed directly."
            };
        }
        catch (Exception ex)
        {
            return new FilePreviewData
            {
                Message = $"Preview unavailable: {ex.Message}"
            };
        }
    }

    private static async Task<FilePreviewData> BuildOfficePreviewAsync(string path, string ext, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var text = ext switch
            {
                ".docx" => ExtractDocxText(path, ct),
                ".xlsx" => ExtractXlsxText(path, ct),
                ".odt" => ExtractOdtText(path, ct),
                ".ods" => ExtractOdsText(path, ct),
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                return new FilePreviewData
                {
                    Message = "No readable text content extracted from office document."
                };
            }

            if (text.Length > MaxTextChars)
                text = text[..MaxTextChars] + "\n\n… preview truncated …";

            return new FilePreviewData { TextPreview = text };
        }, ct);
    }

    private static string ExtractDocxText(string path, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml");
        if (entry == null) return string.Empty;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

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
        using var archive = ZipFile.OpenRead(path);

        // shared strings table (optional)
        var sharedStrings = new List<string>();
        var sstEntry = archive.GetEntry("xl/sharedStrings.xml");
        if (sstEntry != null)
        {
            using var sstStream = sstEntry.Open();
            var sstDoc = XDocument.Load(sstStream);
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
            using var sheetStream = sheetEntry.Open();
            var sheetDoc = XDocument.Load(sheetStream);

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
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("content.xml");
        if (entry == null) return string.Empty;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

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
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("content.xml");
        if (entry == null) return string.Empty;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

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

    private static FilePreviewData BuildImagePreview(string path)
    {
        return new FilePreviewData
        {
            ImagePreview = new Bitmap(path)
        };
    }

    private static FilePreviewData BuildPdfPreview(string path)
    {
        const int targetWidth = 1200;
        const int targetHeight = 1600;

        lock (PdfRenderLock)
        {
            using var docReader = PdfLib.Value.GetDocReader(path, new PageDimensions(targetWidth, targetHeight));
            using var pageReader = docReader.GetPageReader(0);

            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();
            var rawBytes = pageReader.GetImage(new NaiveTransparencyRemover(255, 255, 255));

            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);

            using (var locked = bitmap.Lock())
            {
                var srcStride = width * 4;
                for (var y = 0; y < height; y++)
                {
                    var srcOffset = y * srcStride;
                    var dest = IntPtr.Add(locked.Address, y * locked.RowBytes);
                    Marshal.Copy(rawBytes, srcOffset, dest, srcStride);
                }
            }

            return new FilePreviewData
            {
                ImagePreview = bitmap
            };
        }
    }

    private static async Task<FilePreviewData> BuildTextPreviewAsync(string path, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var buffer = new char[MaxTextChars];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(0, MaxTextChars), ct);
        var text = new string(buffer, 0, read);

        var truncated = stream.Position < stream.Length;
        if (truncated)
            text += "\n\n… preview truncated …";

        return new FilePreviewData
        {
            TextPreview = text
        };
    }

    private static bool IsTextLike(FileNode file)
    {
        return TextExtensions.Contains(file.Extension)
            || file.FileType is FileType.Code
            || (file.FileType is FileType.Document && TextExtensions.Contains(file.Extension));
    }
}
