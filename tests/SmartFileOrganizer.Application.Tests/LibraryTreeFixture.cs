using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Scanning.Engine;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Builds a <see cref="LibraryTree"/> in memory from relative paths, as a scan would have stored it.
/// Names are invented; nothing touches the disk.
/// </summary>
internal sealed class LibraryTreeFixture
{
    public const string Root = "/lib";

    private readonly Dictionary<string, DirectoryReadRow> _dirs = new(StringComparer.Ordinal);
    private readonly List<StructureFileRow> _files = [];
    private long _id;

    public LibraryTreeFixture() => Dir(".");

    public static string Full(string relative) => relative is "." or "" ? Root : $"{Root}/{relative}";

    /// <summary>Adds a directory and every parent of it.</summary>
    public LibraryTreeFixture Dir(string relative, DirectoryStatus status = DirectoryStatus.Scanned)
    {
        if (_dirs.TryGetValue(Full(relative), out var existing))
        {
            existing.DirStatus = status;
            return this;
        }
        var parts = relative == "." ? [] : relative.Split('/');
        if (parts.Length > 1)
            Dir(string.Join('/', parts[..^1]));
        var full = Full(relative);
        var parent = parts.Length switch
        {
            0 => "/",
            1 => Root,
            _ => Full(string.Join('/', parts[..^1]))
        };
        _dirs[full] = new DirectoryReadRow
        {
            Id = ++_id,
            FullPath = full,
            Name = parts.Length == 0 ? "lib" : parts[^1],
            ParentPath = parent,
            RelativePath = relative,
            Depth = parts.Length,
            DirStatus = status
        };
        if (_dirs.TryGetValue(parent, out var p))
            p.SubdirCount++;
        return this;
    }

    /// <summary>Adds files to a directory: names with extension, one size each (or <paramref name="size"/>).</summary>
    public LibraryTreeFixture Files(string relativeDir, IEnumerable<string> names, long size = 1000, DateTime? time = null,
        SensitiveMarker markers = SensitiveMarker.None)
    {
        Dir(relativeDir);
        foreach (var name in names)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            _files.Add(new StructureFileRow
            {
                ParentPath = Full(relativeDir),
                Name = name,
                Size = size,
                Extension = ext,
                FileType = (int)FileTypeDetector.Detect(ext),
                LastWriteTime = time ?? new DateTime(2016, 5, 1, 12, 0, 0, DateTimeKind.Utc),
                SensitiveMarkers = markers
            });
        }
        return this;
    }

    public LibraryTreeFixture Numbered(string relativeDir, string prefix, string ext, int count, long size = 1000, DateTime? time = null) =>
        Files(relativeDir, Enumerable.Range(1, count).Select(i => $"{prefix}{i:0000}{ext}"), size, time);

    public IReadOnlyList<DirectoryReadRow> Directories => _dirs.Values.ToList();

    public async Task<LibraryTree> BuildAsync(LibraryWords? words = null) =>
        await LibraryTree.BuildAsync(Root, Directories, Stream(), words ?? LibraryWords.Default, CancellationToken.None);

    private async IAsyncEnumerable<StructureFileRow> Stream()
    {
        foreach (var f in _files)
        {
            await Task.Yield();
            yield return f;
        }
    }
}
