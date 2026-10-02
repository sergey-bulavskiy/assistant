using Assistant.Application.Llm;

namespace Assistant.UnitTests.Fakes;

public class FakeLlmUsageQuery : ILlmUsageQuery
{
    public LlmUsageSummary NextSummary { get; set; } = new(Array.Empty<LlmModelUsage>());

    public List<(long FamilyId, long BotId, long ChatId, int? TopicId, long? AfterMessageId)> Calls { get; } = new();

    public Task<LlmUsageSummary> GetChatUsageAsync(long familyId, long botId, long chatId, int? topicId, long? afterMessageId, CancellationToken cancellationToken)
    {
        Calls.Add((familyId, botId, chatId, topicId, afterMessageId));
        return Task.FromResult(NextSummary);
    }
}
