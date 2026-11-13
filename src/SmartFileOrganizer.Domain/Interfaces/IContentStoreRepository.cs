using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>
/// Assessments and corrections by content (<c>content_assessments</c>, <c>content_corrections</c>), shared by all jobs and
/// kept when a job is deleted (https://github.com/crack666/smart_file_organizer/issues/38).
/// </summary>
public interface IContentStoreRepository
{
    /// <summary>The entry for this content, model, prompt version and keep-hint question, or null.</summary>
    Task<ContentAssessment?> FindAsync(ContentKey key, AssessmentIdentity identity, bool keepHintAsked, CancellationToken ct = default);

    /// <summary>Stores an entry; an existing one with the same key, identity and question is replaced.</summary>
    Task SaveAsync(ContentAssessment assessment, CancellationToken ct = default);

    /// <summary>Counts one take-over of the entry.</summary>
    Task MarkUsedAsync(long id, DateTime usedAt, CancellationToken ct = default);

    Task<ContentCorrection?> FindCorrectionAsync(ContentKey key, CancellationToken ct = default);

    Task SaveCorrectionAsync(ContentCorrection correction, CancellationToken ct = default);

    Task DeleteCorrectionAsync(ContentKey key, CancellationToken ct = default);

    /// <param name="current">Entries of this model and prompt version count as current; null: none is current.</param>
    Task<ContentStoreStats> GetStatsAsync(AssessmentIdentity? current, CancellationToken ct = default);

    /// <summary>
    /// Deletes entries: all of them, or with <paramref name="keepCurrent"/> only those not made with
    /// <paramref name="current"/>. Corrections only with <paramref name="corrections"/>. Returns what was deleted.
    /// </summary>
    Task<(long Entries, long Corrections)> ClearAsync(AssessmentIdentity? current, bool keepCurrent, bool corrections, CancellationToken ct = default);
}
