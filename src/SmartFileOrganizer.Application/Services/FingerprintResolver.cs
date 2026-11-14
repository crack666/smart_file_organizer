using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>One file during a run: its current size and modification time, its checksums as far as known, or its read error.</summary>
internal sealed class FingerprintEntry(string path, FileStat stat)
{
    public string Path { get; } = path;
    public FileStat Stat { get; } = stat;
    public string? Sample { get; set; }
    public string? Full { get; set; }

    /// <summary>When <see cref="Full"/> was computed from the file (now, or in an earlier run if cached).</summary>
    public DateTime? FullAt { get; set; }
    public string? Error { get; set; }
    public bool FromCache { get; set; }
    public bool Dirty { get; set; }
}

/// <summary>
/// Stats each file once per run and computes checksums on demand, using <c>content_fingerprints</c> where it is still
/// valid (same size and modification time). Shared by the comparison with a library and the duplicate report, so a
/// checksum one of them computed spares the other the read.
/// </summary>
internal sealed class FingerprintResolver(IContentHasher hasher, IReadOnlyDictionary<string, ContentFingerprint> cache)
{
    private readonly Dictionary<string, FingerprintEntry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reused = new(StringComparer.Ordinal);
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);

    public int SampleComputed { get; private set; }
    public int FullComputed { get; private set; }
    public int Reused => _reused.Count;

    /// <summary>Files opened for a checksum in this run.</summary>
    public int FilesRead => _read.Count;

    /// <summary>Bytes read from files in this run.</summary>
    public long BytesRead { get; private set; }

    public int DirtyCount { get; private set; }

    public FingerprintEntry? TryGet(string path) => _entries.GetValueOrDefault(path);

    public FingerprintEntry Get(string path)
    {
        if (_entries.TryGetValue(path, out var existing))
            return existing;

        var stat = hasher.Stat(path);
        var entry = new FingerprintEntry(path, stat) { Error = stat.Exists ? null : stat.Error ?? "File not found." };
        if (stat.Exists
            && cache.TryGetValue(path, out var fp)
            && fp.Size == stat.Size
            && fp.LastWriteTicks == stat.LastWriteTicks)
        {
            entry.Sample = fp.SampleHash;
            entry.Full = fp.FullHash;
            entry.FullAt = fp.FullHash == null ? null : fp.FullHashAt ?? fp.ComputedAt;
            entry.FromCache = true;
        }
        _entries[path] = entry;
        return entry;
    }

    public async Task<string?> SampleAsync(FingerprintEntry entry, CancellationToken ct)
    {
        if (entry.Error != null)
            return null;
        if (entry.Sample != null)
        {
            if (entry.FromCache)
                _reused.Add(entry.Path);
            return entry.Sample;
        }
        try
        {
            var result = await hasher.ComputeSampleAsync(entry.Path, entry.Stat.Size, ct);
            entry.Sample = result.SampleHash;
            if (entry.Full == null && result.FullHash != null)
            {
                entry.Full = result.FullHash;
                entry.FullAt = DateTime.UtcNow;
            }
            SampleComputed++;
            BytesRead += result.BytesRead;
            _read.Add(entry.Path);
            MarkDirty(entry);
            return entry.Sample;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entry.Error = ex.Message;
            return null;
        }
    }

    public async Task<string?> FullAsync(FingerprintEntry entry, CancellationToken ct)
    {
        if (entry.Error != null)
            return null;
        if (entry.Full != null)
        {
            if (entry.FromCache)
                _reused.Add(entry.Path);
            return entry.Full;
        }
        try
        {
            entry.Full = await hasher.ComputeFullAsync(entry.Path, entry.Stat.Size, ct);
            entry.FullAt = DateTime.UtcNow;
            FullComputed++;
            BytesRead += entry.Stat.Size;
            _read.Add(entry.Path);
            MarkDirty(entry);
            return entry.Full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entry.Error = ex.Message;
            return null;
        }
    }

    public IReadOnlyList<ContentFingerprint> TakeDirty()
    {
        var now = DateTime.UtcNow;
        var dirty = _entries.Values
            .Where(e => e.Dirty && e.Error == null)
            .Select(e => new ContentFingerprint(e.Path, e.Stat.Size, e.Stat.LastWriteTicks, e.Sample, e.Full, now, e.FullAt))
            .ToList();
        foreach (var e in _entries.Values)
            e.Dirty = false;
        DirtyCount = 0;
        return dirty;
    }

    private void MarkDirty(FingerprintEntry entry)
    {
        if (entry.Dirty)
            return;
        entry.Dirty = true;
        DirtyCount++;
    }
}
