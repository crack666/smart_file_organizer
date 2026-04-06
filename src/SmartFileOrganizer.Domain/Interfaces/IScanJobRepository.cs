using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IScanJobRepository
{
    Task<ScanJob> CreateAsync(ScanJob job, CancellationToken ct = default);
    Task<ScanJob?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<ScanJob>> GetAllAsync(CancellationToken ct = default);
    Task UpdateAsync(ScanJob job, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
}
