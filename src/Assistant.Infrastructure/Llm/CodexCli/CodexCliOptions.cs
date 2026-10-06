namespace Assistant.Infrastructure.Llm.CodexCli;

public sealed class CodexCliOptions
{
    public const string PinnedVersion = "0.160.1";
    public string ExecutablePath { get; init; } = "/usr/local/bin/codex";
    public string HomeDirectory { get; init; } = "/home/app/.codex";
    public int MaxOutputTokens { get; init; } = 1000;
    public int CallTimeoutSeconds { get; init; } = 120;
}
