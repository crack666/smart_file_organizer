using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>
/// Samples of uniform directories (<c>directory_samples</c>). Every state change is one transaction
/// together with the file statuses and results it implies.
/// </summary>
public interface IDirectorySampleRepository
{
    /// <summary>
    /// Stores a drawn sample (state open) and sets the given files from Discovered to Deferred.
    /// Replaces an earlier sample of the same directory.
    /// </summary>
    Task CreateAsync(DirectorySample sample, IReadOnlyList<long> deferredFileIds, CancellationToken ct = default);

    /// <summary>
    /// Stores the confirmed sample and, for every given result whose file is still Deferred, the inherited
    /// result with file status Inherited. Deferred files without a given result go back to Discovered and are
    /// judged one by one (an empty list: the verdict stands, but nothing inherits). Returns how many files inherited.
    /// </summary>
    Task<int> ConfirmAsync(DirectorySample sample, IReadOnlyList<FileClassification> inherited, CancellationToken ct = default);

    /// <summary>
    /// Stores the contradicted sample, raises the directory verdict from uniform to mixed
    /// (keeping the old verdict in <c>escalated_from</c>) and sets the directory's Deferred files back to Discovered.
    /// </summary>
    Task EscalateAsync(DirectorySample sample, string escalationReason, CancellationToken ct = default);

    /// <summary>Removes the sample of a directory that is judged again; Deferred files return to Discovered.</summary>
    Task DeleteAsync(long directoryNodeId, CancellationToken ct = default);

    Task<DirectorySample?> GetByNodeIdAsync(long directoryNodeId, CancellationToken ct = default);

    /// <summary>All samples of a job, ordered by directory path.</summary>
    Task<IReadOnlyList<DirectorySample>> GetForJobAsync(long jobId, CancellationToken ct = default);
}
