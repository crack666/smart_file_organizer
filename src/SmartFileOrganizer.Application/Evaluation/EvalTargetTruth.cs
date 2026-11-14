using System.Globalization;
using System.Text.Json;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Evaluation;

/// <summary>
/// What a picture or a document shows, as the model describes it (Issue 37). In <c>truth.json</c> it is the answer the
/// stub gives for a file; in <c>targets.json</c> the one it gives for a file of the library.
/// </summary>
public sealed record TruthDescription(
    string? Scene = null, string? Activity = null, string? Setting = null, string? Season = null, string? Occasion = null,
    bool? People = null, string? DocumentKind = null, string? Issuer = null, int? Year = null)
{
    public IReadOnlyList<FileFact> ToFacts()
    {
        var facts = new List<FileFact>();
        void Add(FileFactKey key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) facts.Add(new FileFact(key, value.Trim(), FactSource.Model));
        }
        Add(FileFactKey.Scene, Scene);
        Add(FileFactKey.Activity, Activity);
        Add(FileFactKey.Setting, Setting);
        Add(FileFactKey.Season, Season);
        Add(FileFactKey.Occasion, Occasion);
        if (People is { } p) Add(FileFactKey.People, p ? "true" : "false");
        Add(FileFactKey.DocumentKind, DocumentKind);
        Add(FileFactKey.Issuer, Issuer);
        if (Year is { } y) Add(FileFactKey.Year, y.ToString(CultureInfo.InvariantCulture));
        return facts;
    }
}

/// <summary>A file of the library of the corpus and what it shows (the stub's answer when the match samples it).</summary>
public sealed record LibraryFileEntry(string Path, TruthDescription Description, string? Note = null);

/// <summary>
/// Where a unit of the corpus belongs. <see cref="Unit"/> is <c>bundle</c> (the bundle that holds the file at
/// <see cref="Path"/>), <c>file</c> or <c>folder</c>. <see cref="Destination"/> is the folder of the library relative to
/// its root; null means no existing folder fits: a new folder or no target at all is right (Issue 85: rather no
/// target than a wrong one). <see cref="Outcome"/> <c>library_unreachable</c> (Issue 85) expects the match against a
/// library that is gone to fail with that named error instead of answering.
/// </summary>
public sealed record ExpectedTarget(string Unit, string Path, string? Destination, string? Note = null, string? Outcome = null);

/// <summary>The file <c>targets.json</c> next to <c>truth.json</c>: a library with known truth and where things belong in it.</summary>
/// <param name="Library">Folder next to the truth file that is scanned as the library.</param>
public sealed record EvalTargetTruthDocument(int SchemaVersion, string Library, IReadOnlyList<LibraryFileEntry> LibraryFiles, IReadOnlyList<ExpectedTarget> Targets);

/// <summary>What <c>sfo library match</c> proposed for one expected target.</summary>
/// <param name="Destination">The best existing folder (relative to the library root), or the new folder.</param>
/// <param name="NoTarget">Why no folder is proposed at all (Issue 85), when that is the answer.</param>
/// <param name="Failure">The code of a named failure of the match, such as <c>library_unreachable</c>.</param>
public sealed record TargetResult(ExpectedTarget Expected, string? Destination, bool NewFolder, string? Certainty, string? Reason,
    string? DecidedBy, string? Error, string? NoTarget = null, string? Failure = null);

/// <summary>
/// Target proposals against the truth of a library (https://github.com/crack666/smart_file_organizer/issues/56). Kept
/// apart from <c>truth.json</c>, like the findings: without the file, targets are not measured.
/// </summary>
public static class EvalTargetTruth
{
    public const string FileName = "targets.json";
    public const int SchemaVersion = 1;

    public static readonly string[] Units = ["bundle", "file", "folder"];

    /// <summary>The expected outcome of a match against a library that cannot be read (Issue 85).</summary>
    public const string LibraryUnreachable = "library_unreachable";
    public static readonly string[] Outcomes = [LibraryUnreachable];

    public static EvalTargetTruthDocument? LoadBeside(string truthPath)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(truthPath) ?? ".", FileName);
        if (!File.Exists(path)) return null;
        var doc = JsonSerializer.Deserialize<EvalTargetTruthDocument>(File.ReadAllText(path), EvalJson.Options)
                  ?? throw new InvalidDataException($"{path} is empty.");
        if (doc.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"{path}: schema_version {doc.SchemaVersion}, expected {SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(doc.Library) || System.IO.Path.IsPathRooted(doc.Library) || doc.Library.Contains(".."))
            throw new InvalidDataException($"{path}: library must be a folder name next to the truth file.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in doc.Targets)
        {
            if (!Units.Contains(t.Unit))
                throw new InvalidDataException($"{path}: unknown unit '{t.Unit}' at {t.Path} ({string.Join(", ", Units)}).");
            if (t.Outcome is { } o && !Outcomes.Contains(o))
                throw new InvalidDataException($"{path}: unknown outcome '{o}' at {t.Path} ({string.Join(", ", Outcomes)}).");
            if (!seen.Add($"{t.Unit}:{EvalTruth.NormalizePath(t.Path)}:{t.Outcome}"))
                throw new InvalidDataException($"{path}: {t.Unit} {t.Path} is listed twice.");
        }
        return doc;
    }

    /// <summary>The descriptions of the library files by their path relative to the library root.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<FileFact>> Descriptions(EvalTargetTruthDocument doc) =>
        doc.LibraryFiles.ToDictionary(f => EvalTruth.NormalizePath(f.Path), f => f.Description.ToFacts(), StringComparer.Ordinal);

    /// <summary>
    /// A hit when the best proposal is the expected folder, or a new folder or no target where none fits, or the named
    /// failure that was expected. Anything else is <c>wrong</c> and named with what was proposed and why; no answer at
    /// all is <c>missing</c>.
    /// </summary>
    public static void Score(EvalReportBuilder report, IReadOnlyList<TargetResult> results)
    {
        foreach (var r in results.OrderBy(r => r.Expected.Path, StringComparer.Ordinal).ThenBy(r => r.Expected.Outcome, StringComparer.Ordinal))
        {
            var path = EvalTruth.NormalizePath(r.Expected.Path);
            var key = CorpusEvaluator.Key(EvalKind.Target, r.Expected.Outcome is { } o ? $"{r.Expected.Unit}:{path}:{o}" : $"{r.Expected.Unit}:{path}");
            if (r.Expected.Outcome is { } outcome)
            {
                if (r.Failure == outcome)
                    report.Hit(EvalKind.Target);
                else
                    report.Fail(new EvalFailure(EvalKind.Target, key, path, EvalOutcome.Wrong, outcome,
                        r.Failure ?? (r.NewFolder ? $"new folder {r.Destination}" : r.Destination ?? (r.NoTarget is null ? "nothing" : "no target")),
                        r.DecidedBy, r.Error ?? r.NoTarget ?? $"{r.Certainty}: {r.Reason}"));
                continue;
            }
            var want = r.Expected.Destination is { } d ? EvalTruth.NormalizePath(d) : "new folder";
            if (r.Expected.Destination is null && r.NoTarget is not null && r.Error is null)
            {
                report.Hit(EvalKind.Target);
                continue;
            }
            if (r.Error is not null || r.Destination is null && !r.NewFolder)
            {
                report.Fail(new EvalFailure(EvalKind.Target, key, path, EvalOutcome.Missing, want, r.Failure, r.DecidedBy, r.Error ?? r.NoTarget ?? "no proposal"));
                continue;
            }
            var got = r.NewFolder ? $"new folder {r.Destination}" : r.Destination!;
            var hit = r.Expected.Destination is null ? r.NewFolder : !r.NewFolder && r.Destination == want;
            if (hit)
                report.Hit(EvalKind.Target);
            else
                report.Fail(new EvalFailure(EvalKind.Target, key, path, EvalOutcome.Wrong, want, got, r.DecidedBy,
                    $"{r.Certainty}: {r.Reason}"));
        }
    }
}

/// <summary>
/// Runs <c>sfo library match</c> for every expected target of a corpus against its library, with the content check
/// when a model (or the stub) is there. Reads only; writes the folder descriptions and the model's answers like any
/// match.
/// </summary>
public sealed class TargetEvaluator
{
    private readonly LibraryTargetService _targets;
    private readonly IBundleRepository _bundles;
    private readonly IJobReadRepository _reads;
    private readonly IScanJobRepository _jobs;

    public TargetEvaluator(LibraryTargetService targets, IBundleRepository bundles, IJobReadRepository reads, IScanJobRepository jobs)
    {
        _targets = targets;
        _bundles = bundles;
        _reads = reads;
        _jobs = jobs;
    }

    /// <param name="unreachableLibraryJobId">A library job whose folder is gone, matched for the targets that expect
    /// <c>library_unreachable</c> (Issue 85); without it those targets are missing.</param>
    public async Task<IReadOnlyList<TargetResult>> RunAsync(long sourceJobId, long libraryJobId, IReadOnlyList<ExpectedTarget> expected,
        bool useModel, CancellationToken ct = default, long? unreachableLibraryJobId = null)
    {
        var source = await _jobs.GetByIdAsync(sourceJobId, ct) ?? throw new InvalidOperationException($"Job {sourceJobId} not found.");
        var membership = await _bundles.GetMembershipAsync(sourceJobId, ct);
        var results = new List<TargetResult>();
        foreach (var e in expected)
        {
            ct.ThrowIfCancellationRequested();
            var relative = EvalTruth.NormalizePath(e.Path);
            if (e.Outcome == EvalTargetTruth.LibraryUnreachable && unreachableLibraryJobId is null)
            {
                results.Add(new TargetResult(e, null, false, null, null, null, "no unreachable library to match against"));
                continue;
            }
            var library = e.Outcome == EvalTargetTruth.LibraryUnreachable ? unreachableLibraryJobId!.Value : libraryJobId;
            var query = new LibraryMatchQuery { SourceJobId = sourceJobId, Libraries = [library], UseModel = useModel, Limit = 1 };
            if (e.Unit == "bundle")
            {
                var full = Path.Combine(source.RootPath, relative.Replace('/', Path.DirectorySeparatorChar));
                var file = await _reads.GetFileByPathAsync(sourceJobId, full, ct);
                if (file is null || !membership.TryGetValue(file.File.Id, out var bundleId))
                {
                    results.Add(new TargetResult(e, null, false, null, null, null, file is null ? "file not in the scan" : "the file is in no bundle"));
                    continue;
                }
                query = query with { BundleId = bundleId };
            }
            else
            {
                query = query with { Path = relative };
            }

            try
            {
                var answer = await _targets.MatchAsync(query, ct);
                var content = answer.Result?.Content;
                var by = content is null ? null
                    : content.Status == ContentCheckStatus.Checked ? $"content {content.Note ?? "checked"}" : $"content {content.Status.ToCode()}";
                if (answer.Result?.Candidates is { Count: > 0 } candidates)
                    results.Add(new TargetResult(e, candidates[0].Destination, false, candidates[0].Certainty.ToCode(), candidates[0].Reason, by, null));
                else if (answer.Result?.NewFolder is { } n)
                    results.Add(new TargetResult(e, n.Destination, true, n.Certainty.ToCode(), n.Reason, by, null));
                else if (answer.Result?.NoTarget is { } none)
                    results.Add(new TargetResult(e, null, false, null, null, by, null, NoTarget: none));
                else
                    results.Add(new TargetResult(e, null, false, null, null, by, answer.Message ?? "no proposal"));
            }
            catch (LibraryServiceException ex) when (ex.Failure == LibraryFailure.LibraryUnreachable)
            {
                results.Add(new TargetResult(e, null, false, null, null, null,
                    e.Outcome == EvalTargetTruth.LibraryUnreachable ? null : ex.Message, Failure: EvalTargetTruth.LibraryUnreachable));
            }
            catch (LibraryServiceException ex)
            {
                results.Add(new TargetResult(e, null, false, null, null, null, ex.Message));
            }
        }
        return results;
    }
}
