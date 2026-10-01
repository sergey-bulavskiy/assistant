using System.Diagnostics;
using System.Text;

namespace Assistant.Infrastructure.Llm;

public class ProcessRunner : IProcessRunner
{
    // Security review finding S2: an unbounded/misbehaving child could otherwise make us buffer an
    // unlimited amount of stdout in memory. 1 MB comfortably covers a real --output-format json
    // result; anything beyond that is treated as a failure, not parsed.
    private const int MaxStdoutChars = 1024 * 1024;

    // Review should-fix #2: stderr is never parsed as the call's result, only pattern-matched for an
    // auth failure marker (ClaudeCliChatClient) -- a real CLI error message is a few lines, so a much
    // smaller cap than stdout's is still generous while keeping this bounded rather than unbounded.
    // Unlike stdout, hitting this cap does not kill the process tree (stderr overflow alone is never
    // a reason to abort the call).
    private const int MaxStderrChars = 64 * 1024;

    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in request.Arguments)
        {
            // ArgumentList, never a single concatenated args string: a joined string would need
            // shell-style quoting/escaping of the prompt/system-prompt file paths and risks argument
            // injection; ArgumentList passes each value through exactly as given.
            startInfo.ArgumentList.Add(arg);
        }

        // Clean environment (security requirement): clear whatever ProcessStartInfo inherited by
        // default, then set only what request.Environment lists. The caller (ClaudeCliChatClient)
        // is responsible for that list never containing the bot tokens, encryption key or
        // connection string.
        startInfo.Environment.Clear();
        foreach (var (key, value) in request.Environment)
        {
            startInfo.Environment[key] = value;
        }

        // Security review finding S1: create the linked timeout CTS FIRST, so every step below (the
        // stdout/stderr drains, the stdin write, the exit wait) is bounded by it from the very start
        // -- a slow/hanging child can never run longer than request.Timeout regardless of where it
        // stalls.
        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Review finding: every path from here on must leave no child behind -- whatever happens
        // below (a normal return, an overflow, a cancellation we didn't expect to reach HandleCancellation,
        // or an exception), kill the tree unless the process already exited on its own.
        try
        {
            // Start draining stdout/stderr BEFORE writing to stdin (finding S1): a child that fills
            // its stdout/stderr OS pipe buffer before we start reading it would otherwise deadlock
            // against our stdin write (the child blocks writing output while we block writing
            // input). Stderr is capped and never buffered unbounded, same as stdout, but an overflow
            // there is not itself a reason to kill the process.
            var stdoutTask = ReadCappedAsync(process.StandardOutput, MaxStdoutChars, () => KillTree(process), linkedCts.Token);
            var stderrTask = ReadCappedAsync(process.StandardError, MaxStderrChars, onOverflow: null, linkedCts.Token);

            try
            {
                await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), linkedCts.Token);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Finding S1: the child exited before or while we were writing to its stdin (e.g. it
                // crashed immediately on a bad argument). Fall through and still collect the exit code
                // and whatever it already wrote, instead of throwing and losing that information.
            }
            catch (OperationCanceledException)
            {
                // Timeout or caller cancellation fired while writing stdin -- same handling as every
                // other stage below: kill the tree, then report TimedOut or rethrow.
                return HandleCancellation(process, timeoutCts);
            }

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                return HandleCancellation(process, timeoutCts);
            }

            try
            {
                // Finding S2: bound the read-drain itself by the same timeout scope, in case the child
                // exited but something downstream (e.g. a grandchild holding the pipe open) keeps the
                // streams from ever reporting EOF.
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                return HandleCancellation(process, timeoutCts);
            }

            var (stdout, overflowed) = stdoutTask.Result;
            if (overflowed)
            {
                // Finding S2: more than MaxStdoutChars of stdout -- never buffer/parse it. The tree
                // was already killed the moment the cap was hit (see ReadCappedAsync's onOverflow
                // callback), so this returns promptly rather than waiting out the call timeout; a
                // plain failure (empty output fails JSON parsing upstream) rather than a
                // partial/misleading result.
                return new ProcessRunResult(ExitCode: -1, StandardOutput: string.Empty, StandardError: string.Empty, TimedOut: false);
            }

            return new ProcessRunResult(process.ExitCode, stdout, stderrTask.Result.Text, TimedOut: false);
        }
        finally
        {
            if (!process.HasExited)
            {
                KillTree(process);
            }
        }
    }

    private static ProcessRunResult HandleCancellation(Process process, CancellationTokenSource timeoutCts)
    {
        // Timed out or the caller cancelled -- either way, kill the whole process tree rather than
        // leaving an orphaned `claude` process running.
        KillTree(process);

        if (timeoutCts.IsCancellationRequested)
        {
            return new ProcessRunResult(ExitCode: -1, StandardOutput: string.Empty, StandardError: string.Empty, TimedOut: true);
        }

        throw new OperationCanceledException(); // the caller's own cancellationToken fired -- propagate.
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the token firing and Kill() -- nothing to clean up.
        }
    }

    /// <summary>Reads at most <paramref name="maxChars"/> characters from <paramref name="reader"/>,
    /// invoking <paramref name="onOverflow"/> (when given) the moment the cap would be exceeded --
    /// e.g. so stdout's caller can kill the child immediately instead of waiting for the call timeout
    /// (finding S2) -- and keeps draining (discarding) everything after that so the pipe never
    /// backpressures a child that is still exiting, until the stream reports EOF. Never buffers more
    /// than <paramref name="maxChars"/> regardless of how much the child writes.</summary>
    private static async Task<(string Text, bool Overflowed)> ReadCappedAsync(
        StreamReader reader, int maxChars, Action? onOverflow, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        var sb = new StringBuilder();
        var total = 0;
        var overflowed = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return (sb.ToString(), overflowed);
            }

            if (overflowed)
            {
                continue; // discard -- already reported, just drain so the child is never blocked on a full pipe.
            }

            if (total + read > maxChars)
            {
                sb.Append(buffer, 0, maxChars - total);
                overflowed = true;
                onOverflow?.Invoke();
                continue;
            }

            sb.Append(buffer, 0, read);
            total += read;
        }
    }
}
