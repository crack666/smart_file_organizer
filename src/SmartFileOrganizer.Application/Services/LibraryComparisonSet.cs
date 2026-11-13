using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// The stored comparisons of one source with several libraries, read as one
/// (https://github.com/crack666/smart_file_organizer/issues/74). A library often has several roots (separate shares
/// for photos, videos, documents); a file is <c>backed_up</c> as soon as any of them holds it with the same content,
/// and <see cref="FileComparison.MatchJobId"/> says which.
/// </summary>
/// <remarks>
/// Per file the states of the single comparisons are joined in this order: <c>backed_up</c> in any library (the first
/// library named that holds it), else <c>error</c> in any (it is unknown whether another root holds it), else
/// <c>differs</c> in any (a namesake with other content), else <c>new</c>. Duplicate groups come from the first
/// comparison: every comparison of a source finds the same groups.
/// </remarks>
public sealed class LibraryComparisonSet
{
    private LibraryComparisonSet(IReadOnlyList<ComparisonHeader> headers, IReadOnlyList<FileComparison> files)
    {
        Headers = headers;
        Files = files;
        Rows = files.ToDictionary(f => f.FileNodeId);
    }

    /// <summary>One stored comparison per library, in the order the libraries were named.</summary>
    public IReadOnlyList<ComparisonHeader> Headers { get; }

    /// <summary>The joined result per source file, ordered by path.</summary>
    public IReadOnlyList<FileComparison> Files { get; }

    public IReadOnlyDictionary<long, FileComparison> Rows { get; }

    /// <summary>The first library named; it stands for the set where one library is expected.</summary>
    public ComparisonHeader First => Headers[0];

    public IReadOnlyList<long> LibraryJobIds => Headers.Select(h => h.AgainstJobId!.Value).ToList();

    public bool Complete => Headers.All(h => h.Complete);

    public int ErrorCount => Headers.Sum(h => h.ErrorCount);

    /// <summary>The oldest of the comparisons: the set is no newer than that.</summary>
    public DateTime ComparedAt => Headers.Min(h => h.ComparedAt);

    /// <summary>"library job 8" or "library jobs 8 and 9", for reasons and notes.</summary>
    public string Label => Headers.Count == 1
        ? $"library job {Headers[0].AgainstJobId}"
        : $"library jobs {string.Join(", ", LibraryJobIds.Take(LibraryJobIds.Count - 1))} and {LibraryJobIds[^1]}";

    /// <summary>
    /// The comparisons of <paramref name="sourceJobId"/> with <paramref name="libraryJobIds"/>, or, without ids, the
    /// libraries of the newest <c>sfo compare</c> of the job. Null when nothing is stored (or no id is given and the job
    /// was never compared with a library); <paramref name="missing"/> lists the named libraries without a comparison,
    /// and the set holds only the others.
    /// </summary>
    public static async Task<(LibraryComparisonSet? Set, IReadOnlyList<long> Missing)> LoadAsync(
        IContentComparisonRepository repo, long sourceJobId, IReadOnlyList<long>? libraryJobIds, CancellationToken ct = default)
    {
        var headers = new List<ComparisonHeader>();
        var missing = new List<long>();
        if (libraryJobIds is { Count: > 0 })
        {
            foreach (var id in libraryJobIds.Distinct())
            {
                if (await repo.GetComparisonAsync(sourceJobId, id, ct) is { } header)
                    headers.Add(header);
                else
                    missing.Add(id);
            }
        }
        else
        {
            headers.AddRange(await repo.GetLatestLibraryRunAsync(sourceJobId, ct));
        }

        if (headers.Count == 0)
            return (null, missing);

        var lists = new List<IReadOnlyList<FileComparison>>(headers.Count);
        foreach (var header in headers)
            lists.Add(await repo.GetFileComparisonsAsync(header.Id, ct));
        return (From(headers, lists), missing);
    }

    /// <summary>Joins the rows of single comparisons, one list per header, in the same order.</summary>
    public static LibraryComparisonSet From(IReadOnlyList<ComparisonHeader> headers, IReadOnlyList<IReadOnlyList<FileComparison>> rows)
    {
        if (headers.Count == 0 || headers.Count != rows.Count)
            throw new ArgumentException("One list of rows per comparison is needed.", nameof(rows));
        return new LibraryComparisonSet(headers, Merge(headers, rows));
    }

    public static IReadOnlyList<FileComparison> Merge(IReadOnlyList<ComparisonHeader> headers, IReadOnlyList<IReadOnlyList<FileComparison>> rows)
    {
        if (rows.Count == 1)
            return rows[0].Select(r => r with { MatchJobId = r.MatchJobId ?? (r.MatchFileNodeId is null ? null : headers[0].AgainstJobId) }).ToList();

        var byFile = new Dictionary<long, List<(FileComparison Row, long? Library)>>();
        for (var i = 0; i < rows.Count; i++)
        {
            foreach (var row in rows[i])
            {
                if (!byFile.TryGetValue(row.FileNodeId, out var list))
                    byFile[row.FileNodeId] = list = [];
                list.Add((row, headers[i].AgainstJobId));
            }
        }

        var merged = new List<FileComparison>(byFile.Count);
        foreach (var list in byFile.Values)
        {
            var first = list[0].Row;
            var chosen = list.FirstOrDefault(x => x.Row.State == ContentState.BackedUp);
            if (chosen.Row is null) chosen = list.FirstOrDefault(x => x.Row.State == ContentState.Error);
            if (chosen.Row is null) chosen = list.FirstOrDefault(x => x.Row.State == ContentState.Differs);
            if (chosen.Row is null) chosen = list[0];
            var row = chosen.Row with
            {
                DuplicateGroup = first.DuplicateGroup,
                MatchJobId = chosen.Row.MatchJobId ?? (chosen.Row.MatchFileNodeId is null ? null : chosen.Library)
            };
            merged.Add(row);
        }
        return merged.OrderBy(r => r.FullPath, StringComparer.Ordinal).ToList();
    }

    /// <summary>Files <c>backed_up</c> per library job, in the order the libraries were named.</summary>
    public IReadOnlyList<(long LibraryJobId, int Files, long Bytes)> BackedUpByLibrary() =>
        LibraryJobIds.Select(id =>
        {
            var rows = Files.Where(f => f.State == ContentState.BackedUp && f.MatchJobId == id).ToList();
            return (id, rows.Count, rows.Sum(r => r.Size));
        }).ToList();
}
