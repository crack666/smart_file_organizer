using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// The gate alone, without a pipeline around it (https://github.com/crack666/smart_file_organizer/issues/63).
/// Waits are a few milliseconds.
/// </summary>
public sealed class ModelServerGateTests
{
    /// <summary>A server that always answers the probe; the calls themselves are scripted per test.</summary>
    private sealed class ReadyServer : IOllamaService
    {
        public int Probes;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Probes);
            return Task.FromResult(true);
        }

        public Task WarmModelAsync(string? model = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task<FileClassification> ClassifyAsync(OllamaClassificationInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DirectoryPreAssessmentOutcome> PreAssessDirectoryAsync(DirectoryPreAssessmentInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DirectoryClassificationResult> SummarizeDirectoryAsync(DirectorySummaryInput input, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static readonly ModelServerOptions Options = new()
    {
        OutageAfterFailures = 2,
        ProbeIntervalSeconds = 0.001,
        MaxWaitMinutes = 0.5,
        WarmupTimeoutSeconds = 1,
        MaxOutagesPerItem = 3
    };

    private static Task<int> Refused(CancellationToken _) =>
        throw new ModelServerUnavailableException(ModelServerFailure.ConnectionFailed, "refused", null, null);

    [Fact]
    public async Task TheLastItem_ThatBringsTheServerDown_EndsAsAnError_EvenWithoutAnotherCallInBetween()
    {
        // The crashing file is the only one still sent: its failures follow each other with no success of another
        // file in between. The server answers every probe, so each wait ends at once. Until 13.11.2026 the gate then
        // still counted the server as down, so no later failure counted against the item and the run never ended.
        var server = new ReadyServer();
        var gate = new ModelServerGate(server, Options, 1, null, TimeProvider.System, NullLogger.Instance, null, CancellationToken.None);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var outcomes = new List<ModelCallState>();
        for (var i = 0; i < 10 && outcomes.LastOrDefault() != ModelCallState.GaveUp; i++)
            outcomes.Add((await gate.CallAsync("plan.txt", Refused, limit.Token)).State);

        Assert.Equal(ModelCallState.GaveUp, outcomes.Last());
        Assert.Equal(Options.MaxOutagesPerItem, outcomes.Count);
        Assert.True(server.Probes >= 1);
    }

    [Fact]
    public async Task AfterTheServerIsBack_ANewOutageNeedsFailuresInARowAgain()
    {
        var server = new ReadyServer();
        var events = new List<ModelServerWaitState>();
        var gate = new ModelServerGate(server, Options, 1, null, TimeProvider.System, NullLogger.Instance,
            e => { lock (events) events.Add(e.State); }, CancellationToken.None);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Two failures of two items close the gate; the probe finds the server back.
        await gate.CallAsync("a", Refused, limit.Token);
        await gate.CallAsync("b", Refused, limit.Token);
        Assert.Equal([ModelServerWaitState.Waiting, ModelServerWaitState.Resumed], events);

        // One more failure is not yet a new outage.
        await gate.CallAsync("c", Refused, limit.Token);
        Assert.Equal(2, events.Count);
        Assert.Equal(42, (await gate.CallAsync("d", _ => Task.FromResult(42), limit.Token)).Value);
    }
}
