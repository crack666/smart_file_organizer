using System.Text.Json;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Commands;
using SmartFileOrganizer.Cli.Contract;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// The time left from the model calls of a run (https://github.com/crack666/smart_file_organizer/issues/71): known after
/// the first call, without waiting for the rate's first seconds.
/// </summary>
public class ProgressByCallsTests
{
    [Fact]
    public void Classification_CountsSecondChecks_AndSpreadsOverTheParallelCalls()
    {
        // 10 files done: 8 classification calls of 2 s and 2 second checks of 4 s, 24 s in all, 2.4 s per file.
        IReadOnlyList<ModelCallUsage> calls =
        [
            new(ModelCallKinds.PreAssessment, 5, 0, 0, 0, 10.0, 0),
            new(ModelCallKinds.Classification, 8, 0, 0, 0, 2.0, 0),
            new(ModelCallKinds.SecondCheck, 2, 0, 0, 0, 4.0, 0)
        ];

        Assert.Equal(240, AnalyzeCommand.RemainingByCalls(calls, "classification", 10, 100, 1));
        Assert.Equal(120, AnalyzeCommand.RemainingByCalls(calls, "classification", 10, 100, 2));
        Assert.Equal(20, AnalyzeCommand.RemainingByCalls(calls, "pre_assessment", 5, 2, 1));
    }

    [Fact]
    public void NoCallsYet_NoTimes_OrNothingOpen_GiveNoEstimate()
    {
        IReadOnlyList<ModelCallUsage> noTimes = [new(ModelCallKinds.Classification, 3, 0, 0, 0, 0.0, 0)];
        IReadOnlyList<ModelCallUsage> some = [new(ModelCallKinds.Classification, 3, 0, 0, 0, 1.0, 0)];

        Assert.Null(AnalyzeCommand.RemainingByCalls([], "classification", 3, 10, 1));
        Assert.Null(AnalyzeCommand.RemainingByCalls(noTimes, "classification", 3, 10, 1));
        Assert.Null(AnalyzeCommand.RemainingByCalls(some, "summary", 3, 10, 1));
        Assert.Null(AnalyzeCommand.RemainingByCalls(some, "classification", 0, 10, 1));
        Assert.Null(AnalyzeCommand.RemainingByCalls(some, "classification", 3, 0, 1));
        Assert.Null(AnalyzeCommand.RemainingByCalls(some, "scan", 3, 10, 1));
    }

    [Fact]
    public void ProgressLine_CarriesTheEstimate_FromTheFirstEvent()
    {
        var json = new StringWriter();
        var jsonl = new ProgressReporter(ProgressMode.Jsonl, new StderrChannel(json))
        {
            RemainingByCalls = (phase, done, open) => phase == "classification" ? done * 1000 + open : null
        };
        jsonl.Report("classification", 0, 10, 0);
        jsonl.Report("classification", 4, 10, 0);
        jsonl.Report("classification", 10, 10, 0);

        var lines = json.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // The second event falls into the 500 ms throttle; the first and the final one are written.
        Assert.Equal(2, lines.Length);
        using (var first = JsonDocument.Parse(lines[0]))
            Assert.Equal(10, first.RootElement.GetProperty("remaining_seconds_by_calls").GetInt64());
        using (var last = JsonDocument.Parse(lines[1]))
            Assert.Equal(JsonValueKind.Null, last.RootElement.GetProperty("remaining_seconds_by_calls").ValueKind);

        var text = new StringWriter();
        var plain = new ProgressReporter(ProgressMode.Text, new StderrChannel(text)) { RemainingByCalls = (_, _, _) => 600 };
        plain.Report("classification", 2, 50, 0);
        Assert.Contains("[classification] 2/50, 48 open, about 10 min left by model calls", text.ToString(), StringComparison.Ordinal);
    }
}
