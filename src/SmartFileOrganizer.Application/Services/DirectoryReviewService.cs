using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Corrections of directory verdicts by a person, the counterpart of <see cref="ReviewService"/> for files
/// (https://github.com/crack666/smart_file_organizer/issues/14). The correction is stored beside the verdict of rule or
/// model and wins wherever the verdict is read; the next analysis follows it and does not ask the model again.
/// </summary>
/// <remarks>
/// It holds for the one directory: subdirectories that were judged keep their verdict. Files directly in it follow
/// (<see cref="IDirectoryOverrideRepository.ApplyToFilesAsync"/>), results they already have stay.
/// </remarks>
public sealed class DirectoryReviewService
{
    private readonly IDirectoryOverrideRepository _overrides;
    private readonly IDirectoryVerdictRepository _verdicts;
    private readonly IDirectorySampleRepository _samples;

    public DirectoryReviewService(IDirectoryOverrideRepository overrides, IDirectoryVerdictRepository verdicts,
        IDirectorySampleRepository samples)
    {
        _overrides = overrides;
        _verdicts = verdicts;
        _samples = samples;
    }

    /// <summary>Sets the verdict of a directory by hand, replacing an earlier correction.</summary>
    /// <param name="retention">Null: the retention value of rule or model stays.</param>
    public async Task ApplyOverrideAsync(long directoryNodeId, DirectoryVerdictKind verdict, RetentionValue? retention, string? note,
        CancellationToken ct = default)
    {
        await _overrides.UpsertAsync(new DirectoryOverride
        {
            DirectoryNodeId = directoryNodeId,
            Verdict = verdict,
            Retention = retention,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            OverriddenAt = DateTime.UtcNow
        }, ct);
        await FollowAsync(directoryNodeId, verdict, ct);
    }

    /// <summary>Takes the correction back; the verdict of rule or model counts again.</summary>
    public async Task ResetAsync(long directoryNodeId, CancellationToken ct = default)
    {
        if (!await _overrides.DeleteAsync(directoryNodeId, ct)) return;
        var verdict = await _verdicts.GetByNodeIdAsync(directoryNodeId, ct);
        await FollowAsync(directoryNodeId, verdict?.Verdict, ct);
    }

    private async Task FollowAsync(long directoryNodeId, DirectoryVerdictKind? verdict, CancellationToken ct)
    {
        // Files waiting for an open sample would wait for ever once the directory is not uniform any more.
        if (verdict != DirectoryVerdictKind.Uniform
            && await _samples.GetByNodeIdAsync(directoryNodeId, ct) is { State: SampleState.Open })
            await _samples.DeleteAsync(directoryNodeId, ct);

        await _overrides.ApplyToFilesAsync(directoryNodeId,
            verdict is DirectoryVerdictKind.Unit or DirectoryVerdictKind.System, ct);
    }
}
