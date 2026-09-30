using Assistant.Infrastructure.Llm.ClaudeCli;

namespace Assistant.Infrastructure.Llm;

/// <summary>Builds the provider-validation callback <c>LlmConfigParser.Parse</c> needs (spec §3.1:
/// Application never learns provider names or their credentials). This is the one place that
/// knows which provider prefixes this build registers and what each one needs to be usable; an
/// entry whose provider is unknown or fails its check is reported by name only, never by value,
/// and the entry is dropped (spec §8.9).</summary>
public static class LlmProviderValidation
{
    public const string ClaudeCliPrefix = "claude-cli";

    public static Func<string, string?> Create(ClaudeCliOptions claudeCliOptions) => provider => provider switch
    {
        ClaudeCliPrefix => string.IsNullOrWhiteSpace(claudeCliOptions.OAuthToken)
            ? "needs a non-empty CLAUDE_CODE_OAUTH_TOKEN"
            : null,
        _ => $"references an unregistered provider '{provider}'"
    };
}
