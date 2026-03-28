using System.Text;
using Avalonia.Media.Imaging;
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
            {
                return new FilePreviewData
                {
                    Message = "PDF preview needs an additional renderer library (for example PDFium/Docnet/WebView). Metadata is available now; embedded PDF rendering can be added next."
                };
            }

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
