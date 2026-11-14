using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>Stored target matches of findings (<c>finding_targets</c>), one per job and finding key.</summary>
public interface IFindingTargetRepository
{
    /// <summary>All stored matches of a job by finding key.</summary>
    Task<IReadOnlyDictionary<string, FindingTargetRecord>> GetAllAsync(long jobId, CancellationToken ct = default);

    /// <summary>Stores the match, replacing an earlier one for the same job and key.</summary>
    Task UpsertAsync(FindingTargetRecord record, CancellationToken ct = default);
}
