using System.Runtime.InteropServices;
using System.Text;
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
            if (file.FileType == FileType.Image)
                return BuildImagePreview(file.FullPath);

            if (string.Equals(file.Extension, ".pdf", StringComparison.OrdinalIgnoreCase))
                return BuildPdfPreview(file.FullPath);

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
