namespace Assistant.Infrastructure.Llm.ClaudeCli;

// Bound from CLAUDE_CODE_OAUTH_TOKEN (and related CLI/process settings, Task 10) at DI
// composition time (Task 9). Lives in Infrastructure, not Application.Common.LlmOptions:
// Application never learns provider names or their credentials (spec §3.1); only Infrastructure
// knows the claude-cli provider needs this token and how to invoke the CLI.
public class ClaudeCliOptions
{
    public string ExecutablePath { get; init; } = "claude";

    /// <summary>$CLAUDE_HOME (Task 10): a dedicated, writable directory, never the app's own process
    /// HOME -- the child's entire "HOME" env var points here, per spec §8.6.</summary>
    public string HomeDirectory { get; init; } = string.Empty;

    /// <summary>The exact pinned CLI version (CLAUDE_CLI_VERSION, Task 10) -- gates which
    /// version-conditional flags are sent (spec §8.5: `--permission-prompts none` at >= 2.1.259,
    /// `--restricted` at >= 2.1.248).</summary>
    public string PinnedVersion { get; init; } = string.Empty;

    public string OAuthToken { get; set; } = string.Empty;

    public int MaxOutputTokens { get; init; }

    public int CallTimeoutSeconds { get; init; }
}
