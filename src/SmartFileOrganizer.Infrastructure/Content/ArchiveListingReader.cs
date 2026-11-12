using SharpCompress.Archives;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Infrastructure.Content;

public sealed record ArchiveEntryInfo(string Name, long Size);

/// <summary>The table of contents of an archive, read without unpacking anything.</summary>
public sealed class ArchiveListing
{
    public required string Format { get; init; }
    public IReadOnlyList<ArchiveEntryInfo> Entries { get; init; } = [];

    /// <summary>True when the archive has more entries than <see cref="ArchiveListingReader.MaxEntries"/>; the rest was not read.</summary>
    public bool Truncated { get; init; }

    public bool Encrypted { get; init; }
    public long UnpackedBytes => Entries.Sum(e => Math.Max(0, e.Size));
}

/// <summary>
/// Lists ZIP, RAR, 7z and plain TAR archives through SharpCompress. Only the directory of the
/// archive is read; no entry is decompressed and nothing is written to disk. At most
/// <see cref="MaxEntries"/> entries are read, so an archive with millions of entries costs no more
/// than one with ten thousand. Compressed tarballs (<c>.tar.gz</c>, <c>.gz</c>, <c>.bz2</c>, <c>.xz</c>)
/// are not listed: their table of contents only exists inside the compressed stream.
/// </summary>
public static class ArchiveListingReader
{
    public const int MaxEntries = 10_000;

    /// <summary>Format codes from <see cref="FileSignature"/> that can be listed without decompression.</summary>
    public static bool CanList(string? format) => format is "zip" or "rar" or "7z" or "tar";

    public static ArchiveListing Read(string path, string format, CancellationToken ct)
    {
        using var archive = ArchiveFactory.OpenArchive(path, new SharpCompress.Readers.ReaderOptions { LeaveStreamOpen = false });
        var entries = new List<ArchiveEntryInfo>();
        var truncated = false;
        var encrypted = false;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory) continue;
            if (entries.Count >= MaxEntries) { truncated = true; break; }
            if (entry.IsEncrypted) encrypted = true;
            entries.Add(new ArchiveEntryInfo(entry.Key ?? string.Empty, entry.Size));
        }

        return new ArchiveListing { Format = format, Entries = entries, Truncated = truncated, Encrypted = encrypted };
    }

    private static readonly HashSet<string> ImageExt = Set(".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".raw", ".cr2", ".nef", ".arw", ".dng");
    private static readonly HashSet<string> DocumentExt = Set(".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".txt", ".rtf", ".csv", ".md");
    private static readonly HashSet<string> MediaExt = Set(".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma", ".opus", ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".mpg", ".mpeg", ".3gp");
    private static readonly HashSet<string> CodeExt = Set(".cs", ".js", ".ts", ".py", ".java", ".cpp", ".c", ".h", ".hpp", ".go", ".rs", ".php", ".rb", ".kt", ".swift", ".html", ".css", ".json", ".xml", ".yml", ".yaml", ".sql", ".sh", ".ps1", ".csproj", ".sln");
    private static readonly HashSet<string> ProgramExt = Set(".exe", ".dll", ".msi", ".so", ".dylib", ".sys", ".jar", ".apk", ".deb", ".rpm");
    private static readonly HashSet<string> ProjectMarkers = Set("package.json", ".gitignore", "pom.xml", "build.gradle", "cargo.toml", "go.mod", "makefile", "cmakelists.txt", "pyproject.toml", "requirements.txt");

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What an archive mostly holds: an installer or programs with their libraries are
    /// <see cref="ArchiveContent.Software"/>; otherwise the group with 60 % of the entries; else mixed.
    /// Null for an empty listing.
    /// </summary>
    public static ArchiveContent? DecideContent(IReadOnlyList<ArchiveEntryInfo> entries)
    {
        if (entries.Count == 0) return null;

        var names = entries.Select(e => Path.GetFileName(e.Name.Replace('\\', '/')) ?? string.Empty).ToList();
        var exts = names.Select(n => Path.GetExtension(n) ?? string.Empty).ToList();

        var hasInstaller = names.Any(n => n.StartsWith("setup", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                        || n.StartsWith("install", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                           || exts.Any(e => e.Equals(".msi", StringComparison.OrdinalIgnoreCase));
        var programs = exts.Count(e => ProgramExt.Contains(e));
        var hasExe = exts.Any(e => e.Equals(".exe", StringComparison.OrdinalIgnoreCase));
        var hasLib = exts.Any(e => e.Equals(".dll", StringComparison.OrdinalIgnoreCase));
        if (hasInstaller || (hasExe && hasLib) || programs * 5 >= entries.Count * 3)
            return ArchiveContent.Software;

        double Share(HashSet<string> set) => exts.Count(e => set.Contains(e)) / (double)entries.Count;

        if (Share(ImageExt) >= 0.6) return ArchiveContent.Photos;
        if (Share(DocumentExt) >= 0.6) return ArchiveContent.Documents;
        if (Share(MediaExt) >= 0.6) return ArchiveContent.Media;
        if (Share(CodeExt) >= 0.6 || names.Any(n => ProjectMarkers.Contains(n))) return ArchiveContent.Code;
        return ArchiveContent.Mixed;
    }
}
