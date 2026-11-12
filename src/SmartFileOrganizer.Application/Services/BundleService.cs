using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Bundles;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>Why bundles could not be built.</summary>
public enum BundleFailure
{
    JobNotFound,
    ScanNotCompleted,
    PatternsInvalid
}

public sealed class BundleException(BundleFailure failure, string message) : Exception(message)
{
    public BundleFailure Failure { get; } = failure;
}

/// <summary>A bundle as listings show it: stored facts plus counts computed when read.</summary>
public sealed record BundleItem(
    Bundle Bundle,
    int Children,
    long SensitiveFiles,
    IReadOnlyDictionary<string, long>? Comparison);

/// <summary>State of the bundles of a job: when built, with what, and the statistics with and without dumps.</summary>
public sealed record BundleOverview(
    long JobId,
    DateTime BuiltAt,
    JsonElement Settings,
    BundleSplitStats Stats,
    IReadOnlyDictionary<string, long> TopLevelByKind,
    BundleComparisonInfo? Comparison);

/// <summary>The comparison with a library whose states the bundle counts show.</summary>
public sealed record BundleComparisonInfo(long ComparisonId, long AgainstJobId, DateTime ComparedAt);

public sealed record BundleListQuery
{
    public long JobId { get; init; }

    /// <summary>Children of this bundle; null for the top level (dumps, folders, events, scattered).</summary>
    public long? Parent { get; init; }

    /// <summary>Every bundle of the job, whatever its parent. Overrides <see cref="Parent"/>.</summary>
    public bool All { get; init; }

    public IReadOnlyCollection<BundleKind> Kinds { get; init; } = [];
    public int Limit { get; init; } = JobReadService.DefaultLimit;
    public int Offset { get; init; }
}

/// <summary>
/// Builds, stores and reads the bundles of a job (https://github.com/crack666/smart_file_organizer/issues/39).
/// Building reads only the database (scan rows and stored metadata facts); it never opens a scanned file
/// and never calls a model.
/// </summary>
public sealed class BundleService
{
    private readonly IScanJobRepository _jobs;
    private readonly IJobReadRepository _read;
    private readonly IBundleRepository _bundles;
    private readonly ICaptureInfoSource _captures;
    private readonly BundleOptions _options;
    private readonly ILogger<BundleService> _logger;

    public BundleService(
        IScanJobRepository jobs,
        IJobReadRepository read,
        IBundleRepository bundles,
        ICaptureInfoSource captures,
        BundleOptions options,
        ILogger<BundleService> logger)
    {
        _jobs = jobs;
        _read = read;
        _bundles = bundles;
        _captures = captures;
        _options = options;
        _logger = logger;
    }

    /// <summary>The configured thresholds (section <c>Bundles</c>), as a copy a caller can change for one run.</summary>
    public BundleOptions DefaultOptions() => new()
    {
        EventGapHours = _options.EventGapHours,
        MaxEventDays = _options.MaxEventDays,
        MinEventFiles = _options.MinEventFiles,
        PatternsFile = _options.PatternsFile
    };

    /// <summary>Builds the bundles of a completed job and replaces the stored ones.</summary>
    public async Task<BundleOverview> BuildAsync(long jobId, BundleOptions? options = null, CancellationToken ct = default)
    {
        var job = await _jobs.GetByIdAsync(jobId, ct)
                  ?? throw new BundleException(BundleFailure.JobNotFound, $"Job {jobId} not found.");
        if (job.Status != JobStatus.Completed)
            throw new BundleException(BundleFailure.ScanNotCompleted,
                $"Job {jobId} is {job.Status.ToString().ToLowerInvariant()}; bundles need a completed scan.");

        options ??= _options;
        BundlePatterns patterns;
        try
        {
            patterns = options.PatternsFile is { } file ? BundlePatterns.Load(file) : BundlePatterns.Default;
            options.Validate();
        }
        catch (Exception ex) when (ex is BundlePatternsException or ArgumentException)
        {
            throw new BundleException(BundleFailure.PatternsInvalid, ex.Message);
        }

        var directories = await _read.GetDirectoriesAsync(jobId, ct);
        var files = new List<FileNode>();
        await foreach (var row in _read.StreamFilesAsync(new FileListFilter { JobId = jobId }, ct))
            files.Add(row.File);
        var captures = await _captures.GetAsync(jobId, ct);

        var builder = new BundleBuilder(patterns, options);
        var result = builder.Build(job.RootPath, directories, files, captures);

        var build = new BundleBuild
        {
            JobId = jobId,
            BuiltAt = DateTime.UtcNow,
            SettingsJson = builder.SettingsJson(),
            StatsJson = JsonSerializer.Serialize(result.Stats, StatsJson)
        };
        await _bundles.ReplaceAsync(jobId, result.Roots, build, ct);

        _logger.LogInformation("Built {Count} bundles for job {JobId} ({Dumps} dumps, {Captures} files with capture facts)",
            result.Stats.BundleCount, jobId, result.Stats.DumpCount, captures.Count);

        return (await GetOverviewAsync(jobId, ct))!;
    }

    /// <summary>The last build of the job, or null when bundles were never built or were dropped by a rescan.</summary>
    public async Task<BundleOverview?> GetOverviewAsync(long jobId, CancellationToken ct = default)
    {
        var build = await _bundles.GetBuildAsync(jobId, ct);
        if (build is null) return null;

        var all = await _bundles.GetAllAsync(jobId, ct);
        var byKind = all.Where(b => b.ParentId is null)
            .GroupBy(b => b.Kind.ToCode())
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        var comparison = await _bundles.GetComparisonCountsAsync(jobId, ct);
        using var settings = JsonDocument.Parse(build.SettingsJson);
        return new BundleOverview(
            jobId,
            build.BuiltAt,
            settings.RootElement.Clone(),
            JsonSerializer.Deserialize<BundleSplitStats>(build.StatsJson, StatsJson) ?? new BundleSplitStats(),
            byKind,
            comparison is null ? null : new BundleComparisonInfo(comparison.ComparisonId, comparison.AgainstJobId, comparison.ComparedAt));
    }

    /// <summary>
    /// One page of bundles: the top level, the children of one bundle, or all. Counts of marked files and of
    /// comparison states cover the bundle with its children and are computed now, so they follow later analysis
    /// and comparisons without a rebuild.
    /// </summary>
    public async Task<ReadPage<BundleItem>> ListAsync(BundleListQuery query, CancellationToken ct = default)
    {
        var (all, items) = await LoadItemsAsync(query.JobId, ct);

        IEnumerable<Bundle> selected = all;
        if (!query.All)
        {
            if (query.Parent is { } parent && all.All(b => b.Id != parent))
                throw new JobReadException(ReadFailure.BundleNotFound, $"Bundle {parent} is not part of job {query.JobId}.");
            selected = all.Where(b => b.ParentId == query.Parent);
        }
        if (query.Kinds.Count > 0)
            selected = selected.Where(b => query.Kinds.Contains(b.Kind));

        var list = selected.Select(b => items[b.Id]).ToList();
        var limit = Math.Clamp(query.Limit, 1, JobReadService.MaxLimit);
        var offset = Math.Max(0, query.Offset);
        return new ReadPage<BundleItem>(list.Count, limit, offset, list.Skip(offset).Take(limit).ToList());
    }

    /// <summary>One bundle with its direct children.</summary>
    public async Task<(BundleItem Bundle, IReadOnlyList<BundleItem> Children)> GetAsync(long jobId, long bundleId, CancellationToken ct = default)
    {
        var (all, items) = await LoadItemsAsync(jobId, ct);
        if (!items.TryGetValue(bundleId, out var item))
            throw new JobReadException(ReadFailure.BundleNotFound, $"Bundle {bundleId} is not part of job {jobId}.");
        return (item, all.Where(b => b.ParentId == bundleId).Select(b => items[b.Id]).ToList());
    }

    /// <summary>The bundle and every bundle below it.</summary>
    public static IReadOnlyCollection<long> SelfAndDescendants(IReadOnlyList<Bundle> all, long bundleId)
    {
        var children = all.Where(b => b.ParentId is not null).ToLookup(b => b.ParentId!.Value, b => b.Id);
        var result = new List<long>();
        var stack = new Stack<long>();
        stack.Push(bundleId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            result.Add(id);
            foreach (var child in children[id])
                stack.Push(child);
        }
        return result;
    }

    private async Task<(IReadOnlyList<Bundle> All, Dictionary<long, BundleItem> Items)> LoadItemsAsync(long jobId, CancellationToken ct)
    {
        _ = await _jobs.GetByIdAsync(jobId, ct)
            ?? throw new JobReadException(ReadFailure.JobNotFound, $"Job {jobId} not found.");
        var all = await _bundles.GetAllAsync(jobId, ct);
        var byId = all.ToDictionary(b => b.Id);

        // Marked files: the same check the output guard makes (rule markers and model markers).
        var sensitive = new Dictionary<long, long>();
        if (all.Count > 0)
        {
            var membership = await _bundles.GetMembershipAsync(jobId, ct);
            await foreach (var row in _read.StreamFilesAsync(new FileListFilter { JobId = jobId }, ct))
            {
                if (!SensitiveOutputGuard.GetMatch(row.File).IsMatch) continue;
                if (!membership.TryGetValue(row.File.Id, out var bundleId)) continue;
                foreach (var id in SelfAndAncestors(byId, bundleId))
                    sensitive[id] = sensitive.GetValueOrDefault(id) + 1;
            }
        }

        Dictionary<long, Dictionary<string, long>>? comparison = null;
        if (all.Count > 0 && await _bundles.GetComparisonCountsAsync(jobId, ct) is { } counts)
        {
            comparison = all.ToDictionary(b => b.Id, _ => new Dictionary<string, long>(StringComparer.Ordinal));
            foreach (var (bundleId, state, files) in counts.Counts)
            {
                foreach (var id in SelfAndAncestors(byId, bundleId))
                    comparison[id][state] = comparison[id].GetValueOrDefault(state) + files;
            }
        }

        var childCounts = all.Where(b => b.ParentId is not null).GroupBy(b => b.ParentId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var items = all.ToDictionary(b => b.Id, b => new BundleItem(
            b,
            childCounts.GetValueOrDefault(b.Id),
            sensitive.GetValueOrDefault(b.Id),
            comparison?[b.Id]));
        return (all, items);
    }

    private static IEnumerable<long> SelfAndAncestors(Dictionary<long, Bundle> byId, long id)
    {
        var guard = 0;
        while (byId.TryGetValue(id, out var bundle) && guard++ < 64)
        {
            yield return id;
            if (bundle.ParentId is not { } parent) yield break;
            id = parent;
        }
    }

    private static readonly JsonSerializerOptions StatsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null
    };
}
