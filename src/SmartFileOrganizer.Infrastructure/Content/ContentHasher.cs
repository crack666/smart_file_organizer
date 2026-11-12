using System.Buffers.Binary;
using System.Security.Cryptography;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>
/// SHA-256 checksums for the archive comparison. Files are opened read-only and shared, so
/// neither the source medium nor the archive is changed.
/// </summary>
public sealed class ContentHasher : IContentHasher
{
    /// <summary>Bytes read from the start and from the end for the head-and-tail checksum.</summary>
    public const int EdgeBytes = 64 * 1024;

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

    public async Task<HeadTailResult> ComputeHeadTailAsync(string path, long expectedSize, CancellationToken ct)
    {
        await using var stream = Open(path);
        RequireLength(stream, path, expectedSize);

        if (expectedSize <= 2L * EdgeBytes)
        {
            // Small file: read it whole; the head-and-tail checksum is then the full checksum.
            var all = new byte[expectedSize];
            await stream.ReadExactlyAsync(all, ct);
            RequireEnd(stream, path);
            var full = Convert.ToHexStringLower(SHA256.HashData(all));
            return new HeadTailResult(full, full);
        }

        var buffer = new byte[8 + 2 * EdgeBytes];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, expectedSize);
        await stream.ReadExactlyAsync(buffer.AsMemory(8, EdgeBytes), ct);
        stream.Seek(expectedSize - EdgeBytes, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer.AsMemory(8 + EdgeBytes, EdgeBytes), ct);
        RequireEnd(stream, path);
        return new HeadTailResult(Convert.ToHexStringLower(SHA256.HashData(buffer)), null);
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
