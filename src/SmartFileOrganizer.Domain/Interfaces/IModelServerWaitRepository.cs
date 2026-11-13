using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>
/// Whether an analysis of a job is waiting for the model server right now, so <c>sfo status</c> in another
/// process can show it. One row per waiting job; removed when the run continues or ends.
/// </summary>
public interface IModelServerWaitRepository
{
    Task SetAsync(ModelServerWait wait, CancellationToken ct = default);

    /// <summary>Records that the waiting run probed the server at <paramref name="checkedAt"/>.</summary>
    Task TouchAsync(long jobId, DateTime checkedAt, CancellationToken ct = default);

    Task ClearAsync(long jobId, CancellationToken ct = default);

    Task<ModelServerWait?> GetAsync(long jobId, CancellationToken ct = default);
}
