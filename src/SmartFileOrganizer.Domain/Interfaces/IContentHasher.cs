using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>
/// Reads file content for the comparison. Opens files read-only and never changes them.
/// Read failures throw <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>;
/// the caller records them as errors.
/// </summary>
public interface IContentHasher
{
    /// <summary>Current size and modification time. Never throws; a missing or unreachable file has <c>Exists = false</c>.</summary>
    FileStat Stat(string path);

    /// <summary>
    /// SHA-256 over the size and the first and last 64 KiB. Files up to 128 KiB are read whole;
    /// then the result also carries the full checksum. Throws if the file no longer has <paramref name="expectedSize"/>.
    /// </summary>
    Task<HeadTailResult> ComputeHeadTailAsync(string path, long expectedSize, CancellationToken ct);

    /// <summary>SHA-256 over the whole file. Throws if the file no longer has <paramref name="expectedSize"/>.</summary>
    Task<string> ComputeFullAsync(string path, long expectedSize, CancellationToken ct);
}
