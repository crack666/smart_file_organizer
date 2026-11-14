using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// The outcomes of <c>targets.json</c> added for https://github.com/crack666/smart_file_organizer/issues/85: a unit
/// expected to be split, left out, or without any target is a hit only with exactly that answer.
/// </summary>
public class EvalTargetOutcomeTests
{
    private static (int Hits, IReadOnlyList<EvalFailure> Failures) Score(params TargetResult[] results)
    {
        var report = new EvalReportBuilder();
        EvalTargetTruth.Score(report, results);
        var built = report.Build([EvalKind.Target]);
        return (built.Scores.Single(s => s.Kind == EvalKind.Target).Hits, built.Failures);
    }

    private static ExpectedTarget Expect(string outcome) => new("folder", "Import/x", null, null, outcome);

    [Fact]
    public void Split_IsAHitOnlyWhenTheUnitWasMatchedPartByPart()
    {
        var (hits, _) = Score(new TargetResult(Expect(EvalTargetTruth.Split), null, false, null, null, null, null, NoTarget: "mixture", Parts: 2));
        Assert.Equal(1, hits);
        var (missHits, failures) = Score(new TargetResult(Expect(EvalTargetTruth.Split), "Bilder/Ski", false, "uncertain", "time", null, null));
        Assert.Equal(0, missHits);
        Assert.Equal("Bilder/Ski (whole)", Assert.Single(failures).Actual);
    }

    [Fact]
    public void Excluded_AndNoTarget_AreHitsOnlyWithThatAnswer()
    {
        Assert.Equal(1, Score(new TargetResult(Expect(EvalTargetTruth.Excluded), null, false, null, null, null, null, NoTarget: "system", Excluded: "system")).Hits);
        Assert.Equal(0, Score(new TargetResult(Expect(EvalTargetTruth.Excluded), null, false, null, null, null, null, NoTarget: "no level")).Hits);

        Assert.Equal(1, Score(new TargetResult(Expect(EvalTargetTruth.NoTargetOutcome), null, false, null, null, null, null, NoTarget: "nothing names it")).Hits);
        var (hits, failures) = Score(new TargetResult(Expect(EvalTargetTruth.NoTargetOutcome), "Bilder/{occasion} 2013", true, "review", "new", null, null));
        Assert.Equal(0, hits);
        Assert.Equal("new folder Bilder/{occasion} 2013", Assert.Single(failures).Actual);
    }
}
