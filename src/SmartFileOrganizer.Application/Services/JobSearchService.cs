using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>What a search over a job found.</summary>
/// <param name="Terms">The words searched for; every one must occur in a hit.</param>
/// <param name="FileIds">Every matching file.</param>
/// <param name="Files">The first matching files by path, as many as were asked for.</param>
public sealed record JobSearchResult(
    long JobId, string Text, IReadOnlyList<string> Terms, IReadOnlySet<long> FileIds, IReadOnlyList<FileNode> Files);

/// <summary>
/// Searches the files of one job by name, path below the job root and summary
/// (https://github.com/crack666/smart_file_organizer/issues/27). Several words are searched for separately and must all
/// occur, case ignored (umlauts too). The summary of a file whose content may not be shown (access data) is not searched,
/// so a search cannot find out what stands in it. Reads the database only.
/// </summary>
public sealed class JobSearchService
{
    /// <summary>Files a listing of hits holds at most.</summary>
    public const int DefaultLimit = 500;

    private readonly IJobReadRepository _read;

    public JobSearchService(IJobReadRepository read) => _read = read;

    /// <summary>The words of a search text.</summary>
    public static IReadOnlyList<string> TermsOf(string? text) =>
        (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when every term occurs in at least one of <paramref name="texts"/>.</summary>
    public static bool Matches(IReadOnlyList<string> terms, params string?[] texts) =>
        terms.All(term => texts.Any(t => t is not null && t.Contains(term, StringComparison.OrdinalIgnoreCase)));

    public async Task<JobSearchResult> SearchAsync(
        long jobId, string text, SensitiveOutputOptions output, int limit = DefaultLimit, CancellationToken ct = default)
    {
        var terms = TermsOf(text);
        var ids = new HashSet<long>();
        var files = new List<FileNode>();
        if (terms.Count == 0) return new JobSearchResult(jobId, text, terms, ids, files);

        await foreach (var row in _read.StreamFilesAsync(new FileListFilter { JobId = jobId }, ct))
        {
            var f = row.File;
            var summary = f.Classification?.Summary is { Length: > 0 } s && output.ShowsContent(SensitiveOutputGuard.GetMarkers(f)) ? s : null;
            // A path is stored with the separator of the system; a person may type either.
            if (!Matches(terms, f.Name, f.RelativePath, f.RelativePath.Replace('\\', '/'), summary)) continue;
            ids.Add(f.Id);
            if (files.Count < limit) files.Add(f);
        }
        return new JobSearchResult(jobId, text, terms, ids, files);
    }
}
