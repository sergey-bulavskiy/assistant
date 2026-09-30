namespace Assistant.Infrastructure.Llm;

/// <summary>Well-known <c>ChatResponse.AdditionalProperties</c> keys shared between an
/// <c>IChatClient</c> implementation (e.g. <c>ClaudeCliChatClient</c>) and <see cref="LlmGateway"/>,
/// so both sides agree on the key without duplicating the literal.</summary>
public static class LlmResponseKeys
{
    /// <summary>The provider-reported cost of the call, in US dollars. <see cref="LlmGateway"/>
    /// reads this defensively (it may arrive as <c>decimal</c>, <c>double</c> or a
    /// <c>JsonElement</c> depending on how the value was produced/deserialized).</summary>
    public const string ReportedCostUsd = "reported_cost_usd";
}
