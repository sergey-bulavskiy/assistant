namespace Assistant.Infrastructure.Llm.ClaudeCli;

// Bound from CLAUDE_CODE_OAUTH_TOKEN (and related CLI/process settings, Task 10) at DI
// composition time (Task 9). Lives in Infrastructure, not Application.Common.LlmOptions:
// Application never learns provider names or their credentials (spec §3.1); only Infrastructure
// knows the claude-cli provider needs this token and how to invoke the CLI.
public class ClaudeCliOptions
{
    /// <summary>The plan's pinned CLI version (Verified facts #6), used as <see cref="PinnedVersion"/>'s
    /// default so an unset/misconfigured CLAUDE_CLI_VERSION still meets the `--restricted` threshold
    /// (security review finding B1) instead of silently degrading to an unrestricted CLI invocation.</summary>
    public const string DefaultPinnedVersion = "2.1.285";

    /// <summary>The sentinel meaning "not explicitly configured" for <see cref="ExecutablePath"/>:
    /// <see cref="ClaudeCliChatClient"/> then derives the path from <see cref="HomeDirectory"/>
    /// (`$HomeDirectory/.local/bin/claude`, the plan's verified install path) instead of relying on
    /// `claude` being resolved through PATH.</summary>
    public const string UnsetExecutablePath = "claude";

    public string ExecutablePath { get; init; } = UnsetExecutablePath;

    /// <summary>$CLAUDE_HOME (Task 10): a dedicated, writable directory, never the app's own process
    /// HOME -- the child's entire "HOME" env var points here, per spec §8.6.</summary>
    public string HomeDirectory { get; init; } = string.Empty;

    /// <summary>The exact pinned CLI version (CLAUDE_CLI_VERSION, Task 10) -- gates which
    /// version-conditional flags are sent (spec §8.5: `--permission-prompts none` at >= 2.1.259,
    /// `--restricted` at >= 2.1.248) and whether the client refuses to run at all (security review
    /// finding B1: a version that doesn't support `--restricted` must never be invoked unrestricted).
    /// Defaults to <see cref="DefaultPinnedVersion"/> so an empty/unset value fails safe.</summary>
    public string PinnedVersion { get; init; } = DefaultPinnedVersion;

    public string OAuthToken { get; set; } = string.Empty;

    public int MaxOutputTokens { get; init; }

    public int CallTimeoutSeconds { get; init; }
}
