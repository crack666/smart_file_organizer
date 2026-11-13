using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Bundles;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Library;

/// <summary>The description of a library folder and where it came from.</summary>
/// <param name="Sample">The files looked at, relative to the library root.</param>
/// <param name="Stored">Read from <c>folder_descriptions</c> instead of made now.</param>
public sealed record FolderDescription(
    ContentDescription Description, FolderDescriptionSource Source, IReadOnlyList<string> Sample, string? Model, bool Stored);

/// <summary>What a description of a folder may cost.</summary>
/// <param name="UseModel">The model may describe sampled files that carry no description yet.</param>
/// <param name="Refresh">Describe anew instead of reading the stored description.</param>
/// <param name="SampleSize">Files looked at per folder.</param>
public sealed record FolderDescribeRequest(bool UseModel, bool Refresh, int SampleSize);

/// <summary>
/// Where the description of a candidate folder comes from (https://github.com/crack666/smart_file_organizer/issues/56).
/// The default is <see cref="SampleFolderDescriptionSource"/>, a few of the folder's own files. Another program that
/// already describes pictures (a photo manager, found by the checksum of a file) can be added as a further source with
/// <see cref="FolderDescriptionSource.External"/>; the match takes the first source that gives a description.
/// </summary>
public interface IFolderDescriptionSource
{
    FolderDescriptionSource Kind { get; }

    /// <summary>The description of <paramref name="folder"/>; null when this source has none (nothing to describe).</summary>
    Task<FolderDescription?> DescribeAsync(ScanJob library, LibraryMapEntry folder, FolderDescribeRequest request, CancellationToken ct = default);
}

/// <summary>
/// Describes a library folder from a small sample of its own files, spread over its period: the description facts
/// stored for them if the library was analysed, else the same description call a source file gets (the model sees the
/// picture or the document, read-only). Only photos and videos for a photo folder, documents for a document folder;
/// marked files are never sampled. The result is stored in <c>folder_descriptions</c>, so each folder is sampled once.
/// The library is only read.
/// </summary>
public sealed class SampleFolderDescriptionSource : IFolderDescriptionSource
{
    private readonly IJobReadRepository _reads;
    private readonly IFileFactRepository _facts;
    private readonly IClassificationInputPreparer _preparer;
    private readonly IOllamaService _ollama;
    private readonly IFolderDescriptionRepository _store;
    private readonly ILogger<SampleFolderDescriptionSource> _logger;

    public SampleFolderDescriptionSource(
        IJobReadRepository reads, IFileFactRepository facts, IClassificationInputPreparer preparer, IOllamaService ollama,
        IFolderDescriptionRepository store, ILogger<SampleFolderDescriptionSource> logger)
    {
        _reads = reads;
        _facts = facts;
        _preparer = preparer;
        _ollama = ollama;
        _store = store;
        _logger = logger;
    }

    public FolderDescriptionSource Kind => FolderDescriptionSource.Sample;

    public async Task<FolderDescription?> DescribeAsync(ScanJob library, LibraryMapEntry folder, FolderDescribeRequest request, CancellationToken ct = default)
    {
        if (!request.Refresh && await _store.GetAsync(library.Id, folder.RelativePath, Kind, ct) is { } stored)
            return new FolderDescription(ContentDescription.FromJson(stored.FactsJson), Kind,
                JsonSerializer.Deserialize<List<string>>(stored.SampleJson) ?? [], stored.Model, Stored: true);

        var types = LibraryMap.DominantGroup(folder.Types) switch
        {
            ContentGroup.Media => new[] { FileType.Image, FileType.Video },
            ContentGroup.Document => [FileType.Document],
            _ => null
        };
        if (types is null) return null;

        var files = new List<FileNode>();
        var full = folder.FullPath;
        var prefix = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        await foreach (var row in _reads.StreamFilesAsync(new FileListFilter
                       {
                           JobId = library.Id, Directory = full, Recursive = true, DirectoryPrefix = prefix, FileTypes = types
                       }, ct))
        {
            if (row.File.SensitiveMarkers == SensitiveMarker.None)
                files.Add(row.File);
        }
        var sample = Spread(files, request.SampleSize);
        if (sample.Count == 0) return null;

        var described = new List<IReadOnlyList<FileFact>>();
        string? model = null;
        var asked = false;
        foreach (var file in sample)
        {
            ct.ThrowIfCancellationRequested();
            var known = (await _facts.GetByFileNodeIdAsync(file.Id, ct)).Where(f => f.Key.IsDescription()).ToList();
            if (known.Count > 0 || !request.UseModel)
            {
                described.Add(known);
                continue;
            }
            asked = true;
            var input = await _preparer.PrepareAsync(file, ct);
            var answer = await _ollama.ClassifyAsync(input, ct);
            if (answer.Error is not null)
            {
                _logger.LogInformation("Folder sample {Path}: no description ({Error})", file.RelativePath, answer.Error);
                described.Add([]);
                continue;
            }
            model ??= answer.ModelUsed;
            described.Add(answer.DescribedFacts.Where(f => f.Key.IsDescription()).ToList());
        }

        var description = ContentDescription.From(described);
        var paths = sample.Select(f => f.RelativePath.Replace('\\', '/')).ToList();
        // Kept only when it says something; a folder the model could not describe is asked again next time.
        if (!description.IsEmpty && (asked || described.All(d => d.Count > 0)))
        {
            await _store.SaveAsync(new FolderDescriptionRecord
            {
                JobId = library.Id,
                RelativePath = folder.RelativePath,
                Source = Kind,
                SampleJson = JsonSerializer.Serialize(paths),
                FactsJson = description.ToJson(),
                Sampled = description.Sampled,
                Described = description.Described,
                Model = model,
                DescribedAt = DateTime.UtcNow
            }, ct);
        }
        return new FolderDescription(description, Kind, paths, model, Stored: false);
    }

    /// <summary>Up to <paramref name="size"/> files spread evenly over their times (first, last and between).</summary>
    public static IReadOnlyList<FileNode> Spread(IReadOnlyList<FileNode> files, int size)
    {
        if (files.Count <= size) return files.OrderBy(TimeOf).ThenBy(f => f.FullPath, StringComparer.Ordinal).ToList();
        var ordered = files.OrderBy(TimeOf).ThenBy(f => f.FullPath, StringComparer.Ordinal).ToList();
        if (size <= 1) return [ordered[0]];
        var picked = new List<FileNode>(size);
        for (var i = 0; i < size; i++)
            picked.Add(ordered[(int)Math.Round(i * (ordered.Count - 1) / (double)(size - 1))]);
        return picked.Distinct().ToList();
    }

    private static readonly IReadOnlyDictionary<long, CaptureInfo> NoCaptures = new Dictionary<long, CaptureInfo>();

    private static DateTime TimeOf(FileNode f) => EventGrouper.TimeOf(f, NoCaptures).Time ?? f.LastWriteTime;
}
