using System.Buffers.Binary;
using System.Security.Cryptography;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>
/// SHA-256 checksums for the library comparison and the duplicate report. Files are opened read-only and shared, so
/// neither the source medium nor the library is changed.
/// </summary>
public sealed class ContentHasher : IContentHasher
{
    /// <summary>Bytes read from the start and from the end for the sample checksum.</summary>
    public const int EdgeBytes = 64 * 1024;

    /// <summary>Files up to this size are read whole; their sample checksum is the full checksum.</summary>
    public const long WholeFileLimit = 2L * EdgeBytes;

    /// <summary>Fixed places between head and tail that the sample checksum reads.</summary>
    public const int MiddleSamples = 6;

    /// <summary>Bytes read at each of the <see cref="MiddleSamples"/> places.</summary>
    public const int MiddleBytes = 4 * 1024;

    /// <summary>
    /// The byte ranges the sample checksum reads from a file of <paramref name="size"/> bytes, in order: the head, the
    /// places in between, the tail. Derived from the size alone, so two files of one size are read at the same places.
    /// A file up to <see cref="WholeFileLimit"/> is one range over all of it.
    /// </summary>
    public static IReadOnlyList<(long Offset, int Length)> SampleRanges(long size)
    {
        if (size <= WholeFileLimit)
            return [(0, (int)size)];

        var ranges = new List<(long, int)>(MiddleSamples + 2) { (0, EdgeBytes) };
        var middle = size - 2L * EdgeBytes;
        var chunk = (int)Math.Min(MiddleBytes, middle);
        for (var i = 1; i <= MiddleSamples; i++)
            ranges.Add((EdgeBytes + (middle - chunk) * i / (MiddleSamples + 1), chunk));
        ranges.Add((size - EdgeBytes, EdgeBytes));
        return ranges;
    }

    public FileStat Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return FileStat.Missing("File not found or not reachable.");
            return new FileStat(true, info.Length, info.LastWriteTimeUtc.Ticks, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return FileStat.Missing(ex.Message);
        }
    }

    public async Task<SampleResult> ComputeSampleAsync(string path, long expectedSize, CancellationToken ct)
    {
        await using var stream = Open(path);
        RequireLength(stream, path, expectedSize);

        if (expectedSize <= WholeFileLimit)
        {
            // Small file: read it whole; the sample checksum is then the full checksum.
            var all = new byte[expectedSize];
            await stream.ReadExactlyAsync(all, ct);
            RequireEnd(stream, path);
            var full = Convert.ToHexStringLower(SHA256.HashData(all));
            return new SampleResult(full, full, expectedSize);
        }

        var ranges = SampleRanges(expectedSize);
        var buffer = new byte[8 + ranges.Sum(r => r.Length)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, expectedSize);
        var at = 8;
        foreach (var (offset, length) in ranges)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(buffer.AsMemory(at, length), ct);
            at += length;
        }
        RequireEnd(stream, path);
        return new SampleResult(Convert.ToHexStringLower(SHA256.HashData(buffer)), null, buffer.Length - 8);
    }

    public async Task<string> ComputeFullAsync(string path, long expectedSize, CancellationToken ct)
    {
        await using var stream = Open(path);
        RequireLength(stream, path, expectedSize);
        var hash = await SHA256.HashDataAsync(stream, ct);
        if (stream.Position != expectedSize)
            throw new IOException($"File changed while it was read: {path}");
        return Convert.ToHexStringLower(hash);
    }

    private static FileStream Open(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        BufferSize = 0
    });

    private static void RequireLength(FileStream stream, string path, long expectedSize)
    {
        if (stream.Length != expectedSize)
            throw new IOException($"File size changed from {expectedSize} to {stream.Length} bytes: {path}");
    }

    /// <summary>A file that grew while it was read has no stable content to compare.</summary>
    private static void RequireEnd(FileStream stream, string path)
    {
        if (stream.Position != stream.Length)
            throw new IOException($"File changed while it was read: {path}");
    }
}
