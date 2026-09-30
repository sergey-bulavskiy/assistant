using System.Diagnostics;

namespace Assistant.Infrastructure.Llm;

public class ProcessRunner : IProcessRunner
{
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

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        await process.StandardInput.WriteAsync(request.StandardInput);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timed out or the caller cancelled -- either way, kill the whole process tree rather
            // than leaving an orphaned `claude` process running.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited between the token firing and Kill() -- nothing to clean up.
            }

            if (timeoutCts.IsCancellationRequested)
            {
                return new ProcessRunResult(ExitCode: -1, StandardOutput: string.Empty, StandardError: string.Empty, TimedOut: true);
            }

            throw; // the caller's own cancellationToken fired -- propagate as OperationCanceledException.
        }

        return new ProcessRunResult(process.ExitCode, await stdoutTask, await stderrTask, TimedOut: false);
    }
}
