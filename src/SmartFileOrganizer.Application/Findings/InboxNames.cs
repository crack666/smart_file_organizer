using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Findings;

/// <summary>
/// The subfolder a source gets in the inbox of a library (https://github.com/crack666/smart_file_organizer/issues/97):
/// named after its place, the label of the drive as <c>sfo places</c> lists it, because the folder name of a source root
/// is often a general one (<c>DCIM</c>, <c>New folder</c>). A source below the drive root keeps its folder name after the
/// label. Without a known label, or with <see cref="LibraryMatchOptions.InboxNameFromPlace"/> off, the folder name of the
/// source root (<c>source-&lt;job&gt;</c> for a drive root).
/// </summary>
public sealed class InboxNames
{
    private readonly IPlaceRepository _places;
    private readonly LibraryMatchOptions _options;

    public InboxNames(IPlaceRepository places, LibraryMatchOptions options)
    {
        _places = places;
        _options = options;
    }

    /// <summary>The name of the subfolder of <paramref name="source"/> in an inbox.</summary>
    public async Task<string> SubfolderAsync(ScanJob source, CancellationToken ct = default)
    {
        if (!_options.InboxNameFromPlace) return FolderName(source);
        var place = (await _places.GetAllAsync(ct))
            .Where(p => p.Role == source.Role && PathKey.Same(p.RootPath, source.RootPath))
            .OrderByDescending(p => p.LastJobId == source.Id)
            .FirstOrDefault();
        return Of(source, place?.VolumeLabel);
    }

    /// <summary>The name from the label of the place, else <see cref="FolderName"/>.</summary>
    public static string Of(ScanJob source, string? label)
    {
        var named = Clean(label);
        if (named.Length == 0) return FolderName(source);
        var folder = Clean(Path.GetFileName(Path.TrimEndingDirectorySeparator(source.RootPath)));
        return folder.Length == 0 || string.Equals(folder, named, StringComparison.OrdinalIgnoreCase) ? named : $"{named} - {folder}";
    }

    /// <summary>The folder name of the source root, or <c>source-&lt;job&gt;</c> for a drive root.</summary>
    public static string FolderName(ScanJob source)
    {
        var cleaned = Clean(Path.GetFileName(Path.TrimEndingDirectorySeparator(source.RootPath)));
        return cleaned.Length == 0 ? $"source-{source.Id}" : cleaned;
    }

    private static string Clean(string? name) =>
        new string((name ?? string.Empty).Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '*' or '?' or '/' or '\\' ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
}
