using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IClassificationRepository
{
    Task UpsertAsync(FileClassification classification, CancellationToken ct = default);
    Task<FileClassification?> GetByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default);
}
