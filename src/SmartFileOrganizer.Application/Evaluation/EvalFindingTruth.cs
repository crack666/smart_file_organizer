using System.Text.Json;
using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Evaluation;

/// <summary>
/// One finding a corpus expects. <see cref="Path"/> is the <c>relative_path</c> of the finding; <see cref="Group"/>
/// (for <c>not_backed_up</c> and <c>in_noise</c>), <see cref="Scope"/> (for <c>duplicate</c>) and <see cref="Markers"/>
/// (for <c>sensitive</c>: exactly this set) narrow it when given.
/// </summary>
public sealed record ExpectedFinding(
    string Kind, string Path, string? Group = null, string? Scope = null, IReadOnlyList<string>? Markers = null, string? Note = null);

/// <summary>The file <c>findings.json</c> next to <c>truth.json</c>: every finding the corpus should give, and no other.</summary>
public sealed record EvalFindingTruthDocument(int SchemaVersion, IReadOnlyList<ExpectedFinding> Findings);

/// <summary>
/// The expected findings of a corpus, kept apart from <c>truth.json</c> so the truth of verdicts and categories and
/// the truth of findings can change on their own. Without the file, findings are not measured.
/// </summary>
public static class EvalFindingTruth
{
    public const string FileName = "findings.json";
    public const int SchemaVersion = 1;

    /// <summary>The expected findings next to <paramref name="truthPath"/>, or null when there is no such file.</summary>
    public static IReadOnlyList<ExpectedFinding>? LoadBeside(string truthPath)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(truthPath) ?? ".", FileName);
        if (!File.Exists(path)) return null;
        var doc = JsonSerializer.Deserialize<EvalFindingTruthDocument>(File.ReadAllText(path), EvalJson.Options)
                  ?? throw new InvalidDataException($"{path} is empty.");
        if (doc.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"{path}: schema_version {doc.SchemaVersion}, expected {SchemaVersion}.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in doc.Findings)
        {
            if (!VerdictCodes.TryParse<FindingKind>(f.Kind, out _))
                throw new InvalidDataException($"{path}: unknown kind '{f.Kind}' at {f.Path}.");
            if (!seen.Add(Identity(f)))
                throw new InvalidDataException($"{path}: {Identity(f)} is listed twice.");
        }
        return doc.Findings;
    }

    private static string Identity(ExpectedFinding f) => $"{f.Kind}:{EvalTruth.NormalizePath(f.Path)}{Narrowing(f.Group, f.Scope, f.Markers)}";

    private static string Narrowing(string? group, string? scope, IReadOnlyList<string>? markers) =>
        (group is null ? "" : ":" + group) + (scope is null ? "" : ":" + scope)
        + (markers is null ? "" : ":" + string.Join(",", markers.Order(StringComparer.Ordinal)));

    /// <summary>
    /// Scores the listed findings of a job (open and accepted, as <c>sfo findings</c> shows them) against the
    /// expected ones. An expectation is a hit when a listed finding has its kind, path and, where given, group and
    /// scope; <c>wrong</c> when a finding lies at its path with another kind, group or scope; else <c>missing</c>.
    /// Every listed finding that no expectation takes is <see cref="EvalOutcome.Superfluous"/>.
    /// </summary>
    public static void Score(EvalReportBuilder report, IReadOnlyList<ExpectedFinding> expected, IReadOnlyList<Finding> listed)
    {
        var open = listed.ToList();
        report.Findings(expected.Count, listed.Count);
        foreach (var e in expected.OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Kind, StringComparer.Ordinal))
        {
            var path = EvalTruth.NormalizePath(e.Path);
            var key = CorpusEvaluator.Key(EvalKind.Finding, $"{e.Kind}:{path}{Narrowing(e.Group, e.Scope, e.Markers)}");
            var match = open.FirstOrDefault(f => Matches(e, path, f));
            if (match is not null)
            {
                open.Remove(match);
                report.Hit(EvalKind.Finding);
                continue;
            }
            var near = listed.FirstOrDefault(f => EvalTruth.NormalizePath(f.RelativePath) == path);
            report.Fail(new EvalFailure(EvalKind.Finding, key, path, near is null ? EvalOutcome.Missing : EvalOutcome.Wrong,
                Describe(e.Kind, e.Group, e.Scope, e.Markers), near is null ? null : Describe(near), null,
                near is null ? "no finding at this path" : near.Title));
        }

        foreach (var f in open)
        {
            var path = EvalTruth.NormalizePath(f.RelativePath);
            report.Superfluous(new EvalFailure(EvalKind.Finding,
                CorpusEvaluator.Key(EvalKind.Finding, $"{f.Kind.ToCode()}:{path}:{f.Key}"), path, EvalOutcome.Superfluous,
                null, Describe(f), null, f.Title));
        }
    }

    private static bool Matches(ExpectedFinding e, string path, Finding f) =>
        f.Kind.ToCode() == e.Kind
        && EvalTruth.NormalizePath(f.RelativePath) == path
        && (e.Group is null || e.Group == f.Group)
        && (e.Scope is null || e.Scope == f.Duplicates?.Scope)
        && (e.Markers is null || e.Markers.Order(StringComparer.Ordinal).SequenceEqual(f.Markers.Order(StringComparer.Ordinal)));

    private static string Describe(Finding f) =>
        Describe(f.Kind.ToCode(), f.Group, f.Duplicates?.Scope, f.Markers.Count == 0 ? null : f.Markers);

    private static string Describe(string kind, string? group, string? scope, IReadOnlyList<string>? markers) =>
        kind + (group is null ? "" : "/" + group) + (scope is null ? "" : "/" + scope)
        + (markers is null ? "" : "/" + string.Join(",", markers.Order(StringComparer.Ordinal)));
}
