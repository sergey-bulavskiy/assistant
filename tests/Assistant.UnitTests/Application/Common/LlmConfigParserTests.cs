using Assistant.Application.Common;

namespace Assistant.UnitTests.Application.Common;

public class LlmConfigParserTests
{
    private static LlmOptions Valid() => new()
    {
        ModelsRaw = "claude-cli:sonnet,claude-cli:haiku",
        CallsPerMinuteRaw = "10",
        CallsPerDayRaw = "200",
        MaxContextMessagesRaw = "30",
        MaxInputCharsRaw = "40000",
        MaxOutputTokensRaw = "4000",
        CallTimeoutSecondsRaw = "120",
        MaxConcurrentCallsRaw = "2",
        ModelCooldownMinutesRaw = "30",
        ClaudeCodeOAuthToken = "test-oauth-token"
    };

    [Fact]
    public void Empty_models_means_silently_off_with_no_errors()
    {
        var options = Valid();
        options.ModelsRaw = "";

        var result = LlmConfigParser.Parse(options);

        result.IsEnabled.ShouldBeFalse();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Whitespace_only_models_means_silently_off()
    {
        var options = Valid();
        options.ModelsRaw = "   ";

        var result = LlmConfigParser.Parse(options);

        result.IsEnabled.ShouldBeFalse();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Valid_config_is_enabled_with_models_in_order()
    {
        var result = LlmConfigParser.Parse(Valid());

        result.IsEnabled.ShouldBeTrue();
        result.Config.ShouldNotBeNull();
        result.Config!.Models.Select(m => m.Name).ShouldBe(new[] { "sonnet", "haiku" });
        result.Config.Models.Select(m => m.ProviderPrefix).ShouldAllBe(p => p == "claude-cli");
        result.Config.CallsPerMinute.ShouldBe(10);
        result.Config.CallsPerDay.ShouldBe(200);
        result.Config.MaxContextMessages.ShouldBe(30);
        result.Config.MaxInputChars.ShouldBe(40000);
        result.Config.MaxOutputTokens.ShouldBe(4000);
        result.Config.CallTimeoutSeconds.ShouldBe(120);
        result.Config.MaxConcurrentCalls.ShouldBe(2);
        result.Config.ModelCooldownMinutes.ShouldBe(30);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void Any_invalid_limit_turns_llm_off_and_names_the_bad_variable(string badValue)
    {
        var options = Valid();
        options.CallsPerMinuteRaw = badValue;

        var result = LlmConfigParser.Parse(options);

        result.IsEnabled.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("LLM_CALLS_PER_MINUTE"));
    }

    [Fact]
    public void A_duplicate_model_name_is_dropped_per_entry_not_fatal_to_the_whole_config()
    {
        // Spec §8.9: "Invalid entries are dropped with one Error each; invalid limits turn LLM off."
        // A duplicate name is an entry-level problem, so LLM stays on with the first occurrence kept.
        var options = Valid();
        options.ModelsRaw = "claude-cli:sonnet,claude-cli:sonnet,claude-cli:haiku";

        var result = LlmConfigParser.Parse(options);

        result.IsEnabled.ShouldBeTrue();
        result.Config!.Models.Select(m => m.Name).ShouldBe(new[] { "sonnet", "haiku" });
        result.Errors.ShouldContain(e => e.Contains("LLM_MODELS") && e.Contains("sonnet"));
    }

    [Fact]
    public void A_model_entry_without_a_colon_is_dropped_per_entry_not_fatal_to_the_whole_config()
    {
        var options = Valid();
        options.ModelsRaw = "sonnet,claude-cli:haiku";

        var result = LlmConfigParser.Parse(options);

        result.IsEnabled.ShouldBeTrue();
        result.Config!.Models.Select(m => m.Name).ShouldBe(new[] { "haiku" });
        result.Errors.ShouldContain(e => e.Contains("LLM_MODELS"));
    }

    [Fact]
    public void A_claude_cli_entry_without_an_oauth_token_is_dropped_per_entry()
    {
        // Spec §8.9's last sentence: "A claude-cli: entry additionally requires a non-empty
        // CLAUDE_CODE_OAUTH_TOKEN." This is a per-entry validation concern, not a whole-config on/off
        // switch -- only entries needing the token are dropped.
        var options = Valid();
        options.ClaudeCodeOAuthToken = "";

        var result = LlmConfigParser.Parse(options);

        result.IsEnabled.ShouldBeFalse(); // both configured entries are claude-cli: -- dropping both leaves zero models
        result.Errors.ShouldContain(e => e.Contains("LLM_MODELS") && e.Contains("CLAUDE_CODE_OAUTH_TOKEN"));
    }

    [Fact]
    public void Dropping_every_entry_turns_llm_off_with_the_accumulated_errors()
    {
        // Decision (spec §8.9 doesn't cover this edge case explicitly): if every LLM_MODELS entry is
        // individually invalid/dropped, the catalog ends up empty -- this is NOT the same as
        // LLM_MODELS being empty/unset (which is a silent, error-free off per spec 3.2), so it turns
        // LLM off WITH the per-entry errors logged, rather than silently.
        var options = Valid();
        options.ModelsRaw = "sonnet,haiku"; // both missing the required colon

        var result = LlmConfigParser.Parse(options);

        result.IsEnabled.ShouldBeFalse();
        result.Config.ShouldBeNull();
        result.Errors.Count.ShouldBe(2);
    }

    [Fact]
    public void A_non_claude_cli_entry_does_not_require_the_oauth_token()
    {
        var options = Valid();
        options.ModelsRaw = "some-other-provider:model-x";
        options.ClaudeCodeOAuthToken = "";

        var result = LlmConfigParser.Parse(options);

        result.IsEnabled.ShouldBeTrue();
        result.Config!.Models.Select(m => m.Name).ShouldBe(new[] { "model-x" });
    }
}
