using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>A report about waiting for the model server, for progress output and the GUI.</summary>
/// <param name="Since">When the server was first considered away (UTC).</param>
/// <param name="Waited">How long the run has waited so far.</param>
public sealed record ModelServerWaitEvent(ModelServerWaitState State, DateTime Since, ModelServerFailure Cause, TimeSpan Waited);

/// <summary>The model server stayed away longer than <see cref="ModelServerOptions.MaxWait"/>; the run ends.</summary>
public sealed class ModelServerLostException(DateTime since, ModelServerFailure cause, TimeSpan waited)
    : Exception($"The model server did not answer for {waited.TotalMinutes:F1} min (since {since:u}, {cause.ToCode()}).")
{
    public DateTime Since { get; } = since;
    public ModelServerFailure Cause { get; } = cause;
    public TimeSpan Waited { get; } = waited;
}

/// <summary>How one model call through the gate ended.</summary>
public enum ModelCallState
{
    /// <summary>The call returned; its value is the result of the item (which may itself carry an error).</summary>
    Done,
    /// <summary>The server was away: nothing is stored, the item stays open and is sent again.</summary>
    Open,
    /// <summary>The server failed every time this item was sent; the item is stored with <see cref="ModelCallOutcome{T}.Error"/>.</summary>
    GaveUp
}

public readonly record struct ModelCallOutcome<T>(ModelCallState State, T? Value, string? Error)
{
    public static ModelCallOutcome<T> Done(T value) => new(ModelCallState.Done, value, null);
    public static ModelCallOutcome<T> Open => new(ModelCallState.Open, default, null);
    public static ModelCallOutcome<T> GaveUp(string error) => new(ModelCallState.GaveUp, default, error);
}

/// <summary>
/// Holds the model calls of one run while the model server is away
/// (https://github.com/crack666/smart_file_organizer/issues/63). Every phase sends its calls through
/// <see cref="CallAsync{T}"/>; the gate counts calls in a row that found no server
/// (<see cref="ModelServerUnavailableException"/>), and from <see cref="ModelServerOptions.OutageAfterFailures"/>
/// on it closes: calls in flight are cancelled (their items stay open), new calls wait, and one probe loop asks
/// the server every <see cref="ModelServerOptions.ProbeInterval"/> whether server and model are back. Then the
/// model is loaded once with a generous time limit and the gate opens again. The episode ends with the first
/// call that succeeds; if it lasts longer than <see cref="ModelServerOptions.MaxWait"/>, every waiting caller
/// gets <see cref="ModelServerLostException"/>.
/// </summary>
/// <remarks>
/// Provider-independent: it sees only <see cref="IOllamaService"/> and the exception of its contract, so a second
/// model provider reuses it unchanged. Thread-safe; one instance per run.
/// </remarks>
public sealed class ModelServerGate
{
    private readonly IOllamaService _ollama;
    private readonly ModelServerOptions _options;
    private readonly long _jobId;
    private readonly IModelServerWaitRepository? _waits;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Action<ModelServerWaitEvent>? _onEvent;
    private readonly CancellationToken _runToken;

    private readonly object _sync = new();
    private readonly ConcurrentDictionary<object, int> _failuresPerItem = new();
    private int _consecutiveFailures;
    private DateTime? _downSince;
    private ModelServerFailure _cause;
    private Task? _waitRound;
    private ModelServerLostException? _lost;
    private CancellationTokenSource _inFlight = new();

    public ModelServerGate(
        IOllamaService ollama,
        ModelServerOptions options,
        long jobId,
        IModelServerWaitRepository? waits,
        TimeProvider? time,
        ILogger logger,
        Action<ModelServerWaitEvent>? onEvent,
        CancellationToken runToken)
    {
        _ollama = ollama;
        _options = options;
        _jobId = jobId;
        _waits = waits;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _onEvent = onEvent;
        _runToken = runToken;
    }

    /// <summary>Calls that ended open because the server was away, over the whole run.</summary>
    public int LeftOpen => Volatile.Read(ref _leftOpen);
    private int _leftOpen;

    /// <summary>
    /// Sends one model call for <paramref name="item"/> (any key that identifies the item within the run). Waits
    /// first if the gate is closed. Throws <see cref="ModelServerLostException"/> when the wait exceeds its limit,
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<ModelCallOutcome<T>> CallAsync<T>(object item, Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        await WaitWhileClosedAsync(ct);

        CancellationToken inFlight;
        lock (_sync) inFlight = _inFlight.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, inFlight);
        try
        {
            var value = await call(linked.Token);
            Succeeded();
            return ModelCallOutcome<T>.Done(value);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && inFlight.IsCancellationRequested)
        {
            // Cancelled because another call found the server away: not this item's fault, nothing stored.
            Interlocked.Increment(ref _leftOpen);
            await WaitWhileClosedAsync(ct);
            return ModelCallOutcome<T>.Open;
        }
        catch (ModelServerUnavailableException ex) when (!ct.IsCancellationRequested)
        {
            // Counted against the item only while the server was thought to be up: then this item may be what
            // brings it down (a request that crashes the server every time). Busy says nothing about the item.
            bool wasUp;
            lock (_sync) wasUp = _downSince is null;
            var against = wasUp && ex.Failure != ModelServerFailure.Busy
                ? _failuresPerItem.AddOrUpdate(item, 1, (_, n) => n + 1)
                : _failuresPerItem.GetValueOrDefault(item);

            _logger.LogWarning("Model server did not answer ({Cause}): {Message}", ex.Failure.ToCode(), ex.Message);
            await FailedAsync(ex);
            await WaitWhileClosedAsync(ct);

            if (against >= Math.Max(1, _options.MaxOutagesPerItem))
            {
                return ModelCallOutcome<T>.GaveUp(
                    $"The model server stopped answering {against} times while this item was sent ({ex.Failure.ToCode()}); " +
                    "stored as an error so the run can finish.");
            }
            Interlocked.Increment(ref _leftOpen);
            return ModelCallOutcome<T>.Open;
        }
    }

    /// <summary>Returns when the gate is open; throws when the wait ends without the server.</summary>
    public async Task WaitWhileClosedAsync(CancellationToken ct)
    {
        Task? round;
        lock (_sync)
        {
            if (_lost is not null) throw _lost;
            round = _waitRound;
        }
        if (round is not null)
            await round.WaitAsync(ct);
    }

    private void Succeeded()
    {
        DateTime? since;
        ModelServerFailure cause;
        lock (_sync)
        {
            _consecutiveFailures = 0;
            since = _downSince;
            cause = _cause;
            _downSince = null;
        }
        if (since is { } s)
            _logger.LogInformation("Model server answers again after {Seconds:F0}s ({Cause}).", (Now() - s).TotalSeconds, cause.ToCode());
    }

    private Task FailedAsync(ModelServerUnavailableException ex)
    {
        lock (_sync)
        {
            _consecutiveFailures++;
            if (_downSince is null && _consecutiveFailures >= Math.Max(1, _options.OutageAfterFailures))
            {
                _downSince = Now();
                _cause = ex.Failure;
            }
            if (_downSince is not null && _waitRound is null)
            {
                // Calls still in flight are cut off: a hanging server would otherwise hold each until its timeout.
                _inFlight.Cancel();
                _inFlight.Dispose();
                _inFlight = new CancellationTokenSource();
                _waitRound = WaitRoundAsync(ex.RetryAfter);
            }
            return _waitRound ?? Task.CompletedTask;
        }
    }

    /// <summary>One closed period of the gate: probe until server and model answer, or give up.</summary>
    private async Task WaitRoundAsync(TimeSpan? retryAfter)
    {
        // Leave the lock of the caller before the first await.
        await Task.Yield();
        DateTime since;
        ModelServerFailure cause;
        lock (_sync) { since = _downSince!.Value; cause = _cause; }

        try
        {
            _logger.LogWarning("Waiting for the model server since {Since:u} ({Cause}); up to {Minutes:F0} min.",
                since, cause.ToCode(), _options.MaxWait.TotalMinutes);
            await SaveWaitAsync(new ModelServerWait { JobId = _jobId, Since = since, CheckedAt = Now(), Cause = cause });
            Report(ModelServerWaitState.Waiting, since, cause);

            var delay = Max(_options.ProbeInterval, Min(retryAfter ?? TimeSpan.Zero, _options.MaxRetryAfter));
            while (true)
            {
                var remaining = _options.MaxWait - (Now() - since);
                await Task.Delay(Max(TimeSpan.Zero, Min(delay, remaining)), _time, _runToken);
                delay = _options.ProbeInterval;

                if (await ProbeAsync())
                    break;
                if (Now() - since >= _options.MaxWait)
                {
                    var waited = Now() - since;
                    _logger.LogError("The model server stayed away for {Minutes:F1} min; the run stops. Items not judged stay open.",
                        waited.TotalMinutes);
                    Report(ModelServerWaitState.GivenUp, since, cause);
                    var lost = new ModelServerLostException(since, cause, waited);
                    lock (_sync) _lost = lost;
                    throw lost;
                }
                await TouchWaitAsync();
            }

            await WarmAsync();
            _logger.LogInformation("Model server and model answer again; the run continues.");
            Report(ModelServerWaitState.Resumed, since, cause);
        }
        finally
        {
            await ClearWaitAsync();
            lock (_sync) _waitRound = null;
        }
    }

    private async Task<bool> ProbeAsync()
    {
        try
        {
            return await _ollama.IsReadyAsync(_runToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_runToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// After a restart the model is not loaded and the first call would take as long as loading it. Loading it here,
    /// with its own long limit, keeps that out of the time limit of a file. A failure is not fatal: the calls
    /// that follow find out whether the server really answers.
    /// </summary>
    private async Task WarmAsync()
    {
        try
        {
            using var limit = new CancellationTokenSource(_options.WarmupTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_runToken, limit.Token);
            await _ollama.WarmModelAsync(null, linked.Token);
        }
        catch (Exception ex) when (!_runToken.IsCancellationRequested)
        {
            _logger.LogWarning("Loading the model after the wait failed ({Type}); the run continues anyway.", ex.GetType().Name);
        }
    }

    private void Report(ModelServerWaitState state, DateTime since, ModelServerFailure cause)
    {
        try { _onEvent?.Invoke(new ModelServerWaitEvent(state, since, cause, Now() - since)); }
        catch (Exception ex) { _logger.LogWarning(ex, "A wait report could not be delivered."); }
    }

    // The stored wait is for other processes (sfo status); a database hiccup must not end the run.
    private async Task SaveWaitAsync(ModelServerWait wait)
    {
        if (_waits is null) return;
        try { await _waits.SetAsync(wait, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning("The wait could not be stored: {Error}", ex.Message); }
    }

    private async Task TouchWaitAsync()
    {
        if (_waits is null) return;
        try { await _waits.TouchAsync(_jobId, Now(), CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning("The wait could not be stored: {Error}", ex.Message); }
    }

    private async Task ClearWaitAsync()
    {
        if (_waits is null) return;
        try { await _waits.ClearAsync(_jobId, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning("The wait could not be cleared: {Error}", ex.Message); }
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
