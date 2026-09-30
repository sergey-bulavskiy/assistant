namespace Assistant.Infrastructure.Llm.ClaudeCli;

// Bound from CLAUDE_CODE_OAUTH_TOKEN at DI composition time (Task 9). Lives in Infrastructure,
// not Application.Common.LlmOptions: Application never learns provider names or their
// credentials (spec §3.1); only Infrastructure knows the claude-cli provider needs this token.
public class ClaudeCliOptions
{
    public string OAuthToken { get; set; } = string.Empty;
}
