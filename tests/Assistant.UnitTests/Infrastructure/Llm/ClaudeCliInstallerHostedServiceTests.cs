using System.ComponentModel;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Llm.ClaudeCli;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Assistant.UnitTests.Infrastructure.Llm;

public class ClaudeCliInstallerHostedServiceTests : IDisposable
{
    // B1: a real, on-disk home directory is required for these tests now that the service checks
    // File.Exists(ExecutablePath) before ever touching the process runner (a fresh $CLAUDE_HOME
    // volume has no such file, and must never reach Process.Start at all). Created fresh per test
    // instance and removed in Dispose.
    private readonly string _homeDirectory = Path.Combine(Path.GetTempPath(), "assistant-cli-installer-tests-" + Guid.NewGuid());

    private ClaudeCliOptions Options(string pinnedVersion = "2.1.285", bool executableExists = true)
    {
        var executablePath = Path.Combine(_homeDirectory, ".local", "bin", "claude");
        if (executableExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
            File.WriteAllText(executablePath, string.Empty);
        }

        return new ClaudeCliOptions
        {
            ExecutablePath = executablePath,
            HomeDirectory = _homeDirectory,
            PinnedVersion = pinnedVersion,
            OAuthToken = "test-oauth-token",
            MaxOutputTokens = 4000,
            CallTimeoutSeconds = 120
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_homeDirectory))
            {
                Directory.Delete(_homeDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
    }

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
        var versionChecks = 0;
        var runner = new FakeProcessRunner
        {
            Handler = req =>
            {
                calls.Add(string.Join(' ', req.Arguments));
                if (req.Arguments.Contains("--version"))
                {
                    versionChecks++;
                    // Not installed yet on the first check; installed at the pinned version once
                    // the installer has "run" (B2: the service re-checks after a successful install).
                    return versionChecks == 1
                        ? new ProcessRunResult(1, "", "no such file", false)
                        : new ProcessRunResult(0, "2.1.285 (Claude Code)", "", false);
                }

                return new ProcessRunResult(0, "", "", false); // the installer itself
            }
        };
        var availability = new ModelAvailability(new FixedClock(DateTimeOffset.UtcNow));
        availability.MarkUnavailable("sonnet", DateTimeOffset.MaxValue);

        var service = new ClaudeCliInstallerHostedService(runner, Options(), availability, new[] { "sonnet" }, NullLogger<ClaudeCliInstallerHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await (service.ExecuteTask ?? Task.CompletedTask);

        calls.Count.ShouldBe(3); // version check, install, re-check (B2)
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
    public async Task B1_fresh_volume_with_no_executable_file_never_calls_the_process_runner_for_the_version_check()
    {
        // B1: on a genuinely fresh $CLAUDE_HOME volume the executable file itself does not exist
        // yet. The real ProcessRunner would throw a Win32Exception trying to Process.Start it --
        // the fix is to short-circuit on File.Exists before ever reaching the runner, so this fake
        // throws if the version check is even attempted.
        var installRan = false;
        var runner = new FakeProcessRunner
        {
            Handler = req =>
            {
                if (req.Arguments.Contains("--version"))
                {
                    throw new InvalidOperationException("must not run the version check when the executable file does not exist");
                }

                installRan = true;
                return new ProcessRunResult(0, "", "", false); // the installer
            }
        };
        var availability = new ModelAvailability(new FixedClock(DateTimeOffset.UtcNow));
        availability.MarkUnavailable("sonnet", DateTimeOffset.MaxValue);

        var service = new ClaudeCliInstallerHostedService(
            runner, Options(executableExists: false), availability, new[] { "sonnet" }, NullLogger<ClaudeCliInstallerHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await (service.ExecuteTask ?? Task.CompletedTask);

        installRan.ShouldBeTrue();
        // B2's re-check after a successful install finds no file either (the fake installer above
        // does not actually create one) -- so the entry correctly stays unavailable rather than
        // being marked available on a completely untested "install".
        availability.IsAvailable("sonnet").ShouldBeFalse();
    }

    [Fact]
    public async Task B1_a_Win32Exception_from_the_process_runner_on_the_version_check_is_treated_as_not_installed_and_the_installer_still_runs()
    {
        // B1: even with the executable file present, Process.Start can still fail (e.g. it isn't
        // actually executable, or a race removed it) -- a Win32Exception from the runner must be
        // treated exactly like "not installed", never as an unhandled install failure.
        var versionChecks = 0;
        var installRan = false;
        var runner = new FakeProcessRunner
        {
            Handler = req =>
            {
                if (req.Arguments.Contains("--version"))
                {
                    versionChecks++;
                    if (versionChecks == 1)
                    {
                        throw new Win32Exception("simulated Process.Start failure");
                    }

                    return new ProcessRunResult(0, "2.1.285 (Claude Code)", "", false); // re-check after install
                }

                installRan = true;
                return new ProcessRunResult(0, "", "", false); // the installer
            }
        };
        var availability = new ModelAvailability(new FixedClock(DateTimeOffset.UtcNow));
        availability.MarkUnavailable("sonnet", DateTimeOffset.MaxValue);

        var service = new ClaudeCliInstallerHostedService(runner, Options(), availability, new[] { "sonnet" }, NullLogger<ClaudeCliInstallerHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await (service.ExecuteTask ?? Task.CompletedTask);

        installRan.ShouldBeTrue();
        availability.IsAvailable("sonnet").ShouldBeTrue();
    }

    [Fact]
    public async Task B2_installer_exits_zero_but_the_version_check_still_fails_afterwards_is_not_marked_available_and_logs_one_error()
    {
        var logger = new CapturingLogger<ClaudeCliInstallerHostedService>();
        var runner = new FakeProcessRunner
        {
            // The installer reports success (exit 0), e.g. because `curl | bash` silently downloaded
            // nothing but bash still exited 0 -- the re-check after install must catch this instead
            // of trusting the installer's own exit code alone.
            Handler = req => req.Arguments.Contains("--version")
                ? new ProcessRunResult(1, "", "no such file", false)
                : new ProcessRunResult(0, "", "", false)
        };
        var availability = new ModelAvailability(new FixedClock(DateTimeOffset.UtcNow));
        availability.MarkUnavailable("sonnet", DateTimeOffset.MaxValue);

        var service = new ClaudeCliInstallerHostedService(runner, Options(), availability, new[] { "sonnet" }, logger);
        await service.StartAsync(CancellationToken.None);
        await (service.ExecuteTask ?? Task.CompletedTask);

        availability.IsAvailable("sonnet").ShouldBeFalse();
        logger.Entries.Count(e => e.Level == LogLevel.Error).ShouldBe(1);
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
        var versionChecks = 0;
        var runner = new FakeProcessRunner
        {
            AsyncHandler = async req =>
            {
                if (req.Arguments.Contains("--version"))
                {
                    versionChecks++;
                    return versionChecks == 1
                        ? new ProcessRunResult(1, "", "no such file", false) // not installed yet
                        : new ProcessRunResult(0, "2.1.285 (Claude Code)", "", false); // re-check after install (B2)
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
