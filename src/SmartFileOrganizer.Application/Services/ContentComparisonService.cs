using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Compares a source scan (<see cref="ScanRole.Source"/>) with a library scan
/// (<see cref="ScanRole.Library"/>) by content and finds duplicates within the source
/// (https://github.com/crack666/smart_file_organizer/issues/24).
/// </summary>
/// <remarks>
/// <para>Content identity is tiered so that a large library is read as little as possible:
/// size first; then SHA-256 over size, first and last 64 KiB, only for files that share a size;
/// then SHA-256 over the whole file, only where the head-and-tail checksums agree. A file is
/// <c>backed_up</c> only when the full checksums are equal.</para>
/// <para>Checksums are cached per path in <c>content_fingerprints</c> and reused while size and
/// modification time are unchanged.</para>
/// <para>Nothing is guessed: a source file that cannot be read, changed since its scan, or whose
/// same-size library candidates cannot all be read is <c>error</c>, never <c>new</c> or
/// <c>backed_up</c>. Directories the scans could not read are reported as errors too.</para>
/// <para>Files are only opened for reading; the library is never changed and never sent to a model.</para>
/// </remarks>
public sealed class ContentComparisonService
{
    private const int FingerprintFlushThreshold = 256;

    private readonly IScanJobRepository _jobs;
    private readonly IContentComparisonRepository _repo;
    private readonly IContentHasher _hasher;
    private readonly IFileSystemAccessor _fs;
    private readonly ILogger<ContentComparisonService> _logger;

    public ContentComparisonService(
        IScanJobRepository jobs,
        IContentComparisonRepository repo,
        IContentHasher hasher,
        IFileSystemAccessor fs,
        ILogger<ContentComparisonService> logger)
    {
        _jobs = jobs;
        _repo = repo;
        _hasher = hasher;
        _fs = fs;
        _logger = logger;
    }

    /// <summary>
    /// Compares <paramref name="sourceJobId"/> with <paramref name="againstJobId"/>, stores the result
    /// (replacing an earlier one for the same pair) and returns it. Without a library job only
    /// duplicates within the source are determined and file states stay null.
    /// </summary>
    /// <exception cref="ComparisonException">A job is missing, not completed, has the wrong role, or its root is unreachable.</exception>
    public async Task<ComparisonReport> CompareAsync(
        long sourceJobId,
        long? againstJobId,
        IProgress<ComparisonProgress>? progress = null,
        CancellationToken ct = default)
    {
        var source = await RequireJobAsync(sourceJobId, ScanRole.Source, ct);
        ScanJob? against = null;
        if (againstJobId is { } aid)
        {
            if (aid == sourceJobId)
                throw new ComparisonException(ComparisonFailure.WrongRole, "A job cannot be compared with itself.");
            against = await RequireJobAsync(aid, ScanRole.Library, ct);
        }

        // A folder the scan skipped by rule is not part of the comparison, even if rows of it are left from an
        // earlier scan: the person decided not to look there, and it may be gone by now.
        var skippedSource = await _repo.GetSkippedDirectoriesAsync(source.Id, ct);
        var skippedLibrary = against == null ? [] : await _repo.GetSkippedDirectoriesAsync(against.Id, ct);
        var sourceFiles = Outside(await _repo.GetFilesAsync(source.Id, ct), skippedSource);
        var sizeCandidates = against == null ? [] : Outside(await _repo.GetFilesWithSourceSizesAsync(against.Id, source.Id, ct), skippedLibrary);
        var nameCandidates = against == null ? [] : Outside(await _repo.GetFilesWithSourceNamesAsync(against.Id, source.Id, ct), skippedLibrary);

        var cache = await _repo.GetFingerprintsAsync(
            sourceFiles.Select(f => f.FullPath).Concat(sizeCandidates.Select(f => f.FullPath)).ToList(), ct);
        var resolver = new Resolver(_hasher, cache);

        var outcomes = sourceFiles.ToDictionary(f => f.Id, _ => new Outcome());
        var candidateIssues = new Dictionary<string, ComparisonIssue>(StringComparer.Ordinal);

        try
        {
            await CompareWithLibraryAsync(sourceFiles, sizeCandidates, nameCandidates, against != null,
                resolver, outcomes, candidateIssues, progress, ct);
            await FindDuplicatesAsync(sourceFiles, resolver, outcomes, ct);
        }
        finally
        {
            // Keep what was computed even when the run is interrupted; the next run reuses it.
            await FlushFingerprintsAsync(resolver, force: true, CancellationToken.None);
        }

        var files = sourceFiles.Select(f => ToFileComparison(f, outcomes[f.Id], resolver, against != null)).ToList();
        var matchPaths = sizeCandidates.Concat(nameCandidates)
            .GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().FullPath);
        files = files.Select(f => f.MatchFileNodeId is { } m && matchPaths.TryGetValue(m, out var p) ? f with { MatchPath = p } : f).ToList();

        var errors = await CollectErrorsAsync(source, against, files, ct);
        errors.AddRange(candidateIssues.Values.OrderBy(i => i.Path, StringComparer.Ordinal));

        var header = new ComparisonHeader(0, source.Id, against?.Id, DateTime.UtcNow, errors.Count == 0, errors.Count);
        var id = await _repo.SaveComparisonAsync(header, files, ct);
        header = header with { Id = id };

        _logger.LogInformation(
            "Compared job {Source} with {Against}: {Files} files, {Errors} errors, {HeadTail} head-tail and {Full} full checksums computed, {Reused} reused.",
            source.Id, against?.Id, files.Count, errors.Count, resolver.HeadTailComputed, resolver.FullComputed, resolver.Reused);

        return BuildReport(header, source, against, files, errors,
            new HashingStats(resolver.HeadTailComputed, resolver.FullComputed, resolver.Reused));
    }

    /// <summary>
    /// The stored result of an earlier <see cref="CompareAsync"/> for the same pair, or null.
    /// Errors contain the file errors and the directories the scans could not read.
    /// </summary>
    public async Task<ComparisonReport?> GetStoredAsync(long sourceJobId, long? againstJobId, CancellationToken ct = default)
    {
        var header = await _repo.GetComparisonAsync(sourceJobId, againstJobId, ct);
        if (header == null)
            return null;
        var source = await _jobs.GetByIdAsync(sourceJobId, ct);
        if (source == null)
            return null;
        var against = againstJobId is { } aid ? await _jobs.GetByIdAsync(aid, ct) : null;

        var files = await _repo.GetFileComparisonsAsync(header.Id, ct);
        var errors = await CollectErrorsAsync(source, against, files, ct);
        return BuildReport(header, source, against, files, errors, new HashingStats(0, 0, 0));
    }

    /// <summary>
    /// Per-directory counts including subdirectories, for every directory on the way from a file
    /// up to <paramref name="rootPath"/>. Ordered by path.
    /// </summary>
    public static IReadOnlyList<DirectoryComparison> SummarizeDirectories(string rootPath, IEnumerable<FileComparison> files)
    {
        var root = TrimSeparators(rootPath);
        var counts = new Dictionary<string, int[]>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var dir = Path.GetDirectoryName(file.FullPath);
            while (!string.IsNullOrEmpty(dir))
            {
                if (!counts.TryGetValue(dir, out var c))
                    counts[dir] = c = new int[5];
                c[0]++;
                if (file.State is { } state)
                    c[1 + (int)state]++;

                if (string.Equals(TrimSeparators(dir), root, StringComparison.Ordinal) || dir.Length <= root.Length)
                    break;
                dir = Path.GetDirectoryName(dir);
            }
        }

        return counts
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new DirectoryComparison(kv.Key, kv.Value[0],
                kv.Value[1 + (int)ContentState.BackedUp], kv.Value[1 + (int)ContentState.New],
                kv.Value[1 + (int)ContentState.Differs], kv.Value[1 + (int)ContentState.Error]))
            .ToList();
    }

    // ------------------------------------------------------------------ steps

    private async Task<ScanJob> RequireJobAsync(long id, ScanRole role, CancellationToken ct)
    {
        var job = await _jobs.GetByIdAsync(id, ct)
            ?? throw new ComparisonException(ComparisonFailure.JobNotFound, $"Job {id} not found.");
        if (job.Role != role)
        {
            throw new ComparisonException(ComparisonFailure.WrongRole,
                $"Job {id} has role {job.Role.ToValue()}; expected {role.ToValue()}.");
        }
        if (job.Status != JobStatus.Completed)
        {
            throw new ComparisonException(ComparisonFailure.ScanNotCompleted,
                $"Job {id} is {job.Status.ToString().ToLowerInvariant()}; finish the scan first.");
        }
        if (!_fs.DirectoryExists(job.RootPath))
        {
            throw new ComparisonException(ComparisonFailure.RootUnreachable,
                $"Root of job {id} is not reachable: {job.RootPath}");
        }
        return job;
    }

    private async Task CompareWithLibraryAsync(
        IReadOnlyList<ComparableFile> sourceFiles,
        IReadOnlyList<ComparableFile> sizeCandidates,
        IReadOnlyList<ComparableFile> nameCandidates,
        bool hasLibrary,
        Resolver resolver,
        Dictionary<long, Outcome> outcomes,
        Dictionary<string, ComparisonIssue> candidateIssues,
        IProgress<ComparisonProgress>? progress,
        CancellationToken ct)
    {
        var bySize = sizeCandidates.ToLookup(f => f.Size);
        var byNameAndFolder = nameCandidates.ToLookup(NameKey, StringComparer.OrdinalIgnoreCase);

        var groups = new Dictionary<long, CandidateGroup>();
        long processed = 0;

        foreach (var file in sourceFiles)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = outcomes[file.Id];
            var entry = resolver.Get(file.FullPath);

            if (entry.Error == null && entry.Stat.Size != file.Size)
                entry.Error = $"Changed since the scan ({file.Size} -> {entry.Stat.Size} bytes); scan the source again.";

            if (entry.Error == null && hasLibrary)
            {
                if (!groups.TryGetValue(file.Size, out var group))
                {
                    group = await LoadCandidateGroupAsync(file.Size, bySize[file.Size], resolver, ct);
                    groups[file.Size] = group;
                }

                await MatchAsync(file, entry, group, resolver, outcome, ct);

                if (outcome.State == null && entry.Error == null)
                {
                    // A source read error found here is reported by ToFileComparison.
                    var unknown = await UnreadableCandidatesAsync(file, entry, group, resolver, ct);
                    if (entry.Error == null && unknown.Count > 0)
                    {
                        outcome.State = ContentState.Error;
                        outcome.Error = $"{unknown.Count} file(s) of the same size in the library could not be read, so it is unknown whether this file is backed up. First: {unknown[0].File.FullPath}: {unknown[0].Entry.Error}";
                    }
                    else if (byNameAndFolder[NameKey(file)].FirstOrDefault(f => !SamePath(f.FullPath, file.FullPath)) is { } sameName)
                    {
                        outcome.State = ContentState.Differs;
                        outcome.MatchFileNodeId = sameName.Id;
                        outcome.MatchCount = 1;
                    }
                    else
                    {
                        outcome.State = ContentState.New;
                    }
                }
            }

            await FlushFingerprintsAsync(resolver, force: false, ct);
            progress?.Report(new ComparisonProgress(++processed, sourceFiles.Count, file.FullPath));
        }

        foreach (var (file, entry) in groups.Values.SelectMany(g => g.Candidates).Where(c => c.Entry.Error != null))
        {
            candidateIssues.TryAdd(file.FullPath,
                new ComparisonIssue(ScanRole.Library, ComparisonIssueKind.Read, file.FullPath, entry.Error!));
        }
    }

    /// <summary>Library files of one size, each with its head-and-tail checksum or its read error.</summary>
    private static async Task<CandidateGroup> LoadCandidateGroupAsync(
        long size, IEnumerable<ComparableFile> candidates, Resolver resolver, CancellationToken ct)
    {
        var group = new CandidateGroup();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var entry = resolver.Get(candidate.FullPath);
            if (entry.Error == null && entry.Stat.Size != size)
                continue; // changed since the library scan: it no longer has this content
            if (entry.Error == null)
                await resolver.HeadTailAsync(entry, ct);
            group.Candidates.Add((candidate, entry));
        }
        return group;
    }

    private static async Task MatchAsync(
        ComparableFile file, Entry entry, CandidateGroup group, Resolver resolver, Outcome outcome, CancellationToken ct)
    {
        var readable = group.Candidates
            .Where(c => c.Entry.Error == null && !SamePath(c.File.FullPath, file.FullPath)) // a file never backs up itself
            .ToList();
        if (readable.Count == 0)
            return;

        var headTail = await resolver.HeadTailAsync(entry, ct);
        if (headTail == null)
            return;

        var matches = new List<(ComparableFile File, Entry Entry)>();
        foreach (var (candidate, candidateEntry) in readable)
        {
            if (candidateEntry.HeadTail != headTail)
                continue;

            // backed_up only on equal full checksums of both files; head and tail never suffice.
            var full = await resolver.FullAsync(entry, ct);
            if (full == null)
                return;
            var candidateFull = await resolver.FullAsync(candidateEntry, ct);
            if (candidateFull != null && string.Equals(candidateFull, full, StringComparison.Ordinal))
                matches.Add((candidate, candidateEntry));
            // A candidate that fails here keeps its error and is picked up by UnreadableCandidatesAsync.
        }

        if (matches.Count > 0)
        {
            var (match, matchEntry) = matches.OrderBy(m => m.File.FullPath, StringComparer.Ordinal).First();
            outcome.State = ContentState.BackedUp;
            outcome.MatchFileNodeId = match.Id;
            outcome.MatchCount = matches.Count;
            outcome.MatchHash = entry.Full;
            outcome.MatchVerifiedAt = entry.FullAt < matchEntry.FullAt ? entry.FullAt : matchEntry.FullAt;
        }
    }

    /// <summary>
    /// Unreadable library files of the same size that could still hold this content: those whose
    /// head-and-tail checksum is unknown, and those whose head and tail agree but whose full read failed.
    /// Independent of the order in which source files are processed.
    /// </summary>
    private static async Task<List<(ComparableFile File, Entry Entry)>> UnreadableCandidatesAsync(
        ComparableFile file, Entry entry, CandidateGroup group, Resolver resolver, CancellationToken ct)
    {
        var failed = group.Candidates
            .Where(c => c.Entry.Error != null && !SamePath(c.File.FullPath, file.FullPath))
            .ToList();
        if (failed.Count == 0)
            return failed;

        var headTail = failed.Any(c => c.Entry.HeadTail != null) ? await resolver.HeadTailAsync(entry, ct) : null;
        return failed.Where(c => c.Entry.HeadTail == null || headTail == null || c.Entry.HeadTail == headTail).ToList();
    }

    private async Task FindDuplicatesAsync(
        IReadOnlyList<ComparableFile> sourceFiles, Resolver resolver, Dictionary<long, Outcome> outcomes, CancellationToken ct)
    {
        var found = new List<List<ComparableFile>>();

        // Empty files are all equal and not worth a hint.
        foreach (var sizeGroup in sourceFiles.Where(f => f.Size > 0).GroupBy(f => f.Size).Where(g => g.Count() > 1))
        {
            var byHead = new Dictionary<string, List<(ComparableFile File, Entry Entry)>>(StringComparer.Ordinal);
            foreach (var file in sizeGroup)
            {
                ct.ThrowIfCancellationRequested();
                var entry = resolver.Get(file.FullPath);
                if (entry.Error != null || entry.Stat.Size != file.Size)
                    continue;
                var head = await resolver.HeadTailAsync(entry, ct);
                if (head == null)
                    continue;
                if (!byHead.TryGetValue(head, out var list))
                    byHead[head] = list = [];
                list.Add((file, entry));
            }

            foreach (var headGroup in byHead.Values.Where(l => l.Count > 1))
            {
                var byFull = new Dictionary<string, List<ComparableFile>>(StringComparer.Ordinal);
                foreach (var (file, entry) in headGroup)
                {
                    var full = await resolver.FullAsync(entry, ct);
                    if (full == null)
                        continue;
                    if (!byFull.TryGetValue(full, out var list))
                        byFull[full] = list = [];
                    list.Add(file);
                }
                found.AddRange(byFull.Values.Where(l => l.Count > 1));
            }

            await FlushFingerprintsAsync(resolver, force: false, ct);
        }

        var number = 0;
        foreach (var group in found.OrderBy(g => g[0].FullPath, StringComparer.Ordinal))
        {
            number++;
            foreach (var file in group)
                outcomes[file.Id].DuplicateGroup = number;
        }
    }

    private static IReadOnlyList<ComparableFile> Outside(IReadOnlyList<ComparableFile> files, IReadOnlyList<string> skipped) =>
        skipped.Count == 0 ? files : files.Where(f => !IsAtOrBelowAny(f.FullPath, skipped)).ToList();

    private static bool IsAtOrBelowAny(string path, IReadOnlyList<string> dirs)
    {
        foreach (var dir in dirs)
        {
            var d = Path.TrimEndingDirectorySeparator(dir);
            if (string.Equals(path, d, StringComparison.OrdinalIgnoreCase)) return true;
            if (path.Length > d.Length && path.StartsWith(d, StringComparison.OrdinalIgnoreCase)
                && (path[d.Length] == '/' || path[d.Length] == '\\'))
                return true;
        }
        return false;
    }

    private async Task<List<ComparisonIssue>> CollectErrorsAsync(
        ScanJob source, ScanJob? against, IEnumerable<FileComparison> files, CancellationToken ct)
    {
        var errors = new List<ComparisonIssue>();
        var skippedSource = await _repo.GetSkippedDirectoriesAsync(source.Id, ct);
        foreach (var dir in await _repo.GetDirectoryErrorsAsync(source.Id, ct))
            if (!IsAtOrBelowAny(dir.FullPath, skippedSource))
                errors.Add(new ComparisonIssue(ScanRole.Source, ComparisonIssueKind.Scan, dir.FullPath, dir.Reason ?? "Directory could not be read."));
        if (against != null)
        {
            var skippedLibrary = await _repo.GetSkippedDirectoriesAsync(against.Id, ct);
            foreach (var dir in await _repo.GetDirectoryErrorsAsync(against.Id, ct))
                if (!IsAtOrBelowAny(dir.FullPath, skippedLibrary))
                    errors.Add(new ComparisonIssue(ScanRole.Library, ComparisonIssueKind.Scan, dir.FullPath, dir.Reason ?? "Directory could not be read."));
        }
        foreach (var file in files.Where(f => f.State == ContentState.Error))
            errors.Add(new ComparisonIssue(ScanRole.Source, ComparisonIssueKind.Read, file.FullPath, file.Error ?? "Unknown error."));
        return errors;
    }

    private static ComparisonReport BuildReport(
        ComparisonHeader header, ScanJob source, ScanJob? against,
        IReadOnlyList<FileComparison> files, IReadOnlyList<ComparisonIssue> errors, HashingStats hashing)
    {
        var duplicates = files
            .Where(f => f.DuplicateGroup != null)
            .GroupBy(f => f.DuplicateGroup!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new DuplicateGroup(g.Key, g.First().Size,
                g.Select(f => f.FullPath).OrderBy(p => p, StringComparer.Ordinal).ToList()))
            .ToList();
        var directories = against == null ? [] : SummarizeDirectories(source.RootPath, files);
        return new ComparisonReport(header, source, against, files, duplicates, directories, errors, hashing);
    }

    private static FileComparison ToFileComparison(ComparableFile file, Outcome outcome, Resolver resolver, bool hasLibrary)
    {
        var sourceError = resolver.TryGet(file.FullPath)?.Error;
        if (sourceError != null)
        {
            // The source file itself could not be read: whatever the library holds, its state is unknown.
            return new FileComparison(file.Id, file.FullPath, file.Size, ContentState.Error,
                null, null, 0, outcome.DuplicateGroup, sourceError);
        }

        return new FileComparison(file.Id, file.FullPath, file.Size,
            hasLibrary ? outcome.State : null,
            outcome.MatchFileNodeId, null, outcome.MatchCount, outcome.DuplicateGroup, outcome.Error,
            outcome.MatchHash, outcome.MatchVerifiedAt);
    }

    private async Task FlushFingerprintsAsync(Resolver resolver, bool force, CancellationToken ct)
    {
        if (!force && resolver.DirtyCount < FingerprintFlushThreshold)
            return;
        var dirty = resolver.TakeDirty();
        if (dirty.Count > 0)
            await _repo.UpsertFingerprintsAsync(dirty, ct);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>File name plus the name of the folder it lies in: "same name, similar place".</summary>
    private static string NameKey(ComparableFile file) =>
        file.Name + "\u0000" + Path.GetFileName(TrimSeparators(file.ParentPath));

    private static string TrimSeparators(string path) =>
        path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private sealed class Outcome
    {
        public ContentState? State { get; set; }
        public long? MatchFileNodeId { get; set; }
        public int MatchCount { get; set; }
        public int? DuplicateGroup { get; set; }
        public string? Error { get; set; }
        public string? MatchHash { get; set; }
        public DateTime? MatchVerifiedAt { get; set; }
    }

    private sealed class CandidateGroup
    {
        /// <summary>Library files of one size; <c>Entry.Error</c> is set for those that could not be read.</summary>
        public List<(ComparableFile File, Entry Entry)> Candidates { get; } = [];
    }

    private sealed class Entry(string path, FileStat stat)
    {
        public string Path { get; } = path;
        public FileStat Stat { get; } = stat;
        public string? HeadTail { get; set; }
        public string? Full { get; set; }

        /// <summary>When <see cref="Full"/> was computed from the file (now, or in an earlier run if cached).</summary>
        public DateTime? FullAt { get; set; }
        public string? Error { get; set; }
        public bool FromCache { get; set; }
        public bool Dirty { get; set; }
    }

    /// <summary>Stats each file once per run and computes checksums on demand, using the cache where it is still valid.</summary>
    private sealed class Resolver(IContentHasher hasher, IReadOnlyDictionary<string, ContentFingerprint> cache)
    {
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly HashSet<string> _reused = new(StringComparer.Ordinal);

        public int HeadTailComputed { get; private set; }
        public int FullComputed { get; private set; }
        public int Reused => _reused.Count;
        public int DirtyCount { get; private set; }

        public Entry? TryGet(string path) => _entries.GetValueOrDefault(path);

        public Entry Get(string path)
        {
            if (_entries.TryGetValue(path, out var existing))
                return existing;

            var stat = hasher.Stat(path);
            var entry = new Entry(path, stat) { Error = stat.Exists ? null : stat.Error ?? "File not found." };
            if (stat.Exists
                && cache.TryGetValue(path, out var fp)
                && fp.Size == stat.Size
                && fp.LastWriteTicks == stat.LastWriteTicks)
            {
                entry.HeadTail = fp.HeadTailHash;
                entry.Full = fp.FullHash;
                entry.FullAt = fp.FullHash == null ? null : fp.FullHashAt ?? fp.ComputedAt;
                entry.FromCache = true;
            }
            _entries[path] = entry;
            return entry;
        }

        public async Task<string?> HeadTailAsync(Entry entry, CancellationToken ct)
        {
            if (entry.Error != null)
                return null;
            if (entry.HeadTail != null)
            {
                if (entry.FromCache)
                    _reused.Add(entry.Path);
                return entry.HeadTail;
            }
            try
            {
                var result = await hasher.ComputeHeadTailAsync(entry.Path, entry.Stat.Size, ct);
                entry.HeadTail = result.HeadTailHash;
                if (entry.Full == null && result.FullHash != null)
                {
                    entry.Full = result.FullHash;
                    entry.FullAt = DateTime.UtcNow;
                }
                HeadTailComputed++;
                MarkDirty(entry);
                return entry.HeadTail;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entry.Error = ex.Message;
                return null;
            }
        }

        public async Task<string?> FullAsync(Entry entry, CancellationToken ct)
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
                .Select(e => new ContentFingerprint(e.Path, e.Stat.Size, e.Stat.LastWriteTicks, e.HeadTail, e.Full, now, e.FullAt))
                .ToList();
            foreach (var e in _entries.Values)
                e.Dirty = false;
            DirtyCount = 0;
            return dirty;
        }

        private void MarkDirty(Entry entry)
        {
            if (entry.Dirty)
                return;
            entry.Dirty = true;
            DirtyCount++;
        }
    }
}
