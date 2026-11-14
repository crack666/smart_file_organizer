using Avalonia.Media.Imaging;
using SmartFileOrganizer.Domain.Enums;
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
/// A video gets one still frame through <see cref="VideoFrameExtractor"/> (the optional <c>ffmpeg</c> the analysis
/// uses as well, https://github.com/crack666/smart_file_organizer/issues/102); without it the message says so.
/// </summary>
public class FilePreviewService
{
    private readonly FileContentExtractor _extractor;
    private readonly VideoFrameExtractor? _frames;

    public FilePreviewService(FileContentExtractor extractor) : this(extractor, null) { }

    public FilePreviewService(FileContentExtractor extractor, VideoFrameExtractor? frames)
    {
        _extractor = extractor;
        _frames = frames;
    }

    public async Task<FilePreviewData> BuildAsync(FileNode file, CancellationToken ct = default)
    {
        if (file.FileType == FileType.Video)
            return await BuildVideoAsync(file, ct);
        if (file.FileType == FileType.Audio)
            return new FilePreviewData { Message = "Ton hat kein Bild; die Eckdaten stehen oben. „Öffnen“ spielt die Datei ab." };

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

    /// <summary>One still frame a tenth into the video (a short one: in its middle), or why there is none.</summary>
    private async Task<FilePreviewData> BuildVideoAsync(FileNode file, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file.FullPath) || !File.Exists(file.FullPath))
            return new FilePreviewData { Message = "Preview unavailable: file does not exist." };
        if (_frames is not { IsAvailable: true })
            return new FilePreviewData { Message = $"Kein Standbild: {VideoFrameExtractor.ToolName} ist auf diesem Rechner nicht gefunden. „Öffnen“ zeigt das Video." };

        double duration;
        try
        {
            duration = MediaMetadataReader.Read(file.FullPath).DurationSeconds ?? 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            duration = 0;
        }
        var position = duration < 4 ? Math.Min(1.0, duration / 2) : duration * 0.1;
        var frames = await _frames.ExtractAsync(file.FullPath, [position], ct);
        if (frames.Count == 0)
            return new FilePreviewData { Message = $"Kein Standbild: {VideoFrameExtractor.ToolName} konnte das Video nicht lesen. „Öffnen“ zeigt es." };
        try
        {
            using var stream = new MemoryStream(frames[0], writable: false);
            return new FilePreviewData
            {
                ImagePreview = new Bitmap(stream),
                Message = $"Standbild bei {TimeSpan.FromSeconds(position):m\\:ss}"
            };
        }
        catch (Exception ex)
        {
            return new FilePreviewData { Message = $"Preview unavailable: {ex.Message}" };
        }
    }
}
