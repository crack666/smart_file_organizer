using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>Storage for content fingerprints and comparison results.</summary>
public interface IContentComparisonRepository
{
    /// <summary>All files of a job.</summary>
    Task<IReadOnlyList<ComparableFile>> GetFilesAsync(long jobId, CancellationToken ct = default);

    /// <summary>Files of <paramref name="againstJobId"/> whose size occurs among the files of <paramref name="sourceJobId"/>.</summary>
    Task<IReadOnlyList<ComparableFile>> GetFilesWithSourceSizesAsync(long againstJobId, long sourceJobId, CancellationToken ct = default);

    /// <summary>Files of <paramref name="againstJobId"/> whose name (ASCII case-insensitive) occurs among the files of <paramref name="sourceJobId"/>.</summary>
    Task<IReadOnlyList<ComparableFile>> GetFilesWithSourceNamesAsync(long againstJobId, long sourceJobId, CancellationToken ct = default);

    /// <summary>Full paths of the directories of a job the scan skipped by rule (<c>dir_status</c> skip).</summary>
    Task<IReadOnlyList<string>> GetSkippedDirectoriesAsync(long jobId, CancellationToken ct = default);

    /// <summary>Directories of a job that the scan could not read.</summary>
    Task<IReadOnlyList<ScanDirectoryError>> GetDirectoryErrorsAsync(long jobId, CancellationToken ct = default);

    /// <summary>Stored fingerprints for the given paths (missing paths are absent from the result).</summary>
    Task<IReadOnlyDictionary<string, ContentFingerprint>> GetFingerprintsAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default);

    Task UpsertFingerprintsAsync(IReadOnlyCollection<ContentFingerprint> fingerprints, CancellationToken ct = default);

    /// <summary>Replaces the stored comparison of the same source and library job. Returns the new comparison id.</summary>
    Task<long> SaveComparisonAsync(ComparisonHeader header, IReadOnlyList<FileComparison> files, CancellationToken ct = default);

    /// <summary>The stored comparison of a source job against a library job (or without one), or null.</summary>
    Task<ComparisonHeader?> GetComparisonAsync(long sourceJobId, long? againstJobId, CancellationToken ct = default);

    /// <summary>The newest stored comparison of a source job against any library job, or null.</summary>
    Task<ComparisonHeader?> GetLatestWithLibraryAsync(long sourceJobId, CancellationToken ct = default);

    /// <summary>Per-file results of a stored comparison, ordered by path.</summary>
    Task<IReadOnlyList<FileComparison>> GetFileComparisonsAsync(long comparisonId, CancellationToken ct = default);
}
