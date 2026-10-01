namespace Assistant.Domain.Llm;

// One row per model attempt (spec 3.1: "record every attempt"), including attempts that fail over
// to the next candidate model. Guard refusals (RateLimited, DailyCapReached) make no call and write
// no row -- only actual attempts land here, which is also what the rate/day guards count from.
public class LlmCall
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long BotId { get; set; }
    public string Tier { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public LlmCallOutcome Outcome { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public decimal? ReportedCost { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
