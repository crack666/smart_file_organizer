using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Evaluation;

/// <summary>
/// Result of one evaluation: a score per kind and every failure on its own line. A failure names
/// the item, what the truth expects, what came out, and who decided it, so a wrong result can be
/// traced without opening the database.
/// </summary>
/// <param name="Untracked">Paths the scan found that the truth does not list (truth incomplete); first 50.</param>
/// <param name="Cases">Per-case results when the stored cases were run; null for a corpus run.</param>
public sealed record EvalReport(
    IReadOnlyList<EvalKindScore> Scores,
    IReadOnlyList<EvalFailure> Failures,
    int UntrackedCount,
    IReadOnlyList<string> Untracked,
    IReadOnlyList<EvalCaseResult>? Cases = null);

/// <summary>Counts for one kind. <c>Total = Hits + Wrong + Missing</c>; <c>Accuracy = Hits / Total</c> (null without items).</summary>
/// <param name="Sensitive">Extra counts for the kind <c>sensitive</c>; null for the others.</param>
/// <param name="Findings">Extra counts for the kind <c>finding</c>; null for the others.</param>
public sealed record EvalKindScore(
    EvalKind Kind,
    int Total,
    int Hits,
    int Wrong,
    int Missing,
    double? Accuracy,
    SensitiveCounts? Sensitive = null,
    FindingCounts? Findings = null);

/// <param name="Expected">Findings the corpus expects (<c>findings.json</c>).</param>
/// <param name="Listed">Findings the job lists.</param>
/// <param name="Superfluous">Listed findings no expectation asks for: noise a person would have to read.</param>
public sealed record FindingCounts(int Expected, int Listed, int Superfluous);

/// <param name="ExpectedMarked">Files the truth marks as sensitive.</param>
/// <param name="FullyFound">Of those, files that carry every expected marker.</param>
/// <param name="Missed">Of those, files that lack at least one expected marker.</param>
/// <param name="FalseAlarms">Files the truth does not mark, but that carry a marker.</param>
public sealed record SensitiveCounts(int ExpectedMarked, int FullyFound, int Missed, int FalseAlarms);

/// <summary>One item whose result differs from the truth.</summary>
/// <param name="Key">Stable identity of the item across runs, for example <c>file_category:Downloads/a.pdf</c>.</param>
/// <param name="Expected">Code(s) the truth expects; markers comma-separated, <c>none</c> for no marker.</param>
/// <param name="Actual">Code(s) that came out; null when there is no result.</param>
/// <param name="DecidedBy">Who produced the result: a rule (with its id), the model, or both.</param>
/// <param name="Detail">Why a result is missing, or which markers were missed or extra.</param>
public sealed record EvalFailure(
    EvalKind Kind,
    string Key,
    string Path,
    EvalOutcome Outcome,
    string? Expected,
    string? Actual,
    string? DecidedBy,
    string? Detail);

/// <summary>Outcome of one stored case when it was run again.</summary>
/// <param name="CorrectedFrom">What the model answered when the human corrected it.</param>
/// <param name="Actual">What the model answers now; null when there is no result.</param>
/// <param name="Sensitive">The file is sensitive: only path and markers were stored.</param>
public sealed record EvalCaseResult(
    long CaseId,
    EvalCaseKind Kind,
    string Path,
    string Expected,
    string? CorrectedFrom,
    string? Actual,
    EvalOutcome Outcome,
    bool Sensitive,
    string? Detail);

/// <summary>Collects outcomes per kind and builds the report.</summary>
public sealed class EvalReportBuilder
{
    private readonly Dictionary<EvalKind, int[]> _counts = Enum.GetValues<EvalKind>().ToDictionary(k => k, _ => new int[3]);
    private readonly List<EvalFailure> _failures = [];
    private readonly List<EvalCaseResult> _cases = [];
    private readonly List<string> _untracked = [];
    private int _untrackedCount;
    private int _expectedMarked, _fullyFound, _missed, _falseAlarms;
    private int _findingsExpected, _findingsListed, _findingsSuperfluous;

    public void Hit(EvalKind kind) => _counts[kind][(int)EvalOutcome.Hit]++;

    public void Fail(EvalFailure failure)
    {
        _counts[failure.Kind][(int)failure.Outcome]++;
        _failures.Add(failure);
    }

    public void Sensitive(SensitiveMarker expected, SensitiveMarker actual)
    {
        if (expected != SensitiveMarker.None)
        {
            _expectedMarked++;
            if ((actual & expected) == expected) _fullyFound++;
            else _missed++;
        }
        else if (actual != SensitiveMarker.None)
        {
            _falseAlarms++;
        }
    }

    public void Case(EvalCaseResult result) => _cases.Add(result);

    /// <summary>How many findings were expected and listed; superfluous ones come through <see cref="Superfluous"/>.</summary>
    public void Findings(int expected, int listed)
    {
        _findingsExpected = expected;
        _findingsListed = listed;
    }

    /// <summary>A finding no expectation asks for: listed as a failure, counted apart from <c>total</c>.</summary>
    public void Superfluous(EvalFailure failure)
    {
        _findingsSuperfluous++;
        _failures.Add(failure);
    }

    public void Untracked(string path)
    {
        _untrackedCount++;
        if (_untracked.Count < 50) _untracked.Add(path);
    }

    public EvalReport Build(IEnumerable<EvalKind> kinds, bool withCases = false)
    {
        var scores = kinds.Select(kind =>
        {
            var c = _counts[kind];
            var total = c.Sum();
            return new EvalKindScore(kind, total, c[0], c[1], c[2],
                total == 0 ? null : Math.Round((double)c[0] / total, 4),
                kind == EvalKind.Sensitive ? new SensitiveCounts(_expectedMarked, _fullyFound, _missed, _falseAlarms) : null,
                kind == EvalKind.Finding ? new FindingCounts(_findingsExpected, _findingsListed, _findingsSuperfluous) : null);
        }).ToList();
        var failures = _failures
            .OrderBy(f => f.Kind)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        return new EvalReport(scores, failures, _untrackedCount, _untracked, withCases ? _cases : null);
    }
}
