using System.Net;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Infrastructure.ModelServer;

/// <summary>Retries of one model call: how many, and the pauses between them.</summary>
/// <param name="MaxRetries">Attempts after the first one.</param>
/// <param name="RetryDelay">Pause before the first retry; doubles with each further one.</param>
/// <param name="MaxRetryAfter">Longest pause taken for a <c>Retry-After</c> of the server.</param>
public sealed record ModelHttpRetryPolicy(int MaxRetries, TimeSpan RetryDelay, TimeSpan MaxRetryAfter);

/// <summary>Every attempt of a call ran into its time limit while the server itself answers: an error of this request.</summary>
public sealed class ModelCallTimedOutException(TimeSpan limit, int attempts)
    : Exception($"No answer within {limit.TotalSeconds:0.#}s, {attempts} attempt(s).")
{
    public TimeSpan Limit { get; } = limit;
    public int Attempts { get; } = attempts;
}

/// <summary>
/// Sends one HTTP request to a model server and repeats it on failures that say the server is away rather than the
/// request is wrong (https://github.com/crack666/smart_file_organizer/issues/63): no connection or a broken one,
/// a time limit, HTTP 429, 502, 503 and 504. <c>Retry-After</c> is honoured up to a limit. Every other answer,
/// successful or not, is returned to the caller, which turns a refusal into an error of the item.
/// </summary>
/// <remarks>
/// Knows HTTP, nothing of Ollama: a second provider with another API uses it unchanged. Logs no request or
/// answer content. Pauses run on the <see cref="TimeProvider"/>, so tests need not wait.
/// </remarks>
public sealed class ModelHttpSender
{
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public ModelHttpSender(HttpClient http, TimeProvider? time, ILogger logger)
    {
        _http = http;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// Sends the request built by <paramref name="create"/> (called once per attempt) with the time limit
    /// <paramref name="timeout"/> per attempt; the answer is read completely before it is returned.
    /// </summary>
    /// <param name="probe">Asked after an attempt ran into its time limit: does the server answer a cheap request?
    /// If not, the server is away and the call ends at once; if so, the request is repeated, and when every attempt
    /// timed out, <see cref="ModelCallTimedOutException"/> says this request is the problem.</param>
    /// <param name="what">Short name of the call for the log, without any content.</param>
    /// <exception cref="ModelServerUnavailableException">No answer from the server after the retries.</exception>
    /// <exception cref="ModelCallTimedOutException">Every attempt timed out while the server answers the probe.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> create,
        TimeSpan timeout,
        ModelHttpRetryPolicy policy,
        Func<CancellationToken, Task<bool>>? probe,
        string what,
        CancellationToken ct)
    {
        var retries = Math.Max(0, policy.MaxRetries);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            ModelServerFailure failure;
            TimeSpan? retryAfter = null;
            Exception? error = null;

            using (var limit = new CancellationTokenSource(timeout, _time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token))
            {
                try
                {
                    using var request = create();
                    var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token);
                    if (FailureOf(response.StatusCode) is not { } byStatus)
                        return response;
                    failure = byStatus;
                    retryAfter = RetryAfterOf(response);
                    response.Dispose();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    // Our limit, or the client's own timeout: no answer in time.
                    failure = ModelServerFailure.TimedOut;
                    error = ex;
                    if (probe is not null && !await AnswersAsync(probe, ct))
                    {
                        throw new ModelServerUnavailableException(failure,
                            $"{what}: no answer within {timeout.TotalSeconds:0.#}s, and the server does not answer a probe either.", null, ex);
                    }
                    if (attempt >= retries)
                        throw new ModelCallTimedOutException(timeout, attempt + 1);
                }
                catch (HttpRequestException ex) when (ex.StatusCode is null)
                {
                    failure = ModelServerFailure.ConnectionFailed;
                    error = ex;
                }
                catch (IOException ex)
                {
                    failure = ModelServerFailure.ConnectionFailed;
                    error = ex;
                }
            }

            if (attempt >= retries)
            {
                throw new ModelServerUnavailableException(failure,
                    $"{what}: {Describe(failure)} after {attempt + 1} attempt(s).", retryAfter, error);
            }

            var pause = retryAfter is { } asked
                ? (asked < policy.MaxRetryAfter ? asked : policy.MaxRetryAfter)
                : policy.RetryDelay * Math.Pow(2, attempt);
            _logger.LogDebug("{What}: {Failure}, attempt {Attempt} of {Attempts}; next in {Pause:F1}s",
                what, failure.ToCode(), attempt + 1, retries + 1, pause.TotalSeconds);
            await Task.Delay(pause, _time, ct);
        }
    }

    /// <summary>The failure an HTTP status stands for when the server is away or busy; null for every other status.</summary>
    public static ModelServerFailure? FailureOf(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => ModelServerFailure.Busy,
        HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => ModelServerFailure.BadGateway,
        _ => null
    };

    /// <summary>The pause a <c>Retry-After</c> header asks for (seconds or a date), or null.</summary>
    public TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    private static async Task<bool> AnswersAsync(Func<CancellationToken, Task<bool>> probe, CancellationToken ct)
    {
        try
        {
            return await probe(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string Describe(ModelServerFailure failure) => failure switch
    {
        ModelServerFailure.ConnectionFailed => "no connection to the model server",
        ModelServerFailure.TimedOut => "no answer in time",
        ModelServerFailure.Busy => "the model server is busy",
        _ => "the gateway got no answer from the model server"
    };
}
