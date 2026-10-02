namespace Assistant.Application.Llm;

public record LlmModelUsage(string Model, int Calls, long InputTokens, long OutputTokens);

/// <summary>Answered (Ok) calls of one chat/topic, per model, ordered by model name.</summary>
public record LlmUsageSummary(IReadOnlyList<LlmModelUsage> Models)
{
    public int Calls => Models.Sum(m => m.Calls);

    public long InputTokens => Models.Sum(m => m.InputTokens);

    public long OutputTokens => Models.Sum(m => m.OutputTokens);
}

/// <summary>Token usage for /tokens. botId has the same meaning as llm_calls.bot_id (the bot's
/// Telegram id). Counts only Ok attempts with a trigger message id, and with
/// <paramref name="afterMessageId"/> set only those whose trigger message id is greater (the /new
/// cutoff).</summary>
public interface ILlmUsageQuery
{
    Task<LlmUsageSummary> GetChatUsageAsync(long familyId, long botId, long chatId, int? topicId, long? afterMessageId, CancellationToken cancellationToken);
}
