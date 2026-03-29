using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Handles manual user overrides for file classifications.
/// </summary>
public class ReviewService
{
    private readonly IUserOverrideRepository _overrideRepo;
    private readonly IFileRepository _fileRepo;

    public ReviewService(IUserOverrideRepository overrideRepo, IFileRepository fileRepo)
    {
        _overrideRepo = overrideRepo;
        _fileRepo = fileRepo;
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
    }

    /// <summary>
    /// Accepts the AI suggestion without changes — just marks the file as Approved.
    /// </summary>
    public Task AcceptAsync(long fileNodeId, CancellationToken ct = default)
        => _fileRepo.UpdateStatusAsync(fileNodeId, FileNodeStatus.Approved, ct);

    public Task<UserOverride?> GetOverrideAsync(long fileNodeId, CancellationToken ct = default)
        => _overrideRepo.GetByFileNodeIdAsync(fileNodeId, ct);
}
