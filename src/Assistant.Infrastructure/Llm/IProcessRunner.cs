namespace Assistant.Infrastructure.Llm;

public record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory,
    string StandardInput,
    TimeSpan Timeout);

/// <summary>Abstracts child-process execution so ClaudeCliChatClient is unit-testable with a fake --
/// the real Claude Code CLI must never run in tests or CI (spec 6).</summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken);
}
