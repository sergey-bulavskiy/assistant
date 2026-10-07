namespace Assistant.Domain.Llm;

// One row per model attempt (spec 3.1: "record every attempt"), including attempts that fail over
// to the next candidate model. Guard refusals (RateLimited, DailyCapReached) make no call and write
// no row -- only actual attempts land here, which is also what the rate/day guards count from.
public class LlmCall
{
    public long Id { get; set; }
    /// <summary>Durable image-dispatch identity. Legacy text attempts remain null.</summary>
    public Guid? AttemptKey { get; set; }
    public long FamilyId { get; set; }
    public long BotId { get; set; }
    public string Tier { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public LlmCallOutcome Outcome { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public decimal? ReportedCost { get; set; }

    /// <summary>What this call cost toward the platform budget, in USD: usage x LLM_PRICES for Ok,
    /// 0 for LimitReached (rejected before any billable work), and the pre-call estimate for a
    /// Timeout/Failed call without usage. Always 0 for a zero-price entry (claude-cli is forced to
    /// 0/0). Distinct from ReportedCost, the provider's own self-reported, informational figure.</summary>
    public decimal Cost { get; set; }
    public long DurationMs { get; set; }

    /// <summary>Where the call was triggered (Telegram chat id / forum topic id) and the triggering
    /// message's messages.id. Copied from LlmRequest into every attempt row. TriggerMessageId null
    /// means the call is not counted by /tokens (rows written before these columns existed, or a
    /// trigger message that was never stored).</summary>
    public long? ChatId { get; set; }
    public int? TopicId { get; set; }
    public long? TriggerMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
