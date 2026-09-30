using Assistant.Application.Common;
using Assistant.Infrastructure.Llm;

namespace Assistant.UnitTests.Infrastructure.Llm;

public class ModelCatalogTests
{
    private static ModelCatalog Catalog() => new(new LlmConfig
    {
        Models = new[]
        {
            new ModelCatalogEntry("claude-cli", "sonnet"),
            new ModelCatalogEntry("claude-cli", "haiku")
        },
        CallsPerMinute = 10,
        CallsPerDay = 200,
        MaxContextMessages = 30,
        MaxInputChars = 40000,
        MaxOutputTokens = 4000,
        CallTimeoutSeconds = 120,
        MaxConcurrentCalls = 2,
        ModelCooldownMinutes = 30,
        ClaudeCodeOAuthToken = "test-token"
    });

    [Fact]
    public void With_no_preference_the_order_is_the_configured_tier_order()
    {
        var order = Catalog().GetCandidateOrder(LlmConfig.SmartTier, preferredModel: null);

        order.Select(m => m.Name).ShouldBe(new[] { "sonnet", "haiku" });
    }

    [Fact]
    public void A_known_preference_is_tried_first_then_the_rest_of_the_chain()
    {
        var order = Catalog().GetCandidateOrder(LlmConfig.SmartTier, preferredModel: "haiku");

        order.Select(m => m.Name).ShouldBe(new[] { "haiku", "sonnet" });
    }

    [Fact]
    public void An_unknown_preference_falls_back_to_the_default_chain()
    {
        var order = Catalog().GetCandidateOrder(LlmConfig.SmartTier, preferredModel: "does-not-exist");

        order.Select(m => m.Name).ShouldBe(new[] { "sonnet", "haiku" });
    }

    [Fact]
    public void TryGetByName_is_case_insensitive()
    {
        Catalog().TryGetByName("SONNET", out var entry).ShouldBeTrue();
        entry!.Name.ShouldBe("sonnet");
    }
}
