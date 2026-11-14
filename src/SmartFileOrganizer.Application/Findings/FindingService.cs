using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Bundles;
using SmartFileOrganizer.Application.Decisions;
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
    WrongRole,

    /// <summary>A library was named whose comparison with the job is not stored.</summary>
    ComparisonNotFound
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

    /// <summary>
    /// The library jobs whose stored comparisons count: a file is backed up as soon as one of them holds it. Empty for
    /// the libraries of the newest <c>sfo compare</c> of the job.
    /// </summary>
    public IReadOnlyList<long> Libraries { get; init; } = [];
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
    PlanActionKind Action,
    FindingTarget? Target = null,
    FindingTargetStatus? TargetStatus = null);

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
    IReadOnlyDictionary<FindingKind, long> BelowThreshold,
    FindingOverview? Overview = null,
    FindingStorage? Storage = null);

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
    private readonly IFileFactRepository _facts;
    private readonly FindingOptions _options;
    private readonly BundleOptions _bundleOptions;
    private readonly ILogger<FindingService> _logger;
    private readonly IFreeSpaceProbe? _freeSpace;
    private readonly IFindingTargetRepository? _targets;

    public FindingService(
        IScanJobRepository jobs,
        IJobReadRepository read,
        IBundleRepository bundles,
        ICaptureInfoSource captures,
        IContentComparisonRepository comparisons,
        IFindingDecisionRepository decisions,
        IEvalCaseRepository evalCases,
        IFindingTargetAdvisor advisor,
        IFileFactRepository facts,
        FindingOptions options,
        BundleOptions bundleOptions,
        ILogger<FindingService> logger,
        IFreeSpaceProbe? freeSpace = null,
        IFindingTargetRepository? targets = null)
    {
        _freeSpace = freeSpace;
        _targets = targets;
        _jobs = jobs;
        _read = read;
        _bundles = bundles;
        _captures = captures;
        _comparisons = comparisons;
        _decisions = decisions;
        _evalCases = evalCases;
        _advisor = advisor;
        _facts = facts;
        _options = options;
        _bundleOptions = bundleOptions;
        _logger = logger;
    }

    // ------------------------------------------------------------------ reading

    /// <summary>All findings of the job, ranked, with their stored decisions (rejected and wrong ones included).</summary>
    public Task<FindingReport> ComputeAsync(long jobId, CancellationToken ct = default) =>
        ComputeAsync(jobId, null, ct);

    /// <summary>As <see cref="ComputeAsync(long, CancellationToken)"/>, against these library jobs (null or empty: those of the newest comparison).</summary>
    public async Task<FindingReport> ComputeAsync(long jobId, IReadOnlyList<long>? libraries, CancellationToken ct = default) =>
        (await ComputeInternalAsync(jobId, ct, libraries)).Report;

    /// <summary>The findings a listing shows: filtered by <paramref name="query"/>, in rank order.</summary>
    public async Task<(FindingReport Report, IReadOnlyList<Finding> Items)> ListAsync(FindingQuery query, CancellationToken ct = default)
    {
        var report = await ComputeAsync(query.JobId, query.Libraries, ct);
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
    public Task<FindingSummary> SummarizeAsync(long jobId, int top, CancellationToken ct = default) =>
        SummarizeAsync(jobId, top, null, ct);

    /// <summary>As <see cref="SummarizeAsync(long, int, CancellationToken)"/>, against these library jobs (null or empty: those of the newest comparison).</summary>
    public async Task<FindingSummary> SummarizeAsync(long jobId, int top, IReadOnlyList<long>? libraries, CancellationToken ct = default)
    {
        var report = await ComputeAsync(jobId, libraries, ct);
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
            f.Rank, f.Key, f.Kind, f.Certainty, f.State, f.Title, f.RelativePath, f.Files, f.Bytes, f.Action.Kind,
            f.Target, f.TargetStatus)).ToList();
        return new FindingSummary(report.Job, report.Inputs, report.Coverage, report.MissingSteps,
            listed.Count, listed.Count(f => !f.Unsure), listed.Count(f => f.Unsure),
            byKind, byCertainty, byState, briefs, report.BelowThreshold, report.Overview, report.Storage);
    }

    /// <summary>
    /// The worth of every file of the job as the findings see it, with its place and its stored facts: the input of
    /// stage 2 (<c>sfo decide</c>, https://github.com/crack666/smart_file_organizer/issues/37), and the findings beside it.
    /// </summary>
    public Task<(FindingReport Report, IReadOnlyList<FileValueRow> Files)> ComputeValuesAsync(long jobId, CancellationToken ct = default) =>
        ComputeValuesAsync(jobId, null, ct);

    /// <summary>As <see cref="ComputeValuesAsync(long, CancellationToken)"/>, against these library jobs (null or empty: those of the newest comparison).</summary>
    public async Task<(FindingReport Report, IReadOnlyList<FileValueRow> Files)> ComputeValuesAsync(
        long jobId, IReadOnlyList<long>? libraries, CancellationToken ct = default)
    {
        var computed = await ComputeInternalAsync(jobId, ct, libraries);
        return (computed.Report, computed.Values);
    }

    /// <summary>
    /// The findings with the scanned files of the job by id: the input of the target match
    /// (https://github.com/crack666/smart_file_organizer/issues/83), which hands a finding's files to <c>sfo library match</c>.
    /// </summary>
    public async Task<(FindingReport Report, IReadOnlyDictionary<long, FileNode> Files)> ComputeWithFilesAsync(
        long jobId, IReadOnlyList<long>? libraries, CancellationToken ct = default)
    {
        var computed = await ComputeInternalAsync(jobId, ct, libraries);
        return (computed.Report, computed.Files);
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

    private sealed record Computed(FindingReport Report, IReadOnlyDictionary<long, FileNode> Files, IReadOnlyList<FileValueRow> Values);

    /// <summary>A part of the source findings summarize: a bundle, or the loose files of one folder.</summary>
    private sealed record Unit(string Subject, string? Path, string Rel, string Label, Bundle? Bundle, string? Noise);

    /// <param name="Container">The program, project or system folder the file belongs to (topmost folder judged unit or
    /// system, a program folder, the app data of a device copy); null for a file of its own.</param>
    private sealed record FileFacts(FileNode File, FilePlace Place, FileValue Value, SensitiveMatch Match, FileComparison? Row, Unit Unit, FileTime Time, string? Container);

    private async Task<Computed> ComputeInternalAsync(long jobId, CancellationToken ct, IReadOnlyList<long>? libraries = null)
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
        var factsByFile = await _facts.GetByJobAsync(jobId, ct);

        // The libraries: those named, else those of the newest comparison. A file is backed up as soon as one holds it.
        var (libSet, missingLibraries) = await LibraryComparisonSet.LoadAsync(_comparisons, jobId, libraries, ct);
        if (missingLibraries.Count > 0)
        {
            throw new FindingException(FindingFailure.ComparisonNotFound,
                $"Job {jobId} was not compared with library job {string.Join(", ", missingLibraries)}; run first: "
                + $"sfo compare {jobId} {string.Join(" ", libraries!.Distinct().Select(l => $"--against {l}"))}");
        }
        var libHeader = libSet?.First;
        var libRows = libSet?.Rows;
        var libraryJobs = new List<ScanJob>();
        foreach (var id in libSet?.LibraryJobIds ?? [])
            if (await _jobs.GetByIdAsync(id, ct) is { } libraryJob)
                libraryJobs.Add(libraryJob);
        var dupHeader = libHeader ?? await _comparisons.GetComparisonAsync(jobId, null, ct);
        var dupRows = dupHeader is null
            ? null
            : libSet is not null ? libSet.Files : await _comparisons.GetFileComparisonsAsync(dupHeader.Id, ct);

        // ---- where the wholes are: folders judged unit or system, and the folders that hold one of them
        var wholes = new Dictionary<string, (string? Root, bool InUnit)>(StringComparer.Ordinal);
        (string? Root, bool InUnit) WholeOf(string dirPath)
        {
            if (wholes.TryGetValue(dirPath, out var known)) return known;
            (string? Root, bool InUnit) result = (null, false);
            if (dirByPath.TryGetValue(dirPath, out var d))
            {
                var above = WholeOf(d.ParentPath);
                var self = FindingRules.IsWholeVerdict(d.Verdict);
                result = (above.Root ?? (self ? d.FullPath : null),
                    above.InUnit || d.Verdict == VerdictCodes.ToCode(DirectoryVerdictKind.Unit));
            }
            wholes[dirPath] = result;
            return result;
        }
        var besideWholes = new HashSet<string>(
            dirs.Where(d => FindingRules.IsWholeVerdict(d.Verdict)).Select(d => d.ParentPath)
                .Concat(bundles.Where(FindingRules.IsProgramFolder).Select(x => x.RootPath).OfType<string>().Select(p => Path.GetDirectoryName(p) ?? p)),
            StringComparer.Ordinal);

        // ---- every file: where it lies, what it is worth, whether it is marked
        var facts = new List<FileFacts>(rows.Count);
        var values = new List<FileValueRow>(rows.Count);
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
            var programFolder = top is not null && FindingRules.IsProgramFolder(top);
            var noiseByBundle = part?.Kind == BundleKind.DumpNoise || programFolder;
            var keptByBundle = part?.Kind == BundleKind.DumpKeep || top?.Kind == BundleKind.Folder;
            // A folder judged mixed between program folders (a desktop with unpacked tools) is noise even inside the
            // kept part of a device copy: what is worth keeping there has to be moved before the programs go.
            var besidePrograms = dir is not null && besideWholes.Contains(dir.FullPath);
            var noiseByDir = !noiseByBundle && (!keptByBundle || besidePrograms && top?.Kind != BundleKind.Folder)
                             && FindingRules.DirectoryNoise(dir) is not null;
            captures.TryGetValue(f.Id, out var capture);
            var place = new FilePlace(top, part, dir, capture, noiseByBundle, noiseByDir);

            var whole = WholeOf(f.ParentPath);
            var fileFacts = factsByFile.TryGetValue(f.Id, out var stored) ? stored : [];
            var value = DecisionRules.ValueOf(f, place, fileFacts);
            // Where a file lies is no reason to keep it when it lies inside a unit (the sounds of a program on the
            // desktop): only a judgement of the file itself counts there.
            if (whole.InUnit && value.Worth == FileWorth.Keep && !FindingRules.RestsOnFile(value))
                value = FileValue.None;
            var match = SensitiveOutputGuard.GetMatch(f);
            FileComparison? cmp = null;
            libRows?.TryGetValue(f.Id, out cmp);
            // The whole a file belongs to, for duplicates: two copies of a program are one finding, folder against folder.
            var container = whole.Root
                            ?? (programFolder ? top!.RootPath : null)
                            ?? (part?.Kind == BundleKind.DumpNoise ? part.RootPath ?? "dump_noise|" + part.Id : null);
            values.Add(new FileValueRow(f, place, value, fileFacts));
            facts.Add(new FileFacts(f, place, value, match, cmp,
                UnitOf(job, f, place, dir, part, top, besidePrograms && noiseByDir && keptByBundle),
                EventGrouper.TimeOf(f, captures), container));
        }

        var fileById = facts.ToDictionary(x => x.File.Id, x => x.File);
        var unitSizes = facts.GroupBy(x => x.Unit.Subject, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var findings = new List<Finding>();
        var below = FindingRules.KindOrder.ToDictionary(k => k, _ => 0L);

        // ---- not_backed_up and in_noise: one finding per place, kind of file (documents, images, ...) and sureness
        foreach (var unitGroup in facts
                     .Where(x => !x.Match.IsMatch && x.Value.Worth == FileWorth.Keep)
                     .GroupBy(x => (x.Unit.Subject, Group: FindingRules.GroupOf(x.File.FileType))))
        {
            var unit = unitGroup.First().Unit;
            var group = unitGroup.Key.Group;
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
                    ? InNoise(job, unit, group, part, sure, unitFiles, libHeader)
                    : NotBackedUp(job, unit, group, part, sure, unitFiles, libSet!));
            }
        }

        // ---- sensitive
        findings.AddRange(Sensitive(job, facts, dirs, libRows is not null));

        // ---- duplicate
        if (dupRows is not null)
            findings.AddRange(Duplicates(job, facts, dupRows, libRows is not null, below));

        // ---- misplaced
        findings.AddRange(await MisplacedAsync(job, facts, dirs, patterns, libHeader, libRows, libraryJobs, ct));

        // ---- places of the target match (sfo findings targets), as long as the finding is what was matched
        await ApplyStoredTargetsAsync(jobId, findings, facts, factsByFile, libraryJobs, ct);

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
            libHeader?.AgainstJobId,
            libSet?.LibraryJobIds ?? []);
        var (coverage, missing) = Coverage(job, inputs);
        if (libraryJobs.Count > 0 && ranked.Any(f => f.State is FindingState.Open or FindingState.Accepted
                                                    && f.Action.Kind == PlanActionKind.Move && f.Target is null
                                                    && f.TargetStatus == FindingTargetStatus.NotMatched))
            missing = [.. missing, $"sfo findings targets {jobId}"];
        var overview = Overview(facts, libSet, libraryJobs);
        var storage = Storage(ranked, libraryJobs);
        return new Computed(new FindingReport(job, inputs, coverage, missing, ranked, below, overview, storage), fileById, values);
    }

    /// <summary>File by file: worth keeping and not safe, safe per library, disposable, sensitive.</summary>
    private static FindingOverview Overview(IReadOnlyList<FileFacts> facts, LibraryComparisonSet? libSet, IReadOnlyList<ScanJob> libraries)
    {
        FileTally all = FileTally.Zero, keep = FileTally.Zero, keepNew = FileTally.Zero, keepSafe = FileTally.Zero, keepUnknown = FileTally.Zero;
        FileTally backedUp = FileTally.Zero, disposable = FileTally.Zero, sensitive = FileTally.Zero, notCompared = FileTally.Zero;
        foreach (var x in facts)
        {
            var size = x.File.Size;
            var state = x.Row?.State;
            all = all.Add(size);
            if (state == ContentState.BackedUp) backedUp = backedUp.Add(size);
            if (state is null or ContentState.Error) notCompared = notCompared.Add(size);
            if (x.Match.IsMatch)
            {
                sensitive = sensitive.Add(size);
                continue;
            }
            if (x.Value.Worth == FileWorth.Disposable)
            {
                disposable = disposable.Add(size);
            }
            else if (x.Value.Worth == FileWorth.Keep)
            {
                keep = keep.Add(size);
                if (state is ContentState.New or ContentState.Differs) keepNew = keepNew.Add(size);
                else if (state == ContentState.BackedUp) keepSafe = keepSafe.Add(size);
                else keepUnknown = keepUnknown.Add(size);
            }
        }

        var byLibrary = new List<LibraryTally>();
        if (libSet is not null)
        {
            var roots = libraries.ToDictionary(l => l.Id, l => l.RootPath);
            foreach (var (id, files, bytes) in libSet.BackedUpByLibrary())
                byLibrary.Add(new LibraryTally(id, roots.GetValueOrDefault(id, string.Empty), files, bytes));
        }
        return new FindingOverview(all, keep, keepNew, keepSafe, keepUnknown, backedUp, byLibrary, disposable, sensitive, notCompared);
    }

    /// <summary>
    /// Free and needed space per library root. Needed is what the listed findings with a target in that root move there;
    /// findings that should move but have no target yet are counted apart, since their root is not decided.
    /// </summary>
    private FindingStorage Storage(IReadOnlyList<Finding> findings, IReadOnlyList<ScanJob> libraries)
    {
        var listed = findings.Where(f => f.State is FindingState.Open or FindingState.Accepted && f.Action.Kind == PlanActionKind.Move).ToList();
        var unplaced = listed.Where(f => f.Target is null).ToList();
        var unplacedTally = new FileTally(unplaced.Sum(f => f.Files), unplaced.Sum(f => f.Bytes));
        var reasons = unplaced
            .GroupBy(f => (f.TargetStatus ?? FindingTargetStatus.NotMatched).ToCode(), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        if (libraries.Count == 0)
        {
            return new FindingStorage(StorageStatus.NoLibrary,
                "No permanent storage is known: no target is proposed. Scan it with 'sfo scan <path> --role library' and compare with 'sfo compare <job> --against <library>'.",
                [], unplaced.Count, unplacedTally, [], reasons);
        }

        var roots = new List<RootStorage>();
        var warnings = new List<string>();
        foreach (var library in libraries)
        {
            var placed = listed.Where(f => f.Target is { } t && t.LibraryJobId == library.Id).ToList();
            var needed = placed.Sum(f => f.Bytes);
            var free = _freeSpace?.AvailableBytes(library.RootPath);
            var space = free is not { } bytes ? TargetSpace.Unknown : needed <= bytes ? TargetSpace.Enough : TargetSpace.NotEnough;
            if (space == TargetSpace.NotEnough)
                warnings.Add($"Library job {library.Id} ({library.RootPath}): the proposals need {FindingRules.Size(needed)}, only {FindingRules.Size(free!.Value)} are free.");
            roots.Add(new RootStorage(library.Id, library.RootPath, free, needed, placed.Count, space));
        }

        if (unplacedTally.Bytes > 0 && roots.All(r => r.FreeBytes is not null))
        {
            var freeTotal = roots.Sum(r => r.FreeBytes!.Value - Math.Min(r.NeededBytes, r.FreeBytes!.Value));
            if (unplacedTally.Bytes > freeTotal)
                warnings.Add($"{FindingRules.Count(unplaced.Count, "finding", "findings")} without a target need {FindingRules.Size(unplacedTally.Bytes)}, more than is left free in all library roots ({FindingRules.Size(freeTotal)}).");
        }
        return new FindingStorage(StorageStatus.Known, null, roots, unplaced.Count, unplacedTally, warnings, reasons);
    }

    private static Unit UnitOf(ScanJob job, FileNode f, FilePlace place, DirectoryReadRow? dir, Bundle? part, Bundle? top, bool besidePrograms)
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
                "a folder " + FindingRules.DirectoryNoise(dir) + (besidePrograms ? " between program folders" : string.Empty));
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
            BundleKind.DumpNoise => rel == "." ? "app data of the device copy" : $"app data of the device copy at {rel}",
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
        ResultCertainty certainty, string title, string reason, FindingAction action, bool withComparison, string? group = null)
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
            Group = group,
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

    private Finding NotBackedUp(ScanJob job, Unit unit, string group, IReadOnlyList<FileFacts> files, bool sure, int unitFiles, LibraryComparisonSet lib)
    {
        var certainty = FindingRules.Weakest(files.Select(x => x.Value.Certainty), ResultCertainty.Uncertain);
        var n = GroupCount(group, files.Count);
        var worth = sure ? "worth keeping" : "possibly worth keeping";
        var title = $"{n} ({FindingRules.Size(files.Sum(x => x.File.Size))}) {worth}, not in the library: {unit.Label}";
        var reason = $"{n} of the {unitFiles} in {unit.Rel} {(files.Count == 1 ? "is" : "are")} {worth} by {Bases(files)}, "
                     + $"and the comparison with {lib.Label} on {lib.ComparedAt:yyyy-MM-dd} found no file with the same content.";
        var finding = NewFinding(job, FindingKind.NotBackedUp, $"|{group}" + (sure ? "|sure" : "|unsure"), unit, files, certainty, title, reason,
            new FindingAction(PlanActionKind.Move,
                "Worth keeping and not in the library: move it there (copy, verify, then remove the original)."),
            withComparison: true, group);
        var differs = files.Count(x => x.Row?.State == ContentState.Differs);
        if (differs > 0)
            finding.Notes.Add($"{FindingRules.Count(differs, "file has", "files have")} a namesake with other content in a folder of the same name in the library; compare before replacing anything.");
        if (!lib.Complete)
            finding.Notes.Add("The comparison did not finish; files it did not reach are not counted.");
        finding.Notes.Add($"Where in the library: sfo library match {job.Id} <folder> {string.Join(" ", lib.LibraryJobIds.Select(id => $"--against {id}"))}.");
        return finding;
    }

    private Finding InNoise(ScanJob job, Unit unit, string group, IReadOnlyList<FileFacts> files, bool sure, int unitFiles, ComparisonHeader? lib)
    {
        var certainty = FindingRules.Weakest(files.Select(x => x.Value.Certainty), ResultCertainty.Uncertain);
        var n = GroupCount(group, files.Count);
        var worth = sure ? "worth keeping" : "possibly worth keeping";
        var title = $"{n} ({FindingRules.Size(files.Sum(x => x.File.Size))}) {worth} among noise: {unit.Label}";
        var reason = $"{n} of the {unitFiles} in {unit.Rel} {(files.Count == 1 ? "is" : "are")} {worth} by {Bases(files)}, "
                     + $"but {(files.Count == 1 ? "lies" : "lie")} in {unit.Noise}, a place whose other files have no value.";
        var finding = NewFinding(job, FindingKind.InNoise, $"|{group}" + (sure ? "|sure" : "|unsure"), unit, files, certainty, title, reason,
            new FindingAction(PlanActionKind.Move,
                "Worth keeping in a place without value: move it to a place of its own before the rest is cleaned up."),
            withComparison: lib is not null, group);
        finding.Notes.Add(lib is null
            ? "Not compared with a library: some of these files may be backed up already."
            : "Files whose content is in the library are left out.");
        return finding;
    }

    /// <summary>"3 documents", "1 audio file", "12 other files".</summary>
    private static string GroupCount(string group, long n) => group switch
    {
        "documents" => FindingRules.Count(n, "document", "documents"),
        "images" => FindingRules.Count(n, "image", "images"),
        "videos" => FindingRules.Count(n, "video", "videos"),
        "audio" => FindingRules.Count(n, "audio file", "audio files"),
        "archives" => FindingRules.Count(n, "archive", "archives"),
        "installers" => FindingRules.Count(n, "installer", "installers"),
        _ => FindingRules.Count(n, "other file", "other files")
    };

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
        // Outside noise one finding per folder and marker set: the person looks at each place. Inside app data or a
        // program folder one per noise bundle and marker set, so the browser and mail profiles of an old user
        // profile do not fill the list.
        foreach (var g in facts.Where(x => x.Match.IsMatch)
                     .GroupBy(x => (Place: x.Unit is { Bundle: not null, Noise: not null } u ? u.Subject : "dir|" + x.File.ParentPath, x.Match.Markers)))
        {
            var files = g.ToList();
            var first = files[0];
            var inNoise = first.Unit is { Bundle: not null, Noise: not null };
            var rel = inNoise ? first.Unit.Rel : FindingRules.Rel(first.File.RelativeDir);
            var where = inNoise ? first.Unit.Label : rel;
            var tokens = SensitiveMarkerNames.ToTokens(g.Key.Markers);
            var rules = files.SelectMany(x => FindingRules.SplitRules(x.Match.Rule)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var shownRules = rules.Count <= 5 ? string.Join(", ", rules) : string.Join(", ", rules.Take(5)) + $" and {rules.Count - 5} more";
            var certainty = FindingRules.Weakest(
                files.Select(x => FindingRules.SensitiveCertainty(x.Match.Rule, x.File.Classification?.Certainty)), ResultCertainty.Uncertain);
            var n = FindingRules.Count(files.Count, "file", "files");
            var folders = files.Select(x => x.File.ParentPath).Distinct(StringComparer.Ordinal).Count();
            var unit = inNoise
                ? first.Unit with { Subject = $"{first.Unit.Subject}|{string.Join(",", tokens)}" }
                : new Unit($"dir|{rel}|{string.Join(",", tokens)}", first.File.ParentPath, rel, $"folder {rel}", null, null);
            var finding = NewFinding(job, FindingKind.Sensitive, string.Empty, unit, files, certainty,
                $"{n} marked {string.Join(", ", tokens)} in {where}",
                $"{n} in {where}{(folders > 1 ? $" ({folders} folders)" : string.Empty)} {(files.Count == 1 ? "is" : "are")} marked {string.Join(", ", tokens)} by the rule{(rules.Count == 1 ? "" : "s")} {shownRules}; only path and marker are shown, never the content.",
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
        var allGroups = dupRows
            .Where(r => r.DuplicateGroup is not null && byId.ContainsKey(r.FileNodeId) && !byId[r.FileNodeId].Match.IsMatch)
            .GroupBy(r => r.DuplicateGroup!.Value)
            .Select(g => g.Select(r => byId[r.FileNodeId]).ToList())
            .Where(g => g.Count >= 2)
            .ToList();

        // Copies inside programs, projects, system folders and app data are no finding of their own: the language
        // files of a tool unpacked twice would fill the list. Two copies of the same whole are one finding, folder
        // against folder; copies within one whole are none.
        foreach (var f in FolderCopies(job, facts, allGroups, withLibrary, below))
            yield return f;

        // Single files: only those worth keeping and outside every whole count.
        var groups = allGroups
            .Select(g => g.Where(x => x.Container is null && x.Value.Worth == FileWorth.Keep).ToList())
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
            var action = DuplicateAction(withLibrary, allBackedUp, keepRel, "copy");

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

    /// <summary>Share of the files of the smaller whole that must be identical before two wholes count as copies of each other.</summary>
    private const double FolderCopyMinShare = 0.5;

    /// <summary>
    /// Wholes (program, project, system folder, app data) that exist more than once: one <c>duplicate</c> finding per set
    /// of copied folders. Identical files shared by wholes that are no copies of each other (a common library in two
    /// programs) and copies within one whole give no finding.
    /// </summary>
    private IEnumerable<Finding> FolderCopies(
        ScanJob job, IReadOnlyList<FileFacts> facts, IReadOnlyList<List<FileFacts>> groups, bool withLibrary,
        Dictionary<FindingKind, long> below)
    {
        var sizeOf = facts.Where(x => x.Container is not null)
            .GroupBy(x => x.Container!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        string RelOf(string container) => Path.IsPathRooted(container) ? RelativeTo(job, container) : container;

        var sets = groups
            .Select(g => g.Where(x => x.Container is not null).ToList())
            .Where(g => g.Select(x => x.Container).Distinct(StringComparer.Ordinal).Count() >= 2)
            .GroupBy(g => string.Join("\n", g.Select(x => x.Container!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                StringComparer.Ordinal);
        foreach (var set in sets)
        {
            var members = set.ToList();
            var containers = set.Key.Split('\n');
            var redundantFiles = members.Sum(g => (long)g.Count - 1);
            var redundantBytes = members.Sum(g => g[0].File.Size * (g.Count - 1));
            var smallestShare = containers.Min(c =>
                (double)members.SelectMany(g => g).Count(x => x.Container == c) / Math.Max(1, sizeOf.GetValueOrDefault(c)));
            if (smallestShare < FolderCopyMinShare
                || redundantBytes < _options.DuplicateMinBytes && redundantFiles < _options.DuplicateMinFiles)
            {
                below[FindingKind.Duplicate] += redundantFiles;
                continue;
            }

            var rels = containers.Select(RelOf).Order(StringComparer.Ordinal).ToList();
            var keepRel = rels[0];
            var keepFull = containers.First(c => RelOf(c) == keepRel);
            var all = members.SelectMany(g => g).ToList();
            var unit = new Unit($"folders|{string.Join("|", rels)}", Path.IsPathRooted(keepFull) ? keepFull : null, keepRel, $"folder {keepRel}", null, null);
            var allBackedUp = withLibrary && all.All(x => x.Row?.State == ContentState.BackedUp);
            var others = rels.Skip(1).ToList();
            var title = others.Count == 1
                ? $"{others[0]} is a copy of {keepRel}: {FindingRules.Count(members.Count, "identical file", "identical files")} ({FindingRules.Size(redundantBytes)})"
                : $"{rels.Count} copies of the same folder: {string.Join(", ", rels.Take(3))}{(rels.Count > 3 ? $" and {rels.Count - 3} more" : "")}";
            var f = NewFinding(job, FindingKind.Duplicate, string.Empty, unit, all, ResultCertainty.Certain, title,
                $"{FindingRules.Count(members.Count, "file of", "files of")} {keepRel} exist with identical content (same full SHA-256) in "
                + $"{string.Join(", ", others.Take(3))}{(others.Count > 3 ? $" and {others.Count - 3} more" : "")}; "
                + $"{(smallestShare >= 0.999 ? "the folders are the same" : $"at least {smallestShare:P0} of each folder is the same")}, "
                + $"{FindingRules.Count(redundantFiles, "copy", "copies")} with {FindingRules.Size(redundantBytes)} {(redundantFiles == 1 ? "is" : "are")} redundant.",
                DuplicateAction(withLibrary, allBackedUp, keepRel, "folder"), withComparison: withLibrary);
            f.Bytes = redundantBytes;
            f.Duplicates = new FindingDuplicates(members.Count, redundantFiles, redundantBytes,
                rels.Take(FindingDuplicatesMaxDirs).ToList(), rels.Count, keepRel, FindingDuplicates.FoldersScope);
            f.Directories = containers.Where(Path.IsPathRooted).ToList();
            yield return f;
        }
    }

    /// <summary>
    /// Keep one copy, the others can go (<c>delete_source</c>); without a library nothing is removed (<c>leave</c>),
    /// because no second place holds the content should the kept copy be the wrong one.
    /// </summary>
    private static FindingAction DuplicateAction(bool withLibrary, bool allBackedUp, string keepRel, string what) =>
        !withLibrary
            ? new FindingAction(PlanActionKind.Leave, $"Without a library no copy is removed; keep the {what} in {keepRel}.")
            : allBackedUp
                ? new FindingAction(PlanActionKind.DeleteSource, "Every copy is in the library with the same content already: the copies here can go.")
                : new FindingAction(PlanActionKind.DeleteSource, $"Keep the {what} in {keepRel}; the other copies can go.");

    private async Task<IReadOnlyList<Finding>> MisplacedAsync(
        ScanJob job, IReadOnlyList<FileFacts> facts, IReadOnlyList<DirectoryReadRow> dirs, BundlePatterns patterns,
        ComparisonHeader? lib, IReadOnlyDictionary<long, FileComparison>? libRows, IReadOnlyList<ScanJob> libraries,
        CancellationToken ct)
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
        if (lib is not null && libraries.Count > 0 && lookups.Count > 0)
        {
            try
            {
                targets = await _advisor.ProposeAsync(job.Id, libraries.Select(l => l.Id).ToList(),
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

            // A target lies in a library root or nowhere: never a folder of the user profile or the system drive.
            if (target is not null)
            {
                var home = libraries.FirstOrDefault(l => IsAtOrBelow(target.Path, Path.TrimEndingDirectorySeparator(l.RootPath)));
                if (home is null)
                {
                    _logger.LogWarning("Dropped a target outside every library root: {Path}", target.Path);
                    finding.Notes.Add("A proposed place outside every library root was dropped: targets lie only in the permanent storage.");
                    target = null;
                }
                else
                {
                    target = target with { LibraryJobId = home.Id, LibraryRoot = home.RootPath };
                }
            }

            if (target is not null)
            {
                finding.Target = target;
                finding.Action = new FindingAction(PlanActionKind.Move, $"Move to {target.RelativePath} in library job {target.LibraryJobId}: {target.Reason}");
            }
            else if (lib is not null && index < 0)
            {
                finding.Notes.Add($"No place looked up: only the {_options.MaxTargetLookups} largest groups are matched against the library (Findings:MaxTargetLookups).");
                finding.Action = new FindingAction(PlanActionKind.Leave, "Not matched against the library; look for a place with sfo library match.");
            }
            else if (lib is not null && index >= 0)
            {
                finding.Notes.Add(NoFolderFitsNote);
                finding.Action = new FindingAction(PlanActionKind.Leave, "No folder of the library fits these files; decide where they belong.");
            }

            finding.Certainty = FindingRules.MisplacedCertainty(fileCertainty, target);
            if (target is null || target.ContentCheck != TargetContentCheck.Fits)
                finding.Notes.Add(lib is null
                    ? "No permanent storage (library) is known: no place is proposed, and the finding stays uncertain."
                    : NotComparedNote);
        }

        return candidates.Select(c => c.Finding).ToList();
    }

    private const string NoFolderFitsNote = "No folder of the library fits these files.";
    private const string NotComparedNote = "The content of the proposed place was not compared with these files, so the finding stays uncertain at most.";

    /// <summary>
    /// Sets the stored target match on every finding that needs a place (https://github.com/crack666/smart_file_organizer/issues/83),
    /// as long as it rests on the same files, descriptions and libraries; otherwise the finding is <c>not_matched</c>. A
    /// target is kept only inside a library root of this run, as the places of the rules are. A marked file never gets one.
    /// </summary>
    private async Task ApplyStoredTargetsAsync(
        long jobId, List<Finding> findings, IReadOnlyList<FileFacts> facts,
        IReadOnlyDictionary<long, IReadOnlyList<FileFact>> factsByFile, IReadOnlyList<ScanJob> libraries, CancellationToken ct)
    {
        var targetFindings = findings.Where(f => FindingRules.TargetKinds.Contains(f.Kind)).ToList();
        if (targetFindings.Count == 0) return;
        var byId = facts.ToDictionary(x => x.File.Id);
        var libraryIds = libraries.Select(l => l.Id).ToList();
        IReadOnlyDictionary<string, FindingTargetRecord> stored = _targets is null || libraries.Count == 0
            ? new Dictionary<string, FindingTargetRecord>()
            : await _targets.GetAllAsync(jobId, ct);

        foreach (var finding in targetFindings)
        {
            var files = finding.FileIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            finding.TargetInput = TargetInputHash(files.Select(x => x.File.Id), factsByFile, libraryIds);
            finding.TargetStatus = FindingTargetStatus.NotMatched;
            if (files.Any(x => x.Match.IsMatch)) continue;
            if (!stored.TryGetValue(finding.Key, out var row) || row.InputHash != finding.TargetInput) continue;

            var fileCertainty = FindingRules.Weakest(
                files.Select(x => x.Value.Worth == FileWorth.None ? ResultCertainty.Uncertain : x.Value.Certainty), ResultCertainty.Uncertain);
            if (row.Status is FindingTargetStatus.ExistingFolder or FindingTargetStatus.NewFolder)
            {
                var home = row.Path is { } path
                    ? libraries.FirstOrDefault(l => l.Id == row.LibraryJobId && IsAtOrBelow(path, Path.TrimEndingDirectorySeparator(l.RootPath)))
                    : null;
                if (home is null)
                {
                    finding.TargetStatus = FindingTargetStatus.NoFittingFolder;
                    finding.Notes.Add("A matched place outside every library root was dropped: targets lie only in the permanent storage.");
                    continue;
                }

                var target = new FindingTarget(row.Path!, row.RelativePath ?? row.Path!, row.Certainty ?? ResultCertainty.Uncertain,
                    row.Reason, row.SameContentFiles, row.ContentCheck, home.Id, home.RootPath);
                finding.Target = target;
                finding.TargetStatus = row.Status;
                finding.Action = new FindingAction(PlanActionKind.Move,
                    $"Move to {target.RelativePath}{(row.Status == FindingTargetStatus.NewFolder ? " (a new folder)" : string.Empty)} in library job {home.Id}: {target.Reason}");
                finding.Notes.RemoveAll(n => n == NoFolderFitsNote || n.StartsWith("No place looked up", StringComparison.Ordinal));
                if (finding.Kind == FindingKind.Misplaced)
                {
                    finding.Certainty = FindingRules.MisplacedCertainty(fileCertainty, target);
                    if (target.ContentCheck == TargetContentCheck.Fits)
                        finding.Notes.Remove(NotComparedNote);
                }
                continue;
            }

            if (row.Status == FindingTargetStatus.Split)
            {
                // The files do not belong together (Issue 85): each part keeps its own place, inside a library root only.
                var parts = row.Parts.Select(p =>
                {
                    if (p.Status is not (FindingTargetStatus.ExistingFolder or FindingTargetStatus.NewFolder)) return p;
                    var inRoot = p.Path is { } partPath
                                 && libraries.Any(l => l.Id == p.LibraryJobId && IsAtOrBelow(partPath, Path.TrimEndingDirectorySeparator(l.RootPath)));
                    return inRoot ? p : new FindingTargetPart
                    {
                        Index = p.Index, FileIds = p.FileIds, Label = p.Label, Status = FindingTargetStatus.NoFittingFolder,
                        ContentCheck = p.ContentCheck, Reason = "A matched place outside every library root was dropped."
                    };
                }).ToList();
                finding.TargetStatus = FindingTargetStatus.Split;
                finding.TargetParts = parts;
                var placed = parts.Where(p => p.Status is FindingTargetStatus.ExistingFolder or FindingTargetStatus.NewFolder).ToList();
                finding.Notes.Add($"Split by content ({parts.Count} parts, {placed.Count} with a place): {row.Reason}");
                if (placed.Count > 0)
                {
                    finding.Action = new FindingAction(PlanActionKind.Move,
                        $"Move part by part: {string.Join("; ", placed.Take(3).Select(p => $"{p.FileIds.Count} files ({p.Label}) to {p.RelativePath}"))}"
                        + (placed.Count < parts.Count ? $"; {parts.Count - placed.Count} part(s) without a place stay." : "."));
                    finding.Notes.RemoveAll(n => n == NoFolderFitsNote || n.StartsWith("No place looked up", StringComparison.Ordinal));
                }
                if (finding.Kind == FindingKind.Misplaced && finding.Target is not null)
                {
                    // A place the rules alone proposed for the whole mixture does not count.
                    finding.Target = null;
                    if (placed.Count == 0)
                        finding.Action = new FindingAction(PlanActionKind.Leave, "The files do not belong together, and no part has a place; decide where they belong.");
                    finding.Certainty = FindingRules.MisplacedCertainty(fileCertainty, null);
                }
                continue;
            }

            finding.TargetStatus = row.Status;
            finding.Notes.Add($"No target ({row.Status.ToCode()}): {row.Reason}");
            // The match by content found no fitting folder: a place the rules alone proposed does not count either.
            if (row.Status == FindingTargetStatus.NoFittingFolder && finding.Kind == FindingKind.Misplaced && finding.Target is not null)
            {
                finding.Target = null;
                finding.Action = new FindingAction(PlanActionKind.Leave, "No folder of the library fits these files; decide where they belong.");
                finding.Certainty = FindingRules.MisplacedCertainty(fileCertainty, null);
            }
        }
    }

    /// <summary>
    /// The input hash of a target match for these files as the facts stand now (after the match described some of them;
    /// Issue 85), the same the findings compute.
    /// </summary>
    public async Task<string> TargetInputAsync(long jobId, IReadOnlyList<long> fileIds, IReadOnlyList<long> libraryIds, CancellationToken ct = default)
    {
        var facts = new Dictionary<long, IReadOnlyList<FileFact>>();
        foreach (var id in fileIds)
            facts[id] = await _facts.GetByFileNodeIdAsync(id, ct);
        return TargetInputHash(fileIds, facts, libraryIds);
    }

    /// <summary>What a target match rests on: the files, the description facts of each, and the libraries, in order.</summary>
    private static string TargetInputHash(
        IEnumerable<long> fileIds, IReadOnlyDictionary<long, IReadOnlyList<FileFact>> factsByFile, IReadOnlyList<long> libraries)
    {
        var sb = new StringBuilder("finding-target-v1\n");
        sb.Append(string.Join(",", libraries)).Append('\n');
        foreach (var id in fileIds.Order())
        {
            sb.Append(id).Append(':');
            if (factsByFile.TryGetValue(id, out var stored))
            {
                foreach (var fact in stored.Where(f => f.Key.IsDescription()).Select(f => f.Key.ToCode() + "=" + f.Value).Order(StringComparer.Ordinal))
                    sb.Append(fact).Append(';');
            }
            sb.Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
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
                    : "uncertain at most until a target is checked by content (sfo findings targets)"),
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
