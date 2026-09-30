using System.Diagnostics;
using Assistant.Infrastructure.Llm;

namespace Assistant.UnitTests.Infrastructure.Llm;

/// <summary>Runs a REAL child process (never the real Claude Code CLI -- see this project's
/// AGENTS.md pitfalls) to cover behaviour a fake IProcessRunner cannot: real OS process isolation,
/// timeouts and pipe backpressure. CI runs on Linux; skipped on Windows via
/// <see cref="UnixOnlyFactAttribute"/> (this machine).</summary>
public class ProcessRunnerTests
{
    private static ProcessRunRequest Request(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        string standardInput = "",
        TimeSpan? timeout = null) =>
        new(fileName, arguments, environment ?? new Dictionary<string, string>(), Path.GetTempPath(), standardInput,
            timeout ?? TimeSpan.FromSeconds(10));

    [UnixOnlyFact]
    public async Task Only_the_allowlisted_environment_variables_reach_the_child()
    {
        var runner = new ProcessRunner();
        var environment = new Dictionary<string, string> { ["ASSISTANT_TEST_VAR"] = "visible-value" };

        var result = await runner.RunAsync(Request("/bin/sh", new[] { "-c", "env" }, environment), CancellationToken.None);

        result.TimedOut.ShouldBeFalse();
        result.StandardOutput.ShouldContain("ASSISTANT_TEST_VAR=visible-value");
        // Nothing beyond what we explicitly passed -- in particular, none of this xunit test host's
        // own process environment (e.g. its PATH) leaked through.
        result.StandardOutput.Trim().ShouldBe("ASSISTANT_TEST_VAR=visible-value");
    }

    [UnixOnlyFact]
    public async Task A_slow_child_is_killed_and_reported_as_timed_out()
    {
        var runner = new ProcessRunner();
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            Request("/bin/sh", new[] { "-c", "sleep 30" }, timeout: TimeSpan.FromMilliseconds(500)),
            CancellationToken.None);

        stopwatch.Stop();
        result.TimedOut.ShouldBeTrue();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10)); // killed promptly, not left to run the full 30s
    }

    [UnixOnlyFact]
    public async Task Caller_cancellation_throws_instead_of_returning_a_timed_out_result()
    {
        var runner = new ProcessRunner();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            runner.RunAsync(Request("/bin/sh", new[] { "-c", "sleep 30" }, timeout: TimeSpan.FromSeconds(30)), cts.Token));
    }

    [UnixOnlyFact]
    public async Task A_child_writing_far_more_than_the_stdout_cap_does_not_hang()
    {
        var runner = new ProcessRunner();
        var stopwatch = Stopwatch.StartNew();

        // ~2 MB of output, well beyond the 1 MB cap, with a timeout far longer than it should ever
        // take: overflow must kill the tree the moment the cap is hit (not wait out the timeout), so
        // this returns promptly as a failure.
        var result = await runner.RunAsync(
            Request("/bin/sh", new[] { "-c", "yes | head -c 2000000" }, timeout: TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        stopwatch.Stop();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10)); // promptly, nowhere near the 30s timeout
        result.ExitCode.ShouldBe(-1);
        result.TimedOut.ShouldBeFalse(); // it's an overflow failure, not a timeout
    }

    [UnixOnlyFact]
    public async Task A_child_ignoring_a_large_stdin_write_times_out_instead_of_hanging_in_the_write()
    {
        var runner = new ProcessRunner();
        var stopwatch = Stopwatch.StartNew();

        // A child that never reads stdin and a stdin payload well beyond the OS pipe buffer size: the
        // WriteAsync call itself blocks and must be cancelled by the timeout (covers the stdin-write
        // cancellation path), not left to hang.
        var result = await runner.RunAsync(
            Request("/bin/sh", new[] { "-c", "sleep 30" }, standardInput: new string('x', 5_000_000), timeout: TimeSpan.FromMilliseconds(500)),
            CancellationToken.None);

        stopwatch.Stop();
        result.TimedOut.ShouldBeTrue();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }
}
