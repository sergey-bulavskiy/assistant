using System.Net;
using System.Text.Json;
using Assistant;
using Xunit;

namespace Assistant.Tests;

public sealed class TelegramPollingTests
{
    private static JsonElement Updates(params long[] ids) =>
        JsonSerializer.SerializeToElement(ids.Select(id => new { update_id = id }));

    [Fact]
    public async Task FailedHandlingRetriesSameOffsetAndDoesNotReplayCompletedUpdates()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var offsets = new List<long>();
        var handled = new List<long>();
        var failedOnce = false;
        Task<JsonElement> Get(long offset, CancellationToken token)
        {
            offsets.Add(offset);
            return Task.FromResult(offset == 0 ? Updates(10, 11) : Updates(11));
        }
        Task Handle(JsonElement update, CancellationToken token)
        {
            var id = update.GetProperty("update_id").GetInt64();
            handled.Add(id);
            if (id == 11 && !failedOnce) { failedOnce = true; throw new TimeoutException(); }
            if (id == 11) stop.Cancel();
            return Task.CompletedTask;
        }

        await TelegramPolling.RunAsync(Get, Handle, TimeSpan.Zero, stop.Token);

        Assert.Equal(new long[] { 0, 11 }, offsets);
        Assert.Equal(new long[] { 10, 11, 11 }, handled);
    }

    [Fact]
    public async Task TransientGetTimeoutRetriesWithSameOffset()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var offsets = new List<long>();
        Task<JsonElement> Get(long offset, CancellationToken token)
        {
            offsets.Add(offset);
            if (offsets.Count == 1) throw new OperationCanceledException();
            return Task.FromResult(Updates(7));
        }
        Task Handle(JsonElement update, CancellationToken token) { stop.Cancel(); return Task.CompletedTask; }

        await TelegramPolling.RunAsync(Get, Handle, TimeSpan.Zero, stop.Token);

        Assert.Equal(new long[] { 0, 0 }, offsets);
    }

    [Fact]
    public async Task HostCancellationStopsWithoutRetry()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var calls = 0;
        Task<JsonElement> Get(long offset, CancellationToken token)
        {
            calls++;
            stop.Cancel();
            throw new OperationCanceledException(token);
        }

        await TelegramPolling.RunAsync(Get, (_, _) => Task.CompletedTask, TimeSpan.Zero, stop.Token);

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task PermanentHttpFailurePropagatesWithoutRetry(HttpStatusCode status)
    {
        var calls = 0;
        Task<JsonElement> Get(long offset, CancellationToken token)
        {
            calls++;
            throw new HttpRequestException("synthetic", null, status);
        }

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            TelegramPolling.RunAsync(Get, (_, _) => Task.CompletedTask, TimeSpan.Zero, CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConsecutiveTransientFailuresStopAtLimit()
    {
        var calls = 0;
        Task<JsonElement> Get(long offset, CancellationToken token)
        {
            calls++;
            throw new HttpRequestException("synthetic", null, HttpStatusCode.ServiceUnavailable);
        }

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            TelegramPolling.RunAsync(Get, (_, _) => Task.CompletedTask, TimeSpan.Zero, CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(5, calls);
    }
}
