using System.Text;
using Avalonia.Media.Imaging;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.Services;

public class AiClassificationInputPreparer : IClassificationInputPreparer
{
    private readonly FilePreviewService _previewService;

    public AiClassificationInputPreparer(FilePreviewService previewService)
    {
        _previewService = previewService;
    }

    public async Task<OllamaClassificationInput> PrepareAsync(FileNode file, CancellationToken ct = default)
    {
        if (file.FileType == Domain.Enums.FileType.Image && File.Exists(file.FullPath))
        {
            return new OllamaClassificationInput
            {
                File = file,
                Base64Images = [Convert.ToBase64String(await File.ReadAllBytesAsync(file.FullPath, ct))]
            };
        }

        var preview = await _previewService.BuildAsync(file, ct);
        var images = new List<string>();

        if (preview.ImagePreview != null)
        {
            images.Add(EncodeBitmapToBase64(preview.ImagePreview));
        }

        return new OllamaClassificationInput
        {
            File = file,
            ExtractedText = preview.TextPreview,
            Base64Images = images
        };
    }

    private static string EncodeBitmapToBase64(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }
}