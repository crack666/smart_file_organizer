using System.Text;
using Avalonia.Media.Imaging;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Desktop.Services;

public class AiClassificationInputPreparer : IClassificationInputPreparer
{
    private readonly FilePreviewService _previewService;
    private readonly OllamaOptions _options;

    public AiClassificationInputPreparer(FilePreviewService previewService, OllamaOptions options)
    {
        _previewService = previewService;
        _options = options;
    }

    // Raster formats that Ollama vision models reliably accept
    private static readonly HashSet<string> RasterVisionExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    public async Task<OllamaClassificationInput> PrepareAsync(FileNode file, CancellationToken ct = default)
    {
        // Video: classify heuristically from filename + path — never upload bytes
        if (file.FileType == Domain.Enums.FileType.Video)
        {
            return new OllamaClassificationInput
            {
                File = file,
                ExtractedText = "[Video file. Classify based on the filename and directory name only.]"
            };
        }

        if (file.FileType == Domain.Enums.FileType.Image && File.Exists(file.FullPath))
        {
            var ext = file.Extension?.ToLowerInvariant() ?? string.Empty;

            // SVG is XML/vector — vision models can't decode it; read as text instead
            if (ext == ".svg")
            {
                try
                {
                    var svgContent = await File.ReadAllTextAsync(file.FullPath, ct);
                    if (svgContent.Length > 4_000)
                        svgContent = svgContent[..4_000] + "\n… truncated …";
                    return new OllamaClassificationInput { File = file, ExtractedText = svgContent };
                }
                catch
                {
                    return new OllamaClassificationInput
                    {
                        File = file,
                        ExtractedText = "[SVG file. Content could not be read. Classify from filename and metadata only.]"
                    };
                }
            }

            // Only send formats that Ollama vision reliably handles (PNG, JPEG, GIF, WebP)
            if (RasterVisionExtensions.Contains(ext))
            {
                if (file.Size <= _options.MaxImageUploadBytes)
                {
                    try
                    {
                        var bytes = await File.ReadAllBytesAsync(file.FullPath, ct);
                        return new OllamaClassificationInput
                        {
                            File = file,
                            Base64Images = [Convert.ToBase64String(bytes)]
                        };
                    }
                    catch
                    {
                        return new OllamaClassificationInput
                        {
                            File = file,
                            ExtractedText = "[Image could not be read. Classify from filename and metadata only.]"
                        };
                    }
                }

                // Image too large for visual analysis — semantic only
                return new OllamaClassificationInput
                {
                    File = file,
                    ExtractedText = $"[Image too large for visual analysis: {FormatSize(file.Size)}. Assessment based on filename and metadata only.]"
                };
            }

            // Other image formats (bmp, tiff, heic, raw, ico, …) — not supported by vision models
            return new OllamaClassificationInput
            {
                File = file,
                ExtractedText = $"[{ext.TrimStart('.')} image — visual analysis not supported for this format. Classify from filename and metadata only.]"
            };
        }

        var preview = await _previewService.BuildAsync(file, ct);
        var images = new List<string>();

        if (preview.ImagePreview != null)
        {
            // Respect document size limit for rendered previews
            if (file.Size <= _options.MaxDocumentUploadBytes)
                images.Add(EncodeBitmapToBase64(preview.ImagePreview));
        }

        return new OllamaClassificationInput
        {
            File = file,
            ExtractedText = preview.TextPreview,
            Base64Images = images
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    private static string EncodeBitmapToBase64(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }
}