using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IClassificationRepository
{
    Task UpsertAsync(FileClassification classification, CancellationToken ct = default);
    Task<FileClassification?> GetByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default);

    /// <summary>Stores a result and, if given, its check in one transaction, so a check never exists
    /// without its result or the other way round.</summary>
    Task SaveAsync(FileClassification classification, ResultCheck? check, CancellationToken ct = default);

    /// <summary>All checks of a file, oldest first.</summary>
    Task<IReadOnlyList<ResultCheck>> GetChecksAsync(long fileNodeId, CancellationToken ct = default);
}
