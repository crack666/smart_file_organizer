using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Content;

namespace SmartFileOrganizer.Desktop.Services;

/// <summary>One picture of a video's filmstrip and where in the video it was taken.</summary>
public sealed record PreviewFrame(Bitmap Image, TimeSpan At)
{
    public string Label => At.ToString(At.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
}

public sealed class FilePreviewData
{
    public Bitmap? ImagePreview { get; init; }
    public string TextPreview { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    /// <summary>Pictures spread over a video's running time, oldest first; empty for other files.</summary>
    public IReadOnlyList<PreviewFrame> Frames { get; init; } = [];

    /// <summary>A video or a sound file: the view offers "Abspielen" (the default program).</summary>
    public bool CanPlay { get; init; }

    public bool HasImagePreview => ImagePreview != null;
    public bool HasTextPreview => !string.IsNullOrWhiteSpace(TextPreview);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    /// <summary>The image, or for a video its first frame; the other frames are released. For views that show one picture.</summary>
    public Bitmap? TakeStill()
    {
        if (ImagePreview is not null || Frames.Count == 0) return ImagePreview;
        foreach (var f in Frames.Skip(1)) f.Image.Dispose();
        return Frames[0].Image;
    }

    public void Dispose()
    {
        ImagePreview?.Dispose();
        foreach (var f in Frames) f.Image.Dispose();
    }
}

/// <summary>
/// GUI adapter over <see cref="FileContentExtractor"/>: the extraction logic lives in the core (shared with the AI input),
/// this class only turns encoded image bytes into Avalonia bitmaps. A video gets a filmstrip of up to
/// <see cref="FilmstripFrames"/> pictures spread over its running time through <see cref="VideoFrameExtractor"/> (the
/// optional <c>ffmpeg</c> the analysis uses as well; https://github.com/crack666/smart_file_organizer/issues/109, until
/// 16.11.2026 one still frame, #102). The pictures are kept per file in the cache folder of the application under the
/// temp folder, never beside the file or in a library; a changed file (size or time) gets new pictures. Without the
/// tool the message says so and "Abspielen" is the way to see the video.
/// </summary>
public class FilePreviewService
{
    /// <summary>Pictures in a filmstrip: three rows of three in the detail column.</summary>
    public const int FilmstripFrames = 9;

    /// <summary>Width the pictures are taken at: sharp in a column of about 130 px, also on a screen scaled 200 %.</summary>
    public const int FilmstripWidth = 320;

    /// <summary>Filmstrips kept in the cache; the oldest beyond are removed.</summary>
    public const int MaxCachedFilmstrips = 300;

    private readonly FileContentExtractor _extractor;
    private readonly VideoFrameExtractor? _frames;

    public FilePreviewService(FileContentExtractor extractor) : this(extractor, null) { }

    public FilePreviewService(FileContentExtractor extractor, VideoFrameExtractor? frames)
        : this(extractor, frames, DefaultCacheFolder) { }

    /// <param name="cacheFolder">Where filmstrips are kept (tests: a temp folder of their own).</param>
    public FilePreviewService(FileContentExtractor extractor, VideoFrameExtractor? frames, string cacheFolder)
    {
        _extractor = extractor;
        _frames = frames;
        CacheFolder = cacheFolder;
    }

    /// <summary>The cache of the application under the temp folder of the user.</summary>
    public static string DefaultCacheFolder => Path.Combine(Path.GetTempPath(), "SmartFileOrganizer", "preview-cache");

    public string CacheFolder { get; }

    /// <summary>True when filmstrips can be made (<c>ffmpeg</c> found).</summary>
    public bool CanMakeFilmstrips => _frames is { IsAvailable: true };

    public async Task<FilePreviewData> BuildAsync(FileNode file, CancellationToken ct = default)
    {
        if (file.FileType == FileType.Video)
            return await BuildVideoAsync(file, ct);
        if (file.FileType == FileType.Audio)
            return AudioPreview();

        var content = await _extractor.ExtractAsync(file, ct);
        if (content.Kind == PreviewKind.Video) return await BuildVideoAsync(file, ct);
        if (content.Kind == PreviewKind.Audio) return AudioPreview();

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FilePreviewData { Message = $"Kein Bild: die Datei ließ sich nicht entschlüsseln ({ex.GetType().Name}). „Öffnen“ zeigt sie im Standardprogramm." };
        }
    }

    /// <summary>
    /// One small picture of a photo or video, <paramref name="width"/> pixels wide, for a strip of many
    /// (https://github.com/crack666/smart_file_organizer/issues/27); null when there is none. Decoded at that width and off
    /// the UI thread, so a strip of large photos stays small in memory. A video gives the first picture of its stored
    /// filmstrip, else one picture from its first third (not stored).
    /// </summary>
    public async Task<Bitmap?> BuildThumbnailAsync(FileNode file, int width, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(file.FullPath) || !File.Exists(file.FullPath)) return null;
        try
        {
            byte[]? bytes;
            if (file.FileType == FileType.Video)
            {
                if (_frames is not { IsAvailable: true }) return null;
                var stored = await Task.Run(() => ReadCache(CacheKey(new FileInfo(file.FullPath))), ct);
                bytes = stored is not null
                    ? stored[0].Png
                    : await _frames.ExtractOneAsync(file.FullPath, await Task.Run(() => Duration(file.FullPath), ct) / 3, width, ct);
            }
            else
            {
                bytes = (await Task.Run(() => _extractor.ExtractAsync(file, ct), ct)).ImageBytes;
            }
            if (bytes is null) return null;
            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return Bitmap.DecodeToWidth(stream, width);
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // not decodable: the strip leaves it out
        }
    }

    private static FilePreviewData AudioPreview() => new()
    {
        CanPlay = true,
        Message = "Ton hat kein Bild; Dauer und Tags stehen oben. „Abspielen“ spielt die Datei im Standardprogramm."
    };

    /// <summary>The positions of the pictures: the middle of <paramref name="count"/> equal parts of the running time.</summary>
    public static IReadOnlyList<double> FilmstripPositions(double durationSeconds, int count = FilmstripFrames)
    {
        if (durationSeconds <= 0) return [0];
        var n = Math.Clamp((int)Math.Floor(durationSeconds), 1, count);
        return Enumerable.Range(0, n).Select(i => Math.Round(durationSeconds * (i + 0.5) / n, 2)).ToList();
    }

    private async Task<FilePreviewData> BuildVideoAsync(FileNode file, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file.FullPath) || !File.Exists(file.FullPath))
            return new FilePreviewData { Message = "Keine Vorschau: die Datei ist nicht mehr da." };
        if (_frames is not { IsAvailable: true })
            return new FilePreviewData
            {
                CanPlay = true,
                Message = $"Kein Bildstreifen: {VideoFrameExtractor.ToolName} ist auf diesem Rechner nicht gefunden. „Abspielen“ zeigt das Video im Standardprogramm."
            };

        var info = new FileInfo(file.FullPath);
        var key = CacheKey(info);
        var frames = await Task.Run(() => ReadCache(key), ct);
        if (frames is null)
        {
            var duration = await Task.Run(() => Duration(file.FullPath), ct);
            var made = new List<(double At, byte[] Png)>();
            foreach (var position in FilmstripPositions(duration))
            {
                ct.ThrowIfCancellationRequested();
                if (await _frames.ExtractOneAsync(file.FullPath, position, FilmstripWidth, ct) is { } png)
                    made.Add((position, png));
                // No picture at the first position: the tool cannot read this file, and asking eight more times
                // (each with its own timeout) would only delay the honest hint.
                else if (made.Count == 0) break;
            }
            if (made.Count == 0)
                return new FilePreviewData
                {
                    CanPlay = true,
                    Message = $"Kein Bildstreifen: {VideoFrameExtractor.ToolName} konnte das Video nicht lesen. „Abspielen“ zeigt es im Standardprogramm."
                };
            await Task.Run(() => WriteCache(key, made), CancellationToken.None);
            frames = made;
        }

        var decoded = new List<PreviewFrame>();
        try
        {
            foreach (var (at, png) in frames)
            {
                using var stream = new MemoryStream(png, writable: false);
                decoded.Add(new PreviewFrame(new Bitmap(stream), TimeSpan.FromSeconds(at)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var f in decoded) f.Image.Dispose();
            return new FilePreviewData { CanPlay = true, Message = $"Kein Bildstreifen: ein Bild ließ sich nicht lesen ({ex.GetType().Name})." };
        }
        return new FilePreviewData
        {
            Frames = decoded,
            CanPlay = true,
            Message = decoded.Count == 1
                ? $"Ein Bild bei {decoded[0].Label}. „Abspielen“ zeigt das ganze Video."
                : $"{decoded.Count} Bilder, gleichmäßig über die Laufzeit verteilt. „Abspielen“ zeigt das ganze Video."
        };
    }

    private static double Duration(string path)
    {
        try
        {
            if (VideoContainerReader.Read(path)?.DurationSeconds is > 0 and var d) return d;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall back to the general metadata reader.
        }
        try
        {
            return MediaMetadataReader.Read(path).DurationSeconds ?? 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------ cache

    /// <summary>Path, size and time of last change, plus the strip's shape: a changed file or strip gets a new entry.</summary>
    private static string CacheKey(FileInfo info)
    {
        var text = string.Join('|', Path.GetFullPath(info.FullName), info.Length, info.LastWriteTimeUtc.Ticks, FilmstripFrames, FilmstripWidth);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
    }

    private string Folder(string key) => Path.Combine(CacheFolder, "filmstrip", key);

    private List<(double At, byte[] Png)>? ReadCache(string key)
    {
        var dir = Folder(key);
        try
        {
            if (!File.Exists(Path.Combine(dir, "complete"))) return null;
            var frames = Directory.GetFiles(dir, "*.png")
                .Select(p => (Path: p, Ms: long.TryParse(Path.GetFileNameWithoutExtension(p).Split('-').Last(), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? ms : -1))
                .Where(x => x.Ms >= 0)
                .OrderBy(x => x.Ms)
                .Select(x => (x.Ms / 1000.0, File.ReadAllBytes(x.Path)))
                .ToList();
            return frames.Count > 0 ? frames : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the pictures into a folder of their own and renames it into place, so a reader never sees half a strip.</summary>
    private void WriteCache(string key, IReadOnlyList<(double At, byte[] Png)> frames)
    {
        var target = Folder(key);
        var temp = target + ".part-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(temp);
            for (var i = 0; i < frames.Count; i++)
                File.WriteAllBytes(Path.Combine(temp, $"{i:00}-{(long)Math.Round(frames[i].At * 1000)}.png"), frames[i].Png);
            File.WriteAllText(Path.Combine(temp, "complete"), string.Empty);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(temp, target);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is a convenience: without it the strip is made again next time.
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void Prune()
    {
        var all = new DirectoryInfo(Path.Combine(CacheFolder, "filmstrip")).GetDirectories();
        if (all.Length <= MaxCachedFilmstrips) return;
        foreach (var old in all.OrderBy(d => d.LastWriteTimeUtc).Take(all.Length - MaxCachedFilmstrips))
        {
            try { old.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
