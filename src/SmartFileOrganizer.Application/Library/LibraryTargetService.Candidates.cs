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
/// (https://github.com/crack666/smart_file_organizer/issues/95). A sensitive folder (marked itself or below a marked
/// folder, or holding a marked file) shows no file names, no subfolder names and no description.
/// </summary>
/// <param name="Description">The stored description (<c>folder_descriptions</c>); null when none is stored or the folder is sensitive.</param>
/// <param name="FileNames">A few names of its own files (direct files first), never of a marked file.</param>
/// <param name="Subfolders">Names of its subfolders, at most a few.</param>
public sealed record FolderFacts(
    ScanJob Library,
    LibraryMapEntry Entry,
    bool Sensitive,
    SensitiveMarker Markers,
    ContentDescription? Description,
    DateTime? DescribedAt,
    IReadOnlyList<string> FileNames,
    IReadOnlyList<string> Subfolders);

/// <summary>A candidate folder of the rules with what it holds.</summary>
public sealed record FolderCandidate(MatchCandidate Candidate, FolderFacts Folder);

/// <summary>Where the rules would put a new folder: below <see cref="Parent"/>, named after the pattern of its siblings.</summary>
public sealed record NewFolderHint(ScanJob Library, LibraryMapEntry Parent, string? Pattern, string Name, bool NameComplete);

/// <summary>The files of a unit, or one part of a mixed unit, with the candidates of the rules.</summary>
/// <param name="Index">Index of the part (as <c>sfo findings place --part</c> names it); null for the unit as a whole.</param>
/// <param name="Described">What the files show, from the stored description facts of all their unmarked files.</param>
/// <param name="NotMatched">Why the part got no candidates (more parts than <c>MaxUnitParts</c>); null otherwise.</param>
public sealed record CandidateGroup(
    int? Index,
    MatchUnit Unit,
    IReadOnlyList<FileNode> Files,
    string? Label,
    ContentDescription Described,
    IReadOnlyList<FolderCandidate> Candidates,
    NewFolderHint? NewFolder,
    string? NotMatched = null);

/// <summary>The candidates of one unit: none when it is excluded, one group when it is one whole, else one per part.</summary>
public sealed record UnitCandidates(string Key, MatchUnit Unit, string? Excluded, IReadOnlyList<CandidateGroup> Groups)
{
    public bool Split => Groups.Count > 1;
}

/// <summary>A library root: free space and its inbox folder.</summary>
public sealed record LibraryRootFacts(ScanJob Library, long? FreeBytes, string InboxFolder);

/// <summary>The answer of <see cref="LibraryTargetService.CandidatesAsync"/>.</summary>
public sealed record CandidateAnswer(ScanJob Source, IReadOnlyList<LibraryRootFacts> Libraries, IReadOnlyList<UnitCandidates> Units);

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
            maps.Add(new LibrarySide(library, map, await DirectoryMarkersAsync(library.Id, ct)));
        }
        var roots = libraries.Select(l => new LibraryRootFacts(l, _freeSpace?.AvailableBytes(l.RootPath), _options.InboxFolderOf(l.RootPath))).ToList();
        var folders = new Dictionary<(long, string), FolderFacts>();

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
        return new CandidateAnswer(source, roots, answers);

        string Label(IReadOnlyList<FileNode> files)
        {
            var described = Describe(files);
            return described.IsEmpty ? $"{files[0].Name}{(files.Count > 1 ? $" and {files.Count - 1} more" : string.Empty)} (not described)" : described.Gist();
        }

        // What the unmarked files show; a marked file never adds to a description.
        ContentDescription Describe(IReadOnlyList<FileNode> files) => ContentDescription.From(files
            .Where(f => SensitiveOutputGuard.GetMarkers(f) == SensitiveMarker.None)
            .Select(f => facts.TryGetValue(f.Id, out var x) ? x : (IReadOnlyList<FileFact>)[])
            .ToList());

        async Task<CandidateGroup> GroupAsync(int? index, MatchUnit unit, IReadOnlyList<FileNode> files, string? label, string? notMatched)
        {
            var described = Describe(files);
            if (notMatched is not null)
                return new CandidateGroup(index, unit, files, label, described, [], null, notMatched);

            var ranked = maps
                .SelectMany((m, order) => LibraryMatcher.Preselect(m.Map, unit, _options, words).Select(c => (Candidate: c, Side: m, Order: order)))
                .OrderByDescending(x => x.Candidate.Score).ThenBy(x => x.Order)
                .Take(Math.Max(0, query.Limit))
                .ToList();
            var candidates = new List<FolderCandidate>(ranked.Count);
            foreach (var (candidate, side, _) in ranked)
            {
                var key = (side.Library.Id, candidate.Target.RelativePath);
                if (!folders.TryGetValue(key, out var folder))
                    folders[key] = folder = await FolderFactsAsync(side.Library, side.Map, candidate.Target, side.Markers, query.Names, ct);
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
            return new CandidateGroup(index, unit, files, label, described, candidates, hint);
        }
    }

    /// <summary>The library job and its target map, from the scan alone (no model, nothing written).</summary>
    public async Task<(ScanJob Library, LibraryMap Map)> MapAsync(long libraryJobId, CancellationToken ct = default)
    {
        ValidateOptions();
        return await LoadMapAsync(libraryJobId, LibraryWords.Default, ct);
    }

    private sealed record LibrarySide(ScanJob Library, LibraryMap Map, IReadOnlyDictionary<string, SensitiveMarker> Markers);

    /// <summary>The stored markers of the marked folders of a library job, by full path.</summary>
    private async Task<IReadOnlyDictionary<string, SensitiveMarker>> DirectoryMarkersAsync(long libraryJobId, CancellationToken ct) =>
        (await _reads.GetDirectoriesAsync(libraryJobId, ct))
        .Where(d => d.SensitiveMarkers != SensitiveMarker.None)
        .ToDictionary(d => d.FullPath, d => d.SensitiveMarkers, PathComparer);

    /// <summary>
    /// What a library folder holds, from the scan: whether it is sensitive, a few names of its own unmarked files and of
    /// its subfolders, and its stored description. A sensitive folder gives no names and no description.
    /// </summary>
    private async Task<FolderFacts> FolderFactsAsync(
        ScanJob library, LibraryMap map, LibraryMapEntry entry, IReadOnlyDictionary<string, SensitiveMarker> dirMarkers, int names, CancellationToken ct)
    {
        var markers = SensitiveMarker.None;
        for (var n = entry.Node; n is not null; n = n.Parent)
            markers |= dirMarkers.GetValueOrDefault(n.FullPath);
        foreach (var s in map.Tree.SensitiveFiles.Where(s => s.Dir == entry.Node))
            markers |= s.Markers;

        // Direct files first: every marker counts, names only of unmarked files.
        var fileNames = new List<string>();
        await foreach (var row in _reads.StreamFilesAsync(new FileListFilter { JobId = library.Id, Directory = entry.FullPath }, ct))
        {
            var fileMarkers = SensitiveOutputGuard.GetMarkers(row.File);
            markers |= fileMarkers;
            if (fileMarkers == SensitiveMarker.None && fileNames.Count < names)
                fileNames.Add(row.File.Name);
        }
        // A folder that only holds folders: names from below, never of a marked file.
        if (fileNames.Count < names && entry.Children.Count > 0 && markers == SensitiveMarker.None)
        {
            var prefix = entry.FullPath.EndsWith(Path.DirectorySeparatorChar) ? entry.FullPath : entry.FullPath + Path.DirectorySeparatorChar;
            await foreach (var row in _reads.StreamFilesAsync(new FileListFilter
                           {
                               JobId = library.Id, Directory = entry.FullPath, Recursive = true, DirectoryPrefix = prefix
                           }, ct))
            {
                if (fileNames.Count >= names) break;
                if (string.Equals(row.File.ParentPath, entry.FullPath, PathComparison)) continue;
                if (SensitiveOutputGuard.GetMarkers(row.File) != SensitiveMarker.None || dirMarkers.ContainsKey(row.File.ParentPath)) continue;
                fileNames.Add(Path.GetRelativePath(entry.FullPath, row.File.FullPath).Replace('\\', '/'));
            }
        }

        var sensitive = markers != SensitiveMarker.None;
        ContentDescription? description = null;
        DateTime? describedAt = null;
        if (!sensitive && _folderDescriptions is not null
            && await _folderDescriptions.GetAsync(library.Id, entry.RelativePath, FolderDescriptionSource.Sample, ct) is { } stored)
        {
            description = ContentDescription.FromJson(stored.FactsJson);
            describedAt = stored.DescribedAt;
        }
        return new FolderFacts(library, entry, sensitive, markers, description, describedAt,
            sensitive ? [] : fileNames,
            sensitive ? [] : entry.Children.Select(c => c.Name).Take(Math.Max(names, 1) * 2).ToList());
    }

    /// <summary>
    /// One folder of a library as the scan holds it (<c>sfo library folder</c>, Issue 95): its facts, its subfolders and a
    /// page of its own files. No model. A marked file is listed with path and markers only; a sensitive folder lists none of
    /// its other files by name.
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
            // In a sensitive folder only the marked files are listed (path and markers); the others are counted.
            if (facts.Sensitive && SensitiveOutputGuard.GetMarkers(row.File) == SensitiveMarker.None) continue;
            files.Add(row.File);
        }
        limit = Math.Clamp(limit, 1, JobReadService.MaxLimit);
        offset = Math.Max(0, offset);
        var page = files.Skip(offset).Take(limit).ToList();
        int? next = offset + page.Count < files.Count ? offset + page.Count : null;
        return new FolderDetail(facts, facts.Sensitive ? [] : entry.Children, page, files.Count, limit, offset, next);
    }

    /// <summary>
    /// Describes one library folder with the local model, and only this one (<c>sfo library describe</c>, Issue 95): a small
    /// sample of its own files, stored in <c>folder_descriptions</c>. A sensitive folder is not described. No other folder
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
