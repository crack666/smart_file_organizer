using Avalonia.Media.Imaging;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Content;

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

/// <summary>
/// GUI adapter over <see cref="FileContentExtractor"/>: the extraction logic lives in the core
/// (shared with the AI input), this class only turns encoded image bytes into an Avalonia bitmap.
/// </summary>
public class FilePreviewService
{
    private readonly FileContentExtractor _extractor;

    public FilePreviewService(FileContentExtractor extractor)
    {
        _extractor = extractor;
    }

    public async Task<FilePreviewData> BuildAsync(FileNode file, CancellationToken ct = default)
    {
        var content = await _extractor.ExtractAsync(file, ct);

        if (!content.HasImage)
        {
            return new FilePreviewData { TextPreview = content.TextPreview, Message = content.Message };
        }

        try
        {
            using var stream = new MemoryStream(content.ImageBytes!, writable: false);
            return new FilePreviewData
            {
                ImagePreview = new Bitmap(stream),
                TextPreview = content.TextPreview,
                Message = content.Message
            };
        }
        catch (Exception ex)
        {
            return new FilePreviewData { Message = $"Preview unavailable: {ex.Message}" };
        }
    }
}
