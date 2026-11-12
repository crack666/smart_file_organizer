using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>Stored bundles of a job (https://github.com/crack666/smart_file_organizer/issues/39).</summary>
public interface IBundleRepository
{
    /// <summary>
    /// Replaces every bundle of the job with <paramref name="roots"/> (and their children and members) and
    /// records the build, in one transaction.
    /// </summary>
    Task ReplaceAsync(long jobId, IReadOnlyList<Bundle> roots, BundleBuild build, CancellationToken ct = default);

    /// <summary>The last build of the job, or null when bundles were never built (or dropped by a rescan).</summary>
    Task<BundleBuild?> GetBuildAsync(long jobId, CancellationToken ct = default);

    /// <summary>All bundles of the job, flat, ordered by id (parents before children). Members are not loaded.</summary>
    Task<IReadOnlyList<Bundle>> GetAllAsync(long jobId, CancellationToken ct = default);

    /// <summary>File id to the bundle it belongs to directly.</summary>
    Task<IReadOnlyDictionary<long, long>> GetMembershipAsync(long jobId, CancellationToken ct = default);

    /// <summary>Comparison states per bundle from the latest comparison of the job with a library; null without one.</summary>
    Task<BundleComparisonCounts?> GetComparisonCountsAsync(long jobId, CancellationToken ct = default);
}

/// <summary>
/// Capture time and device of photos and videos, read from stored metadata facts. The bundle builder uses
/// them before dates in file names and modification times.
/// </summary>
public interface ICaptureInfoSource
{
    /// <summary>Known capture infos of the job's files by file id; files without any are left out.</summary>
    Task<IReadOnlyDictionary<long, CaptureInfo>> GetAsync(long jobId, CancellationToken ct = default);
}
