using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Llm.ClaudeCli;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Assistant.UnitTests.Infrastructure.Llm;

public class ClaudeCliInstallerHostedServiceTests
{
    private static ClaudeCliOptions Options(string pinnedVersion = "2.1.285") => new()
    {
        ExecutablePath = "/home/app/.claude-home/.local/bin/claude",
        HomeDirectory = "/home/app/.claude-home",
        PinnedVersion = pinnedVersion,
        OAuthToken = "test-oauth-token",
        MaxOutputTokens = 4000,
        CallTimeoutSeconds = 120
    };

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task Already_installed_at_the_pinned_version_marks_every_claude_cli_entry_available_without_reinstalling()
    {
        var runner = new FakeProcessRunner
        {
            // First call is always the "--version" check (spec §8.10).
            Handler = req => req.Arguments.Contains("--version")
                ? new ProcessRunResult(0, "2.1.285 (Claude Code)", "", false)
                : throw new InvalidOperationException("should not run the installer when already at the pinned version")
        };
        var availability = new ModelAvailability(new FixedClock(DateTimeOffset.UtcNow));
        availability.MarkUnavailable("sonnet", DateTimeOffset.MaxValue); // as the service's own startup would have just done

        var service = new ClaudeCliInstallerHostedService(runner, Options(), availability, new[] { "sonnet", "haiku" }, NullLogger<ClaudeCliInstallerHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await (service.ExecuteTask ?? Task.CompletedTask);

        availability.IsAvailable("sonnet").ShouldBeTrue();
        availability.IsAvailable("haiku").ShouldBeTrue();
    }

    [Fact]
    public async Task Missing_or_different_version_runs_the_installer_then_marks_every_entry_available()
    {
        var calls = new List<string>();
        var runner = new FakeProcessRunner
        {
            Handler = req =>
            {
                calls.Add(string.Join(' ', req.Arguments));
                if (req.Arguments.Contains("--version"))
                {
                    return new ProcessRunResult(1, "", "no such file", false); // not installed yet
                }

                return new ProcessRunResult(0, "", "", false); // the installer itself
            }
        };
        var availability = new ModelAvailability(new FixedClock(DateTimeOffset.UtcNow));
        availability.MarkUnavailable("sonnet", DateTimeOffset.MaxValue);

        var service = new ClaudeCliInstallerHostedService(runner, Options(), availability, new[] { "sonnet" }, NullLogger<ClaudeCliInstallerHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await (service.ExecuteTask ?? Task.CompletedTask);

        calls.Count.ShouldBe(2); // version check, then install
        availability.IsAvailable("sonnet").ShouldBeTrue();
    }

    [Fact]
    public async Task An_install_failure_logs_one_error_leaves_entries_unavailable_and_never_throws()
    {
        var logger = new CapturingLogger<ClaudeCliInstallerHostedService>();
        var runner = new FakeProcessRunner
        {
            Handler = req => req.Arguments.Contains("--version")
                ? new ProcessRunResult(1, "", "", false)
                : new ProcessRunResult(1, "", "install failed", false)
        };
        var availability = new ModelAvailability(new FixedClock(DateTimeOffset.UtcNow));
        availability.MarkUnavailable("sonnet", DateTimeOffset.MaxValue);

        var service = new ClaudeCliInstallerHostedService(runner, Options(), availability, new[] { "sonnet" }, logger);
        await service.StartAsync(CancellationToken.None); // must not throw
        await (service.ExecuteTask ?? Task.CompletedTask);

        availability.IsAvailable("sonnet").ShouldBeFalse();
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task StopAsync_completes_immediately_there_is_nothing_to_stop()
    {
        var service = new ClaudeCliInstallerHostedService(new FakeProcessRunner(), Options(), new ModelAvailability(new FixedClock(DateTimeOffset.UtcNow)), new[] { "sonnet" }, NullLogger<ClaudeCliInstallerHostedService>.Instance);

        await service.StopAsync(CancellationToken.None); // must not throw even though StartAsync was never called
    }

    [Fact]
    public async Task StartAsync_returns_promptly_while_the_install_is_still_running()
    {
        // Proves the fix: a BackgroundService's StartAsync only kicks off ExecuteAsync in the
        // background and returns -- it never awaits the install itself, so a slow/hanging download
        // (the real installer pipes curl through bash, with no bound on how long that can take)
        // cannot block ASP.NET Core's own startup, /health or bot polling.
        var installStarted = new TaskCompletionSource();
        var releaseInstall = new TaskCompletionSource<ProcessRunResult>();
        var runner = new FakeProcessRunner
        {
            AsyncHandler = async req =>
            {
                if (req.Arguments.Contains("--version"))
                {
                    return new ProcessRunResult(1, "", "no such file", false); // not installed yet
                }

                installStarted.SetResult();
                return await releaseInstall.Task; // never completes until the test releases it
            }
        };
        var availability = new ModelAvailability(new FixedClock(DateTimeOffset.UtcNow));
        availability.MarkUnavailable("sonnet", DateTimeOffset.MaxValue);

        var service = new ClaudeCliInstallerHostedService(runner, Options(), availability, new[] { "sonnet" }, NullLogger<ClaudeCliInstallerHostedService>.Instance);

        var startTask = service.StartAsync(CancellationToken.None);
        var completedTask = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completedTask.ShouldBe(startTask); // StartAsync itself must complete well before the installer does
        await startTask;

        await Task.WhenAny(installStarted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        installStarted.Task.IsCompleted.ShouldBeTrue(); // the installer is genuinely running in the background
        service.ExecuteTask!.IsCompleted.ShouldBeFalse(); // and has not finished
        availability.IsAvailable("sonnet").ShouldBeFalse(); // so the model is still marked unavailable

        releaseInstall.SetResult(new ProcessRunResult(0, "", "", false));
        await service.ExecuteTask!;
        availability.IsAvailable("sonnet").ShouldBeTrue();
    }
}
