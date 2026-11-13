using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Infrastructure.ModelServer;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Which answers of a model server are retried and which go back to the caller
/// (https://github.com/crack666/smart_file_organizer/issues/63). Pauses are a few milliseconds.
/// </summary>
public sealed class ModelHttpSenderTests
{
    private static readonly ModelHttpRetryPolicy Policy = new(2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(20));

    private sealed class Scripted(Func<int, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public int Calls;

        /// <summary>Never answers; ends only when the request is cancelled, like a hung server.</summary>
        public static Scripted Hanging() => new(_ => null!);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Calls);
            var task = answer(n);
            if (task is null)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            }
            return await task;
        }
    }

    private static ModelHttpSender Sender(Scripted handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://model-server.test/"), Timeout = Timeout.InfiniteTimeSpan },
            TimeProvider.System, NullLogger.Instance);

    private static Task<HttpResponseMessage> Send(ModelHttpSender sender, TimeSpan? timeout = null,
        Func<CancellationToken, Task<bool>>? probe = null, ModelHttpRetryPolicy? policy = null) =>
        sender.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "api/chat"), timeout ?? TimeSpan.FromSeconds(5),
            policy ?? Policy, probe, "test", CancellationToken.None);

    private static Task<HttpResponseMessage> Status(HttpStatusCode code) => Task.FromResult(new HttpResponseMessage(code));

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ModelServerFailure.Busy)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ModelServerFailure.Busy)]
    [InlineData(HttpStatusCode.BadGateway, ModelServerFailure.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout, ModelServerFailure.BadGateway)]
    public async Task TransientStatus_IsRetried_UpToMaxRetries_ThenTheServerCountsAsAway(HttpStatusCode code, ModelServerFailure failure)
    {
        var handler = new Scripted(_ => Status(code));

        var ex = await Assert.ThrowsAsync<ModelServerUnavailableException>(() => Send(Sender(handler)));

        Assert.Equal(failure, ex.Failure);
        Assert.Equal(Policy.MaxRetries + 1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)]
    public async Task OtherStatus_GoesBackToTheCaller_AtOnce(HttpStatusCode code)
    {
        var handler = new Scripted(_ => Status(code));

        using var response = await Send(Sender(handler));

        Assert.Equal(code, response.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ARetryThatSucceeds_ReturnsTheAnswer()
    {
        var handler = new Scripted(n => n == 1 ? throw new HttpRequestException("refused") : Status(HttpStatusCode.OK));

        using var response = await Send(Sender(handler));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ConnectionErrors_CountAsConnectionFailed()
    {
        var handler = new Scripted(n => n % 2 == 1 ? throw new HttpRequestException("refused") : throw new IOException("reset"));

        var ex = await Assert.ThrowsAsync<ModelServerUnavailableException>(() => Send(Sender(handler)));

        Assert.Equal(ModelServerFailure.ConnectionFailed, ex.Failure);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task RetryAfter_IsHonoured_AndCapped()
    {
        var handler = new Scripted(_ =>
        {
            var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(busy);
        });
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<ModelServerUnavailableException>(() => Send(Sender(handler)));

        // An hour was asked for; the cap of 20 ms per pause applies.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromHours(1), ex.RetryAfter);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Timeout_WithAServerThatDoesNotAnswerAProbe_CountsAsAway_WithoutFurtherAttempts()
    {
        var handler = Scripted.Hanging();

        var ex = await Assert.ThrowsAsync<ModelServerUnavailableException>(() =>
            Send(Sender(handler), TimeSpan.FromMilliseconds(30), _ => Task.FromResult(false)));

        Assert.Equal(ModelServerFailure.TimedOut, ex.Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Timeout_WithAServerThatAnswersAProbe_IsAnErrorOfThisRequest()
    {
        var handler = Scripted.Hanging();

        var ex = await Assert.ThrowsAsync<ModelCallTimedOutException>(() =>
            Send(Sender(handler), TimeSpan.FromMilliseconds(30), _ => Task.FromResult(true)));

        Assert.Equal(3, ex.Attempts);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Cancellation_IsNeverRetried()
    {
        var handler = Scripted.Hanging();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sender(handler).SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "api/chat"), TimeSpan.FromSeconds(5), Policy, null, "test", cts.Token));

        Assert.Equal(1, handler.Calls);
    }
}
