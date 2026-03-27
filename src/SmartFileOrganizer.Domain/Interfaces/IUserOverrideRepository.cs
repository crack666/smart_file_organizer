using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IUserOverrideRepository
{
    Task UpsertAsync(UserOverride userOverride, CancellationToken ct = default);
    Task<UserOverride?> GetByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default);
    Task<IReadOnlyList<UserOverride>> GetAllAsync(CancellationToken ct = default);
}
