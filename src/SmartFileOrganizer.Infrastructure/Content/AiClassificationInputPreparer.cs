using System.Globalization;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>
/// Builds the evidence for one file, per file type: metadata facts (capture date, device, page
/// count, tags, archive listing), the content the model may look at (pixels, rendered pages, video
/// frames, text), and what the evidence contains and lacks. Files no model can judge from content
/// (programs, fonts, databases, unknown binaries) get a rule result instead of a model call.
/// Video and audio bytes are never uploaded; archives are listed, never unpacked.
/// </summary>
public class AiClassificationInputPreparer : IClassificationInputPreparer
{
    /// <summary>Characters of embedded PDF text sent to the model.</summary>
    public const int MaxPdfTextChars = 6_000;

    /// <summary>Archive entries listed by name in the evidence (all are counted).</summary>
    public const int MaxListedArchiveEntries = 60;

    private readonly FileContentExtractor _extractor;
    private readonly OllamaOptions _options;
    private readonly VideoFrameExtractor _frames;

    public AiClassificationInputPreparer(FileContentExtractor extractor, OllamaOptions options)
        : this(extractor, options, new VideoFrameExtractor()) { }

    public AiClassificationInputPreparer(FileContentExtractor extractor, OllamaOptions options, VideoFrameExtractor frames)
    {
        _extractor = extractor;
        _options = options;
        _frames = frames;
    }

    // Raster formats that Ollama vision models reliably accept
    private static readonly HashSet<string> RasterVisionExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    public async Task<OllamaClassificationInput> PrepareAsync(FileNode file, CancellationToken ct = default)
    {
        var ev = new Evidence();

        if (string.IsNullOrWhiteSpace(file.FullPath) || !File.Exists(file.FullPath))
        {
            ev.Lacks("file content: the file was not found when the evidence was built; only name, path and size are known");
            return ev.ToInput(file);
        }

        var ext = file.Extension?.ToLowerInvariant() ?? string.Empty;
        string? format = null;
        try
        {
            format = FileSignature.Detect(FileSignature.ReadPrefix(file.FullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ev.Lacks($"file content: could not be read ({ex.GetType().Name})");
            return ev.ToInput(file);
        }
        if (format is not null && format != "text")
            ev.Fact(FileFactKey.DetectedFormat, format);

        try
        {
            switch (Route(file.FileType, format))
            {
                case Kind.Image: await AddImageAsync(file, ext, ev, ct); break;
                case Kind.Video: await AddVideoAsync(file, ev, ct); break;
                case Kind.Audio: AddAudio(file, ext, ev); break;
                case Kind.Archive: AddArchive(file, ext, format, ev, ct); break;
                case Kind.Document: await AddDocumentAsync(file, ext, format, ev, ct); break;
                case Kind.Text: await AddTextAsync(file, ev, ct); break;
                default: AddRuleResult(file, ext, format, ev); break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Type only: exception messages may carry paths or content, and the notes are stored.
            ev.Lacks($"file content: reading failed ({ex.GetType().Name})");
        }

        return ev.ToInput(file);
    }

    private enum Kind { Image, Video, Audio, Archive, Document, Text, Rule }

    /// <summary>The scan type decides; for unknown and other files the first bytes decide.</summary>
    private static Kind Route(FileType type, string? format) => type switch
    {
        FileType.Image => Kind.Image,
        FileType.Video => Kind.Video,
        FileType.Audio => Kind.Audio,
        FileType.Archive => Kind.Archive,
        FileType.Document => Kind.Document,
        FileType.Code => Kind.Text,
        FileType.Executable or FileType.Font or FileType.Database => Kind.Rule,
        _ => format switch
        {
            "jpeg" or "png" or "gif" or "webp" or "tiff" or "bmp" => Kind.Image,
            "mp4" or "avi" or "matroska" => Kind.Video,
            "mp3" or "flac" or "ogg" or "wav" => Kind.Audio,
            "zip" or "rar" or "7z" or "tar" or "gzip" or "bzip2" or "xz" => Kind.Archive,
            "pdf" or "rtf" => Kind.Document,
            "text" => Kind.Text,
            _ => Kind.Rule
        }
    };

    // ─── Images ──────────────────────────────────────────────────────────────

    private async Task AddImageAsync(FileNode file, string ext, Evidence ev, CancellationToken ct)
    {
        if (ext == ".svg")
        {
            var svg = await File.ReadAllTextAsync(file.FullPath, ct);
            if (svg.Length > 4_000) svg = svg[..4_000] + "\n… truncated …";
            ev.Section("SVG source (vector graphic)", svg);
            ev.Has("SVG source text");
            ev.Lacks("rendered image: SVG is not rasterised");
            return;
        }

        AddMediaMetadata(file, ext, ev, isVideo: false);

        if (!RasterVisionExtensions.Contains(ext))
        {
            ev.Lacks($"image pixels: format {ext.TrimStart('.')} is not supported by the vision model");
            return;
        }
        if (file.Size > _options.MaxImageUploadBytes)
        {
            ev.Lacks($"image pixels: file is {FormatSize(file.Size)}, above the upload limit of {FormatSize(_options.MaxImageUploadBytes)}");
            return;
        }

        ev.Image(await File.ReadAllBytesAsync(file.FullPath, ct), "the image itself");
    }

    private static void AddMediaMetadata(FileNode file, string ext, Evidence ev, bool isVideo)
    {
        MediaMetadata meta;
        try
        {
            meta = MediaMetadataReader.Read(file.FullPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ev.Lacks($"{(isVideo ? "video" : "image")} metadata: could not be read ({ex.GetType().Name})");
            return;
        }

        if (meta.Width is { } w) ev.Fact(FileFactKey.Width, w);
        if (meta.Height is { } h) ev.Fact(FileFactKey.Height, h);
        if (meta.TakenAt is { } taken) ev.Fact(FileFactKey.TakenAt, taken.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        ev.Fact(FileFactKey.Device, meta.Device);
        if (meta.HasLocation) ev.Fact(FileFactKey.HasLocation, true);
        if (isVideo && meta.DurationSeconds is { } d) ev.Fact(FileFactKey.DurationSeconds, (long)Math.Round(d));

        if (ev.Facts.Any(f => f.Key is not FileFactKey.DetectedFormat))
            ev.Has(isVideo ? "video metadata from the container" : "image metadata (dimensions, EXIF)");
        if (meta.TakenAt is null) ev.Lacks("capture date: not in the metadata");
        if (meta.Device is null) ev.Lacks("camera or device: not in the metadata");

        if (!isVideo)
        {
            if (MediaMetadataReader.DecideOrigin(meta, file.Name, ext) is { } origin)
                ev.Fact(FileFactKey.ImageOrigin, origin);
            else
                ev.Lacks("image origin (camera, screenshot, scan): the metadata do not tell");
        }
    }

    // ─── Video ───────────────────────────────────────────────────────────────

    private async Task AddVideoAsync(FileNode file, Evidence ev, CancellationToken ct)
    {
        AddMediaMetadata(file, file.Extension?.ToLowerInvariant() ?? string.Empty, ev, isVideo: true);
        ev.Lacks("video and sound content: the video file itself is never uploaded");

        if (!_frames.IsAvailable)
        {
            ev.Lacks($"still frames: no video tool ({VideoFrameExtractor.ToolName}) found on this machine");
            return;
        }

        var duration = ev.FactValue(FileFactKey.DurationSeconds) is { } s && double.TryParse(s, CultureInfo.InvariantCulture, out var secs) ? secs : 0;
        var positions = duration >= 4 ? new[] { duration * 0.1, duration * 0.5 } : [Math.Min(1.0, duration / 2)];
        var frames = await _frames.ExtractAsync(file.FullPath, positions, ct);
        for (var i = 0; i < frames.Count; i++)
            ev.Image(frames[i], $"still frame at {TimeSpan.FromSeconds(positions[Math.Min(i, positions.Length - 1)]):h\\:mm\\:ss}");
        if (frames.Count == 0)
            ev.Lacks($"still frames: {VideoFrameExtractor.ToolName} could not decode the video");
    }

    // ─── Audio ───────────────────────────────────────────────────────────────

    private static void AddAudio(FileNode file, string ext, Evidence ev)
    {
        ev.Lacks("sound content: audio is not transcribed; judge from tags, duration and path");

        AudioMetadata meta;
        try
        {
            meta = AudioMetadataReader.Read(file.FullPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ev.Lacks($"audio tags: could not be read ({ex.GetType().Name})");
            return;
        }

        ev.Fact(FileFactKey.Title, meta.Title);
        ev.Fact(FileFactKey.Artist, meta.Artist);
        ev.Fact(FileFactKey.Album, meta.Album);
        if (meta.DurationSeconds is { } d) ev.Fact(FileFactKey.DurationSeconds, (long)Math.Round(d));

        var tags = new List<string>();
        if (meta.Genre is not null) tags.Add($"genre: {meta.Genre}");
        if (meta.Year is not null) tags.Add($"year: {meta.Year}");
        if (meta.Format is not null) tags.Add($"format: {meta.Format}");
        if (meta.Channels is not null) tags.Add($"channels: {meta.Channels}");
        if (meta.BitrateKbps is not null) tags.Add($"bitrate: {meta.BitrateKbps} kbit/s");
        ev.Section("Audio details", string.Join(Environment.NewLine, tags));

        if (meta.Title is null && meta.Artist is null && meta.Album is null)
            ev.Lacks("audio tags: no title, artist or album in the file");
        else
            ev.Has("audio tags");
        if (meta.DurationSeconds is null) ev.Lacks("duration: could not be determined");

        if (AudioMetadataReader.DecideKind(meta, file.Name, ext, file.RelativeDir) is { } kind)
            ev.Fact(FileFactKey.AudioKind, kind);
        else
            ev.Lacks("speech or music: tags, format and path do not tell");
    }

    // ─── Archives ────────────────────────────────────────────────────────────

    private static void AddArchive(FileNode file, string ext, string? format, Evidence ev, CancellationToken ct)
    {
        if (!ArchiveListingReader.CanList(format))
        {
            var why = format is "gzip" or "bzip2" or "xz"
                ? "compressed stream, its contents are only known after unpacking, which this tool never does"
                : $"format {(format ?? ext.TrimStart('.'))} cannot be listed";
            ev.Lacks($"archive contents: {why}");
            return;
        }

        ArchiveListing listing;
        try
        {
            listing = ArchiveListingReader.Read(file.FullPath, format!, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ev.Lacks($"archive contents: listing could not be read, encrypted or damaged ({ex.GetType().Name})");
            return;
        }

        ev.Fact(FileFactKey.EntryCount, listing.Entries.Count);
        ev.Fact(FileFactKey.UnpackedBytes, listing.UnpackedBytes);
        if (listing.Encrypted) ev.Fact(FileFactKey.Encrypted, true);
        if (ArchiveListingReader.DecideContent(listing.Entries) is { } content)
            ev.Fact(FileFactKey.ArchiveContent, content);

        var shown = listing.Entries.Take(MaxListedArchiveEntries).Select(e => $"{e.Name} | {e.Size}");
        ev.Section(
            $"Archive listing ({Math.Min(listing.Entries.Count, MaxListedArchiveEntries)} of {listing.Entries.Count}{(listing.Truncated ? "+" : string.Empty)} files, name | uncompressed bytes)",
            string.Join(Environment.NewLine, shown));
        ev.Has("archive listing (names and sizes, not unpacked)");
        if (listing.Truncated)
            ev.Lacks($"archive listing: stopped after {ArchiveListingReader.MaxEntries:N0} entries");
        if (listing.Entries.Count > MaxListedArchiveEntries)
            ev.Lacks($"archive listing: only the first {MaxListedArchiveEntries} names are shown");
        ev.Lacks("archive file contents: entries are never unpacked");
    }

    // ─── Documents ───────────────────────────────────────────────────────────

    private async Task AddDocumentAsync(FileNode file, string ext, string? format, Evidence ev, CancellationToken ct)
    {
        if (ext == ".pdf" || format == "pdf")
        {
            AddPdf(file, ev, ct);
            return;
        }

        if (OfficePropertiesReader.Supports(ext))
        {
            try
            {
                if (OfficePropertiesReader.Read(file.FullPath) is { } props)
                {
                    ev.Fact(FileFactKey.Title, props.Title);
                    ev.Fact(FileFactKey.Author, props.Author);
                    if (props.Created is { } created) ev.Fact(FileFactKey.CreatedAt, created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    if (props.Pages is { } pages) ev.Fact(FileFactKey.PageCount, pages);
                    ev.Has("document properties");
                }
                else
                {
                    ev.Lacks("document properties: none in the file");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ev.Lacks($"document properties: could not be read ({ex.GetType().Name})");
            }

            var content = await _extractor.ExtractAsync(file, ct);
            if (ev.Section("Document text", content.TextPreview))
                ev.Has(content.TextPreview.Contains("preview truncated") ? "document text (truncated)" : "document text");
            else
                ev.Lacks("document text: none found");
            return;
        }

        if (format is "text" or "rtf")
        {
            await AddTextAsync(file, ev, ct);
            return;
        }

        ev.Lacks(format == "ole"
            ? $"document text: legacy Office format {ext.TrimStart('.')} is not read"
            : $"document text: format {ext.TrimStart('.')} is not read");
    }

    private void AddPdf(FileNode file, Evidence ev, CancellationToken ct)
    {
        PdfEvidence pdf;
        try
        {
            pdf = _extractor.ReadPdf(file.FullPath, MaxPdfTextChars, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ev.Lacks($"PDF content: could not be opened, encrypted or damaged ({ex.GetType().Name})");
            return;
        }

        ev.Fact(FileFactKey.PageCount, pdf.PageCount);
        ev.Fact(FileFactKey.HasText, pdf.HasText);

        if (pdf.HasText)
        {
            ev.Section("Embedded PDF text", pdf.Text);
            var pages = pdf.PagesRead == pdf.PageCount ? $"all {pdf.PageCount} pages" : $"pages 1-{pdf.PagesRead} of {pdf.PageCount}";
            ev.Has($"embedded text of {pages}{(pdf.TextTruncated ? $", cut at {MaxPdfTextChars:N0} characters" : string.Empty)}");
            if (pdf.PagesRead < pdf.PageCount) ev.Lacks($"text of pages {pdf.PagesRead + 1}-{pdf.PageCount}: above the character limit");
        }
        else
        {
            ev.Lacks("embedded text: none, the PDF holds only images (probably a scan); read the rendered pages");
        }

        if (file.Size > _options.MaxDocumentUploadBytes)
        {
            ev.Lacks($"page images: file is {FormatSize(file.Size)}, above the upload limit of {FormatSize(_options.MaxDocumentUploadBytes)}");
        }
        else
        {
            foreach (var (page, png) in pdf.RenderedPages)
                ev.Image(png, $"page {page} of {pdf.PageCount} rendered as image");
        }
        ev.Lacks("PDF document properties (title, author): not read");
    }

    // ─── Text ────────────────────────────────────────────────────────────────

    private async Task AddTextAsync(FileNode file, Evidence ev, CancellationToken ct)
    {
        var content = await _extractor.ExtractAsync(file, ct);
        var text = content.TextPreview;

        if (!content.HasText)
        {
            // Not a known text extension: the first bytes said text, so read an excerpt directly.
            var prefix = FileSignature.ReadPrefix(file.FullPath, FileContentExtractor.MaxTextChars);
            if (FileSignature.Detect(prefix.AsSpan(0, Math.Min(prefix.Length, FileSignature.ProbeBytes))) == "text")
            {
                text = FileSignature.DecodeText(prefix);
                if (file.Size > prefix.Length) text += "\n\n… preview truncated …";
            }
        }

        if (ev.Section("Text excerpt", text))
            ev.Has(text.Contains("preview truncated") ? "text excerpt (beginning of the file)" : "full text");
        else
            ev.Lacks("text: the file is empty or not readable as text");
    }

    // ─── Rules ───────────────────────────────────────────────────────────────

    private static readonly HashSet<string> InstallerExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".dmg", ".deb", ".rpm", ".apk", ".pkg", ".appimage" };

    private static readonly HashSet<string> ThumbnailCaches =
        new(StringComparer.OrdinalIgnoreCase) { "Thumbs.db", "ehthumbs.db", "ehthumbs_vista.db" };

    /// <summary>
    /// Files whose content the model cannot judge: programs, fonts, databases and binaries of
    /// unknown format. A rule decides; the summary names the rule.
    /// </summary>
    private static void AddRuleResult(FileNode file, string ext, string? format, Evidence ev)
    {
        (FileCategory Category, Importance Importance, double Confidence, string Rule, string Summary) decision =
            (file.FileType, format) switch
            {
                _ when ThumbnailCaches.Contains(file.Name) =>
                    (FileCategory.TrashCandidate, Importance.Low, 0.95, "thumbnail-cache",
                        "Thumbnail cache written by the operating system; recreated automatically."),
                (FileType.Font, _) or (_, "font") =>
                    (FileCategory.SystemFile, Importance.Low, 0.9, "font", "Font file."),
                (FileType.Database, _) or (_, "sqlite") =>
                    (FileCategory.ReviewNeeded, Importance.Unknown, 0.5, "database",
                        "Database file; its content is not inspected. A person should check whether it holds own data."),
                (FileType.Executable, _) or (_, "pe" or "elf") when InstallerExtensions.Contains(ext) || ext.Length == 0 =>
                    (FileCategory.SoftwareInstaller, Importance.Low, 0.9, "program", "Program or installer; can usually be downloaded again."),
                (FileType.Executable, _) or (_, "pe" or "elf") =>
                    (FileCategory.SystemFile, Importance.Low, 0.9, "library", "Program library or system component."),
                _ =>
                    (FileCategory.Unknown, Importance.Unknown, 0.0, "unknown-binary",
                        $"Binary file of unknown format{(format is null ? string.Empty : $" ({format})")}; no content evidence beyond name, path and size.")
            };

        ev.Lacks("file content: not readable by the model for this kind of file; decided by rule");
        ev.RuleResult = new FileClassification
        {
            FileNodeId = file.Id,
            Category = decision.Category,
            Importance = decision.Importance,
            Confidence = decision.Confidence,
            Summary = decision.Summary,
            ModelUsed = $"rule:{decision.Rule}",
            AnalyzedAt = DateTime.UtcNow,
            IsAiResult = false
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
