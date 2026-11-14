using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IFileFactRepository
{
    /// <summary>Replaces all facts of a file with <paramref name="facts"/> (none: deletes them).</summary>
    Task ReplaceAsync(long fileNodeId, IReadOnlyList<FileFact> facts, CancellationToken ct = default);

    /// <summary>
    /// Replaces only the facts of the given keys (and of the keys in <paramref name="facts"/>); every other fact of the file
    /// stays. Used by <c>sfo facts refresh</c>, which adds what metadata and names tell to an earlier analysis.
    /// </summary>
    Task ReplaceKeysAsync(long fileNodeId, IReadOnlyCollection<FileFactKey> keys, IReadOnlyList<FileFact> facts, CancellationToken ct = default);

    Task<IReadOnlyList<FileFact>> GetByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default);

    /// <summary>The facts of every file of a job, by file id; files without facts are left out.</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<FileFact>>> GetByJobAsync(long jobId, CancellationToken ct = default);
}
