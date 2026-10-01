namespace Assistant.Application.Common;

/// <summary>Pure cost math in USD, prices per million tokens. Rounded UP to 4 decimal places (the
/// llm_calls.cost column is numeric(10,4)): conservative for a budget, and a genuinely nonzero cost
/// is never recorded as exactly 0 (MidpointRounding.AwayFromZero would only affect midpoints).</summary>
public static class LlmCostCalculator
{
    public static decimal Compute(decimal inputTokens, decimal outputTokens, ModelPrice price) =>
        Round(inputTokens / 1_000_000m * price.InputUsdPerMillion + outputTokens / 1_000_000m * price.OutputUsdPerMillion);

    /// <summary>Input tokens ~= (system prompt chars + input chars) / 2 -- the same formula
    /// <see cref="Estimate"/> uses for its input side, exposed separately so a partial usage result
    /// (only one of input/output token counts reported) can fall back to the estimate for just the
    /// missing side instead of the whole call's estimate.</summary>
    public static decimal EstimatedInputTokens(int systemPromptChars, int inputChars) =>
        (systemPromptChars + (decimal)inputChars) / 2m;

    /// <summary>Pre-call estimate: input tokens ~= (system prompt chars + input chars) / 2, output
    /// tokens = the configured maximum. Used both to skip a candidate that would push spend over a
    /// hard cap and as the recorded cost of a paid Timeout/Failed call that returned no usage.</summary>
    public static decimal Estimate(int systemPromptChars, int inputChars, int maxOutputTokens, ModelPrice price) =>
        Round(EstimatedInputTokens(systemPromptChars, inputChars) / 1_000_000m * price.InputUsdPerMillion
            + maxOutputTokens / 1_000_000m * price.OutputUsdPerMillion);

    public static bool IsZero(ModelPrice price) => price.InputUsdPerMillion == 0m && price.OutputUsdPerMillion == 0m;

    private static decimal Round(decimal value) => Math.Ceiling(value * 10_000m) / 10_000m;
}
