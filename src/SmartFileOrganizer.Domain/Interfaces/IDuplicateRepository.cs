using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>
/// Reads for the duplicate report over one or more jobs (https://github.com/crack666/smart_file_organizer/issues/87).
/// Only the scan rows are read; checksums come from <see cref="IContentComparisonRepository"/>.
/// </summary>
public interface IDuplicateRepository
{
    /// <summary>
    /// Stage 1, without opening a file: the files of the jobs of at least <paramref name="minSize"/> bytes (never
    /// empty ones) whose size occurs at least twice among them. Ordered by size, then path.
    /// </summary>
    Task<IReadOnlyList<DuplicateCandidate>> GetSizeCandidatesAsync(IReadOnlyList<long> jobIds, long minSize, CancellationToken ct = default);

    /// <summary>Number and bytes of all files of the jobs, as scanned.</summary>
    Task<(long Files, long Bytes)> GetTotalsAsync(IReadOnlyList<long> jobIds, CancellationToken ct = default);

    /// <summary>Files of at least <paramref name="minSize"/> bytes (never empty ones) per folder and job, not below it.</summary>
    Task<IReadOnlyList<FolderFileCount>> GetFolderFileCountsAsync(IReadOnlyList<long> jobIds, long minSize, CancellationToken ct = default);
}
