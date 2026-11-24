using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>Outcome of a correction of several files at once.</summary>
/// <param name="Errors">The message of each file that could not be stored.</param>
public sealed record BulkReviewResult(int Applied, IReadOnlyList<string> Errors);

/// <summary>
/// Handles manual user overrides for file classifications. A category that differs from the
/// model's answer is also kept as a test case (<see cref="EvalCaseService"/>), so later changes
/// to prompts, rules or model can be checked against it. The category also follows the content of the file into every
/// other job (<see cref="ContentStoreService"/>, https://github.com/crack666/smart_file_organizer/issues/38).
/// </summary>
public class ReviewService
{
    private readonly IUserOverrideRepository _overrideRepo;
    private readonly IFileRepository _fileRepo;
    private readonly EvalCaseService? _evalCases;
    private readonly ContentStoreService? _contentStore;

    public ReviewService(IUserOverrideRepository overrideRepo, IFileRepository fileRepo, EvalCaseService? evalCases = null,
        ContentStoreService? contentStore = null)
    {
        _overrideRepo = overrideRepo;
        _fileRepo = fileRepo;
        _evalCases = evalCases;
        _contentStore = contentStore;
    }

    public async Task ApplyOverrideAsync(
        long fileNodeId,
        FileCategory category,
        string? suggestedTarget,
        string? note,
        CancellationToken ct = default)
    {
        var uo = new UserOverride
        {
            FileNodeId = fileNodeId,
            OverriddenCategory = category,
            OverriddenTarget = suggestedTarget,
            Note = note,
            OverriddenAt = DateTime.UtcNow,
            Origin = ResultOrigin.Human
        };

        await _overrideRepo.UpsertAsync(uo, ct);
        await _fileRepo.UpdateStatusAsync(fileNodeId, FileNodeStatus.Approved, ct);

        // The category holds for the content wherever it lies; the target belongs to this place and stays here.
        if (_contentStore != null)
            await _contentStore.RecordCorrectionAsync(fileNodeId, category, note, ct);

        // Never fails the override; returns null when there is nothing to keep.
        if (_evalCases != null)
            await _evalCases.RecordCategoryCorrectionAsync(fileNodeId, category, ct);
    }

    /// <summary>
    /// The same category for several files (https://github.com/crack666/smart_file_organizer/issues/14): each is stored as
    /// a correction of its own; target and note of an earlier correction stay. A file that fails does not stop the rest.
    /// </summary>
    /// <param name="onApplied">Called with each file id after it was stored.</param>
    public async Task<BulkReviewResult> ApplyCategoryAsync(IReadOnlyCollection<long> fileNodeIds, FileCategory category,
        Action<long>? onApplied = null, CancellationToken ct = default)
    {
        var applied = 0;
        var errors = new List<string>();
        foreach (var id in fileNodeIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var existing = await _overrideRepo.GetByFileNodeIdAsync(id, ct);
                await ApplyOverrideAsync(id, category, existing?.OverriddenTarget, existing?.Note, ct);
                applied++;
                onApplied?.Invoke(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(ex.Message);
            }
        }
        return new BulkReviewResult(applied, errors);
    }

    /// <summary>
    /// Accepts the AI suggestion without changes — just marks the file as Approved.
    /// </summary>
    public Task AcceptAsync(long fileNodeId, CancellationToken ct = default)
        => _fileRepo.UpdateStatusAsync(fileNodeId, FileNodeStatus.Approved, ct);

    /// <summary>
    /// Resets any manual review: deletes the override record and sets status back to AiAnalyzed.
    /// </summary>
    public async Task ResetReviewAsync(long fileNodeId, CancellationToken ct = default)
    {
        await _overrideRepo.DeleteByFileNodeIdAsync(fileNodeId, ct);
        await _fileRepo.UpdateStatusAsync(fileNodeId, FileNodeStatus.AiAnalyzed, ct);
        // Withdrawn here, withdrawn for the content: other files keep the rows they already got.
        if (_contentStore != null)
            await _contentStore.WithdrawCorrectionAsync(fileNodeId, ct);
    }

    public Task<UserOverride?> GetOverrideAsync(long fileNodeId, CancellationToken ct = default)
        => _overrideRepo.GetByFileNodeIdAsync(fileNodeId, ct);
}
