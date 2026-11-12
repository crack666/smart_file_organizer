using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Handles manual user overrides for file classifications. A category that differs from the
/// model's answer is also kept as a test case (<see cref="EvalCaseService"/>), so later changes
/// to prompts, rules or model can be checked against it.
/// </summary>
public class ReviewService
{
    private readonly IUserOverrideRepository _overrideRepo;
    private readonly IFileRepository _fileRepo;
    private readonly EvalCaseService? _evalCases;

    public ReviewService(IUserOverrideRepository overrideRepo, IFileRepository fileRepo, EvalCaseService? evalCases = null)
    {
        _overrideRepo = overrideRepo;
        _fileRepo = fileRepo;
        _evalCases = evalCases;
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
            OverriddenAt = DateTime.UtcNow
        };

        await _overrideRepo.UpsertAsync(uo, ct);
        await _fileRepo.UpdateStatusAsync(fileNodeId, FileNodeStatus.Approved, ct);

        // Never fails the override; returns null when there is nothing to keep.
        if (_evalCases != null)
            await _evalCases.RecordCategoryCorrectionAsync(fileNodeId, category, ct);
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
    }

    public Task<UserOverride?> GetOverrideAsync(long fileNodeId, CancellationToken ct = default)
        => _overrideRepo.GetByFileNodeIdAsync(fileNodeId, ct);
}
