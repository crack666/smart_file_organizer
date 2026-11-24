using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>Corrections of directory verdicts (<c>directory_overrides</c>). Readers get them merged into the verdict
/// from <see cref="IDirectoryVerdictRepository"/> and <see cref="IJobReadRepository"/>.</summary>
public interface IDirectoryOverrideRepository
{
    Task UpsertAsync(DirectoryOverride directoryOverride, CancellationToken ct = default);

    /// <summary>True when there was a correction.</summary>
    Task<bool> DeleteAsync(long directoryNodeId, CancellationToken ct = default);

    /// <summary>
    /// The files directly in the directory follow its verdict: handled as a whole, those still to be judged are Skipped;
    /// otherwise those only Skipped for that reason (no result, not left out as backed up) are judged again. Files with
    /// a result keep it. Returns the number of files changed.
    /// </summary>
    Task<int> ApplyToFilesAsync(long directoryNodeId, bool handledAsWhole, CancellationToken ct = default);
}
