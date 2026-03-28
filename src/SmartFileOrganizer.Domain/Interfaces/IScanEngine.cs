using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public record ScanProgress(
    long JobId,
    long ProcessedFiles,
    long TotalFiles,
    long ProcessedDirectories,
    string CurrentPath,
    int ErrorCount
);

public interface IScanEngine
{
    /// <summary>
    /// Run (or resume) the given scan job.
    /// Reports live progress via <paramref name="progress"/>.
    /// Throws OperationCanceledException when cancelled.
    /// </summary>
    Task RunAsync(ScanJob job, IProgress<ScanProgress> progress, CancellationToken ct);
}
