using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>What <see cref="BackedUpSkipService.ApplyAsync"/> left out of the analysis.</summary>
/// <param name="AgainstJobId">The library job of the comparison.</param>
/// <param name="Files">Files waiting for the analysis that are <c>backed_up</c> and now Skipped.</param>
/// <param name="Directories">Folders whose every file below is <c>backed_up</c>: no pre-assessment, no summary.</param>
public sealed record BackedUpSkip(long AgainstJobId, long ComparisonId, int Files, int Directories);

/// <summary>
/// Leaves out of the analysis what the library already holds by content
/// (https://github.com/crack666/smart_file_organizer/issues/42). Only the state <c>backed_up</c> of the stored
/// comparison counts, which rests on the full SHA-256 of both files; <c>new</c>, <c>differs</c> and <c>error</c>
/// are judged as before. A skip removes no marker: sensitive files stay sensitive.
/// </summary>
public class BackedUpSkipService
{
    private readonly IContentComparisonRepository _comparisons;
    private readonly IDirectoryRepository _directories;
    private readonly IFileRepository _files;
    private readonly ILogger<BackedUpSkipService> _logger;

    public BackedUpSkipService(IContentComparisonRepository comparisons, IDirectoryRepository directories,
        IFileRepository files, ILogger<BackedUpSkipService> logger)
    {
        _comparisons = comparisons;
        _directories = directories;
        _files = files;
        _logger = logger;
    }

    /// <summary>The stored comparison of the job against this library job, or null.</summary>
    public Task<ComparisonHeader?> FindComparisonAsync(long jobId, long againstJobId, CancellationToken ct = default) =>
        _comparisons.GetComparisonAsync(jobId, againstJobId, ct);

    /// <summary>
    /// Marks the files and folders of <paramref name="jobId"/> that the comparison against
    /// <paramref name="againstJobId"/> found <c>backed_up</c>. Without a library job, or when no comparison is
    /// stored, every earlier mark is released and the whole job is analysed as before; then null is returned.
    /// </summary>
    public async Task<BackedUpSkip?> ApplyAsync(long jobId, long? againstJobId, CancellationToken ct = default)
    {
        var header = againstJobId is { } against ? await _comparisons.GetComparisonAsync(jobId, against, ct) : null;
        if (header is null)
        {
            await _comparisons.SetAnalysisSkipAsync(jobId, [], [], ct);
            return null;
        }

        var rows = await _comparisons.GetFileComparisonsAsync(header.Id, ct);
        var backedUp = rows.Where(r => r.State == ContentState.BackedUp).Select(r => r.FileNodeId).ToList();

        // A folder is left out only when every file of the job below it is backed_up. That needs the comparison
        // to cover every file of the job; otherwise no folder is left out, only single files.
        var directoryIds = new List<long>();
        if (rows.Count == await _files.CountByJobAsync(jobId, ct))
            directoryIds = FullyBackedUp(await _directories.GetAllForJobOrderedByDepthAsync(jobId, ct), rows);
        else
            _logger.LogWarning("The comparison {Id} does not cover every file of job {JobId}; no folder is left out.", header.Id, jobId);

        var (files, directories) = await _comparisons.SetAnalysisSkipAsync(jobId, backedUp, directoryIds, ct);
        _logger.LogInformation("Job {JobId} against library job {Against}: {Files} file(s) and {Dirs} folder(s) left out as backed_up.",
            jobId, header.AgainstJobId, files, directories);
        return new BackedUpSkip(header.AgainstJobId ?? 0, header.Id, files, directories);
    }

    /// <summary>Folders with at least one file below them, every one of them <c>backed_up</c>.</summary>
    public static List<long> FullyBackedUp(IReadOnlyList<DirectoryNode> directories, IReadOnlyList<FileComparison> rows)
    {
        var byPath = directories.ToDictionary(d => d.FullPath, StringComparer.Ordinal);
        var hasFile = new HashSet<long>();
        var notBackedUp = new HashSet<long>();
        foreach (var row in rows)
        {
            for (var path = Path.GetDirectoryName(row.FullPath); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            {
                if (!byPath.TryGetValue(path, out var dir)) continue;
                hasFile.Add(dir.Id);
                if (row.State != ContentState.BackedUp) notBackedUp.Add(dir.Id);
            }
        }
        return hasFile.Where(id => !notBackedUp.Contains(id)).Order().ToList();
    }
}
