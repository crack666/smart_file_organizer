using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>The decisions of stage 2 (<c>file_decisions</c>), one row per file of a job.</summary>
public interface IFileDecisionRepository
{
    /// <summary>Replaces every decision of the job with <paramref name="decisions"/> in one transaction.</summary>
    Task ReplaceAsync(long jobId, IReadOnlyList<FileDecision> decisions, CancellationToken ct = default);

    /// <summary>The stored decisions of the job, by file id; empty before the first <c>sfo decide</c>.</summary>
    Task<IReadOnlyDictionary<long, FileDecision>> GetByJobAsync(long jobId, CancellationToken ct = default);
}
