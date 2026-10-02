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
        Prices = new Dictionary<string, ModelPrice>(),
        Budget = null,
        FastModels = Array.Empty<ModelCatalogEntry>()
    });

    private static ModelCatalog CatalogOf(string[] models, string[] fast)
    {
        var entries = models.Select(n => new ModelCatalogEntry("claude-cli", n)).ToArray();
        return new ModelCatalog(new LlmConfig
        {
            Models = entries,
            CallsPerMinute = 10,
            CallsPerDay = 200,
            MaxContextMessages = 30,
            MaxInputChars = 40000,
            MaxOutputTokens = 4000,
            CallTimeoutSeconds = 120,
            MaxConcurrentCalls = 2,
            ModelCooldownMinutes = 30,
            Prices = new Dictionary<string, ModelPrice>(),
            Budget = null,
            FastModels = fast.Select(n => entries.First(e => e.Name == n)).ToArray()
        });
    }

    [Fact]
    public void Fast_tier_tries_the_fast_models_in_their_own_order_then_the_rest_of_the_chain()
    {
        var catalog = CatalogOf(new[] { "a", "b", "c", "d" }, new[] { "c", "b" });

        catalog.GetCandidateOrder(LlmConfig.FastTier, null).Select(m => m.Name)
            .ShouldBe(new[] { "c", "b", "a", "d" });
    }

    [Fact]
    public void Fast_tier_without_fast_models_uses_the_whole_chain()
    {
        Catalog().GetCandidateOrder("fast", null).Select(m => m.Name).ShouldBe(new[] { "sonnet", "haiku" });
    }

    [Fact]
    public void Fast_tier_name_ignores_case()
    {
        var catalog = CatalogOf(new[] { "a", "b", "c" }, new[] { "c" });

        catalog.GetCandidateOrder("FAST", null).Select(m => m.Name).ShouldBe(new[] { "c", "a", "b" });
    }

    [Fact]
    public void Smart_tier_ignores_the_fast_models()
    {
        var catalog = CatalogOf(new[] { "a", "b", "c", "d" }, new[] { "c", "b" });

        catalog.GetCandidateOrder(LlmConfig.SmartTier, null).Select(m => m.Name)
            .ShouldBe(new[] { "a", "b", "c", "d" });
    }

    [Fact]
    public void A_preference_still_comes_first_in_the_fast_tier()
    {
        var catalog = CatalogOf(new[] { "a", "b", "c", "d" }, new[] { "c", "b" });

        catalog.GetCandidateOrder("fast", "d").Select(m => m.Name).ShouldBe(new[] { "d", "c", "b", "a" });
    }

    [Fact]
    public void With_no_preference_the_order_is_the_configured_tier_order()
    {
        var order = Catalog().GetCandidateOrder(LlmConfig.SmartTier, preferredModel: null);

        order.Select(m => m.Name).ShouldBe(new[] { "sonnet", "haiku" });
    }

    [Fact]
    public void A_known_preference_is_tried_first_then_the_rest_of_the_chain_in_its_original_order()
    {
        var catalog = new ModelCatalog(new LlmConfig
        {
            Models = new[]
            {
                new ModelCatalogEntry("claude-cli", "a"),
                new ModelCatalogEntry("claude-cli", "b"),
                new ModelCatalogEntry("claude-cli", "c")
            },
            CallsPerMinute = 10,
            CallsPerDay = 200,
            MaxContextMessages = 30,
            MaxInputChars = 40000,
            MaxOutputTokens = 4000,
            CallTimeoutSeconds = 120,
            MaxConcurrentCalls = 2,
            ModelCooldownMinutes = 30,
            Prices = new Dictionary<string, ModelPrice>(),
            Budget = null,
            FastModels = Array.Empty<ModelCatalogEntry>()
        });

        var order = catalog.GetCandidateOrder(LlmConfig.SmartTier, preferredModel: "b");

        order.Select(m => m.Name).ShouldBe(new[] { "b", "a", "c" });
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
