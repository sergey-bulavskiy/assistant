using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Llm.ClaudeCli;

namespace Assistant.UnitTests.Infrastructure.Llm;

public class LlmProviderValidationTests
{
    [Fact]
    public void Claude_cli_without_a_token_yields_an_error()
    {
        var validate = LlmProviderValidation.Create(new ClaudeCliOptions { OAuthToken = "" });

        var error = validate("claude-cli");

        error.ShouldNotBeNull();
        error.ShouldContain("CLAUDE_CODE_OAUTH_TOKEN");
    }

    [Fact]
    public void Claude_cli_with_a_whitespace_only_token_yields_an_error()
    {
        var validate = LlmProviderValidation.Create(new ClaudeCliOptions { OAuthToken = "   " });

        var error = validate("claude-cli");

        error.ShouldNotBeNull();
        error.ShouldContain("CLAUDE_CODE_OAUTH_TOKEN");
    }

    [Fact]
    public void Claude_cli_with_a_token_is_valid()
    {
        var validate = LlmProviderValidation.Create(new ClaudeCliOptions { OAuthToken = "test-oauth-token" });

        validate("claude-cli").ShouldBeNull();
    }

    [Fact]
    public void An_unregistered_provider_yields_an_error_naming_it()
    {
        var validate = LlmProviderValidation.Create(new ClaudeCliOptions { OAuthToken = "test-oauth-token" });

        var error = validate("some-other-provider");

        error.ShouldNotBeNull();
        error.ShouldContain("some-other-provider");
    }

    [Fact]
    public void The_error_text_never_contains_the_whitespace_only_token_value()
    {
        const string whitespaceToken = "  \t  ";
        var validate = LlmProviderValidation.Create(new ClaudeCliOptions { OAuthToken = whitespaceToken });

        var error = validate("claude-cli");

        error.ShouldNotBeNull();
        error.ShouldNotContain(whitespaceToken);
    }
    [Theory]
    [InlineData("codex-cli:synthetic", "")]
    [InlineData("claude-cli:legacy", "CODEX-CLI:synthetic")]
    [InlineData("codex-cli:", "")]
    public void Subscription_entry_in_either_raw_chain_disables_legacy_providers(string models, string fastModels)
    {
        var subscriptionOnly = LlmProviderValidation.RequiresSubscriptionOnly(models, fastModels);
        var validate = LlmProviderValidation.Create(new ClaudeCliOptions { OAuthToken = "test-token" }, subscriptionOnly);

        subscriptionOnly.ShouldBeTrue();
        validate("codex-cli").ShouldBeNull();
        validate("claude-cli").ShouldNotBeNull().ShouldContain("disabled");
        validate("openai").ShouldNotBeNull().ShouldContain("disabled");
        validate("anthropic").ShouldNotBeNull().ShouldContain("disabled");
    }

    [Fact]
    public void Removing_subscription_entries_allows_deliberate_legacy_rollback()
    {
        var subscriptionOnly = LlmProviderValidation.RequiresSubscriptionOnly("claude-cli:synthetic", "claude-cli:synthetic");
        var validate = LlmProviderValidation.Create(new ClaudeCliOptions { OAuthToken = "test-token" }, subscriptionOnly);

        subscriptionOnly.ShouldBeFalse();
        validate("claude-cli").ShouldBeNull();
        validate("openai").ShouldBeNull();
    }
}
