using SmartFileOrganizer.Application.Media;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>
/// Reads where an image comes from and what an audio file is, without a model
/// (https://github.com/crack666/smart_file_organizer/issues/103): the metadata through <see cref="MediaMetadataReader"/> and
/// <see cref="AudioMetadataReader"/>, then their rules, the same ones the analysis uses. Reads, never writes.
/// </summary>
public sealed class MediaOriginReader : IMediaOriginReader
{
    public MediaOriginReading? Read(FileNode file)
    {
        if (string.IsNullOrWhiteSpace(file.FullPath) || !File.Exists(file.FullPath)) return null;
        var ext = Path.GetExtension(file.Name);
        try
        {
            switch (file.FileType)
            {
                case FileType.Image:
                {
                    var meta = MediaMetadataReader.Read(file.FullPath);
                    return MediaMetadataReader.DecideOrigin(meta, file.Name, ext, file.RelativeDir) is { } origin
                        ? new MediaOriginReading(new FileFact(FileFactKey.ImageOrigin, origin.Origin.ToCode(), FactSource.Rule, origin.Certainty))
                        : new MediaOriginReading(null);
                }
                case FileType.Audio:
                {
                    var meta = AudioMetadataReader.Read(file.FullPath);
                    return AudioMetadataReader.DecideKind(meta, file.Name, ext, file.RelativeDir) is { } kind
                        ? new MediaOriginReading(new FileFact(FileFactKey.AudioKind, kind.Kind.ToCode(), FactSource.Rule, kind.Certainty))
                        : new MediaOriginReading(null);
                }
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreadable metadata: the stored fact stays as it was, as in the analysis, which then names what is missing.
            return null;
        }
    }
}
