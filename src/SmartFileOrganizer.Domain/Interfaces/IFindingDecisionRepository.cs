using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>Decisions about findings (<c>finding_decisions</c>). Nothing else about a finding is stored.</summary>
public interface IFindingDecisionRepository
{
    /// <summary>All decisions of a job by finding key.</summary>
    Task<IReadOnlyDictionary<string, FindingDecision>> GetAllAsync(long jobId, CancellationToken ct = default);

    /// <summary>Stores the decision, replacing an earlier one for the same job and key.</summary>
    Task UpsertAsync(FindingDecision decision, CancellationToken ct = default);

    /// <summary>Removes the decision, so the finding is open again. Returns false when there was none.</summary>
    Task<bool> DeleteAsync(long jobId, string findingKey, CancellationToken ct = default);
}
