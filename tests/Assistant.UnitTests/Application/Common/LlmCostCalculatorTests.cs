using Assistant.Application.Common;

namespace Assistant.UnitTests.Application.Common;

public class LlmCostCalculatorTests
{
    private static readonly ModelPrice Price = new(InputUsdPerMillion: 1m, OutputUsdPerMillion: 5m);

    [Fact]
    public void Compute_scales_tokens_by_price_per_million()
    {
        // 1,000,000 input tokens at $1/M = $1; 200,000 output tokens at $5/M = $1 -> $2 total.
        LlmCostCalculator.Compute(inputTokens: 1_000_000, outputTokens: 200_000, Price).ShouldBe(2.0000m);
    }

    [Fact]
    public void Compute_is_zero_for_a_zero_price_entry()
    {
        LlmCostCalculator.Compute(1_000_000, 1_000_000, new ModelPrice(0, 0)).ShouldBe(0m);
    }

    [Fact]
    public void Compute_never_rounds_a_tiny_nonzero_cost_down_to_zero()
    {
        // 1 input token at $1/M = $0.000001 -- rounds away from zero to the column's 4th decimal.
        LlmCostCalculator.Compute(1, 0, Price).ShouldBe(0.0001m);
    }

    [Fact]
    public void Estimate_uses_half_the_combined_system_prompt_and_input_chars_plus_max_output_tokens()
    {
        // (100 + 100) chars / 2 = 100 estimated input tokens; + 1000 max output tokens.
        // Cost = 100/1e6*1 + 1000/1e6*5 = 0.0001 + 0.005 = 0.0051
        LlmCostCalculator.Estimate(systemPromptChars: 100, inputChars: 100, maxOutputTokens: 1000, Price).ShouldBe(0.0051m);
    }

    [Fact]
    public void Estimate_is_zero_for_a_zero_price_entry()
    {
        LlmCostCalculator.Estimate(10_000, 10_000, 4096, new ModelPrice(0, 0)).ShouldBe(0m);
    }
}
