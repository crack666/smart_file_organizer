using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Duplicates;

/// <summary>
/// Duplicates across a whole holding: one or more scan jobs of any role
/// (https://github.com/crack666/smart_file_organizer/issues/87). Reports only; it changes no file and stores nothing but
/// checksums.
/// </summary>
/// <remarks>
/// <para>Tiered, so that as little as possible is read: (1) size, from the scan rows, without opening a file: only files
/// whose size occurs more than once go on; (2) the sample checksum of <see cref="IContentHasher.ComputeSampleAsync"/>
/// (head, tail and six places in between, files up to 128 KiB whole); files that still agree form a group,
/// <c>likely</c>; (3) the full SHA-256, only with <see cref="DuplicateOptions.Verify"/> and only for those groups;
/// a group with equal full checksums is <c>certain</c>. Where every file of a group already has a full checksum (small
/// files, an earlier comparison or verification) the group is split by it without reading.</para>
/// <para>Checksums go to <c>content_fingerprints</c>, valid while path, size and modification time are unchanged, the
/// same store the comparison with a library uses; a second run without changes reads nothing, and a run cut off goes on
/// where it stopped.</para>
/// <para>Folders that are wholly or mostly the same (at least half of the files of each folder have a copy in the
/// other, the rule of the <c>duplicate</c> finding for wholes) are reported as one group instead of their files.</para>
/// </remarks>
public sealed class DuplicateService
{
    /// <summary>Share of the files of each folder that must have a copy in another folder of the group.</summary>
    public const double FolderCopyMinShare = 0.5;

    /// <summary>A folder with fewer files is never reported as a copy; its file is.</summary>
    public const int FolderMinFiles = 2;

    private const int FingerprintFlushThreshold = 256;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IScanJobRepository _jobs;
    private readonly IDuplicateRepository _duplicates;
    private readonly IContentComparisonRepository _fingerprints;
    private readonly IContentHasher _hasher;
    private readonly IFileSystemAccessor _fs;
    private readonly PlanOptions _plan;
    private readonly ILogger<DuplicateService> _logger;

    public DuplicateService(
        IScanJobRepository jobs,
        IDuplicateRepository duplicates,
        IContentComparisonRepository fingerprints,
        IContentHasher hasher,
        IFileSystemAccessor fs,
        PlanOptions plan,
        ILogger<DuplicateService> logger)
    {
        _jobs = jobs;
        _duplicates = duplicates;
        _fingerprints = fingerprints;
        _hasher = hasher;
        _fs = fs;
        _plan = plan;
        _logger = logger;
    }

    /// <summary>The duplicates among the files of <paramref name="jobIds"/>, largest waste first.</summary>
    /// <exception cref="ComparisonException">A job is missing, not completed, or its root is unreachable; checked for
    /// every job before any file is read.</exception>
    public async Task<DuplicateReport> ReportAsync(
        IReadOnlyList<long> jobIds,
        DuplicateOptions? options = null,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new DuplicateOptions();
        var ids = jobIds.Distinct().ToList();
        if (ids.Count == 0)
            throw new ArgumentException("At least one job is needed.", nameof(jobIds));

        var jobs = new List<ScanJob>();
        foreach (var id in ids)
            jobs.Add(await RequireJobAsync(id, ct));
        var jobById = jobs.ToDictionary(j => j.Id);
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var minSize = Math.Max(1, options.MinSize);

        // ---- stage 1: size, from the scan rows
        var skipped = new Dictionary<long, IReadOnlyList<string>>();
        foreach (var job in jobs)
            skipped[job.Id] = await _fingerprints.GetSkippedDirectoriesAsync(job.Id, ct);
        var rows = await _duplicates.GetSizeCandidatesAsync(ids, minSize, ct);
        var sizeGroups = rows
            .Where(r => !IsAtOrBelowAny(r.File.FullPath, skipped[r.JobId]))
            .OrderBy(r => order[r.JobId]).ThenBy(r => r.File.FullPath, StringComparer.Ordinal)
            .DistinctBy(r => r.File.FullPath, PathComparer) // one path seen by two jobs is one file, not two copies
            .GroupBy(r => r.File.Size)
            .Where(g => g.Count() > 1)
            .Select(g => g.OrderBy(r => r.File.FullPath, StringComparer.Ordinal).ToList())
            .ToList();
        var sizeCandidates = sizeGroups.Sum(g => g.Count);
        var sizeCandidateBytes = sizeGroups.Sum(g => g[0].File.Size * g.Count);
        var (totalFiles, totalBytes) = await _duplicates.GetTotalsAsync(ids, ct);

        var cache = await _fingerprints.GetFingerprintsAsync(
            sizeGroups.SelectMany(g => g).Select(c => c.File.FullPath).ToList(), ct);
        var resolver = new FingerprintResolver(_hasher, cache);
        var issues = new Dictionary<string, DuplicateIssue>(PathComparer);
        var found = new List<FileGroup>();
        var sampleGroupFiles = 0;

        DuplicateMember Member(DuplicateCandidate c) =>
            new(c.File.Id, c.JobId, jobById[c.JobId].Role, c.File.FullPath, InManagedArea(c.File.FullPath));

        void Fail(DuplicateCandidate c, FingerprintEntry e) =>
            issues.TryAdd(c.File.FullPath, new DuplicateIssue(c.JobId, c.File.FullPath, e.Error ?? "Unknown error."));

        try
        {
            // ---- stage 2: sample checksum
            var sampled = new List<List<(DuplicateCandidate C, FingerprintEntry E)>>();
            long processed = 0;
            foreach (var sizeGroup in sizeGroups)
            {
                var bySample = new Dictionary<string, List<(DuplicateCandidate, FingerprintEntry)>>(StringComparer.Ordinal);
                foreach (var c in sizeGroup)
                {
                    ct.ThrowIfCancellationRequested();
                    var e = resolver.Get(c.File.FullPath);
                    if (e.Error == null && e.Stat.Size != c.File.Size)
                        e.Error = $"Changed since the scan ({c.File.Size} -> {e.Stat.Size} bytes); scan again.";
                    if (await resolver.SampleAsync(e, ct) is { } sample)
                    {
                        if (!bySample.TryGetValue(sample, out var list))
                            bySample[sample] = list = [];
                        list.Add((c, e));
                    }
                    else
                    {
                        Fail(c, e);
                    }
                    progress?.Report(new DuplicateProgress("sample", ++processed, sizeCandidates, resolver.BytesRead, c.File.FullPath));
                }
                sampled.AddRange(bySample.Values.Where(l => l.Count > 1));
                await FlushAsync(resolver, force: false, ct);
            }
            sampleGroupFiles = sampled.Sum(g => g.Count);

            // ---- stage 3: full checksum, on request, or where every file has one already
            long verified = 0;
            foreach (var group in sampled)
            {
                if (!options.Verify && group.Any(x => x.E.Full == null))
                {
                    found.Add(new FileGroup(group.Select(x => Member(x.C)).ToList(), group[0].C.File.Size, DuplicateCertainty.Likely, null));
                    continue;
                }

                var byFull = new Dictionary<string, List<DuplicateCandidate>>(StringComparer.Ordinal);
                foreach (var (c, e) in group)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await resolver.FullAsync(e, ct) is { } full)
                    {
                        if (!byFull.TryGetValue(full, out var list))
                            byFull[full] = list = [];
                        list.Add(c);
                    }
                    else
                    {
                        Fail(c, e);
                    }
                    if (options.Verify)
                        progress?.Report(new DuplicateProgress("verify", ++verified, sampleGroupFiles, resolver.BytesRead, c.File.FullPath));
                }
                foreach (var (hash, list) in byFull.Where(kv => kv.Value.Count > 1))
                    found.Add(new FileGroup(list.Select(Member).ToList(), list[0].File.Size, DuplicateCertainty.Certain, hash));
                await FlushAsync(resolver, force: false, ct);
            }
        }
        finally
        {
            // Keep what was computed even when the run is interrupted; the next run reuses it.
            await FlushAsync(resolver, force: true, CancellationToken.None);
        }

        foreach (var g in found)
            g.Members.Sort((a, b) => StringComparer.Ordinal.Compare(a.FullPath, b.FullPath));

        var groups = await SummarizeFoldersAsync(ids, jobById, minSize, found, ct);
        var ordered = groups
            .OrderByDescending(g => g.WastedBytes)
            .ThenBy(g => g.Folders.Count > 0 ? g.Folders[0].Path : g.Files[0].FullPath, StringComparer.Ordinal)
            .ToList();

        _logger.LogInformation(
            "Duplicates over jobs {Jobs}: {Candidates} size candidates, {Groups} groups, {Bytes} bytes read from {Files} files, {Reused} from cache.",
            string.Join(",", ids), sizeCandidates, ordered.Count, resolver.BytesRead, resolver.FilesRead, resolver.Reused);

        return new DuplicateReport(jobs, options.Verify, minSize, totalFiles, totalBytes,
            new DuplicateStages(sizeCandidates, sizeCandidateBytes, sampleGroupFiles, resolver.SampleComputed,
                resolver.FullComputed, resolver.Reused, resolver.FilesRead, resolver.BytesRead),
            ordered,
            issues.Values.OrderBy(i => i.Path, StringComparer.Ordinal).ToList());
    }

    /// <summary>What the person may do with a group; the command itself does nothing.</summary>
    public static DuplicateAdvice AdviceFor(DuplicateCertainty certainty, IReadOnlyCollection<DuplicateMember> members) =>
        certainty == DuplicateCertainty.Likely ? DuplicateAdvice.VerifyFirst
        : members.All(m => m.Role == ScanRole.Library) ? DuplicateAdvice.LibraryOnly
        : members.Any(m => m.Role == ScanRole.Library) ? DuplicateAdvice.SourceCopiesHeld
        : DuplicateAdvice.NoLibraryCopy;

    // ------------------------------------------------------------------ folders

    private sealed record FileGroup(List<DuplicateMember> Members, long Size, DuplicateCertainty Certainty, string? Hash)
    {
        public long Wasted => Size * (Members.Count - 1);
    }

    /// <summary>
    /// Folders that are wholly or mostly the same become one group; the file groups they hold completely are no longer
    /// listed on their own. Every other file group stays as it is.
    /// </summary>
    private async Task<List<DuplicateGroupReport>> SummarizeFoldersAsync(
        IReadOnlyList<long> ids, IReadOnlyDictionary<long, ScanJob> jobById, long minSize, List<FileGroup> groups,
        CancellationToken ct)
    {
        var result = new List<DuplicateGroupReport>();
        var covered = new bool[groups.Count];

        if (groups.Count > 0)
        {
            var rootOf = jobById.ToDictionary(kv => kv.Key, kv => kv.Value.RootPath);

            // Files per folder including every folder below it.
            var below = new Dictionary<string, int>(PathComparer);
            var jobOfDir = new Dictionary<string, long>(PathComparer);
            foreach (var row in await _duplicates.GetFolderFileCountsAsync(ids, minSize, ct))
            {
                foreach (var dir in Ancestors(row.Path, rootOf[row.JobId]))
                {
                    below[dir] = below.GetValueOrDefault(dir) + row.Files;
                    jobOfDir.TryAdd(dir, row.JobId);
                }
            }

            // Per folder: the groups with a file at or below it, and how many of their files lie there.
            var index = new Dictionary<string, Dictionary<int, int>>(PathComparer);
            for (var gi = 0; gi < groups.Count; gi++)
            {
                foreach (var m in groups[gi].Members)
                {
                    var parent = Path.GetDirectoryName(m.FullPath);
                    if (parent is null)
                        continue;
                    foreach (var dir in Ancestors(parent, rootOf[m.JobId]))
                    {
                        if (!index.TryGetValue(dir, out var counts))
                            index[dir] = counts = [];
                        counts[gi] = counts.GetValueOrDefault(gi) + 1;
                    }
                }
            }

            var finals = new Dictionary<string, List<string>>(PathComparer);
            var tried = new HashSet<string>(PathComparer);
            foreach (var group in groups)
            {
                var parents = group.Members.Select(m => Path.GetDirectoryName(m.FullPath)!)
                    .Distinct(PathComparer).Order(StringComparer.Ordinal).ToList();
                if (parents.Count < 2 || !Disjoint(parents) || !tried.Add(Key(parents)))
                    continue;
                if (Evaluate(parents, index, below) is not { } set)
                    continue;
                set = Lift(set, index, below, jobOfDir, rootOf);
                finals.TryAdd(Key(set), set);
            }

            // A set inside a larger one is part of it.
            var sets = finals.Values
                .Where(s => !finals.Values.Any(o => !ReferenceEquals(o, s)
                                                   && s.All(f => o.Any(of => IsAtOrBelow(f, of)))))
                .OrderByDescending(s => s.Sum(f => below.GetValueOrDefault(f)))
                .ThenBy(Key, StringComparer.Ordinal)
                .ToList();

            foreach (var set in sets)
            {
                var inSet = new List<int>();
                for (var gi = 0; gi < groups.Count; gi++)
                {
                    if (covered[gi])
                        continue;
                    var folders = groups[gi].Members.Select(m => set.FirstOrDefault(f => IsAtOrBelow(m.FullPath, f))).ToList();
                    if (folders.Any(f => f is null) || folders.Distinct(PathComparer).Count() < 2)
                        continue;
                    inSet.Add(gi);
                }
                if (inSet.Count == 0)
                    continue;
                foreach (var gi in inSet)
                    covered[gi] = true;

                var members = inSet.SelectMany(gi => groups[gi].Members).ToList();
                var certainty = inSet.All(gi => groups[gi].Certainty == DuplicateCertainty.Certain)
                    ? DuplicateCertainty.Certain
                    : DuplicateCertainty.Likely;
                var matched = Matched(set, index);
                var folders2 = set.Select(f =>
                {
                    var jobId = jobOfDir[f];
                    return new DuplicateFolder(jobId, jobById[jobId].Role, f, below.GetValueOrDefault(f), matched[f], InManagedArea(f));
                }).ToList();
                result.Add(new DuplicateGroupReport(DuplicateScope.Folders, certainty,
                    inSet.Sum(gi => groups[gi].Size), set.Count, inSet.Sum(gi => groups[gi].Wasted), null,
                    members.OrderBy(m => m.FullPath, StringComparer.Ordinal).ToList(), folders2,
                    AdviceFor(certainty, members)));
            }
        }

        for (var gi = 0; gi < groups.Count; gi++)
        {
            if (covered[gi])
                continue;
            var g = groups[gi];
            result.Add(new DuplicateGroupReport(DuplicateScope.Files, g.Certainty, g.Size, g.Members.Count, g.Wasted, g.Hash,
                g.Members, [], AdviceFor(g.Certainty, g.Members)));
        }
        return result;
    }

    /// <summary>
    /// The folders of <paramref name="set"/> that are copies of each other: each has at least
    /// <see cref="FolderMinFiles"/> files and at least <see cref="FolderCopyMinShare"/> of them have a copy in another
    /// folder of the set. Folders that fall short are dropped and the rest is weighed again; null below two folders.
    /// </summary>
    private static List<string>? Evaluate(
        List<string> set, Dictionary<string, Dictionary<int, int>> index, Dictionary<string, int> below)
    {
        while (set.Count >= 2)
        {
            var matched = Matched(set, index);
            var failing = set.Where(f =>
            {
                var files = below.GetValueOrDefault(f);
                return files < FolderMinFiles || matched[f] < FolderCopyMinShare * files;
            }).ToList();
            if (failing.Count == 0)
                return set;
            set = set.Where(f => !failing.Contains(f, PathComparer)).ToList();
        }
        return null;
    }

    /// <summary>Per folder of the set: its files that belong to a group with a file in another folder of the set.</summary>
    private static Dictionary<string, int> Matched(List<string> set, Dictionary<string, Dictionary<int, int>> index)
    {
        var result = new Dictionary<string, int>(PathComparer);
        foreach (var f in set)
        {
            var m = 0;
            if (index.TryGetValue(f, out var groups))
            {
                foreach (var (gi, count) in groups)
                {
                    if (set.Any(o => !PathComparer.Equals(o, f) && index.TryGetValue(o, out var og) && og.ContainsKey(gi)))
                        m += count;
                }
            }
            result[f] = m;
        }
        return result;
    }

    /// <summary>Goes up one level at a time while the parents are copies of each other too, so the topmost copy is reported.</summary>
    private static List<string> Lift(
        List<string> set, Dictionary<string, Dictionary<int, int>> index, Dictionary<string, int> below,
        Dictionary<string, long> jobOfDir, Dictionary<long, string> rootOf)
    {
        while (true)
        {
            var parents = new List<string>(set.Count);
            foreach (var f in set)
            {
                if (!jobOfDir.TryGetValue(f, out var jobId) || SameDir(f, rootOf[jobId]))
                    return set;
                if (Path.GetDirectoryName(f) is not { } parent)
                    return set;
                parents.Add(parent);
            }
            parents = parents.Order(StringComparer.Ordinal).ToList();
            if (parents.Distinct(PathComparer).Count() != parents.Count || !Disjoint(parents))
                return set;
            if (Evaluate(parents, index, below) is not { } lifted || lifted.Count != parents.Count)
                return set;
            set = lifted;
        }
    }

    /// <summary>The folder itself and every folder above it up to the job root; nothing outside the root.</summary>
    private static IEnumerable<string> Ancestors(string dir, string root)
    {
        string? current = dir;
        while (current is not null && IsAtOrBelow(current, root))
        {
            yield return current;
            if (SameDir(current, root))
                yield break;
            current = Path.GetDirectoryName(current);
        }
    }

    private static bool Disjoint(List<string> folders)
    {
        for (var i = 0; i < folders.Count; i++)
            for (var j = 0; j < folders.Count; j++)
                if (i != j && IsAtOrBelow(folders[i], folders[j]))
                    return false;
        return true;
    }

    private static string Key(IEnumerable<string> folders) =>
        string.Join("\n", folders.Order(PathComparer));

    // ------------------------------------------------------------------ helpers

    private async Task<ScanJob> RequireJobAsync(long id, CancellationToken ct)
    {
        var job = await _jobs.GetByIdAsync(id, ct)
            ?? throw new ComparisonException(ComparisonFailure.JobNotFound, $"Job {id} not found.");
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

    private bool InManagedArea(string path) =>
        _plan.ManagedAreas.Any(area => !string.IsNullOrWhiteSpace(area) && IsAtOrBelow(path, area));

    private async Task FlushAsync(FingerprintResolver resolver, bool force, CancellationToken ct)
    {
        if (!force && resolver.DirtyCount < FingerprintFlushThreshold)
            return;
        var dirty = resolver.TakeDirty();
        if (dirty.Count > 0)
            await _fingerprints.UpsertFingerprintsAsync(dirty, ct);
    }

    private static bool IsAtOrBelowAny(string path, IReadOnlyList<string> dirs) => dirs.Any(d => IsAtOrBelow(path, d));

    private static bool SameDir(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), PathComparison);

    /// <summary>True when <paramref name="path"/> is <paramref name="dir"/> or lies below it; a drive root counts too.</summary>
    private static bool IsAtOrBelow(string path, string dir)
    {
        var d = Path.TrimEndingDirectorySeparator(dir);
        var p = Path.TrimEndingDirectorySeparator(path);
        if (string.Equals(p, d, PathComparison))
            return true;
        if (!p.StartsWith(d, PathComparison) || p.Length <= d.Length)
            return false;
        return d.EndsWith('/') || d.EndsWith('\\') || p[d.Length] == '/' || p[d.Length] == '\\';
    }
}
