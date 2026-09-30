namespace Assistant.Infrastructure.Llm;

public record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);
