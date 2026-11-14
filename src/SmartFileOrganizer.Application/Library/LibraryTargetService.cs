using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Bundles;
using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Library;

/// <summary>What <c>sfo library show</c> asks for.</summary>
public sealed record LibraryShowQuery
{
    public long JobId { get; init; }
    public LibraryWords Words { get; init; } = LibraryWords.Default;

    /// <summary>Only this folder and its subtree: relative to the library root or a full path.</summary>
    public string? Under { get; init; }

    /// <summary>Levels below <see cref="Under"/> (or the root); null for all.</summary>
    public int? Depth { get; init; }

    /// <summary>Only folders whose name contains this text (case, umlauts and accents aside).</summary>
    public string? Search { get; init; }

    /// <summary>Only folders whose period (from the files or the name) touches this range.</summary>
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }

    public IReadOnlyCollection<FolderRole> Roles { get; init; } = [];
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
    public bool UseModel { get; init; } = true;
    public bool Refresh { get; init; }
    public string? Model { get; init; }

    /// <summary>Write the patterns file anew from the tree, replacing hand edits.</summary>
    public bool ResetPatterns { get; init; }
}

/// <summary>The patterns file of a library job: where it is, whether it was written now, what it holds.</summary>
/// <param name="File">Full path; null when no patterns directory is configured.</param>
/// <param name="Ignored">Target paths of the file that are no folder of the library.</param>
public sealed record LibraryPatternsInfo(string? File, bool Created, TargetStructure? Targets, IReadOnlyList<string> Ignored, string? Note);

/// <summary>How the model part of a show or match went.</summary>
/// <param name="Status">An <see cref="AdviceStatus"/> code.</param>
public sealed record LibraryModelPart(string Status, string? Model, string? Error, int Dropped)
{
    public static LibraryModelPart Skipped { get; } = new(AdviceStatus.Skipped, null, null, 0);
}

/// <summary>The answer of <c>sfo library show</c>: one page of the target map.</summary>
public sealed record LibraryShow(
    ScanJob Job,
    LibraryMap Map,
    LibraryPatternsInfo Patterns,
    int Total,
    int Limit,
    int Offset,
    int? NextOffset,
    IReadOnlyList<LibraryMapEntry> Items,
    LibraryModelPart Advice);

/// <summary>What <c>sfo library match</c> asks for: a path or a bundle of a source job.</summary>
public sealed record LibraryMatchQuery
{
    public long SourceJobId { get; init; }
    public string? Path { get; init; }
    public long? BundleId { get; init; }

    /// <summary>
    /// Exactly these files of the source as the unit (a finding, https://github.com/crack666/smart_file_organizer/issues/83)
    /// instead of everything below <see cref="Path"/>. <see cref="Path"/>, when it names a folder, still gives the unit its
    /// name and whether it is kept whole; otherwise the files are placed as loose files.
    /// </summary>
    public IReadOnlyList<FileNode>? Files { get; init; }

    /// <summary>The library job; null for the library of the latest comparison, else the newest library job.</summary>
    public long? Against { get; init; }

    /// <summary>
    /// Several library jobs (https://github.com/crack666/smart_file_organizer/issues/74): each is matched, and the best
    /// places of all of them are listed by score. Wins over <see cref="Against"/> when not empty.
    /// </summary>
    public IReadOnlyList<long> Libraries { get; init; } = [];

    public LibraryWords Words { get; init; } = LibraryWords.Default;
    public int? Limit { get; init; }
    public bool UseModel { get; init; } = true;
    public bool Refresh { get; init; }
    public string? Model { get; init; }
}

/// <summary>The answer of <c>sfo library match</c>.</summary>
/// <param name="Library">The library of the best proposal (or of the new folder); the first one named when there is none.</param>
/// <param name="Libraries">Every library matched, in the order named.</param>
/// <param name="CandidateLibraries">The library of each of <see cref="MatchResult.Candidates"/>, in the same order.</param>
/// <param name="NewFolderLibrary">The library the new folder would lie in.</param>
/// <param name="Space">Per library root: free space and what the unit needs there (its files not yet backed up).</param>
/// <param name="UnconfirmedLibraries">The library of each of <see cref="MatchResult.Unconfirmed"/>, in the same order.</param>
public sealed record LibraryMatchAnswer(
    ScanJob Source,
    ScanJob? Library,
    LibraryChoice? Choice,
    MatchStatus Status,
    string? Message,
    MatchUnit Unit,
    ComparisonHeader? Comparison,
    MatchResult? Result,
    LibraryPatternsInfo? Patterns,
    LibraryModelPart Advice,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ScanJob>? Libraries = null,
    IReadOnlyList<ScanJob>? CandidateLibraries = null,
    ScanJob? NewFolderLibrary = null,
    IReadOnlyList<RootStorage>? Space = null,
    IReadOnlyList<ScanJob>? UnconfirmedLibraries = null);

/// <summary>
/// <c>sfo library show</c> and <c>sfo library match</c> (https://github.com/crack666/smart_file_organizer/issues/25):
/// the target map of a library and the best places in it for a file, folder or bundle of a source. Reads scan
/// data, metadata facts and comparisons; writes only the patterns file next to the database and the model's stored
/// answers. Nothing in the source or the library is changed: proposals are data for the plan.
/// </summary>
public sealed partial class LibraryTargetService
{
    public const string MapTask = "map";
    public const string NameTask = "name";

    private readonly LibraryService _library;
    private readonly IScanJobRepository _jobs;
    private readonly IJobReadRepository _reads;
    private readonly IBundleRepository _bundles;
    private readonly ICaptureInfoSource _captures;
    private readonly IContentComparisonRepository _comparisons;
    private readonly LibraryMatchOptions _options;
    private readonly ILogger<LibraryTargetService> _logger;
    private readonly IFreeSpaceProbe? _freeSpace;
    private readonly IFileFactRepository? _facts;
    private readonly IContentFitJudge? _judge;
    private readonly IReadOnlyList<IFolderDescriptionSource> _descriptionSources;
    private readonly IOllamaService? _ollama;
    private readonly ILibraryRootProbe _rootProbe;

    public LibraryTargetService(
        LibraryService library,
        IScanJobRepository jobs,
        IJobReadRepository reads,
        IBundleRepository bundles,
        ICaptureInfoSource captures,
        IContentComparisonRepository comparisons,
        LibraryMatchOptions options,
        ILogger<LibraryTargetService> logger,
        IFreeSpaceProbe? freeSpace = null,
        IFileFactRepository? facts = null,
        IContentFitJudge? judge = null,
        IEnumerable<IFolderDescriptionSource>? descriptionSources = null,
        IOllamaService? ollama = null,
        ILibraryRootProbe? rootProbe = null)
    {
        _rootProbe = rootProbe ?? FileSystemLibraryRootProbe.Instance;
        _freeSpace = freeSpace;
        _facts = facts;
        _judge = judge;
        // The own sample is the default; other sources (descriptions another program holds) are asked first.
        _descriptionSources = (descriptionSources ?? []).OrderBy(s => s.Kind == FolderDescriptionSource.Sample ? 1 : 0).ToList();
        _ollama = ollama;
        _library = library;
        _jobs = jobs;
        _reads = reads;
        _bundles = bundles;
        _captures = captures;
        _comparisons = comparisons;
        _options = options;
        _logger = logger;
    }

    public LibraryMatchOptions Options => _options;

    // ------------------------------------------------------------------ show

    public async Task<LibraryShow> ShowAsync(LibraryShowQuery query, CancellationToken ct = default)
    {
        ValidateOptions();
        var (job, map) = await LoadMapAsync(query.JobId, query.Words, ct);

        var advice = LibraryModelPart.Skipped;
        if (query.UseModel)
            advice = await DescribeAsync(job, map, query.Model, query.Refresh, ct);

        var patterns = await PatternsAsync(job, map, query.ResetPatterns, ct);

        IEnumerable<LibraryMapEntry> selected = map.Entries;
        var baseDepth = 0;
        if (!string.IsNullOrWhiteSpace(query.Under))
        {
            var under = Find(job, map, query.Under)
                        ?? throw new LibraryServiceException(LibraryFailure.PathNotFound, $"'{query.Under}' is no folder of library job {job.Id}.");
            baseDepth = under.Depth;
            selected = selected.Where(e => e == under || IsBelow(e, under));
        }
        if (query.Depth is { } depth)
            selected = selected.Where(e => e.Depth - baseDepth <= depth);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var needle = LibraryWords.Normalize(query.Search.Trim());
            selected = selected.Where(e => LibraryWords.Normalize(e.Name).Contains(needle, StringComparison.Ordinal));
        }
        if (query.From is not null || query.To is not null)
        {
            var from = query.From ?? DateTime.MinValue;
            var to = query.To ?? DateTime.MaxValue;
            selected = selected.Where(e =>
                e.NamePeriod is { } np && np.From <= to && from <= np.To
                || e.From is { } f && e.To is { } t && f <= to && from <= t);
        }
        if (query.Roles.Count > 0)
            selected = selected.Where(e => query.Roles.Contains(e.Role));

        var list = selected.ToList();
        var limit = Math.Clamp(query.Limit, 1, JobReadService.MaxLimit);
        var offset = Math.Max(0, query.Offset);
        var page = list.Skip(offset).Take(limit).ToList();
        int? next = offset + page.Count < list.Count ? offset + page.Count : null;
        return new LibraryShow(job, map, patterns, list.Count, limit, offset, next, page, advice);
    }

    // ------------------------------------------------------------------ match

    public async Task<LibraryMatchAnswer> MatchAsync(LibraryMatchQuery query, CancellationToken ct = default)
    {
        ValidateOptions();
        var source = await _jobs.GetByIdAsync(query.SourceJobId, ct)
                     ?? throw new LibraryServiceException(LibraryFailure.JobNotFound, $"Job {query.SourceJobId} not found.");
        if (source.Role != ScanRole.Source)
            throw new LibraryServiceException(LibraryFailure.WrongRole,
                $"Job {source.Id} is a library; library match looks for places for what a source job holds.");
        if (source.Status != JobStatus.Completed)
            throw new LibraryServiceException(LibraryFailure.ScanNotCompleted,
                $"The scan of job {source.Id} is {source.Status.ToString().ToLowerInvariant()}; finish it first (sfo scan --resume {source.Id}).");

        var (libraries, choice) = await ChooseLibrariesAsync(source, query, ct);
        var (files, unitBase) = await LoadUnitAsync(source, query, ct);
        var notes = new List<string>();

        if (libraries.Count == 0)
        {
            var nothing = await BuildUnitAsync(source, files, unitBase, null, query.Words, ct);
            return new LibraryMatchAnswer(source, null, null, MatchStatus.NoLibrary,
                "No library has been scanned, so no permanent storage is known: there is no existing order to fit into, and no target is proposed. "
                + "Scan your organised archive with 'sfo scan <path> --role library'; without one, 'sfo library propose' suggests a structure.",
                nothing.Unit, null, null, null, LibraryModelPart.Skipped, notes, []);
        }

        EnsureReadable(libraries);
        var (set, missing) = await LibraryComparisonSet.LoadAsync(_comparisons, source.Id, libraries.Select(l => l.Id).ToList(), ct);
        foreach (var id in missing)
            notes.Add($"Job {source.Id} was not compared with library {id}; the signal 'same content already there' is missing (sfo compare {source.Id} --against {id}).");
        foreach (var header in set?.Headers.Where(h => !h.Complete) ?? [])
            notes.Add($"The comparison with library {header.AgainstJobId} is incomplete ({header.ErrorCount} errors); some files may lie in the library without being counted.");
        if (choice == LibraryChoice.Latest)
            notes.Add($"Library {libraries[0].Id} is the newest library job; choose another with --against.");

        var built = await BuildUnitAsync(source, files, unitBase, set?.Files, query.Words, ct);
        var unit = built.Unit;
        if (unit.Files == 0)
            notes.Add("The unit holds no files; places are judged by its name alone.");
        if (unit.States is { } states && states.GetValueOrDefault(ContentStateValues.BackedUp) == unit.Files && unit.Files > 0)
            notes.Add($"All {unit.Files} files are already in the library; nothing of it needs a new place.");

        // What the unit shows, from the description facts of its analysed files.
        var unitDescription = await DescribeUnitAsync(source, files, ct);
        var modelReady = new Lazy<Task<bool>>(() => _ollama is null ? Task.FromResult(true) : _ollama.IsAvailableAsync(ct));

        // Each library on its own; the best places of all of them by score, the first library named on a tie.
        var limit = Math.Clamp(query.Limit ?? _options.MaxCandidates, 1, 50);
        var matched = new List<(ScanJob Library, LibraryMap Map, LibraryPatternsInfo Patterns, MatchResult Result)>();
        foreach (var library in libraries)
        {
            var (_, map) = await LoadMapAsync(library.Id, query.Words, ct);
            var patternsOf = await PatternsAsync(library, map, reset: false, ct);
            var check = await CheckContentAsync(source, library, map, unit, unitDescription, query, modelReady, ct);
            matched.Add((library, map, patternsOf, LibraryMatcher.Match(map, unit, _options, limit, query.Words, check)));
        }
        var content = matched.Select(m => m.Result.Content).FirstOrDefault(c => c?.Status == ContentCheckStatus.Checked)
                      ?? matched.Select(m => m.Result.Content).FirstOrDefault()
                      ?? ContentCheck.NoModel;
        var rejected = matched.SelectMany(m => m.Result.Rejected ?? []).ToList();
        if (content.Status is not (ContentCheckStatus.Checked or ContentCheckStatus.NoCandidates))
            notes.Add($"The content was not checked ({ContentNote(content)}); without the same files at the target no existing folder is proposed.");

        var ranked = matched
            .SelectMany((m, order) => m.Result.Candidates.Select(c => (Candidate: c, m.Library, Order: order)))
            .OrderByDescending(x => x.Candidate.Score).ThenBy(x => x.Order)
            .Take(limit)
            .ToList();
        var unconfirmed = matched
            .SelectMany((m, order) => (m.Result.Unconfirmed ?? []).Select(c => (Candidate: c, m.Library, Order: order)))
            .OrderByDescending(x => x.Candidate.Score).ThenBy(x => x.Order)
            .Take(limit)
            .ToList();
        // A new folder in the library that holds this kind of content (own pictures, papers, films), then the most
        // certain proposal, then the library named first (https://github.com/crack666/smart_file_organizer/issues/85).
        var withNewFolder = ranked.Count > 0
            ? null
            : matched.Select((m, order) => (m, order)).Where(x => x.m.Result.NewFolder is not null)
                .OrderByDescending(x => Math.Round(LibraryMatcher.KindShare(x.m.Map, unit), 2))
                .ThenBy(x => (int)x.m.Result.NewFolder!.Certainty).ThenBy(x => x.order)
                .Select(x => ((ScanJob Library, LibraryMap Map, LibraryPatternsInfo Patterns, MatchResult Result)?)x.m).FirstOrDefault();
        var noTarget = ranked.Count == 0 && withNewFolder is null
            ? matched.Select(m => m.Result.NoTarget).FirstOrDefault(t => t is not null)
            : null;
        if (noTarget is not null && matched.Count > 1)
            noTarget = $"No existing folder is confirmed by content, and none of the {matched.Count} libraries holds {LibraryMatcher.KindWords(LibraryMatcher.KindOf(unit))}: a new folder would be a guess as well.";
        var result = new MatchResult(ranked.Select(x => x.Candidate).ToList(), matched.Sum(m => m.Result.Considered),
            withNewFolder?.Result.NewFolder, rejected, content, unconfirmed.Select(x => x.Candidate).ToList(), noTarget);
        var home = ranked.Count > 0 ? ranked[0].Library : withNewFolder?.Library ?? libraries[0];
        var homeMatch = matched.First(m => m.Library.Id == home.Id);
        var homeMap = homeMatch.Map;
        var patterns = homeMatch.Patterns;

        var advice = LibraryModelPart.Skipped;
        if (result.NewFolder is { NameComplete: false } proposal && query.UseModel)
        {
            var (named, part) = await NameAsync(source, homeMap, unit, proposal, query.Model, query.Refresh, ct);
            advice = part;
            result = result with { NewFolder = named };
        }
        else if (query.UseModel)
        {
            advice = new LibraryModelPart(AdviceStatus.Skipped, null, null, 0);
        }

        // Room at each root for what of the unit is not in the library yet.
        var needed = files.Where(f => set?.Rows.GetValueOrDefault(f.Id)?.State != ContentState.BackedUp).Sum(f => f.Size);
        var space = libraries.Select(l =>
        {
            var free = _freeSpace?.AvailableBytes(l.RootPath);
            var state = free is not { } bytes ? TargetSpace.Unknown : needed <= bytes ? TargetSpace.Enough : TargetSpace.NotEnough;
            return new RootStorage(l.Id, l.RootPath, free, needed, 0, state);
        }).ToList();
        foreach (var s in space.Where(s => s.Space == TargetSpace.NotEnough))
            notes.Add($"Library {s.LibraryJobId} ({s.RootPath}) has {FindingRules.Size(s.FreeBytes!.Value)} free; the files not yet backed up need {FindingRules.Size(s.NeededBytes)}.");

        return new LibraryMatchAnswer(source, home, choice, MatchStatus.Ok, null, unit, set?.Headers.FirstOrDefault(h => h.AgainstJobId == home.Id) ?? set?.First,
            result, patterns, advice, notes, libraries, ranked.Select(x => x.Library).ToList(), withNewFolder?.Library, space,
            unconfirmed.Select(x => x.Library).ToList());
    }

    // ------------------------------------------------------------------ library and map

    /// <summary>
    /// Every library root must be readable (https://github.com/crack666/smart_file_organizer/issues/85): a share that is
    /// not connected cannot show what its folders hold, and a target there could not be checked. Until then the match ran
    /// on the stored scan and reported success with reasons such as "the folder's files could not be described".
    /// </summary>
    /// <exception cref="LibraryServiceException"><see cref="LibraryFailure.LibraryUnreachable"/>, naming every root.</exception>
    public async Task EnsureReadableAsync(IEnumerable<long> libraryJobIds, CancellationToken ct = default)
    {
        var jobs = new List<ScanJob>();
        foreach (var id in libraryJobIds.Distinct())
        {
            if (await _jobs.GetByIdAsync(id, ct) is { } job) jobs.Add(job);
        }
        EnsureReadable(jobs);
    }

    /// <inheritdoc cref="EnsureReadableAsync"/>
    public void EnsureReadable(IEnumerable<ScanJob> libraries)
    {
        var problems = libraries
            .Select(l => (Library: l, Problem: _rootProbe.Problem(l.RootPath)))
            .Where(x => x.Problem is not null)
            .Select(x => $"library {x.Library.Id} ({x.Library.RootPath}): {x.Problem}")
            .ToList();
        if (problems.Count > 0)
            throw new LibraryServiceException(LibraryFailure.LibraryUnreachable,
                $"Cannot read {string.Join("; ", problems)}. No target is proposed while a library cannot be read; connect it and run again, "
                + "or leave it out with --against.");
    }

    /// <summary>
    /// The libraries to match against: those named; else those of the newest comparison of the source; else the newest
    /// library job. Empty when no library exists.
    /// </summary>
    private async Task<(IReadOnlyList<ScanJob> Libraries, LibraryChoice? Choice)> ChooseLibrariesAsync(
        ScanJob source, LibraryMatchQuery query, CancellationToken ct)
    {
        var ids = query.Libraries.Count > 0 ? query.Libraries.Distinct().ToList() : query.Against is { } one ? [one] : [];
        if (ids.Count > 0)
        {
            var named = new List<ScanJob>();
            foreach (var id in ids)
            {
                var job = await _jobs.GetByIdAsync(id, ct)
                          ?? throw new LibraryServiceException(LibraryFailure.JobNotFound, $"Job {id} not found.");
                if (job.Role != ScanRole.Library)
                    throw new LibraryServiceException(LibraryFailure.WrongRole,
                        $"Job {id} is a {job.Role.ToValue()}, not a library (sfo scan <path> --role library).");
                named.Add(job);
            }
            return (named, LibraryChoice.Option);
        }

        var byComparison = new List<ScanJob>();
        foreach (var header in await _comparisons.GetLatestLibraryRunAsync(source.Id, ct))
        {
            if (header.AgainstJobId is { } compared
                && await _jobs.GetByIdAsync(compared, ct) is { Role: ScanRole.Library, Status: JobStatus.Completed } job)
                byComparison.Add(job);
        }
        if (byComparison.Count > 0)
            return (byComparison, LibraryChoice.Comparison);

        var newest = (await _jobs.GetAllAsync(ct))
            .Where(j => j.Role == ScanRole.Library && j.Status == JobStatus.Completed)
            .OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id)
            .FirstOrDefault();
        return newest is null ? ([], null) : ([newest], LibraryChoice.Latest);
    }

    private async Task<(ScanJob Job, LibraryMap Map)> LoadMapAsync(long jobId, LibraryWords words, CancellationToken ct)
    {
        var (job, tree) = await _library.LoadAsync(jobId, words, ct);
        if (job.Role != ScanRole.Library)
            throw new LibraryServiceException(LibraryFailure.WrongRole,
                $"Job {jobId} is a {job.Role.ToValue()}; the target map is built from a library (sfo scan <path> --role library). "
                + "Without a library, 'sfo library propose' suggests a structure.");
        var rules = LibraryRules.Run(tree, words, new LibraryReviewOptions());
        return (job, LibraryMap.Build(tree, rules, words, BundlePatterns.Default, _options));
    }

    private static LibraryMapEntry? Find(ScanJob job, LibraryMap map, string path)
    {
        var p = path.Trim();
        if (p is "" or "." or "/") return map.Root;
        if (map.ByRelativePath.TryGetValue(p.Replace('\\', '/').Trim('/'), out var byRelative)) return byRelative;
        var full = JobReadService.ResolvePath(job, p);
        return map.ByFullPath.TryGetValue(full, out var byFull) ? byFull : null;
    }

    private static bool IsBelow(LibraryMapEntry e, LibraryMapEntry ancestor)
    {
        for (var p = e.Parent; p is not null; p = p.Parent)
            if (p == ancestor) return true;
        return false;
    }

    /// <summary>
    /// Loads the patterns file of the library job, or writes it from the map when it does not exist (or on reset).
    /// A hand-edited file wins over what the names show.
    /// </summary>
    private async Task<LibraryPatternsInfo> PatternsAsync(ScanJob job, LibraryMap map, bool reset, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.PatternsDirectory))
        {
            var inMemory = map.ToPatterns(job);
            return new LibraryPatternsInfo(null, false, inMemory, [], null);
        }

        var file = Path.Combine(_options.PatternsDirectory, $"job-{job.Id}.json");
        if (File.Exists(file) && !reset)
        {
            TargetStructure loaded;
            try
            {
                loaded = TargetStructureFile.Load(file);
            }
            catch (TargetStructureException ex)
            {
                throw new LibraryServiceException(LibraryFailure.PatternsInvalid,
                    $"{file}: {string.Join("; ", ex.Problems)}. Fix it, or write it anew with --reset-patterns.");
            }
            var ignored = map.Apply(loaded);
            return new LibraryPatternsInfo(file, false, loaded, ignored,
                ignored.Count > 0 ? $"{ignored.Count} target(s) of the patterns file are no folder of the library and are ignored." : null);
        }

        var built = map.ToPatterns(job);
        if (built is null)
            return new LibraryPatternsInfo(file, false, null, [], "No folder holds subfolders with a common naming pattern; nothing to write.");
        try
        {
            Directory.CreateDirectory(_options.PatternsDirectory);
            await File.WriteAllTextAsync(file, TargetStructureFile.Serialize(built) + Environment.NewLine, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not write the patterns file {File}: {Message}", file, ex.Message);
            return new LibraryPatternsInfo(null, false, built, [], $"The patterns file could not be written: {ex.Message}");
        }
        map.Apply(built);
        return new LibraryPatternsInfo(file, true, built, [], null);
    }

    // ------------------------------------------------------------------ the unit

    private sealed record UnitBase(MatchUnitKind Kind, string? FullPath, Bundle? Bundle, DirectoryReadRow? Directory);

    private async Task<(IReadOnlyList<FileNode> Files, UnitBase Base)> LoadUnitAsync(ScanJob source, LibraryMatchQuery query, CancellationToken ct)
    {
        var files = new List<FileNode>();
        if (query.Files is { } given)
        {
            if (given.Count == 0)
                throw new LibraryServiceException(LibraryFailure.PathNotFound, $"No files of job {source.Id} were given.");
            if (query.Path is { Length: > 0 } folder)
            {
                var folderPath = JobReadService.ResolvePath(source, folder);
                var folderRow = (await _reads.GetDirectoriesAsync(source.Id, ct))
                    .FirstOrDefault(d => string.Equals(d.FullPath, folderPath, PathComparison));
                if (folderRow is not null)
                    return (given, new UnitBase(MatchUnitKind.Folder, folderRow.FullPath, null, folderRow));
            }
            return (given, new UnitBase(MatchUnitKind.File, given[0].FullPath, null, null));
        }
        if (query.BundleId is { } bundleId)
        {
            var all = await _bundles.GetAllAsync(source.Id, ct);
            var bundle = all.FirstOrDefault(b => b.Id == bundleId)
                         ?? throw new LibraryServiceException(LibraryFailure.BundleNotFound, $"Bundle {bundleId} is not part of job {source.Id}.");
            var ids = BundleService.SelfAndDescendants(all, bundleId);
            await foreach (var row in _reads.StreamFilesAsync(new FileListFilter { JobId = source.Id, BundleIds = ids }, ct))
                files.Add(row.File);
            return (files, new UnitBase(MatchUnitKind.Bundle, bundle.RootPath, bundle, null));
        }

        var full = JobReadService.ResolvePath(source, query.Path ?? ".");
        var dirs = await _reads.GetDirectoriesAsync(source.Id, ct);
        var dir = dirs.FirstOrDefault(d => string.Equals(d.FullPath, full, PathComparison));
        if (dir is not null)
        {
            var prefix = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
            await foreach (var row in _reads.StreamFilesAsync(new FileListFilter
                           {
                               JobId = source.Id, Directory = dir.FullPath, Recursive = true, DirectoryPrefix = prefix
                           }, ct))
                files.Add(row.File);
            return (files, new UnitBase(MatchUnitKind.Folder, dir.FullPath, null, dir));
        }

        var file = await _reads.GetFileByPathAsync(source.Id, full, ct)
                   ?? throw new LibraryServiceException(LibraryFailure.PathNotFound, $"'{query.Path}' is neither a folder nor a file of job {source.Id}.");
        files.Add(file.File);
        return (files, new UnitBase(MatchUnitKind.File, file.File.FullPath, null, null));
    }

    private sealed record BuiltUnit(MatchUnit Unit);

    private async Task<BuiltUnit> BuildUnitAsync(
        ScanJob source, IReadOnlyList<FileNode> files, UnitBase b, IReadOnlyList<FileComparison>? rows, LibraryWords words, CancellationToken ct)
    {
        var captures = files.Count > 0 ? await _captures.GetAsync(source.Id, ct) : new Dictionary<long, CaptureInfo>();
        return BuildUnit(source, files, b, captures, rows, words);
    }

    // ------------------------------------------------------------------ many groups at once

    /// <summary>
    /// The best existing folder of the library for each group of source files, by the rules alone (no model, no
    /// new-folder name). The map, the capture facts and the comparison are read once for all groups, so findings can
    /// ask for many groups (https://github.com/crack666/smart_file_organizer/issues/26). Nothing is written, not even
    /// the patterns file. Null for a group no existing folder fits.
    /// </summary>
    public async Task<IReadOnlyList<MatchCandidate?>> BestPlacesAsync(
        long sourceJobId, long libraryJobId, IReadOnlyList<IReadOnlyList<FileNode>> groups, CancellationToken ct = default) =>
        (await BestPlacesAsync(sourceJobId, [libraryJobId], groups, ct)).Select(p => p?.Candidate).ToList();

    /// <summary>
    /// As <see cref="BestPlacesAsync(long, long, IReadOnlyList{IReadOnlyList{FileNode}}, CancellationToken)"/> over
    /// several libraries (https://github.com/crack666/smart_file_organizer/issues/74): per group the best place of all
    /// of them by score, the first library named on a tie, with the library it lies in.
    /// </summary>
    public async Task<IReadOnlyList<(MatchCandidate Candidate, ScanJob Library)?>> BestPlacesAsync(
        long sourceJobId, IReadOnlyList<long> libraryJobIds, IReadOnlyList<IReadOnlyList<FileNode>> groups, CancellationToken ct = default)
    {
        var best = new (MatchCandidate Candidate, ScanJob Library)?[groups.Count];
        foreach (var libraryJobId in libraryJobIds.Distinct())
        {
            var (library, places) = await BestPlacesInAsync(sourceJobId, libraryJobId, groups, ct);
            for (var i = 0; i < places.Count; i++)
            {
                if (places[i] is { } candidate && (best[i] is not { } current || candidate.Score > current.Candidate.Score))
                    best[i] = (candidate, library);
            }
        }
        return best;
    }

    private async Task<(ScanJob Library, IReadOnlyList<MatchCandidate?> Places)> BestPlacesInAsync(
        long sourceJobId, long libraryJobId, IReadOnlyList<IReadOnlyList<FileNode>> groups, CancellationToken ct)
    {
        ValidateOptions();
        var source = await _jobs.GetByIdAsync(sourceJobId, ct)
                     ?? throw new LibraryServiceException(LibraryFailure.JobNotFound, $"Job {sourceJobId} not found.");
        var library = await _jobs.GetByIdAsync(libraryJobId, ct)
                      ?? throw new LibraryServiceException(LibraryFailure.JobNotFound, $"Job {libraryJobId} not found.");
        if (groups.Count == 0) return (library, []);

        var words = LibraryWords.Default;
        var comparison = await _comparisons.GetComparisonAsync(source.Id, library.Id, ct);
        var rows = comparison is null ? null : await _comparisons.GetFileComparisonsAsync(comparison.Id, ct);
        var captures = await _captures.GetAsync(source.Id, ct);
        var (_, map) = await LoadMapAsync(library.Id, words, ct);

        var result = new List<MatchCandidate?>(groups.Count);
        foreach (var files in groups)
        {
            ct.ThrowIfCancellationRequested();
            var first = files.Count > 0 ? files[0].FullPath : source.RootPath;
            var unit = BuildUnit(source, files, new UnitBase(MatchUnitKind.File, first, null, null), captures, rows, words).Unit;
            result.Add(LibraryMatcher.Match(map, unit, _options, 1, words).Candidates.FirstOrDefault());
        }
        return (library, result);
    }

    private BuiltUnit BuildUnit(
        ScanJob source, IReadOnlyList<FileNode> files, UnitBase b, IReadOnlyDictionary<long, CaptureInfo> captures,
        IReadOnlyList<FileComparison>? comparisonRows, LibraryWords words)
    {
        var patterns = BundlePatterns.Default;
        var types = new long[Enum.GetValues<FileType>().Length];
        foreach (var f in files)
            types[Enum.IsDefined(f.FileType) ? (int)f.FileType : (int)FileType.Unknown]++;

        // Times: capture date, a date in the name, the modification time (as the bundles use them).
        DateTime? from = null, to = null;
        var sources = new HashSet<TimeSource>();
        foreach (var f in files)
        {
            var t = EventGrouper.TimeOf(f, captures);
            if (t.Time is not { } time) continue;
            sources.Add(t.Source);
            if (from is null || time < from) from = time;
            if (to is null || time > to) to = time;
        }
        TimeSource? timeSource = sources.Count switch { 0 => null, 1 => sources.Single(), _ => TimeSource.Mixed };

        // Name, and whether the unit stays whole under its own name.
        string name;
        bool keepWhole;
        string keepReason;
        var isDump = b.Bundle?.Kind == BundleKind.Dump;
        switch (b.Kind)
        {
            case MatchUnitKind.Bundle when b.Bundle!.Kind is BundleKind.Folder or BundleKind.Dump && b.Bundle.RootPath is { } root:
                name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
                keepWhole = true;
                keepReason = isDump
                    ? "A device copy is kept whole under its own name."
                    : "A folder with a meaningful name is kept whole under its own name: never renamed, never split.";
                break;
            case MatchUnitKind.Bundle:
                name = b.Bundle!.SuggestedName;
                keepWhole = false;
                keepReason = $"A {b.Bundle.Kind.ToCode()} bundle has no folder of its own; its files need a place.";
                break;
            case MatchUnitKind.Folder:
                name = b.Directory!.Depth == 0 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(b.FullPath!)) : b.Directory.Name;
                keepWhole = b.Directory.Depth > 0 && !patterns.IsGenericName(name) && LibraryRules.PlaceholderKind(name, words) is null;
                keepReason = keepWhole
                    ? "A folder with a meaningful name is kept whole under its own name: never renamed, never split."
                    : b.Directory.Depth == 0
                        ? "The root of the source is the medium, not a folder to keep; its files need places."
                        : $"'{name}' says nothing about its content; its files need a place.";
                break;
            default:
                name = Path.GetFileName(b.FullPath!);
                keepWhole = false;
                keepReason = files.Count > 1 ? "Loose files go into a folder." : "A single file goes into a folder.";
                break;
        }

        var group = LibraryMap.DominantGroup(types);
        var role = isDump ? FolderRole.Dump : group switch
        {
            ContentGroup.Media => FolderRole.Occasion,
            ContentGroup.Document => FolderRole.DocumentType,
            ContentGroup.Audio => FolderRole.MediaTitle,
            ContentGroup.Software => FolderRole.Project,
            _ => FolderRole.Topic
        };

        // Folders the files lie in, most files first.
        var folders = files.GroupBy(f => f.ParentPath, PathComparer)
            .Select(g => (Path: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Path, StringComparer.Ordinal)
            .ToList();
        var folderNames = folders.Select(f => Path.GetFileName(Path.TrimEndingDirectorySeparator(f.Path)))
            .Where(n => n.Length > 0).Distinct().Take(10).ToList();

        IReadOnlyList<string> unitWords;
        string? occasion;
        if (keepWhole)
        {
            unitWords = LibraryMap.Words(name, words);
            occasion = StripDates(name);
        }
        else
        {
            var mainFolder = folders.Count > 0 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(folders[0].Path)) : null;
            var meaningful = mainFolder is not null && folders[0].Path != source.RootPath
                             && !patterns.IsGenericName(mainFolder) && LibraryRules.PlaceholderKind(mainFolder, words) is null
                ? mainFolder
                : null;
            occasion = meaningful is null ? null : StripDates(meaningful);
            unitWords = CommonWords(files, words).Concat(meaningful is null ? [] : LibraryMap.Words(meaningful, words)).Distinct().ToList();
        }

        // Comparison: states and where the same content lies in the library.
        Dictionary<string, long>? states = null;
        var matchDirs = new List<string>();
        if (comparisonRows is not null)
        {
            var ids = files.Select(f => f.Id).ToHashSet();
            states = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var c in comparisonRows)
            {
                if (!ids.Contains(c.FileNodeId)) continue;
                var code = c.State?.ToValue() ?? "none";
                states[code] = states.GetValueOrDefault(code) + 1;
                if (c.State == ContentState.BackedUp && c.MatchPath is { } match && Path.GetDirectoryName(match) is { } matchDir)
                    matchDirs.Add(matchDir);
            }
        }

        var unit = new MatchUnit
        {
            Kind = b.Kind,
            FullPath = b.FullPath,
            BundleId = b.Bundle?.Id,
            BundleKind = b.Bundle?.Kind,
            Name = name,
            KeepWhole = keepWhole,
            KeepReason = keepReason,
            Role = role,
            Files = files.Count,
            Types = types,
            From = from,
            To = to,
            TimeSource = timeSource,
            Words = unitWords,
            MatchDirectories = matchDirs,
            States = states,
            SourceFolders = folderNames,
            OccasionWords = string.IsNullOrWhiteSpace(occasion) ? null : occasion
        };
        return new BuiltUnit(unit);
    }

    /// <summary>Words that at least half of the file names share (all words for a single file): the kind of document, an occasion.</summary>
    private static IEnumerable<string> CommonWords(IReadOnlyList<FileNode> files, LibraryWords words)
    {
        if (files.Count == 0) return [];
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            foreach (var w in LibraryMap.Words(Path.GetFileNameWithoutExtension(f.Name), words))
                counts[w] = counts.GetValueOrDefault(w) + 1;
        }
        return counts.Where(c => c.Value * 2 >= files.Count).OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => c.Key).Take(5);
    }

    /// <summary>A name without its dates and years: <c>Skifahrt 2015</c> → <c>Skifahrt</c>.</summary>
    private static string StripDates(string name) =>
        SpacesRegex().Replace(DatesRegex().Replace(name, " "), " ").Trim(' ', '-', '_', '.', '(', ')');

    // ------------------------------------------------------------------ content (https://github.com/crack666/smart_file_organizer/issues/56)

    /// <summary>
    /// What the unit shows: the description facts its analysed files carry (scene, occasion, document kind, issuer,
    /// ...), from a sample of at most <c>UnitSampleSize</c> described files spread over its period. Empty when no file
    /// of it was described.
    /// </summary>
    private async Task<ContentDescription> DescribeUnitAsync(ScanJob source, IReadOnlyList<FileNode> files, CancellationToken ct)
    {
        if (_facts is null || files.Count == 0) return ContentDescription.Empty;
        var all = await _facts.GetByJobAsync(source.Id, ct);
        var described = files
            .Where(f => all.TryGetValue(f.Id, out var facts) && facts.Any(x => x.Key.IsDescription()))
            .ToList();
        var sample = SampleFolderDescriptionSource.Spread(described, _options.UnitSampleSize);
        return ContentDescription.From(sample.Select(f => all[f.Id]).ToList());
    }

    /// <summary>
    /// Describes the best candidates of one library (at most <c>MaxContentCandidates</c>) and lets the judge compare
    /// them with the unit. Without a model, without a description of the unit, or when the model is not reachable,
    /// nothing is checked and the match says why.
    /// </summary>
    private async Task<ContentCheck> CheckContentAsync(
        ScanJob source, ScanJob library, LibraryMap map, MatchUnit unit, ContentDescription unitDescription, LibraryMatchQuery query,
        Lazy<Task<bool>> modelReady, CancellationToken ct)
    {
        if (!query.UseModel || _judge is null || _descriptionSources.Count == 0)
            return ContentCheck.NotChecked(ContentCheckStatus.NoModel, "no model", unitDescription);
        if (unitDescription.IsEmpty)
            return ContentCheck.NotChecked(ContentCheckStatus.NoDescription,
                "the files of it are not described yet; sfo analyze describes them", unitDescription);
        var chosen = LibraryMatcher.Preselect(map, unit, _options, query.Words).Take(_options.MaxContentCandidates).ToList();
        if (chosen.Count == 0)
            return ContentCheck.NotChecked(ContentCheckStatus.NoCandidates, "no candidate folder", unitDescription);
        if (!await modelReady.Value)
            return ContentCheck.NotChecked(ContentCheckStatus.Unavailable, "the local model is not reachable", unitDescription);

        var byKey = new Dictionary<string, CandidateContent>(StringComparer.Ordinal);
        var asked = new List<(FitCandidate Fit, MatchCandidate Candidate)>();
        var request = new FolderDescribeRequest(UseModel: true, query.Refresh, _options.FolderSampleSize);
        foreach (var c in chosen)
        {
            FolderDescription? described = null;
            foreach (var s in _descriptionSources)
            {
                described = await s.DescribeAsync(library, c.Target, request, ct);
                if (described is { Description.IsEmpty: false }) break;
            }
            var key = ContentCheck.KeyOf(c.Placement, c.Target.RelativePath);
            if (described is not { Description.IsEmpty: false })
            {
                byKey[key] = new CandidateContent(ContentFit.Unclear, "the folder's files could not be described", described?.Description);
                continue;
            }
            asked.Add((new FitCandidate($"c{asked.Count + 1}", key, c.Placement, c.Target.RelativePath, PeriodText(c.Target),
                described.Description), c));
        }
        if (asked.Count == 0)
            return new ContentCheck(ContentCheckStatus.Checked, unitDescription, byKey, null);

        var answer = await _judge.JudgeAsync(source.Id, unitDescription, asked.Select(a => a.Fit).ToList(), query.Model, query.Refresh, ct);
        if (answer.Status != ContentCheckStatus.Checked)
            return ContentCheck.NotChecked(answer.Status, answer.Error ?? "the model gave no usable answer", unitDescription);
        foreach (var (fit, _) in asked)
        {
            var verdict = answer.Verdicts.GetValueOrDefault(fit.Id) ?? new FitVerdict(ContentFit.Unclear, "the model said nothing about this folder");
            // fits only with a concrete shared feature, whatever the judge said (Issue 85).
            verdict = FitRules.Enforce(verdict, query.Words);
            byKey[fit.Key] = new CandidateContent(verdict.Fit, verdict.Reason, fit.Description, verdict.Shared, verdict.SharedKind);
        }
        return new ContentCheck(ContentCheckStatus.Checked, unitDescription, byKey, answer.Model);
    }

    private static string? PeriodText(LibraryMapEntry e) =>
        e.NamePeriod is { } np ? $"{LibraryMap.Day(np.From)}..{LibraryMap.Day(np.To)} (name)"
        : e.From is { } f && e.To is { } t ? $"{LibraryMap.Day(f)}..{LibraryMap.Day(t)}"
        : null;

    private static string ContentNote(ContentCheck c) => c.Status switch
    {
        ContentCheckStatus.NoModel => "no model",
        ContentCheckStatus.Unavailable => "the local model is not reachable",
        ContentCheckStatus.NoDescription => "the files of it are not described yet; sfo analyze describes them",
        ContentCheckStatus.NoCandidates => "no candidate folder",
        _ => c.Note ?? "the model gave no usable answer"
    };

    // ------------------------------------------------------------------ the model

    /// <summary>Asks the model what the upper folders are for. Only folder names, roles, counts, types, periods and patterns are sent.</summary>
    private async Task<LibraryModelPart> DescribeAsync(ScanJob job, LibraryMap map, string? model, bool refresh, CancellationToken ct)
    {
        var listed = map.Entries.Where(e => e.Depth <= _options.ModelMaxDepth).Take(_options.ModelMaxFolders).ToList();
        var facts = MapFacts(listed);
        var (status, record) = await _library.AskAsync(job.Id, MapTask, LibraryAdviceTask.Map, facts, model, refresh, ct);
        if (record is null) return new LibraryModelPart(status, null, null, 0);
        if (record.Error is not null) return new LibraryModelPart(AdviceStatus.Failed, record.Model, record.Error, 0);

        var known = listed.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var dropped = 0;
        if (!TryParseObject(record.Content, out var doc, out var error))
            return new LibraryModelPart(AdviceStatus.Failed, record.Model, error, 0);
        if (doc.TryGetProperty("purposes", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var path = Str(item, "path")?.Replace('\\', '/').Trim('/');
                var purpose = Str(item, "purpose");
                if (path is null || purpose is null || purpose.Length > 300 || !known.TryGetValue(path.Length == 0 ? "." : path, out var e))
                {
                    dropped++;
                    continue;
                }
                e.Purpose = purpose;
                e.PurposeSource = AdviceOrigin.Model;
            }
        }
        return new LibraryModelPart(status, record.Model, null, dropped);
    }

    public static string MapFacts(IEnumerable<LibraryMapEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("path | role | children_role | files | types | period | children named like | examples");
        foreach (var e in entries)
        {
            var period = e.NamePeriod is { } np ? $"name {LibraryMap.Day(np.From)}..{LibraryMap.Day(np.To)}"
                : e.From is { } f && e.To is { } t ? $"{f.Year}-{t.Year}" : "-";
            sb.Append(e.RelativePath).Append(" | ").Append(e.Role.ToCode()).Append(" | ").Append(e.ChildrenRole.ToCode() ?? "-")
                .Append(" | ").Append(e.Files.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(LibraryMap.Counts(e.Types) is { Length: > 0 } c ? c : "-")
                .Append(" | ").Append(period)
                .Append(" | ").Append(e.Pattern ?? "-")
                .Append(" | ").AppendLine(e.ChildrenPattern is { } p ? string.Join("; ", p.Examples) : "-");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Asks the model for the name of a new folder in the style of its siblings. It sees the pattern, the sibling
    /// names, counts, the period and the names of the source folders, never a file or its content. A name that does
    /// not fit (path characters, a sibling's name, missing year) is refused and the rule's name kept.
    /// </summary>
    private async Task<(NewFolderProposal Proposal, LibraryModelPart Part)> NameAsync(
        ScanJob source, LibraryMap map, MatchUnit unit, NewFolderProposal proposal, string? model, bool refresh, CancellationToken ct)
    {
        var parent = proposal.Parent;
        var facts = NameFacts(parent, unit, proposal);
        var (status, record) = await _library.AskAsync(source.Id, NameTask, LibraryAdviceTask.Name, facts, model, refresh, ct);
        if (record is null) return (proposal, new LibraryModelPart(status, null, null, 0));
        if (record.Error is not null) return (proposal, new LibraryModelPart(AdviceStatus.Failed, record.Model, record.Error, 0));
        if (!TryParseObject(record.Content, out var doc, out var error))
            return (proposal, new LibraryModelPart(AdviceStatus.Failed, record.Model, error, 0));

        var name = Str(doc, "name");
        var problem = CheckName(name, parent, unit, proposal.Pattern);
        if (problem is not null)
            return (proposal, new LibraryModelPart(AdviceStatus.Failed, record.Model, $"The model's name was not used: {problem}.", 1));

        var reason = proposal.Reason.Replace("; the occasion is unknown and stays to be filled in", string.Empty, StringComparison.Ordinal)
                         .TrimEnd('.')
                     + $"; name proposed by the local model from the neighbouring names{(Str(doc, "reason") is { } why ? $" ({why})" : string.Empty)}.";
        var destination = parent.Depth == 0 ? name! : $"{parent.RelativePath}/{name}";
        return (proposal with
        {
            Name = name!,
            NameSource = AdviceOrigin.Model,
            NameComplete = true,
            Destination = destination,
            Reason = reason
        }, new LibraryModelPart(status, record.Model, null, 0));
    }

    public static string NameFacts(LibraryMapEntry parent, MatchUnit unit, NewFolderProposal proposal)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Parent folder: {(parent.Depth == 0 ? "(top of the archive)" : parent.RelativePath)}");
        sb.AppendLine($"Naming pattern there: {proposal.Pattern ?? "-"}");
        sb.AppendLine($"Sibling folder names: {string.Join("; ", parent.Children.Select(c => c.Name).Take(12))}");
        sb.AppendLine($"What goes in: {LibraryMap.Counts(unit.Types)}");
        if (unit.From is { } f && unit.To is { } t)
            sb.AppendLine($"Taken: {LibraryMap.Day(f)} to {LibraryMap.Day(t)} ({unit.TimeSource.ToCode()})");
        sb.AppendLine($"Folders it came from: {string.Join("; ", unit.SourceFolders.Take(8))}");
        sb.AppendLine($"Name by the rules: {proposal.Name}");
        return sb.ToString();
    }

    private static string? CheckName(string? name, LibraryMapEntry parent, MatchUnit unit, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(name)) return "it is empty";
        if (name.Length > 80) return "it is longer than 80 characters";
        if (name.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|', '{', '}']) >= 0)
            return "it holds characters a folder name cannot have, or a placeholder";
        if (name.Trim() != name || name.EndsWith('.')) return "it starts or ends with a space or a dot";
        if (parent.Children.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            return "a folder of that name already exists there";
        if (pattern is not null && YearPlaceholderRegex().IsMatch(pattern) && unit.From is { } from
            && !name.Contains(from.Year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            return $"it lacks the year {from.Year} the pattern asks for";
        return null;
    }

    // ------------------------------------------------------------------ helpers

    private void ValidateOptions()
    {
        try
        {
            _options.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new LibraryServiceException(LibraryFailure.OptionsInvalid, ex.Message);
        }
    }

    private static bool TryParseObject(string? content, out JsonElement doc, out string? error)
    {
        doc = default;
        error = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            error = "The model returned no content.";
            return false;
        }
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            error = "The model's answer holds no JSON object.";
            return false;
        }
        try
        {
            using var parsed = JsonDocument.Parse(content[start..(end + 1)]);
            doc = parsed.RootElement.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"The model's answer is not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex(@"(?<!\d)(?:(?:19|20)\d{2}(?:[-_.]?(?:0[1-9]|1[0-2])(?:[-_.]?(?:0[1-9]|[12]\d|3[01]))?)?|(?:0?[1-9]|[12]\d|3[01])\.(?:0?[1-9]|1[0-2])\.(?:19|20)\d{2})(?!\d)")]
    private static partial Regex DatesRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"\{\s*(?:year|jahr)\s*\}", RegexOptions.IgnoreCase)]
    private static partial Regex YearPlaceholderRegex();
}
