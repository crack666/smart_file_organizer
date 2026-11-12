using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IFileFactRepository
{
    /// <summary>Replaces all facts of a file with <paramref name="facts"/> (none: deletes them).</summary>
    Task ReplaceAsync(long fileNodeId, IReadOnlyList<FileFact> facts, CancellationToken ct = default);

    Task<IReadOnlyList<FileFact>> GetByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default);
}
