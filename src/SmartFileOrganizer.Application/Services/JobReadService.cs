using System.Text.Json;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Read side of a job for the CLI read commands (<c>dirs</c>, <c>files</c>, <c>show</c>, <c>summary</c>).
/// Strictly read-only: it reads the job through the repository, not through
/// <see cref="ScanJobService.GetJobAsync"/>, which may normalise and store the job status.
/// </summary>
/// <remarks>
/// Every file passes <see cref="SensitiveOutputGuard.Project"/> before it is filtered or returned, so
/// filters on category and confidence see the same values the caller sees. Model texts about a
/// directory pass <see cref="DirectoryTextGuard"/> against the marked files of its subtree.
/// </remarks>
public class JobReadService
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;
    public const int DefaultTop = 10;
    public const int MaxTop = 25;

    private readonly IScanJobRepository _jobs;
    private readonly IJobReadRepository _read;
    private readonly IDirectoryVerdictRepository _verdicts;

    public JobReadService(IScanJobRepository jobs, IJobReadRepository read, IDirectoryVerdictRepository verdicts)
    {
        _jobs = jobs;
        _read = read;
        _verdicts = verdicts;
    }

    public async Task<ReadPage<DirectoryItem>> ListDirectoriesAsync(DirectoryListQuery query, CancellationToken ct = default)
    {
        var job = await RequireJobAsync(query.JobId, ct);
        var index = await BuildIndexAsync(job.Id, visit: null, ct);

        IEnumerable<DirectoryEntry> entries = index.Entries;
        var baseDepth = 0;
        if (query.Under is not null)
        {
            var under = RequireDirectory(index, ResolvePath(job, query.Under), query.Under);
            baseDepth = under.Row.Depth;
            var prefix = WithSeparator(under.Row.FullPath);
            entries = entries.Where(e => e.Row.FullPath == under.Row.FullPath
                                         || e.Row.FullPath.StartsWith(prefix, StringComparison.Ordinal));
        }

        if (query.MaxDepth is { } maxDepth)
            entries = entries.Where(e => e.Row.Depth - baseDepth <= maxDepth);
        if (query.MinSize is { } minSize)
            entries = entries.Where(e => e.Bytes >= minSize);
        if (query.Sensitive is { } sensitive)
            entries = entries.Where(e => e.SensitiveFiles > 0 == sensitive);

        var items = entries.Select(e => ToItem(e, index, protect: false));
        if (query.Verdicts.Count > 0)
            items = items.Where(i => Matches(query.Verdicts, i.VerdictFilterCode));
        if (query.Retentions.Count > 0)
            items = items.Where(i => Matches(query.Retentions, i.Retention ?? ReadFilterCodes.None));

        items = query.Sort switch
        {
            DirectorySort.Size => items.OrderByDescending(i => i.SizeBytes).ThenBy(i => i.FullPath, StringComparer.Ordinal),
            DirectorySort.Files => items.OrderByDescending(i => i.Files).ThenBy(i => i.FullPath, StringComparer.Ordinal),
            _ => items.OrderBy(i => i.FullPath, StringComparer.Ordinal)
        };

        var all = items.ToList();
        var (limit, offset) = Paging(query);
        // Texts are protected only for the page that leaves the process.
        var page = all.Skip(offset).Take(limit)
            .Select(i => ToItem(index.ById[i.Id], index, protect: true, query.Output))
            .ToList();
        return new ReadPage<DirectoryItem>(all.Count, limit, offset, page);
    }

    public async Task<ReadPage<FileItem>> ListFilesAsync(FileListQuery query, CancellationToken ct = default)
    {
        var job = await RequireJobAsync(query.JobId, ct);

        string? directory = null;
        if (query.Directory is not null)
        {
            directory = ResolvePath(job, query.Directory);
            var dirs = await _read.GetDirectoriesAsync(job.Id, ct);
            if (!dirs.Any(d => d.FullPath == directory))
                throw new JobReadException(ReadFailure.PathNotFound, $"Directory '{query.Directory}' is not part of job {job.Id}.");
        }

        var filter = new FileListFilter
        {
            JobId = job.Id,
            Directory = directory,
            Recursive = query.Recursive,
            DirectoryPrefix = directory is null ? null : WithSeparator(directory),
            FileTypes = query.Types,
            Statuses = query.Statuses,
            OrderBySize = query.Sort == FileSort.Size
        };

        var (limit, offset) = Paging(query);
        var page = new List<FileItem>(Math.Min(limit, 256));
        long total = 0;

        await foreach (var row in _read.StreamFilesAsync(filter, ct))
        {
            var record = SensitiveOutputGuard.Project(row.File, query.Output);
            if (!MatchesFile(query, row, record))
                continue;

            if (total >= offset && page.Count < limit)
                page.Add(new FileItem(record, row.DirectoryVerdict, row.DirectoryRetention));
            total++;
        }

        return new ReadPage<FileItem>(total, limit, offset, page);
    }

    /// <summary>A directory or a file of the job by path (full, or relative to the job root).</summary>
    public async Task<PathDetail> ShowAsync(long jobId, string path, SensitiveOutputOptions? output, CancellationToken ct = default)
    {
        var job = await RequireJobAsync(jobId, ct);
        var fullPath = ResolvePath(job, path);
        output ??= SensitiveOutputOptions.Default;

        var file = await _read.GetFileByPathAsync(job.Id, fullPath, ct);
        if (file is not null)
        {
            var record = SensitiveOutputGuard.Project(file.File, output);
            return new PathDetail(null, new FileItem(record, file.DirectoryVerdict, file.DirectoryRetention));
        }

        var index = await BuildIndexAsync(job.Id, visit: null, ct);
        var entry = RequireDirectory(index, fullPath, path);
        var marked = index.MarkedNamesBelow(entry);

        var verdict = await _verdicts.GetByNodeIdAsync(entry.Row.Id, ct);
        var analyses = await _read.GetDirectoryAnalysesAsync(entry.Row.Id, ct);

        string? unitPath = null;
        if (verdict?.UnitDirectoryNodeId is { } unitId && index.ById.TryGetValue(unitId, out var unit))
            unitPath = unit.Row.FullPath;

        var item = ToItem(entry, index, protect: true, output);
        var rawResponse = DirectoryTextGuard.Protect(verdict?.RawResponse, marked, output, out var rawWithheld);

        var analysisItems = analyses.Select(a => ToAnalysis(a, marked, output, ref rawWithheld)).ToList();

        return new PathDetail(new DirectoryDetail
        {
            Directory = item with { TextWithheld = item.TextWithheld || rawWithheld },
            UnitDirectory = unitPath,
            Profile = verdict?.Profile,
            VerdictModel = verdict?.ModelUsed,
            JudgedAt = verdict is null || verdict.JudgedAt == default ? null : verdict.JudgedAt,
            VerdictRawResponse = rawResponse,
            Analyses = analysisItems
        }, null);
    }

    /// <summary>One-page overview: counts and distributions, plus the <paramref name="top"/> largest subtrees.</summary>
    public async Task<JobSummary> SummarizeAsync(long jobId, int top = DefaultTop, CancellationToken ct = default)
    {
        var job = await RequireJobAsync(jobId, ct);
        top = Math.Clamp(top, 0, MaxTop);

        var byCategory = new Dictionary<string, long>(StringComparer.Ordinal);
        var byType = new Dictionary<string, long>(StringComparer.Ordinal);
        var byStatus = new Dictionary<string, long>(StringComparer.Ordinal);
        var byMarker = new Dictionary<string, long>(StringComparer.Ordinal);
        long files = 0, bytes = 0, pending = 0, errors = 0, sensitive = 0;

        var index = await BuildIndexAsync(job.Id, (row, match) =>
        {
            var file = row.File;
            files++;
            bytes += file.Size;

            // Same category the files command reports, so the numbers add up for the caller.
            var record = SensitiveOutputGuard.Project(file);
            Increment(byCategory, record.Category ?? "unclassified");
            Increment(byType, file.FileType.ToString());
            Increment(byStatus, file.Status.ToString());

            if (file.Status is FileNodeStatus.Discovered or FileNodeStatus.Processing && IsAiEligible(file.FileType))
                pending++;
            if (file.Status == FileNodeStatus.Error)
                errors++;

            if (match.IsMatch)
            {
                sensitive++;
                foreach (var token in SensitiveMarkerNames.ToTokens(match.Markers))
                    Increment(byMarker, token);
            }
        }, ct);

        var items = index.Entries.Select(e => ToItem(e, index, protect: false)).ToList();
        var byVerdict = new Dictionary<string, long>(StringComparer.Ordinal);
        var byRetention = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            Increment(byVerdict, item.VerdictFilterCode);
            Increment(byRetention, item.Retention ?? ReadFilterCodes.None);
        }

        var minDepth = items.Count == 0 ? 0 : items.Min(i => i.Depth);
        var largest = items
            .Where(i => i.Depth > minDepth)
            .OrderByDescending(i => i.SizeBytes).ThenBy(i => i.FullPath, StringComparer.Ordinal)
            .Take(top)
            // No model texts in the overview: it stays small and needs no text protection.
            .Select(i => i with { Purpose = null, Reason = null, Summary = null, VerdictError = null, SummaryError = null })
            .ToList();

        return new JobSummary
        {
            Job = job,
            Directories = items.Count,
            Files = files,
            TotalBytes = bytes,
            PendingFiles = pending,
            ErrorFiles = errors,
            SensitiveFiles = sensitive,
            ByCategory = Sorted(byCategory),
            ByFileType = Sorted(byType),
            ByStatus = Sorted(byStatus),
            BySensitiveMarker = Sorted(byMarker),
            DirectoriesByVerdict = Sorted(byVerdict),
            DirectoriesByRetention = Sorted(byRetention),
            LargestDirectories = largest
        };
    }

    /// <summary>Resolves a path given by the caller: relative to the job root unless rooted; no trailing separator.</summary>
    public static string ResolvePath(ScanJob job, string path)
    {
        var combined = string.IsNullOrWhiteSpace(path) || path == "."
            ? job.RootPath
            : Path.IsPathRooted(path) ? path : Path.Combine(job.RootPath, path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));
    }

    // ---------------------------------------------------------------- internals

    private async Task<ScanJob> RequireJobAsync(long jobId, CancellationToken ct) =>
        await _jobs.GetByIdAsync(jobId, ct)
        ?? throw new JobReadException(ReadFailure.JobNotFound, $"Job {jobId} not found.");

    private static DirectoryEntry RequireDirectory(DirectoryIndex index, string fullPath, string given) =>
        index.ByPath.TryGetValue(fullPath, out var entry)
            ? entry
            : throw new JobReadException(ReadFailure.PathNotFound, $"'{given}' is neither a directory nor a file of this job.");

    private static (int Limit, int Offset) Paging(ReadQuery query) =>
        (Math.Clamp(query.Limit, 1, MaxLimit), Math.Max(0, query.Offset));

    private static string WithSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static bool Matches(IReadOnlyCollection<string> wanted, string code) =>
        wanted.Contains(code, StringComparer.OrdinalIgnoreCase);

    private static bool IsAiEligible(FileType type) => type is FileType.Image or FileType.Video or FileType.Document;

    private static void Increment(Dictionary<string, long> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;

    /// <summary>Largest count first, so the caller sees what matters without sorting.</summary>
    private static IReadOnlyDictionary<string, long> Sorted(Dictionary<string, long> counts)
    {
        var sorted = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, value) in counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
            sorted[key] = value;
        return sorted;
    }

    private static bool MatchesFile(FileListQuery query, FileReadRow row, FileOutputRecord record)
    {
        if (query.Sensitive is { } sensitive && record.Sensitive.Count > 0 != sensitive)
            return false;

        if (query.Categories.Count > 0)
        {
            var category = Enum.TryParse<FileCategory>(record.Category, out var c) ? c : FileCategory.Unknown;
            if (!query.Categories.Contains(category))
                return false;
        }

        if (query.MaxConfidence is { } max && (record.Confidence is not { } confidence || confidence > max))
            return false;

        if (query.Verdicts.Count > 0 && !Matches(query.Verdicts, row.DirectoryVerdict ?? ReadFilterCodes.None))
            return false;

        if (query.Retentions.Count > 0 && !Matches(query.Retentions, row.DirectoryRetention ?? ReadFilterCodes.None))
            return false;

        return true;
    }

    private static DirectoryItem ToItem(DirectoryEntry entry, DirectoryIndex index, bool protect, SensitiveOutputOptions? output = null)
    {
        var r = entry.Row;
        var item = new DirectoryItem
        {
            Id = r.Id,
            FullPath = r.FullPath,
            RelativePath = r.RelativePath,
            Depth = r.Depth,
            Status = r.DirStatus,
            SizeBytes = entry.Bytes,
            Files = entry.Files,
            DirectFiles = entry.DirectFiles,
            Subdirs = r.SubdirCount,
            Verdict = r.Verdict,
            VerdictSource = r.VerdictSource,
            VerdictRule = r.VerdictRule,
            Retention = r.Retention,
            VerdictError = r.VerdictError,
            Purpose = r.Purpose,
            Reason = r.Reason,
            Summary = r.AiSummary,
            SummaryError = r.AiSummaryError,
            SensitiveFiles = entry.SensitiveFiles
        };

        if (!protect || entry.SensitiveFiles == 0)
            return item;

        var marked = index.MarkedNamesBelow(entry);
        var purpose = DirectoryTextGuard.Protect(item.Purpose, marked, output, out var w1);
        var reason = DirectoryTextGuard.Protect(item.Reason, marked, output, out var w2);
        var summary = DirectoryTextGuard.Protect(item.Summary, marked, output, out var w3);
        var verdictError = DirectoryTextGuard.Protect(item.VerdictError, marked, output, out var w4);
        var summaryError = DirectoryTextGuard.Protect(item.SummaryError, marked, output, out var w5);

        return item with
        {
            Purpose = purpose,
            Reason = reason,
            Summary = summary,
            VerdictError = verdictError,
            SummaryError = summaryError,
            TextWithheld = w1 || w2 || w3 || w4 || w5
        };
    }

    private static DirectoryAnalysisItem ToAnalysis(
        DirectoryClassificationResult a, IReadOnlyCollection<string> marked, SensitiveOutputOptions output, ref bool withheld)
    {
        var summary = DirectoryTextGuard.Protect(a.Summary, marked, output, out var w1);
        var theme = DirectoryTextGuard.Protect(a.Theme, marked, output, out var w2);
        var error = DirectoryTextGuard.Protect(a.Error, marked, output, out var w3);
        withheld |= w1 || w2 || w3;

        var anomalies = new List<AnomalyItem>();
        foreach (var (name, reason) in ParseAnomalies(a.AnomalousFileIds))
        {
            // The reason is about this file's content; withhold it when the file is marked.
            string? shown;
            if (!output.IncludeSensitive && marked.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                shown = DirectoryTextGuard.NeutralHint;
                withheld = true;
            }
            else
            {
                shown = DirectoryTextGuard.Protect(reason, marked, output, out var w4);
                withheld |= w4;
            }
            anomalies.Add(new AnomalyItem(name, shown));
        }

        return new DirectoryAnalysisItem
        {
            Phase = a.Phase,
            Summary = summary,
            Theme = theme,
            Homogeneity = a.Homogeneity,
            DominantType = a.DominantType,
            SamplingStrategy = a.SamplingStrategy,
            SampleSize = a.SampleSize,
            Anomalies = anomalies,
            ModelUsed = a.ModelUsed,
            AnalyzedAt = a.AnalyzedAt == default ? null : a.AnalyzedAt,
            Error = error
        };
    }

    /// <summary>Reads the stored anomaly list <c>[{name, reason}]</c> (or <c>[{id, name, reason}]</c>); tolerates anything else.</summary>
    private static IEnumerable<(string Name, string? Reason)> ParseAnomalies(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            var result = new List<(string, string?)>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                var name = element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var reason = element.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                result.Add((name, reason));
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// All directories of a job with subtree totals and marked files rolled up along the parent chain.
    /// Streams every file once; <paramref name="visit"/> sees each file with its sensitive match.
    /// </summary>
    private async Task<DirectoryIndex> BuildIndexAsync(
        long jobId, Action<FileReadRow, SensitiveMatch>? visit, CancellationToken ct)
    {
        var rows = await _read.GetDirectoriesAsync(jobId, ct);
        var index = new DirectoryIndex(rows);

        foreach (var totals in await _read.GetDirectoryFileTotalsAsync(jobId, ct))
        {
            if (index.ByPath.TryGetValue(totals.ParentPath, out var direct))
                direct.DirectFiles = totals.Files;
            foreach (var entry in index.SelfAndAncestors(totals.ParentPath))
            {
                entry.Files += totals.Files;
                entry.Bytes += totals.Bytes;
            }
        }

        await foreach (var row in _read.StreamFilesAsync(new FileListFilter { JobId = jobId }, ct))
        {
            var match = SensitiveOutputGuard.GetMatch(row.File);
            visit?.Invoke(row, match);
            if (!match.IsMatch) continue;

            foreach (var entry in index.SelfAndAncestors(row.File.ParentPath))
            {
                entry.SensitiveFiles++;
                entry.MarkedNames.Add(row.File.Name);
            }
        }

        return index;
    }

    private sealed class DirectoryEntry(DirectoryReadRow row)
    {
        public DirectoryReadRow Row { get; } = row;
        public long Files { get; set; }
        public long DirectFiles { get; set; }
        public long Bytes { get; set; }
        public long SensitiveFiles { get; set; }

        /// <summary>Names of marked files in the subtree.</summary>
        public HashSet<string> MarkedNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class DirectoryIndex
    {
        public DirectoryIndex(IReadOnlyList<DirectoryReadRow> rows)
        {
            Entries = rows.Select(r => new DirectoryEntry(r)).ToList();
            ByPath = new Dictionary<string, DirectoryEntry>(StringComparer.Ordinal);
            ById = new Dictionary<long, DirectoryEntry>();
            foreach (var entry in Entries)
            {
                ByPath[entry.Row.FullPath] = entry;
                ById[entry.Row.Id] = entry;
            }
        }

        public List<DirectoryEntry> Entries { get; }
        public Dictionary<string, DirectoryEntry> ByPath { get; }
        public Dictionary<long, DirectoryEntry> ById { get; }

        public IReadOnlyCollection<string> MarkedNamesBelow(DirectoryEntry entry) => entry.MarkedNames;

        /// <summary>The directory at <paramref name="path"/> and every ancestor that belongs to the job.</summary>
        public IEnumerable<DirectoryEntry> SelfAndAncestors(string path)
        {
            var seen = 0;
            while (ByPath.TryGetValue(path, out var entry) && seen++ < 4096)
            {
                yield return entry;
                if (entry.Row.ParentPath == path) yield break;
                path = entry.Row.ParentPath;
            }
        }
    }
}
