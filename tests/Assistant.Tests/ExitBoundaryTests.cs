using System.Text.Json;
using Assistant;
using Xunit;

namespace Assistant.Tests;

public sealed class ExitBoundaryTests
{
    [Fact]
    public async Task ExhaustedTimeoutsReturnFailureExitCode()
    {
        var attempts = 0;
        Task<JsonElement> Get(long offset, CancellationToken token)
        {
            attempts++;
            throw new TaskCanceledException("synthetic timeout");
        }

        var exitCode = await ExitBoundary.RunAsync(() =>
            TelegramPolling.RunAsync(Get, (_, _) => Task.CompletedTask, TimeSpan.Zero, CancellationToken.None),
            CancellationToken.None);

        Assert.Equal(5, attempts);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task HostCancellationReturnsSuccessExitCode()
    {
        using var stop = new CancellationTokenSource();
        var calls = 0;
        Task Work()
        {
            calls++;
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        }

        var exitCode = await ExitBoundary.RunAsync(Work, stop.Token);

        Assert.Equal(1, calls);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task NormalCompletionReturnsSuccessExitCode()
    {
        var calls = 0;
        var exitCode = await ExitBoundary.RunAsync(() => { calls++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(0, exitCode);
    }
}
