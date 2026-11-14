using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Library;

/// <summary>One group of source files to find candidate folders for: the files of a finding.</summary>
/// <param name="FolderPath">A named folder or device copy the files form (full path); it then keeps its name. Null for loose files.</param>
public sealed record CandidateUnit(string Key, IReadOnlyList<FileNode> Files, string? FolderPath);

/// <summary>What <see cref="LibraryTargetService.CandidatesAsync"/> asks for.</summary>
/// <param name="Limit">Candidate folders per unit or part, best first.</param>
/// <param name="Names">File names listed per candidate folder.</param>
public sealed record CandidateQuery(long SourceJobId, IReadOnlyList<long> Libraries, IReadOnlyList<CandidateUnit> Units, int Limit, int Names);

/// <summary>
/// Facts about one library folder, read from the scan and the stored folder descriptions, never from a model
/// (https://github.com/crack666/smart_file_organizer/issues/95). A sensitive folder is marked itself, lies below a marked
/// folder or holds a marked file; the markers are a hint (https://github.com/crack666/smart_file_organizer/issues/99).
/// Only with <c>Sensitive:Disclosure</c> <c>restricted</c> does such a folder show no file names, no subfolder names and
/// no description (<see cref="Withheld"/>); that was the behaviour for every sensitive folder before 14.11.2026.
/// </summary>
/// <param name="Description">The stored description (<c>folder_descriptions</c>); null when none is stored or <paramref name="Withheld"/>.</param>
/// <param name="FileNames">A few names of its own files (direct files first); with <c>restricted</c> never of a marked file.</param>
/// <param name="Subfolders">Names of its subfolders, at most a few.</param>
/// <param name="Withheld">Names, subfolders, purpose and description are withheld (<c>restricted</c> and sensitive).</param>
/// <param name="MarkedFiles">
/// Its own marked files with their markers (path relative to the library root, at most <see cref="LibraryTargetService.MaxMarkedListed"/>),
/// the hint where something important lies; empty when <paramref name="Withheld"/>.
/// </param>
/// <param name="Origin">
/// Kind and origin of the videos in the folder and below, from the stored facts of the library scan (Issue 103); null
/// when it holds no video with a kind or when <paramref name="Withheld"/>.
/// </param>
public sealed record FolderFacts(
    ScanJob Library,
    LibraryMapEntry Entry,
    bool Sensitive,
    SensitiveMarker Markers,
    ContentDescription? Description,
    DateTime? DescribedAt,
    IReadOnlyList<string> FileNames,
    IReadOnlyList<string> Subfolders,
    bool Withheld = false,
    IReadOnlyList<SensitiveFileRef>? MarkedFiles = null,
    Media.VideoOriginMix? Origin = null);

/// <summary>A candidate folder of the rules with what it holds.</summary>
public sealed record FolderCandidate(MatchCandidate Candidate, FolderFacts Folder);

/// <summary>Where the rules would put a new folder: below <see cref="Parent"/>, named after the pattern of its siblings.</summary>
public sealed record NewFolderHint(ScanJob Library, LibraryMapEntry Parent, string? Pattern, string Name, bool NameComplete);

/// <summary>The files of a unit, or one part of a mixed unit, with the candidates of the rules.</summary>
/// <param name="Index">Index of the part (as <c>sfo findings place --part</c> names it); null for the unit as a whole.</param>
/// <param name="Described">What the files show, from the stored description facts of all their unmarked files.</param>
/// <param name="NotMatched">Why the part got no candidates (more parts than <c>MaxUnitParts</c>); null otherwise.</param>
/// <param name="Weak">
/// Candidates that fit by period and kind of files only (<see cref="LibraryTargetService.IsWeak"/>), best first, kept
/// apart from <see cref="Candidates"/>: for loose files they name the same few folders again and again (Issue 97).
/// </param>
/// <param name="Origin">Kind and origin of its videos, from the stored facts as the disclosure shows them (Issue 103).</param>
public sealed record CandidateGroup(
    int? Index,
    MatchUnit Unit,
    IReadOnlyList<FileNode> Files,
    string? Label,
    ContentDescription Described,
    IReadOnlyList<FolderCandidate> Candidates,
    NewFolderHint? NewFolder,
    string? NotMatched = null,
    IReadOnlyList<WeakCandidate>? Weak = null,
    Media.VideoOriginMix? Origin = null);

/// <summary>A candidate of the rules that fits by period and kind of files only, with its library.</summary>
public sealed record WeakCandidate(MatchCandidate Candidate, ScanJob Library);

/// <summary>The candidates of one unit: none when it is excluded, one group when it is one whole, else one per part.</summary>
public sealed record UnitCandidates(string Key, MatchUnit Unit, string? Excluded, IReadOnlyList<CandidateGroup> Groups)
{
    public bool Split => Groups.Count > 1;
}

/// <summary>A library root: free space, its inbox folder and its top level.</summary>
/// <param name="Root">The root folder; when it is withheld (<see cref="FolderFacts.Withheld"/>), <paramref name="TopLevel"/> is empty, as in <c>sfo library folder</c>.</param>
/// <param name="TopLevel">The folders directly below the root (Issue 97), so a controller sees the order of the library at once.</param>
/// <param name="Hints">What a person wrote about the library in its hints file (Issue 103); null when there is none.</param>
public sealed record LibraryRootFacts(ScanJob Library, long? FreeBytes, string InboxFolder, FolderFacts? Root = null, IReadOnlyList<FolderFacts>? TopLevel = null,
    LibraryHints? Hints = null);

/// <summary>The answer of <see cref="LibraryTargetService.CandidatesAsync"/>.</summary>
/// <param name="Marked">The marked files of the units: marked themselves or lying in a marked folder of the source.</param>
public sealed record CandidateAnswer(
    ScanJob Source, IReadOnlyList<LibraryRootFacts> Libraries, IReadOnlyList<UnitCandidates> Units, IReadOnlyDictionary<long, SensitiveMarker> Marked);

/// <summary>One library folder in detail: its facts, its subfolders and one page of its own files.</summary>
public sealed record FolderDetail(
    FolderFacts Facts,
    IReadOnlyList<LibraryMapEntry> Subfolders,
    IReadOnlyList<FileNode> Files,
    int TotalFiles,
    int Limit,
    int Offset,
    int? NextOffset);

/// <summary>The answer of <see cref="LibraryTargetService.DescribeFolderAsync"/>.</summary>
/// <param name="Refused">Why nothing was described (a sensitive folder, nothing to describe); null when described.</param>
/// <param name="ModelUnavailable">The local model could not be reached; nothing was described.</param>
public sealed record FolderDescribeAnswer(FolderFacts Folder, FolderDescription? Description, string? Refused, bool ModelUnavailable = false);

public sealed partial class LibraryTargetService
{
    // ------------------------------------------------------------------ facts for a controlling model (Issue 95)

    /// <summary>
    /// The facts a controlling model decides from (https://github.com/crack666/smart_file_organizer/issues/95): for each
    /// unit, what it is and the candidate folders of the rules (time, names, kinds of files, the same files already there)
    /// in every library, with what those folders hold and their stored description. No model is asked and nothing is
    /// described; only the patterns file next to the database may be written, as in <c>library match</c>.
    /// </summary>
    /// <exception cref="LibraryServiceException">A library is missing, of the wrong role, or cannot be read.</exception>
    public async Task<CandidateAnswer> CandidatesAsync(CandidateQuery query, CancellationToken ct = default)
    {
        ValidateOptions();
        var source = await _jobs.GetByIdAsync(query.SourceJobId, ct)
                     ?? throw new LibraryServiceException(LibraryFailure.JobNotFound, $"Job {query.SourceJobId} not found.");
        var libraries = new List<ScanJob>();
        foreach (var id in query.Libraries.Distinct())
        {
            var job = await _jobs.GetByIdAsync(id, ct) ?? throw new LibraryServiceException(LibraryFailure.JobNotFound, $"Job {id} not found.");
            if (job.Role != ScanRole.Library)
                throw new LibraryServiceException(LibraryFailure.WrongRole, $"Job {id} is a {job.Role.ToValue()}, not a library.");
            libraries.Add(job);
        }
        EnsureReadable(libraries);

        var words = LibraryWords.Default;
        var (set, _) = await LibraryComparisonSet.LoadAsync(_comparisons, source.Id, libraries.Select(l => l.Id).ToList(), ct);
        var captures = await _captures.GetAsync(source.Id, ct);
        var facts = _facts is null ? new Dictionary<long, IReadOnlyList<FileFact>>() : await _facts.GetByJobAsync(source.Id, ct);
        var maps = new List<LibrarySide>();
        foreach (var library in libraries)
        {
            var (_, map) = await LoadMapAsync(library.Id, words, ct);
            await PatternsAsync(library, map, reset: false, ct);
            maps.Add(new LibrarySide(library, map, await DirectoryMarkersAsync(library.Id, ct), await LibraryFactsAsync(library, ct)));
        }
        var folders = new Dictionary<(long, string), FolderFacts>();
        var roots = new List<LibraryRootFacts>(maps.Count);
        foreach (var side in maps)
        {
            // The top level once per library instead of the same broad folders as a candidate of nearly every unit.
            var root = await FolderFactsAsync(side.Library, side.Map, side.Map.Root, side.Markers, 0, ct, side.Facts);
            var top = new List<FolderFacts>();
            if (!root.Withheld)
            {
                foreach (var child in side.Map.Root.Children)
                    top.Add(await FolderFactsAsync(side.Library, side.Map, child, side.Markers, 0, ct, side.Facts));
            }
            roots.Add(new LibraryRootFacts(side.Library, _freeSpace?.AvailableBytes(side.Library.RootPath),
                _options.InboxFolderOf(side.Library.RootPath), root, top,
                LibraryHints.Read(_options.HintsFileOf(side.Library.RootPath), _options.MaxHintChars)));
        }

        // A file counts as marked when it is marked itself or lies in a marked folder of the source. The disclosure decides
        // whether its name and its description facts are shown (https://github.com/crack666/smart_file_organizer/issues/99).
        var sourceDirMarkers = await DirectoryMarkersAsync(source.Id, ct);
        var marked = new Dictionary<long, SensitiveMarker>();
        foreach (var file in query.Units.SelectMany(u => u.Files))
        {
            var m = SensitiveOutputGuard.GetMarkers(file);
            for (var dir = file.ParentPath; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                m |= sourceDirMarkers.GetValueOrDefault(dir);
            if (m != SensitiveMarker.None) marked[file.Id] = m;
        }

        var answers = new List<UnitCandidates>(query.Units.Count);
        foreach (var unitQuery in query.Units)
        {
            ct.ThrowIfCancellationRequested();
            var (files, unitBase) = await LoadUnitAsync(source, new LibraryMatchQuery
            {
                SourceJobId = source.Id, Files = unitQuery.Files, Path = unitQuery.FolderPath
            }, ct);
            var unit = BuildUnit(source, files, unitBase, captures, set?.Files, words).Unit;
            var excluded = _options.MatchUnclearWorth ? null : await ExcludedAsync(source, files, ct);
            if (excluded is not null)
            {
                answers.Add(new UnitCandidates(unitQuery.Key, unit, excluded, []));
                continue;
            }

            var split = !unit.KeepWhole && files.Count >= 2 ? await SplitAsync(source, files, ct) : [files];
            var groups = new List<CandidateGroup>();
            if (split.Count < 2)
            {
                groups.Add(await GroupAsync(null, unit, files, null, null));
            }
            else
            {
                var matched = split.OrderByDescending(p => p.Count).Take(_options.MaxUnitParts).ToHashSet();
                for (var i = 0; i < split.Count; i++)
                {
                    var part = split[i];
                    var partUnit = BuildUnit(source, part, new UnitBase(MatchUnitKind.File, part[0].FullPath, null, null), captures, set?.Files, words).Unit;
                    groups.Add(await GroupAsync(i, partUnit, part, Label(part),
                        matched.Contains(part) ? null : $"Not listed: the files show more than {_options.MaxUnitParts} different things; only the largest parts get candidates."));
                }
            }
            answers.Add(new UnitCandidates(unitQuery.Key, unit, null, groups));
        }
        return new CandidateAnswer(source, roots, answers, marked);

        string Label(IReadOnlyList<FileNode> files)
        {
            var described = Describe(files);
            if (!described.IsEmpty) return described.Gist();
            if (Media.VideoOriginMix.From(Shown(files)) is { } origin) return origin.Gist();
            var named = files.FirstOrDefault(f => _disclosure.ShowsStructure(marked.GetValueOrDefault(f.Id)));
            return named is null
                ? $"{files.Count} marked file(s) (not described)"
                : $"{named.Name}{(files.Count > 1 ? $" and {files.Count - 1} more" : string.Empty)} (not described)";
        }

        // What the files show. A marked file adds what the disclosure shows of it: with open all its facts, for access data
        // only the short ones (no free text); with restricted nothing, as before 14.11.2026.
        ContentDescription Describe(IReadOnlyList<FileNode> files) => ContentDescription.From(Shown(files));

        List<IReadOnlyList<FileFact>> Shown(IReadOnlyList<FileNode> files) => files
            .Where(f => _disclosure.ShowsClassification(marked.GetValueOrDefault(f.Id)))
            .Select(f => _disclosure.ShownFacts(marked.GetValueOrDefault(f.Id), facts.TryGetValue(f.Id, out var x) ? x : []))
            .ToList();

        async Task<CandidateGroup> GroupAsync(int? index, MatchUnit unit, IReadOnlyList<FileNode> files, string? label, string? notMatched)
        {
            var described = Describe(files);
            var origin = Media.VideoOriginMix.From(Shown(files));
            if (notMatched is not null)
                return new CandidateGroup(index, unit, files, label, described, [], null, notMatched, Origin: origin);

            var all = maps
                .SelectMany((m, order) => LibraryMatcher.Preselect(m.Map, unit, _options, words).Select(c => (Candidate: c, Side: m, Order: order)))
                .OrderByDescending(x => x.Candidate.Score).ThenBy(x => x.Order)
                .ToList();
            var limit = Math.Max(0, query.Limit);
            var ranked = all.Where(x => !IsWeak(x.Candidate)).Take(limit).ToList();
            var weak = all.Where(x => IsWeak(x.Candidate)).Take(limit).Select(x => new WeakCandidate(x.Candidate, x.Side.Library)).ToList();
            var candidates = new List<FolderCandidate>(ranked.Count);
            foreach (var (candidate, side, _) in ranked)
            {
                var key = (side.Library.Id, candidate.Target.RelativePath);
                if (!folders.TryGetValue(key, out var folder))
                    folders[key] = folder = await FolderFactsAsync(side.Library, side.Map, candidate.Target, side.Markers, query.Names, ct, side.Facts);
                candidates.Add(new FolderCandidate(candidate, folder));
            }

            // Where the rules would put a new folder: the library that holds this kind of content, the first one named on a tie.
            NewFolderHint? hint = null;
            var best = -1.0;
            foreach (var side in maps)
            {
                var result = LibraryMatcher.Match(side.Map, unit, _options, 1, words);
                if (result.NewFolder is not { } proposal) continue;
                var share = Math.Round(LibraryMatcher.KindShare(side.Map, unit), 2);
                if (share <= best) continue;
                best = share;
                hint = new NewFolderHint(side.Library, proposal.Parent, proposal.Pattern, proposal.Name, proposal.NameComplete);
            }
            return new CandidateGroup(index, unit, files, label, described, candidates, hint, Weak: weak, Origin: origin);
        }
    }

    /// <summary>
    /// A candidate that agrees by period and kind of files only (Issue 97): no name, no siblings, no same files there and
    /// no content fit, and its period is not a specific one. For loose material such a candidate is noise; it names a
    /// broad folder of the right years, the same few for nearly every unit. A folder whose own period is short
    /// (<see cref="SpecificPeriodDays"/>, an occasion or a year) and holds the times of the files exactly (a strong period
    /// signal) is no such folder: there the period does say something.
    /// </summary>
    public static bool IsWeak(MatchCandidate candidate)
    {
        if (candidate.Signals.Any(s => s.Agrees && s.Signal is not (MatchSignal.Period or MatchSignal.FileType)))
            return false;
        var period = candidate.Signals.FirstOrDefault(s => s.Signal == MatchSignal.Period && s.Agrees);
        return period is not { Strong: true } || SpanDays(candidate.Target) is not <= SpecificPeriodDays;
    }

    /// <summary>A folder period up to this many days (an occasion, a month, a year) is specific enough to count alone.</summary>
    public const double SpecificPeriodDays = 366;

    private static double? SpanDays(LibraryMapEntry e) =>
        e.From is { } from && e.To is { } to ? (to - from).TotalDays
        : e.NamePeriod is { } np ? (np.To - np.From).TotalDays
        : null;

    /// <summary>The library job and its target map, from the scan alone (no model, nothing written).</summary>
    public async Task<(ScanJob Library, LibraryMap Map)> MapAsync(long libraryJobId, CancellationToken ct = default)
    {
        ValidateOptions();
        return await LoadMapAsync(libraryJobId, LibraryWords.Default, ct);
    }

    private sealed record LibrarySide(ScanJob Library, LibraryMap Map, IReadOnlyDictionary<string, SensitiveMarker> Markers,
        IReadOnlyDictionary<long, IReadOnlyList<FileFact>> Facts);

    /// <summary>The stored facts of the videos of a library job, by file id, once per call (for the origin of its folders).</summary>
    private async Task<IReadOnlyDictionary<long, IReadOnlyList<FileFact>>> LibraryFactsAsync(ScanJob library, CancellationToken ct) =>
        _facts is null ? new Dictionary<long, IReadOnlyList<FileFact>>() : await _facts.GetByJobAsync(library.Id, ct);

    /// <summary>
    /// Kind and origin of the videos in a folder and below, from the library's stored facts. A marked file adds what the
    /// disclosure shows of it.
    /// </summary>
    private Media.VideoOriginMix? FolderOrigin(LibraryMap map, LibraryMapEntry entry, IReadOnlyDictionary<long, IReadOnlyList<FileFact>> facts)
    {
        if (facts.Count == 0) return null;
        var markedFiles = new Dictionary<long, SensitiveMarker>();
        foreach (var s in map.Tree.SensitiveFiles)
            markedFiles[s.File.Id] = markedFiles.GetValueOrDefault(s.File.Id) | s.Markers;
        var shown = new List<IReadOnlyList<FileFact>>();
        foreach (var node in entry.Node.Descendants().Prepend(entry.Node))
        {
            foreach (var file in node.Files)
            {
                if (file.FileType != (int)FileType.Video || !facts.TryGetValue(file.Id, out var list)) continue;
                var markers = markedFiles.GetValueOrDefault(file.Id);
                if (_disclosure.ShowsClassification(markers))
                    shown.Add(_disclosure.ShownFacts(markers, list));
            }
        }
        return Media.VideoOriginMix.From(shown);
    }

    /// <summary>The stored markers of the marked folders of a library job, by full path.</summary>
    private async Task<IReadOnlyDictionary<string, SensitiveMarker>> DirectoryMarkersAsync(long libraryJobId, CancellationToken ct) =>
        (await _reads.GetDirectoriesAsync(libraryJobId, ct))
        .Where(d => d.SensitiveMarkers != SensitiveMarker.None)
        .GroupBy(d => d.FullPath, PathKey.Comparer)
        .ToDictionary(g => g.Key, g => g.Aggregate(SensitiveMarker.None, (m, d) => m | d.SensitiveMarkers), PathKey.Comparer);

    /// <summary>
    /// What a library folder holds, from the scan: whether it is sensitive, a few names of its own files and of its
    /// subfolders, and its stored description. With <c>restricted</c> a sensitive folder gives no names and no description,
    /// and no marked file is named.
    /// </summary>
    private async Task<FolderFacts> FolderFactsAsync(
        ScanJob library, LibraryMap map, LibraryMapEntry entry, IReadOnlyDictionary<string, SensitiveMarker> dirMarkers, int names, CancellationToken ct,
        IReadOnlyDictionary<long, IReadOnlyList<FileFact>>? libraryFacts = null)
    {
        var markers = SensitiveMarker.None;
        for (var n = entry.Node; n is not null; n = n.Parent)
            markers |= dirMarkers.GetValueOrDefault(n.FullPath);
        foreach (var s in map.Tree.SensitiveFiles.Where(s => s.Dir == entry.Node))
            markers |= s.Markers;

        // Direct files first: every marker counts; the disclosure decides whose names are listed.
        var fileNames = new List<string>();
        var markedFiles = new List<SensitiveFileRef>();
        await foreach (var row in _reads.StreamFilesAsync(new FileListFilter { JobId = library.Id, Directory = entry.FullPath }, ct))
        {
            var fileMarkers = SensitiveOutputGuard.GetMarkers(row.File);
            markers |= fileMarkers;
            if (fileMarkers != SensitiveMarker.None && markedFiles.Count < MaxMarkedListed)
                markedFiles.Add(new SensitiveFileRef(Path.GetRelativePath(library.RootPath, row.File.FullPath).Replace('\\', '/'), fileMarkers));
            if (_disclosure.ShowsStructure(fileMarkers) && fileNames.Count < names)
                fileNames.Add(row.File.Name);
        }
        // A folder that only holds folders: names from below.
        if (fileNames.Count < names && entry.Children.Count > 0 && _disclosure.ShowsStructure(markers))
        {
            var prefix = entry.FullPath.EndsWith(Path.DirectorySeparatorChar) ? entry.FullPath : entry.FullPath + Path.DirectorySeparatorChar;
            await foreach (var row in _reads.StreamFilesAsync(new FileListFilter
                           {
                               JobId = library.Id, Directory = entry.FullPath, Recursive = true, DirectoryPrefix = prefix
                           }, ct))
            {
                if (fileNames.Count >= names) break;
                if (string.Equals(row.File.ParentPath, entry.FullPath, PathComparison)) continue;
                if (!_disclosure.ShowsStructure(SensitiveOutputGuard.GetMarkers(row.File) | dirMarkers.GetValueOrDefault(row.File.ParentPath))) continue;
                fileNames.Add(Path.GetRelativePath(entry.FullPath, row.File.FullPath).Replace('\\', '/'));
            }
        }

        var sensitive = markers != SensitiveMarker.None;
        var withheld = !_disclosure.ShowsStructure(markers);
        ContentDescription? description = null;
        DateTime? describedAt = null;
        if (!withheld && _folderDescriptions is not null
            && await _folderDescriptions.GetAsync(library.Id, entry.RelativePath, FolderDescriptionSource.Sample, ct) is { } stored)
        {
            description = ContentDescription.FromJson(stored.FactsJson);
            describedAt = stored.DescribedAt;
        }
        return new FolderFacts(library, entry, sensitive, markers, description, describedAt,
            withheld ? [] : fileNames,
            withheld ? [] : entry.Children.Select(c => c.Name).Take(Math.Max(names, 1) * 2).ToList(),
            withheld,
            withheld ? [] : markedFiles,
            withheld ? null : FolderOrigin(map, entry, libraryFacts ?? await LibraryFactsAsync(library, ct)));
    }

    /// <summary>Marked files listed per folder as a hint (<see cref="FolderFacts.MarkedFiles"/>).</summary>
    public const int MaxMarkedListed = 20;

    /// <summary>
    /// One folder of a library as the scan holds it (<c>sfo library folder</c>, Issue 95): its facts, its subfolders and a
    /// page of its own files. No model. A marked file is listed with its markers; with <c>restricted</c> a sensitive folder
    /// lists none of its other files by name and none of its subfolders.
    /// </summary>
    public async Task<FolderDetail> FolderAsync(long libraryJobId, string path, int limit, int offset, CancellationToken ct = default)
    {
        ValidateOptions();
        var (job, map) = await LoadMapAsync(libraryJobId, LibraryWords.Default, ct);
        var entry = Find(job, map, path)
                    ?? throw new LibraryServiceException(LibraryFailure.PathNotFound, $"'{path}' is no folder of library job {job.Id}.");
        var markers = await DirectoryMarkersAsync(job.Id, ct);
        var facts = await FolderFactsAsync(job, map, entry, markers, 5, ct);

        var files = new List<FileNode>();
        await foreach (var row in _reads.StreamFilesAsync(new FileListFilter { JobId = job.Id, Directory = entry.FullPath }, ct))
        {
            // Restricted: in a sensitive folder only the marked files are listed (path and markers); the others are counted.
            if (facts.Withheld && SensitiveOutputGuard.GetMarkers(row.File) == SensitiveMarker.None) continue;
            files.Add(row.File);
        }
        limit = Math.Clamp(limit, 1, JobReadService.MaxLimit);
        offset = Math.Max(0, offset);
        var page = files.Skip(offset).Take(limit).ToList();
        int? next = offset + page.Count < files.Count ? offset + page.Count : null;
        return new FolderDetail(facts, facts.Withheld ? [] : entry.Children, page, files.Count, limit, offset, next);
    }

    /// <summary>
    /// Describes one library folder with the local model, and only this one (<c>sfo library describe</c>, Issue 95): a small
    /// sample of its own files, stored in <c>folder_descriptions</c>. A sensitive folder is not described, whatever
    /// <c>Sensitive:Disclosure</c> says: the setting is about the output, not about what goes to the model. No other folder
    /// is touched.
    /// </summary>
    public async Task<FolderDescribeAnswer> DescribeFolderAsync(long libraryJobId, string path, bool refresh, CancellationToken ct = default)
    {
        ValidateOptions();
        var (job, map) = await LoadMapAsync(libraryJobId, LibraryWords.Default, ct);
        EnsureReadable([job]);
        var entry = Find(job, map, path)
                    ?? throw new LibraryServiceException(LibraryFailure.PathNotFound, $"'{path}' is no folder of library job {job.Id}.");
        var facts = await FolderFactsAsync(job, map, entry, await DirectoryMarkersAsync(job.Id, ct), 0, ct);
        if (facts.Sensitive)
            return new FolderDescribeAnswer(facts, null, "The folder is sensitive (it, a folder above it or one of its files is marked); it is not described.");
        var source = _descriptionSources.FirstOrDefault(s => s.Kind == FolderDescriptionSource.Sample) ?? _descriptionSources.FirstOrDefault();
        if (source is null)
            return new FolderDescribeAnswer(facts, null, "No description source is configured.");
        if ((refresh || facts.Description is null) && _ollama is not null && !await _ollama.IsAvailableAsync(ct))
            return new FolderDescribeAnswer(facts, null, "The local model is not reachable; nothing was described.", ModelUnavailable: true);
        var described = await source.DescribeAsync(job, entry, new FolderDescribeRequest(UseModel: true, refresh, _options.FolderSampleSize), ct);
        if (described is not { Description.IsEmpty: false })
            return new FolderDescribeAnswer(facts, described, "Nothing in the folder could be described (no pictures or documents, or the model gave no answer).");
        return new FolderDescribeAnswer(facts with { Description = described.Description, DescribedAt = DateTime.UtcNow }, described, null);
    }
}
