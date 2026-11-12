using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Bundles;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Findings;

/// <summary>Why findings could not be read or decided.</summary>
public enum FindingFailure
{
    /// <summary>No finding with this key in the job (any more).</summary>
    NotFound,

    /// <summary>Findings are about sources; a library is only read.</summary>
    WrongRole
}

public sealed class FindingException(FindingFailure failure, string message) : Exception(message)
{
    public FindingFailure Failure { get; } = failure;
}

/// <summary>Filter of a findings listing.</summary>
public sealed record FindingQuery
{
    public long JobId { get; init; }

    /// <summary>Only these kinds; empty for all.</summary>
    public IReadOnlyCollection<FindingKind> Kinds { get; init; } = [];

    /// <summary>Only findings about files or folders at or below this path (full, or relative to the job root).</summary>
    public string? Under { get; init; }

    /// <summary>Only findings at least this certain (<c>likely</c> keeps certain and likely).</summary>
    public ResultCertainty? MinCertainty { get; init; }

    /// <summary>Only findings in these states; empty for open and accepted (rejected and wrong ones are not listed again).</summary>
    public IReadOnlyCollection<FindingState> States { get; init; } = [];
}

/// <summary>Counts of one kind.</summary>
public sealed record FindingKindTotals(long Findings, long Sure, long Unsure, long Files, long Bytes);

/// <summary>One finding in a line, for the summary.</summary>
public sealed record FindingBrief(
    int Rank,
    string Key,
    FindingKind Kind,
    ResultCertainty Certainty,
    FindingState State,
    string Title,
    string? RelativePath,
    long Files,
    long Bytes,
    PlanActionKind Action);

/// <summary>Everything about the findings of a job that fits one page.</summary>
public sealed record FindingSummary(
    ScanJob Job,
    FindingInputsInfo Inputs,
    IReadOnlyDictionary<FindingKind, KindCoverage> Coverage,
    IReadOnlyList<string> MissingSteps,
    long Listed,
    long Sure,
    long Unsure,
    IReadOnlyDictionary<FindingKind, FindingKindTotals> ByKind,
    IReadOnlyDictionary<ResultCertainty, long> ByCertainty,
    IReadOnlyDictionary<FindingState, long> ByState,
    IReadOnlyList<FindingBrief> Top,
    IReadOnlyDictionary<FindingKind, long> BelowThreshold);

/// <summary>
/// Computes the findings of a source job from what the earlier steps stored (scan, analysis, comparison, bundles,
/// library map), and stores what a person or agent decides about them
/// (https://github.com/crack666/smart_file_organizer/issues/26). Reads no scanned file, calls no model, changes
/// nothing on disk: accept, reject and wrong only write a row in <c>finding_decisions</c> (and, for wrong, a test case).
/// </summary>
public sealed class FindingService
{
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IScanJobRepository _jobs;
    private readonly IJobReadRepository _read;
    private readonly IBundleRepository _bundles;
    private readonly ICaptureInfoSource _captures;
    private readonly IContentComparisonRepository _comparisons;
    private readonly IFindingDecisionRepository _decisions;
    private readonly IEvalCaseRepository _evalCases;
    private readonly IFindingTargetAdvisor _advisor;
    private readonly FindingOptions _options;
    private readonly BundleOptions _bundleOptions;
    private readonly ILogger<FindingService> _logger;

    public FindingService(
        IScanJobRepository jobs,
        IJobReadRepository read,
        IBundleRepository bundles,
        ICaptureInfoSource captures,
        IContentComparisonRepository comparisons,
        IFindingDecisionRepository decisions,
        IEvalCaseRepository evalCases,
        IFindingTargetAdvisor advisor,
        FindingOptions options,
        BundleOptions bundleOptions,
        ILogger<FindingService> logger)
    {
        _jobs = jobs;
        _read = read;
        _bundles = bundles;
        _captures = captures;
        _comparisons = comparisons;
        _decisions = decisions;
        _evalCases = evalCases;
        _advisor = advisor;
        _options = options;
        _bundleOptions = bundleOptions;
        _logger = logger;
    }

    // ------------------------------------------------------------------ reading

    /// <summary>All findings of the job, ranked, with their stored decisions (rejected and wrong ones included).</summary>
    public async Task<FindingReport> ComputeAsync(long jobId, CancellationToken ct = default) =>
        (await ComputeInternalAsync(jobId, ct)).Report;

    /// <summary>The findings a listing shows: filtered by <paramref name="query"/>, in rank order.</summary>
    public async Task<(FindingReport Report, IReadOnlyList<Finding> Items)> ListAsync(FindingQuery query, CancellationToken ct = default)
    {
        var report = await ComputeAsync(query.JobId, ct);
        var job = report.Job;
        var under = query.Under is { Length: > 0 } u ? FullPathOf(job, u) : null;
        var states = query.States.Count > 0 ? query.States : [FindingState.Open, FindingState.Accepted];
        var items = report.Findings
            .Where(f => states.Contains(f.State))
            .Where(f => query.Kinds.Count == 0 || query.Kinds.Contains(f.Kind))
            .Where(f => query.MinCertainty is not { } min || (int)f.Certainty <= (int)min)
            .Where(f => under is null || f.Directories.Any(d => IsAtOrBelow(d, under)))
            .ToList();
        return (report, items);
    }

    /// <summary>Counts and the most important findings, small enough for one page however large the job.</summary>
    public async Task<FindingSummary> SummarizeAsync(long jobId, int top, CancellationToken ct = default)
    {
        var report = await ComputeAsync(jobId, ct);
        return Summarize(report, top);
    }

    public static FindingSummary Summarize(FindingReport report, int top)
    {
        var listed = report.Findings.Where(f => f.State is FindingState.Open or FindingState.Accepted).ToList();
        var byKind = FindingRules.KindOrder.ToDictionary(k => k, k =>
        {
            var of = listed.Where(f => f.Kind == k).ToList();
            return new FindingKindTotals(of.Count, of.Count(f => !f.Unsure), of.Count(f => f.Unsure),
                of.Sum(f => f.Files), of.Sum(f => f.Bytes));
        });
        var byCertainty = Enum.GetValues<ResultCertainty>().ToDictionary(c => c, c => (long)listed.Count(f => f.Certainty == c));
        var byState = Enum.GetValues<FindingState>().ToDictionary(s => s, s => (long)report.Findings.Count(f => f.State == s));
        var briefs = listed.Take(Math.Max(0, top)).Select(f => new FindingBrief(
            f.Rank, f.Key, f.Kind, f.Certainty, f.State, f.Title, f.RelativePath, f.Files, f.Bytes, f.Action.Kind)).ToList();
        return new FindingSummary(report.Job, report.Inputs, report.Coverage, report.MissingSteps,
            listed.Count, listed.Count(f => !f.Unsure), listed.Count(f => f.Unsure),
            byKind, byCertainty, byState, briefs, report.BelowThreshold);
    }

    /// <summary>One finding by key, in any state.</summary>
    public async Task<Finding> GetAsync(long jobId, string key, CancellationToken ct = default)
    {
        var computed = await ComputeInternalAsync(jobId, ct);
        return Find(computed.Report, key);
    }

    /// <summary>One finding and its files as they may leave the machine (through <see cref="SensitiveOutputGuard"/>).</summary>
    public async Task<(Finding Finding, IReadOnlyList<FileOutputRecord> Files)> GetWithFilesAsync(
        long jobId, string key, SensitiveOutputOptions? output, CancellationToken ct = default)
    {
        var computed = await ComputeInternalAsync(jobId, ct);
        var finding = Find(computed.Report, key);
        var files = finding.FileIds
            .Select(id => computed.Files.TryGetValue(id, out var f) ? f : null)
            .Where(f => f is not null)
            .Select(f => SensitiveOutputGuard.Project(f!, output))
            .OrderBy(r => r.FullPath, StringComparer.Ordinal)
            .ToList();
        return (finding, files);
    }

    // ------------------------------------------------------------------ deciding

    /// <summary>
    /// Records a decision: <see cref="FindingState.Accepted"/> (the plan of
    /// https://github.com/crack666/smart_file_organizer/issues/11 turns it into an action),
    /// <see cref="FindingState.Rejected"/> (not listed again), <see cref="FindingState.Wrong"/> (not listed again, kept
    /// as a test case) or <see cref="FindingState.Open"/> (the decision is withdrawn). Changes no file.
    /// </summary>
    public async Task<(Finding Finding, EvalCase? Case)> DecideAsync(
        long jobId, string key, FindingState state, ResultOrigin origin, string? note, CancellationToken ct = default)
    {
        if (origin is not (ResultOrigin.Human or ResultOrigin.Agent))
            throw new ArgumentException("Only a person (human) or a steering agent (agent) decides a finding.", nameof(origin));

        var computed = await ComputeInternalAsync(jobId, ct);
        var finding = Find(computed.Report, key);
        if (state == FindingState.Open)
        {
            await _decisions.DeleteAsync(jobId, finding.Key, ct);
            finding.State = FindingState.Open;
            finding.DecidedBy = null;
            finding.DecidedAt = null;
            finding.DecisionNote = null;
            return (finding, null);
        }

        var now = DateTime.UtcNow;
        await _decisions.UpsertAsync(new FindingDecision
        {
            JobId = jobId,
            FindingKey = finding.Key,
            Kind = finding.Kind,
            State = state,
            Origin = origin,
            Note = note,
            DecidedAt = now,
            SnapshotJson = Snapshot(finding)
        }, ct);
        finding.State = state;
        finding.DecidedBy = origin;
        finding.DecidedAt = now;
        finding.DecisionNote = note;

        EvalCase? evalCase = null;
        if (state == FindingState.Wrong)
            evalCase = await RecordWrongAsync(computed.Report.Job, finding, note, now, ct);
        return (finding, evalCase);
    }

    /// <summary>
    /// A finding reported wrong becomes a test case (<c>eval_cases</c>, kind <c>finding</c>): what the rules said
    /// (<c>observed</c> = kind code) against what is true (<c>expected</c> = <c>none</c>). A sensitive finding keeps
    /// only its markers, no evidence.
    /// </summary>
    private async Task<EvalCase> RecordWrongAsync(ScanJob job, Finding finding, string? note, DateTime now, CancellationToken ct)
    {
        var markers = SensitiveMarker.None;
        foreach (var token in finding.Markers)
            if (SensitiveMarkerNames.TryParseToken(token, out var m)) markers |= m;

        string? evidence = null;
        if (finding.Kind != FindingKind.Sensitive && markers == SensitiveMarker.None)
        {
            evidence = JsonSerializer.Serialize(new
            {
                key = finding.Key,
                kind = finding.Kind.ToCode(),
                certainty = finding.Certainty.ToCode(),
                reason = finding.Reason,
                files = finding.Files,
                examples = finding.Examples,
                action = finding.Action.Kind.ToCode(),
                note
            }, SnapshotJson);
        }

        var evalCase = new EvalCase
        {
            Kind = EvalCaseKind.Finding,
            Origin = EvalCaseOrigin.FindingReportedWrong,
            JobId = job.Id,
            SubjectPath = (finding.Path ?? job.RootPath) + "#" + finding.Key,
            RelativePath = finding.RelativePath,
            Expected = "none",
            Observed = finding.Kind.ToCode(),
            SensitiveMarkers = markers,
            EvidenceJson = evidence,
            CreatedAt = now,
            UpdatedAt = now
        };
        await _evalCases.UpsertAsync(evalCase, ct);
        return evalCase;
    }

    private static string Snapshot(Finding f) => JsonSerializer.Serialize(new
    {
        key = f.Key,
        kind = f.Kind.ToCode(),
        certainty = f.Certainty.ToCode(),
        title = f.Title,
        reason = f.Reason,
        relative_path = f.RelativePath,
        files = f.Files,
        bytes = f.Bytes,
        action = new { kind = f.Action.Kind.ToCode(), reason = f.Action.Reason, single_decision = f.Action.SingleDecision },
        target = f.Target is { } t ? new { relative_path = t.RelativePath, certainty = t.Certainty.ToCode(), content_check = t.ContentCheck.ToCode() } : null,
        file_ids = f.FileIds
    }, SnapshotJson);

    private static Finding Find(FindingReport report, string key) =>
        report.Findings.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal))
        ?? throw new FindingException(FindingFailure.NotFound,
            $"Finding {key} is not part of job {report.Job.Id}; the data it rested on may have changed. List the findings again.");

    // ------------------------------------------------------------------ computing

    private sealed record Computed(FindingReport Report, IReadOnlyDictionary<long, FileNode> Files);

    /// <summary>A part of the source findings summarize: a bundle, or the loose files of one folder.</summary>
    private sealed record Unit(string Subject, string? Path, string Rel, string Label, Bundle? Bundle, string? Noise);

    private sealed record FileFacts(FileNode File, FilePlace Place, FileValue Value, SensitiveMatch Match, FileComparison? Row, Unit Unit, FileTime Time);

    private async Task<Computed> ComputeInternalAsync(long jobId, CancellationToken ct)
    {
        _options.Validate();
        var job = await _jobs.GetByIdAsync(jobId, ct)
                  ?? throw new JobReadException(ReadFailure.JobNotFound, $"Job {jobId} not found.");
        if (job.Role == ScanRole.Library)
            throw new FindingException(FindingFailure.WrongRole,
                $"Job {jobId} is a library; findings are about sources. Compare a source with it instead (sfo compare <source> --against {jobId}).");

        var patterns = _bundleOptions.PatternsFile is { } file ? BundlePatterns.Load(file) : BundlePatterns.Default;
        var dirs = await _read.GetDirectoriesAsync(jobId, ct);
        var dirByPath = new Dictionary<string, DirectoryReadRow>(StringComparer.Ordinal);
        foreach (var d in dirs) dirByPath[d.FullPath] = d;

        var rows = new List<FileReadRow>();
        await foreach (var row in _read.StreamFilesAsync(new FileListFilter { JobId = jobId }, ct))
            rows.Add(row);

        var build = await _bundles.GetBuildAsync(jobId, ct);
        IReadOnlyList<Bundle> bundles = build is null ? [] : await _bundles.GetAllAsync(jobId, ct);
        IReadOnlyDictionary<long, long> membership = build is null ? new Dictionary<long, long>() : await _bundles.GetMembershipAsync(jobId, ct);
        var bundleById = bundles.ToDictionary(b => b.Id);
        var captures = await _captures.GetAsync(jobId, ct);

        var libHeader = await _comparisons.GetLatestWithLibraryAsync(jobId, ct);
        var libRows = libHeader is null
            ? null
            : (await _comparisons.GetFileComparisonsAsync(libHeader.Id, ct)).ToDictionary(r => r.FileNodeId);
        var dupHeader = libHeader ?? await _comparisons.GetComparisonAsync(jobId, null, ct);
        var dupRows = dupHeader is null
            ? null
            : libHeader is not null ? libRows!.Values.ToList() : await _comparisons.GetFileComparisonsAsync(dupHeader.Id, ct);

        // ---- every file: where it lies, what it is worth, whether it is marked
        var facts = new List<FileFacts>(rows.Count);
        foreach (var row in rows)
        {
            var f = row.File;
            dirByPath.TryGetValue(f.ParentPath, out var dir);
            Bundle? direct = membership.TryGetValue(f.Id, out var bid) && bundleById.TryGetValue(bid, out var b) ? b : null;
            var chain = new List<Bundle>();
            for (var x = direct; x is not null; x = x.ParentId is { } p && bundleById.TryGetValue(p, out var parent) ? parent : null)
                chain.Add(x);
            var part = chain.FirstOrDefault(x => x.Kind is BundleKind.DumpKeep or BundleKind.DumpNoise);
            var top = chain.Count > 0 ? chain[^1] : null;
            var noiseByBundle = part?.Kind == BundleKind.DumpNoise || top is not null && FindingRules.IsProgramFolder(top);
            var keptByBundle = part?.Kind == BundleKind.DumpKeep || top?.Kind == BundleKind.Folder;
            var noiseByDir = !noiseByBundle && !keptByBundle && FindingRules.DirectoryNoise(dir) is not null;
            captures.TryGetValue(f.Id, out var capture);
            var place = new FilePlace(top, part, dir, capture, noiseByBundle, noiseByDir);

            var value = FindingRules.ValueOf(f, place);
            var match = SensitiveOutputGuard.GetMatch(f);
            FileComparison? cmp = null;
            libRows?.TryGetValue(f.Id, out cmp);
            facts.Add(new FileFacts(f, place, value, match, cmp, UnitOf(job, f, place, dir, part, top), EventGrouper.TimeOf(f, captures)));
        }

        var fileById = facts.ToDictionary(x => x.File.Id, x => x.File);
        var unitSizes = facts.GroupBy(x => x.Unit.Subject, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var findings = new List<Finding>();
        var below = FindingRules.KindOrder.ToDictionary(k => k, _ => 0L);

        // ---- not_backed_up and in_noise
        foreach (var unitGroup in facts
                     .Where(x => !x.Match.IsMatch && x.Value.Worth == FileWorth.Keep)
                     .GroupBy(x => x.Unit.Subject, StringComparer.Ordinal))
        {
            var unit = unitGroup.First().Unit;
            var noise = unit.Noise is not null;
            var counted = noise
                ? unitGroup.Where(x => x.Row?.State != ContentState.BackedUp).ToList()
                : libRows is null
                    ? []
                    : unitGroup.Where(x => x.Row?.State is ContentState.New or ContentState.Differs).ToList();
            if (counted.Count == 0) continue;
            var kind = noise ? FindingKind.InNoise : FindingKind.NotBackedUp;
            var unitFiles = unitSizes[unit.Subject];
            foreach (var sure in new[] { true, false })
            {
                var part = counted.Where(x => FindingRules.IsSure(x.Value.Certainty) == sure).ToList();
                if (part.Count == 0) continue;
                if (part.Count < (sure ? _options.MinFiles : _options.MinUncertainFiles))
                {
                    below[kind] += part.Count;
                    continue;
                }
                findings.Add(noise
                    ? InNoise(job, unit, part, sure, unitFiles, libHeader)
                    : NotBackedUp(job, unit, part, sure, unitFiles, libHeader!));
            }
        }

        // ---- sensitive
        findings.AddRange(Sensitive(job, facts, dirs, libRows is not null));

        // ---- duplicate
        if (dupRows is not null)
            findings.AddRange(Duplicates(job, facts, dupRows, libRows is not null, below));

        // ---- misplaced
        findings.AddRange(await MisplacedAsync(job, facts, dirs, patterns, libHeader, libRows, ct));

        // ---- decisions, order
        var decisions = await _decisions.GetAllAsync(jobId, ct);
        foreach (var finding in findings)
        {
            if (!decisions.TryGetValue(finding.Key, out var d)) continue;
            finding.State = d.State;
            finding.DecidedBy = d.Origin;
            finding.DecidedAt = d.DecidedAt;
            finding.DecisionNote = d.Note;
        }
        var ranked = FindingRules.Rank(findings);

        var inputs = new FindingInputsInfo(
            rows.Count,
            rows.Count(r => r.File.Override is not null || r.File.Classification is { Error: null }),
            rows.Count(r => r.File.Override is null && r.File.Classification is null
                            && r.File.Status is FileNodeStatus.Discovered or FileNodeStatus.Deferred or FileNodeStatus.Processing),
            dirs.Count,
            dirs.Count(d => d.VerdictSource is not null),
            build?.BuiltAt,
            libHeader,
            dupHeader,
            libHeader?.AgainstJobId);
        var (coverage, missing) = Coverage(job, inputs);
        return new Computed(new FindingReport(job, inputs, coverage, missing, ranked, below), fileById);
    }

    private static Unit UnitOf(ScanJob job, FileNode f, FilePlace place, DirectoryReadRow? dir, Bundle? part, Bundle? top)
    {
        if (place.NoiseByBundle)
        {
            var noiseBundle = part?.Kind == BundleKind.DumpNoise ? part : top!;
            var why = noiseBundle.Kind == BundleKind.DumpNoise ? "app data and caches of a device copy" : "a copied program folder";
            return BundleUnit(job, noiseBundle, why);
        }
        if (place.NoiseByDirectory)
        {
            var rel = FindingRules.Rel(dir?.RelativePath ?? f.RelativeDir);
            return new Unit($"dir|{rel}|noise", f.ParentPath, rel, $"folder {rel}", null,
                "a folder " + FindingRules.DirectoryNoise(dir));
        }
        if (part?.Kind == BundleKind.DumpKeep) return BundleUnit(job, part, null);
        if (top?.Kind is BundleKind.Dump or BundleKind.Folder) return BundleUnit(job, top, null);

        // Loose files (events, scattered, outside every bundle): one unit per folder, however many events it holds.
        var relDir = FindingRules.Rel(f.RelativeDir);
        return new Unit($"dir|{relDir}|loose", f.ParentPath, relDir, $"loose files in {relDir}", null, null);
    }

    private static Unit BundleUnit(ScanJob job, Bundle b, string? noise)
    {
        var rel = b.RootPath is { } root ? RelativeTo(job, root) : FindingRules.Rel(b.Facts.Directories.FirstOrDefault());
        var kind = b.Kind.ToCode();
        var part = b.Part is { } p ? p.ToCode() : b.Origin is { } o ? o.ToCode() : "-";
        var label = b.Kind switch
        {
            BundleKind.DumpKeep => $"{part} part of the device copy at {rel}",
            BundleKind.DumpNoise => $"app data of the device copy at {rel}",
            BundleKind.Dump => $"{part} copy at {rel}",
            _ => $"folder {rel}"
        };
        return new Unit($"bundle|{kind}|{rel}|{part}", b.RootPath, rel, label, b, noise);
    }

    private static string RelativeTo(ScanJob job, string fullPath)
    {
        var rel = Path.GetRelativePath(job.RootPath, fullPath);
        return FindingRules.Rel(rel == "." ? null : rel);
    }

    private Finding NewFinding(
        ScanJob job, FindingKind kind, string subjectSuffix, Unit unit, IReadOnlyList<FileFacts> files,
        ResultCertainty certainty, string title, string reason, FindingAction action, bool withComparison)
    {
        var ordered = files.OrderBy(x => x.File.FullPath, StringComparer.Ordinal).ToList();
        DateTime? from = null, to = null;
        var sources = new HashSet<TimeSource>();
        foreach (var x in ordered)
        {
            var t = x.Time;
            if (t.Time is not { } time) continue;
            sources.Add(t.Source);
            if (from is null || time < from) from = time;
            if (to is null || time > to) to = time;
        }
        TimeSource? source = sources.Count switch { 0 => null, 1 => sources.Single(), _ => TimeSource.Mixed };

        return new Finding
        {
            Key = FindingRules.Key(kind, unit.Subject + subjectSuffix),
            Kind = kind,
            Certainty = certainty,
            Title = title,
            Reason = reason,
            Path = unit.Path,
            RelativePath = unit.Rel,
            BundleId = unit.Bundle?.Id,
            BundleKind = unit.Bundle?.Kind,
            Files = ordered.Count,
            Bytes = ordered.Sum(x => x.File.Size),
            ByFileType = ordered.GroupBy(x => BundleBuilder.TypeCode(x.File.FileType))
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (long)g.Count()),
            Period = from is null ? null : new FindingPeriod(from, to, source),
            Comparison = withComparison
                ? ordered.GroupBy(x => x.Row?.State is { } s ? s.ToValue() : "not_compared")
                    .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (long)g.Count())
                : null,
            Action = action,
            Examples = ordered.Take(_options.Examples).Select(x => FindingRules.Rel(x.File.RelativePath)).ToList(),
            FileIds = ordered.Select(x => x.File.Id).ToList(),
            Directories = ordered.Select(x => x.File.ParentPath).Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private Finding NotBackedUp(ScanJob job, Unit unit, IReadOnlyList<FileFacts> files, bool sure, int unitFiles, ComparisonHeader lib)
    {
        var certainty = FindingRules.Weakest(files.Select(x => x.Value.Certainty), ResultCertainty.Uncertain);
        var n = FindingRules.Count(files.Count, "file", "files");
        var worth = sure ? "worth keeping" : "possibly worth keeping";
        var title = $"{n} {worth}, not in the library: {unit.Label}";
        var reason = $"{n} of the {unitFiles} in {unit.Rel} {(files.Count == 1 ? "is" : "are")} {worth} by {Bases(files)}, "
                     + $"and the comparison with library job {lib.AgainstJobId} on {lib.ComparedAt:yyyy-MM-dd} found no file with the same content.";
        var finding = NewFinding(job, FindingKind.NotBackedUp, sure ? "|sure" : "|unsure", unit, files, certainty, title, reason,
            new FindingAction(PlanActionKind.Move,
                "Worth keeping and not in the library: move it there (copy, verify, then remove the original)."),
            withComparison: true);
        var differs = files.Count(x => x.Row?.State == ContentState.Differs);
        if (differs > 0)
            finding.Notes.Add($"{FindingRules.Count(differs, "file has", "files have")} a namesake with other content in a folder of the same name in the library; compare before replacing anything.");
        if (!lib.Complete)
            finding.Notes.Add("The comparison did not finish; files it did not reach are not counted.");
        finding.Notes.Add($"Where in the library: sfo library match {job.Id} --path <folder> --against {lib.AgainstJobId}.");
        return finding;
    }

    private Finding InNoise(ScanJob job, Unit unit, IReadOnlyList<FileFacts> files, bool sure, int unitFiles, ComparisonHeader? lib)
    {
        var certainty = FindingRules.Weakest(files.Select(x => x.Value.Certainty), ResultCertainty.Uncertain);
        var n = FindingRules.Count(files.Count, "file", "files");
        var worth = sure ? "worth keeping" : "possibly worth keeping";
        var title = $"{n} {worth} among noise: {unit.Label}";
        var reason = $"{n} of the {unitFiles} in {unit.Rel} {(files.Count == 1 ? "is" : "are")} {worth} by {Bases(files)}, "
                     + $"but {(files.Count == 1 ? "lies" : "lie")} in {unit.Noise}, a place whose other files have no value.";
        var finding = NewFinding(job, FindingKind.InNoise, sure ? "|sure" : "|unsure", unit, files, certainty, title, reason,
            new FindingAction(PlanActionKind.Move,
                "Worth keeping in a place without value: move it to a place of its own before the rest is cleaned up."),
            withComparison: lib is not null);
        finding.Notes.Add(lib is null
            ? "Not compared with a library: some of these files may be backed up already."
            : "Files whose content is in the library are left out.");
        return finding;
    }

    private static string Bases(IReadOnlyList<FileFacts> files)
    {
        var words = files.Select(x => x.Value.Basis switch
            {
                "human" => "a person",
                "agent" => "an agent",
                "model" => "the model",
                "inherited" => "a confirmed sample",
                "rule" => "a rule",
                "directory" => "the folder verdict",
                "bundle" => "where they lie",
                "name" => "their names",
                _ => "their file type"
            })
            .Distinct().ToList();
        return words.Count switch
        {
            1 => words[0],
            2 => words[0] + " and " + words[1],
            _ => string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1]
        };
    }

    private IEnumerable<Finding> Sensitive(ScanJob job, IReadOnlyList<FileFacts> facts, IReadOnlyList<DirectoryReadRow> dirs, bool withLibrary)
    {
        foreach (var g in facts.Where(x => x.Match.IsMatch)
                     .GroupBy(x => (Dir: x.File.ParentPath, x.Match.Markers)))
        {
            var files = g.ToList();
            var first = files[0].File;
            var rel = FindingRules.Rel(first.RelativeDir);
            var tokens = SensitiveMarkerNames.ToTokens(g.Key.Markers);
            var rules = files.SelectMany(x => FindingRules.SplitRules(x.Match.Rule)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var certainty = FindingRules.Weakest(
                files.Select(x => FindingRules.SensitiveCertainty(x.Match.Rule, x.File.Classification?.Certainty)), ResultCertainty.Uncertain);
            var n = FindingRules.Count(files.Count, "file", "files");
            var unit = new Unit($"dir|{rel}|{string.Join(",", tokens)}", g.Key.Dir, rel, $"folder {rel}", null, null);
            var finding = NewFinding(job, FindingKind.Sensitive, string.Empty, unit, files, certainty,
                $"{n} marked {string.Join(", ", tokens)} in {rel}",
                $"{n} in {rel} {(files.Count == 1 ? "is" : "are")} marked {string.Join(", ", tokens)} by the rule{(rules.Count == 1 ? "" : "s")} {string.Join(", ", rules)}; only path and marker are shown, never the content.",
                SensitiveAction, withComparison: withLibrary);
            finding.Markers = tokens;
            finding.SensitiveRules = rules;
            if (withLibrary)
            {
                var backed = files.Count(x => x.Row?.State == ContentState.BackedUp);
                finding.Notes.Add(backed > 0
                    ? $"{FindingRules.Count(backed, "file is", "files are")} in the library already; even so, nothing is removed without a person deciding."
                    : "None of them is in the library.");
            }
            yield return finding;
        }

        foreach (var d in dirs.Where(d => d.SensitiveMarkers != SensitiveMarker.None))
        {
            var rel = FindingRules.Rel(d.RelativePath);
            var tokens = SensitiveMarkerNames.ToTokens(d.SensitiveMarkers);
            var rules = FindingRules.SplitRules(d.SensitiveRule);
            var unit = new Unit($"skipped|{rel}", d.FullPath, rel, $"skipped folder {rel}", null, null);
            var finding = NewFinding(job, FindingKind.Sensitive, string.Empty, unit, [], ResultCertainty.Likely,
                $"Skipped folder marked {string.Join(", ", tokens)}: {rel}",
                $"The scan skipped {rel} and its name or place marks it {string.Join(", ", tokens)} (rule {string.Join(", ", rules)}); its files were not read or listed.",
                SensitiveAction, withComparison: false);
            finding.Markers = tokens;
            finding.SensitiveRules = rules;
            finding.Directories = [d.FullPath];
            yield return finding;
        }
    }

    private static readonly FindingAction SensitiveAction = new(PlanActionKind.Leave,
        "Sensitive: a person decides it on its own, never in a batch; it is never removed or quarantined automatically.",
        SingleDecision: true);

    private IEnumerable<Finding> Duplicates(
        ScanJob job, IReadOnlyList<FileFacts> facts, IReadOnlyList<FileComparison> dupRows, bool withLibrary,
        Dictionary<FindingKind, long> below)
    {
        var byId = facts.ToDictionary(x => x.File.Id);
        var groups = dupRows
            .Where(r => r.DuplicateGroup is not null && byId.ContainsKey(r.FileNodeId) && !byId[r.FileNodeId].Match.IsMatch)
            .GroupBy(r => r.DuplicateGroup!.Value)
            .Select(g => g.Select(r => byId[r.FileNodeId]).ToList())
            .Where(g => g.Count >= 2)
            .ToList();

        foreach (var set in groups.GroupBy(g => string.Join("\n",
                     g.Select(x => FindingRules.Rel(x.File.RelativeDir)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                     StringComparer.Ordinal))
        {
            var members = set.ToList();
            var redundantFiles = members.Sum(g => (long)g.Count - 1);
            var redundantBytes = members.Sum(g => g[0].File.Size * (g.Count - 1));
            if (redundantBytes < _options.DuplicateMinBytes && redundantFiles < _options.DuplicateMinFiles)
            {
                below[FindingKind.Duplicate] += redundantFiles;
                continue;
            }

            var all = members.SelectMany(g => g).ToList();
            var dirRels = set.Key.Split('\n');
            var keep = dirRels
                .Select(rel => all.First(x => FindingRules.Rel(x.File.RelativeDir) == rel))
                .OrderBy(x => x.Place.NoiseByBundle || x.Place.NoiseByDirectory)
                .ThenBy(x => FindingRules.Rel(x.File.RelativeDir), StringComparer.Ordinal)
                .First();
            var keepRel = FindingRules.Rel(keep.File.RelativeDir);
            var unit = new Unit($"dirs|{set.Key.Replace('\n', '|')}", keep.File.ParentPath, keepRel, $"folder {keepRel}", null, null);

            var allBackedUp = withLibrary && all.All(x => x.Row?.State == ContentState.BackedUp);
            var action = allBackedUp
                ? new FindingAction(PlanActionKind.DeleteSource, "Every copy is in the library with the same content already: the copies here can go.")
                : withLibrary
                    ? new FindingAction(PlanActionKind.Move, $"Move the copy in {keepRel} to the library; once it is there, the other copies can go.")
                    : new FindingAction(PlanActionKind.Leave, $"Without a library no copy can be removed safely; keep the copy in {keepRel}.");

            var shown = dirRels.Take(3).ToList();
            var where = shown.Count == 1 ? $"in {shown[0]}" : "in " + string.Join(", ", shown) + (dirRels.Length > 3 ? $" and {dirRels.Length - 3} more folders" : "");
            var f = NewFinding(job, FindingKind.Duplicate, string.Empty, unit, all, ResultCertainty.Certain,
                $"{FindingRules.Count(redundantFiles, "redundant copy", "redundant copies")} ({FindingRules.Size(redundantBytes)}) {where}",
                $"{FindingRules.Count(members.Count, "file exists", "files exist")} with identical content (same full SHA-256) more than once {where}; "
                + $"{FindingRules.Count(redundantFiles, "copy", "copies")} with {FindingRules.Size(redundantBytes)} {(redundantFiles == 1 ? "is" : "are")} redundant.",
                action, withComparison: withLibrary);
            f.Bytes = redundantBytes;
            f.Duplicates = new FindingDuplicates(members.Count, redundantFiles, redundantBytes,
                dirRels.Take(FindingDuplicatesMaxDirs).ToList(), dirRels.Length, keepRel);
            yield return f;
        }
    }

    private const int FindingDuplicatesMaxDirs = 10;

    private async Task<IReadOnlyList<Finding>> MisplacedAsync(
        ScanJob job, IReadOnlyList<FileFacts> facts, IReadOnlyList<DirectoryReadRow> dirs, BundlePatterns patterns,
        ComparisonHeader? lib, IReadOnlyDictionary<long, FileComparison>? libRows, CancellationToken ct)
    {
        var uniform = VerdictCodes.ToCode(DirectoryVerdictKind.Uniform);
        var byDir = facts.ToLookup(x => x.File.ParentPath, StringComparer.Ordinal);
        var candidates = new List<(Finding Finding, List<FileFacts> Files)>();

        foreach (var d in dirs)
        {
            var judgedUniform = d.Verdict == uniform || d.EscalatedFrom == uniform;
            var files = byDir[d.FullPath].Where(x => !x.Match.IsMatch).ToList();
            if (files.Count == 0) continue;
            var namedFolder = d.VerdictSource is null
                              && files.Any(x => x.Place.Bundle?.Kind == BundleKind.Folder && x.Place.Bundle.RootPath == d.FullPath);
            if (!judgedUniform && !namedFolder) continue;
            if (patterns.IsGenericName(d.Name)) continue;
            if (files.Any(x => x.Place.NoiseByBundle)) continue;

            var counted = files.Where(x => FindingRules.KindGroup(x.File.FileType) is not null).ToList();
            if (counted.Count < _options.MisplacedMinFolderFiles) continue;
            var dominant = counted.GroupBy(x => FindingRules.KindGroup(x.File.FileType)!)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First();
            var share = (double)dominant.Count() / counted.Count;
            if (share < _options.MisplacedDominantShare) continue;

            foreach (var foreign in counted
                         .Where(x => FindingRules.KindGroup(x.File.FileType) != dominant.Key && x.Value.Worth != FileWorth.Disposable)
                         .GroupBy(x => FindingRules.KindGroup(x.File.FileType)!))
            {
                var group = foreign.ToList();
                var rel = FindingRules.Rel(d.RelativePath);
                var n = FindingRules.Count(group.Count, $"{foreign.Key} file", $"{foreign.Key} files");
                var fileCertainty = FindingRules.Weakest(
                    group.Select(x => x.Value.Worth == FileWorth.None ? ResultCertainty.Uncertain : x.Value.Certainty), ResultCertainty.Uncertain);
                var unit = new Unit($"dir|{rel}|{foreign.Key}", d.FullPath, rel, $"folder {rel}", null, null);
                var finding = NewFinding(job, FindingKind.Misplaced, string.Empty, unit, group,
                    FindingRules.MisplacedCertainty(fileCertainty, null),
                    $"{n} in the {dominant.Key} folder {rel}",
                    $"{rel} holds {dominant.Count()} {dominant.Key} files of {counted.Count} ({share:P0}) and was judged {(judgedUniform ? "uniform" : "a named folder")}; "
                    + $"{(group.Count == 1 ? "this" : "these")} {n} {(group.Count == 1 ? "does" : "do")} not fit it.",
                    new FindingAction(PlanActionKind.Leave, "No place can be proposed without a library; decide where it belongs."),
                    withComparison: libRows is not null);
                candidates.Add((finding, group));
            }
        }

        if (candidates.Count == 0) return [];

        // Places in the library, for at most MaxTargetLookups groups (the largest first).
        IReadOnlyList<FindingTarget?> targets = [];
        var lookups = candidates.OrderByDescending(c => c.Files.Count).Take(lib is null ? 0 : _options.MaxTargetLookups).ToList();
        if (lib?.AgainstJobId is { } libraryJob && lookups.Count > 0)
        {
            try
            {
                targets = await _advisor.ProposeAsync(job.Id, libraryJob,
                    lookups.Select(c => (IReadOnlyList<FileNode>)c.Files.Select(x => x.File).ToList()).ToList(),
                    libRows ?? new Dictionary<long, FileComparison>(), ct);
            }
            catch (LibraryServiceException ex)
            {
                _logger.LogWarning("No places for misplaced findings: {Message}", ex.Message);
                foreach (var c in lookups) c.Finding.Notes.Add("No place proposed: " + ex.Message);
            }
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var (finding, files) = candidates[i];
            var index = lookups.FindIndex(c => ReferenceEquals(c.Finding, finding));
            var target = index >= 0 && index < targets.Count ? targets[index] : null;
            var fileCertainty = FindingRules.Weakest(
                files.Select(x => x.Value.Worth == FileWorth.None ? ResultCertainty.Uncertain : x.Value.Certainty), ResultCertainty.Uncertain);

            if (target?.ContentCheck == TargetContentCheck.Contradicts)
            {
                finding.Notes.Add($"The proposed place {target.RelativePath} was dropped: its content contradicts these files.");
                target = null;
            }

            if (target is not null)
            {
                finding.Target = target;
                finding.Action = new FindingAction(PlanActionKind.Move, $"Move to {target.RelativePath}: {target.Reason}");
            }
            else if (lib is not null && index < 0 && candidates.Count > _options.MaxTargetLookups)
                finding.Notes.Add($"No place looked up: only the {_options.MaxTargetLookups} largest groups are matched against the library (Findings:MaxTargetLookups).");
            else if (lib is not null && index >= 0)
                finding.Notes.Add("No folder of the library fits these files.");

            finding.Certainty = FindingRules.MisplacedCertainty(fileCertainty, target);
            if (target is null || target.ContentCheck != TargetContentCheck.Fits)
                finding.Notes.Add(lib is null
                    ? "No library: no place is proposed, and the finding stays uncertain."
                    : "The content of the proposed place was not compared with these files, so the finding stays uncertain at most.");
        }

        return candidates.Select(c => c.Finding).ToList();
    }

    private static (IReadOnlyDictionary<FindingKind, KindCoverage>, IReadOnlyList<string>) Coverage(ScanJob job, FindingInputsInfo inputs)
    {
        var id = job.Id;
        var analysed = inputs.FilesJudged > 0 || inputs.DirectoriesJudged > 0;
        var pending = inputs.FilesPending > 0;
        var bundles = inputs.BundlesBuiltAt is not null;
        var library = inputs.LibraryComparison is not null;
        var anyComparison = inputs.DuplicateComparison is not null;

        var missing = new List<string>();
        if (!analysed || pending) missing.Add($"sfo analyze {id}");
        if (!bundles) missing.Add($"sfo bundles build {id}");
        if (!library)
            missing.Add($"sfo compare {id} --against <library job> (scan the library first: sfo scan <path> --role library)");

        string AnalysisNote() => !analysed
            ? "no analysis: worth rests on bundles, folder names and file types only (sfo analyze)"
            : $"{inputs.FilesPending} files not analysed yet (sfo analyze)";

        var coverage = new Dictionary<FindingKind, KindCoverage>
        {
            [FindingKind.NotBackedUp] = !library
                ? new KindCoverage(FindingCoverage.NotComputed, "no comparison with a library (sfo compare --against)")
                : !analysed || pending
                    ? new KindCoverage(FindingCoverage.Partial, AnalysisNote())
                    : !bundles
                        ? new KindCoverage(FindingCoverage.Partial, "no bundles: files are summarized per folder (sfo bundles build)")
                        : new KindCoverage(FindingCoverage.Complete, null),
            [FindingKind.InNoise] = !bundles && inputs.DirectoriesJudged == 0
                ? new KindCoverage(FindingCoverage.NotComputed, "neither bundles nor folder verdicts: no place is known to be noise (sfo bundles build, sfo analyze)")
                : !bundles
                    ? new KindCoverage(FindingCoverage.Partial, "no bundles: app data of device copies and program folders are not recognised (sfo bundles build)")
                    : inputs.DirectoriesJudged == 0 || pending
                        ? new KindCoverage(FindingCoverage.Partial, AnalysisNote())
                        : !library
                            ? new KindCoverage(FindingCoverage.Partial, "no library: files already backed up are not left out")
                            : new KindCoverage(FindingCoverage.Complete, null),
            [FindingKind.Misplaced] = !analysed && !bundles
                ? new KindCoverage(FindingCoverage.NotComputed, "neither folder verdicts nor bundles: no folder purpose is known (sfo analyze, sfo bundles build)")
                : new KindCoverage(FindingCoverage.Partial, !library
                    ? "no library: no place is proposed; uncertain at most until targets are checked by content"
                    : "targets are not checked by content yet: uncertain at most"),
            [FindingKind.Sensitive] = !analysed || pending
                ? new KindCoverage(FindingCoverage.Partial, "rule markers only; markers of the model come with sfo analyze")
                : new KindCoverage(FindingCoverage.Complete, null),
            [FindingKind.Duplicate] = !anyComparison
                ? new KindCoverage(FindingCoverage.NotComputed, $"no comparison (sfo compare {id})")
                : new KindCoverage(FindingCoverage.Complete, null)
        };
        return (coverage, missing);
    }

    private static string FullPathOf(ScanJob job, string path)
    {
        var p = Path.IsPathRooted(path) ? path : Path.Combine(job.RootPath, path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
    }

    private static bool IsAtOrBelow(string path, string dir)
    {
        var p = Path.TrimEndingDirectorySeparator(path);
        if (string.Equals(p, dir, StringComparison.OrdinalIgnoreCase)) return true;
        return p.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && p.Length > dir.Length && (p[dir.Length] == '/' || p[dir.Length] == '\\');
    }
}
