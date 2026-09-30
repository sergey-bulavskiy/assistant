using Assistant.Application.Common;

namespace Assistant.UnitTests.Application.Common;

public class LlmConfigParserTests
{
    // Application never knows what a provider prefix means (spec §3.1); tests supply a fake
    // validator in place of the real Infrastructure one.
    private static Func<string, string?> AlwaysValid() => _ => null;

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
        ModelCooldownMinutesRaw = "30"
    };

    [Fact]
    public void Empty_models_means_silently_off_with_no_errors()
    {
        var options = Valid();
        options.ModelsRaw = "";

        var result = LlmConfigParser.Parse(options, AlwaysValid());

        result.IsEnabled.ShouldBeFalse();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Whitespace_only_models_means_silently_off()
    {
        var options = Valid();
        options.ModelsRaw = "   ";

        var result = LlmConfigParser.Parse(options, AlwaysValid());

        result.IsEnabled.ShouldBeFalse();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Valid_config_is_enabled_with_models_in_order()
    {
        var result = LlmConfigParser.Parse(Valid(), AlwaysValid());

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

    [Fact]
    public void Provider_prefixes_are_normalized_to_lower_case()
    {
        var options = Valid();
        options.ModelsRaw = "CLAUDE-CLI:sonnet";

        var result = LlmConfigParser.Parse(options, AlwaysValid());

        result.IsEnabled.ShouldBeTrue();
        result.Config!.Models.Single().ProviderPrefix.ShouldBe("claude-cli");
    }

    public static IEnumerable<object[]> LimitVariables()
    {
        yield return new object[] { "LLM_CALLS_PER_MINUTE", (Action<LlmOptions, string>)((o, v) => o.CallsPerMinuteRaw = v) };
        yield return new object[] { "LLM_CALLS_PER_DAY", (Action<LlmOptions, string>)((o, v) => o.CallsPerDayRaw = v) };
        yield return new object[] { "LLM_MAX_CONTEXT_MESSAGES", (Action<LlmOptions, string>)((o, v) => o.MaxContextMessagesRaw = v) };
        yield return new object[] { "LLM_MAX_INPUT_CHARS", (Action<LlmOptions, string>)((o, v) => o.MaxInputCharsRaw = v) };
        yield return new object[] { "LLM_MAX_OUTPUT_TOKENS", (Action<LlmOptions, string>)((o, v) => o.MaxOutputTokensRaw = v) };
        yield return new object[] { "LLM_CALL_TIMEOUT_SECONDS", (Action<LlmOptions, string>)((o, v) => o.CallTimeoutSecondsRaw = v) };
        yield return new object[] { "LLM_MAX_CONCURRENT_CALLS", (Action<LlmOptions, string>)((o, v) => o.MaxConcurrentCallsRaw = v) };
        yield return new object[] { "LLM_MODEL_COOLDOWN_MINUTES", (Action<LlmOptions, string>)((o, v) => o.ModelCooldownMinutesRaw = v) };
    }

    public static IEnumerable<object[]> LimitVariablesTimesBadValues()
    {
        foreach (var variable in LimitVariables())
        {
            foreach (var badValue in new[] { "0", "-1", "not-a-number", "" })
            {
                yield return new object[] { variable[0], variable[1], badValue };
            }
        }
    }

    [Theory]
    [MemberData(nameof(LimitVariablesTimesBadValues))]
    public void Any_invalid_limit_turns_llm_off_and_names_the_bad_variable(string variableName, Action<LlmOptions, string> setRaw, string badValue)
    {
        var options = Valid();
        setRaw(options, badValue);

        var result = LlmConfigParser.Parse(options, AlwaysValid());

        result.IsEnabled.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains(variableName));
    }

    [Fact]
    public void A_duplicate_model_name_is_dropped_per_entry_not_fatal_to_the_whole_config()
    {
        // Spec §8.9: "Invalid entries are dropped with one Error each; invalid limits turn LLM off."
        // A duplicate name is an entry-level problem, so LLM stays on with the first occurrence kept.
        var options = Valid();
        options.ModelsRaw = "claude-cli:sonnet,claude-cli:sonnet,claude-cli:haiku";

        var result = LlmConfigParser.Parse(options, AlwaysValid());

        result.IsEnabled.ShouldBeTrue();
        result.Config!.Models.Select(m => m.Name).ShouldBe(new[] { "sonnet", "haiku" });
        result.Errors.ShouldContain(e => e.Contains("LLM_MODELS") && e.Contains("sonnet"));
    }

    [Fact]
    public void A_model_entry_without_a_colon_is_dropped_per_entry_not_fatal_to_the_whole_config()
    {
        var options = Valid();
        options.ModelsRaw = "sonnet,claude-cli:haiku";

        var result = LlmConfigParser.Parse(options, AlwaysValid());

        result.IsEnabled.ShouldBeTrue();
        result.Config!.Models.Select(m => m.Name).ShouldBe(new[] { "haiku" });
        result.Errors.ShouldContain(e => e.Contains("LLM_MODELS"));
    }

    [Fact]
    public void An_entry_whose_provider_fails_validation_is_dropped_per_entry_not_fatal_to_the_whole_config()
    {
        // The provider-validation callback stands in for Infrastructure's real check (e.g. claude-cli
        // needing a token) without Application learning any provider name or credential.
        var options = Valid();
        options.ModelsRaw = "claude-cli:sonnet,other:haiku";

        var result = LlmConfigParser.Parse(options, provider => provider == "other" ? "is not usable" : null);

        result.IsEnabled.ShouldBeTrue();
        result.Config!.Models.Select(m => m.Name).ShouldBe(new[] { "sonnet" });
        result.Errors.ShouldContain(e => e.Contains("LLM_MODELS") && e.Contains("is not usable"));
    }

    [Fact]
    public void Dropping_every_entry_because_the_provider_fails_validation_turns_llm_off_with_the_error()
    {
        var options = Valid(); // both configured entries use "claude-cli"

        var result = LlmConfigParser.Parse(options, _ => "is not usable");

        result.IsEnabled.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("LLM_MODELS") && e.Contains("is not usable"));
    }

    [Fact]
    public void A_name_from_an_entry_dropped_for_an_invalid_provider_is_not_reserved_for_a_later_entry()
    {
        // Provider validity is checked before the name-uniqueness check, so a name used by a
        // dropped (invalid-provider) entry stays free for a later, valid entry with the same name.
        var options = Valid();
        options.ModelsRaw = "bad:sonnet,claude-cli:sonnet";

        var result = LlmConfigParser.Parse(options, provider => provider == "bad" ? "is not usable" : null);

        result.IsEnabled.ShouldBeTrue();
        result.Config!.Models.Select(m => m.Name).ShouldBe(new[] { "sonnet" });
        result.Config.Models.Single().ProviderPrefix.ShouldBe("claude-cli");
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

        var result = LlmConfigParser.Parse(options, AlwaysValid());

        result.IsEnabled.ShouldBeFalse();
        result.Config.ShouldBeNull();
        result.Errors.Count.ShouldBe(2);
    }
}
