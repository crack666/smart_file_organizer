using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IUserOverrideRepository
{
    Task UpsertAsync(UserOverride userOverride, CancellationToken ct = default);

    /// <summary>Adds the row unless the file already has a correction; true when it was added.</summary>
    Task<bool> InsertIfAbsentAsync(UserOverride userOverride, CancellationToken ct = default);
    Task DeleteByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default);
    Task<UserOverride?> GetByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default);
    Task<IReadOnlyList<UserOverride>> GetAllAsync(CancellationToken ct = default);
}
