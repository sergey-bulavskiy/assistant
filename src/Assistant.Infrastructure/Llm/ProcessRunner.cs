using System.Diagnostics;
using System.Text;

namespace Assistant.Infrastructure.Llm;

public class ProcessRunner : IProcessRunner
{
    // Security review finding S2: an unbounded/misbehaving child could otherwise make us buffer an
    // unlimited amount of stdout in memory. 1 MB comfortably covers a real --output-format json
    // result; anything beyond that is treated as a failure, not parsed.
    private const int MaxStdoutChars = 1024 * 1024;

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

        // Start draining stdout/stderr BEFORE writing to stdin (finding S1): a child that fills its
        // stdout/stderr OS pipe buffer before we start reading it would otherwise deadlock against
        // our stdin write (the child blocks writing output while we block writing input).
        var stdoutTask = ReadCappedAsync(process.StandardOutput, MaxStdoutChars, linkedCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

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
            // Finding S2: more than MaxStdoutChars of stdout -- never buffer/parse it, kill the tree
            // and report a plain failure (empty output fails JSON parsing upstream) rather than a
            // partial/misleading result.
            KillTree(process);
            return new ProcessRunResult(ExitCode: -1, StandardOutput: string.Empty, StandardError: string.Empty, TimedOut: false);
        }

        return new ProcessRunResult(process.ExitCode, stdout, stderrTask.Result, TimedOut: false);
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

    /// <summary>Reads at most <paramref name="maxChars"/> characters from <paramref name="reader"/>.
    /// Returns (text read so far, true) the moment the cap would be exceeded, without buffering any
    /// more of the stream -- see finding S2.</summary>
    private static async Task<(string Text, bool Overflowed)> ReadCappedAsync(
        StreamReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        var sb = new StringBuilder();
        var total = 0;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return (sb.ToString(), false);
            }

            if (total + read > maxChars)
            {
                sb.Append(buffer, 0, maxChars - total);
                return (sb.ToString(), true);
            }

            sb.Append(buffer, 0, read);
            total += read;
        }
    }
}
