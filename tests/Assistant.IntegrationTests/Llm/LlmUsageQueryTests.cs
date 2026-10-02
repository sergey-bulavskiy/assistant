using Assistant.Application.Llm;
using Assistant.Domain.Llm;
using Assistant.Infrastructure.Llm;
using Assistant.IntegrationTests.Infrastructure;

namespace Assistant.IntegrationTests.Llm;

public class LlmUsageQueryTests : IntegrationTestBase
{
    private const long FamilyId = 1;
    private const long BotId = 1001;
    private const long ChatId = -100;

    private void Add(
        long? triggerMessageId, string model = "sonnet", int? inputTokens = 100, int? outputTokens = 20,
        LlmCallOutcome outcome = LlmCallOutcome.Ok, long familyId = FamilyId, long botId = BotId, long? chatId = ChatId, int? topicId = null) =>
        Db.LlmCalls.Add(new LlmCall
        {
            FamilyId = familyId,
            BotId = botId,
            Tier = "smart",
            Provider = "test",
            Model = model,
            Outcome = outcome,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = 0m,
            DurationMs = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ChatId = chatId,
            TopicId = topicId,
            TriggerMessageId = triggerMessageId
        });

    private Task<LlmUsageSummary> QueryAsync(int? topicId, long? afterMessageId) =>
        new LlmUsageQuery(Db).GetChatUsageAsync(FamilyId, BotId, ChatId, topicId, afterMessageId, CancellationToken.None);

    [Fact]
    public async Task Counts_only_ok_calls_of_this_family_bot_chat_and_topic_after_the_cutoff()
    {
        Add(11, "sonnet", 100, 20);
        Add(12, "haiku", 50, 5);
        Add(13, "sonnet", 30, 7);
        Add(10, "sonnet", 1000, 1000);                                       // the /new itself: not after it
        Add(5, "sonnet", 1000, 1000);                                        // before /new
        Add(14, "sonnet", 1000, 1000, outcome: LlmCallOutcome.LimitReached); // fallback attempt
        Add(15, "sonnet", 1000, 1000, outcome: LlmCallOutcome.Failed);
        Add(16, "sonnet", 1000, 1000, chatId: -200);                         // other chat
        Add(17, "sonnet", 1000, 1000, botId: 2002);                          // other bot
        Add(18, "sonnet", 1000, 1000, topicId: 7);                           // a topic of this chat
        Add(19, "sonnet", 1000, 1000, familyId: 2);                          // other family
        Add(null, "sonnet", 1000, 1000);                                     // not counted (no trigger)
        await Db.SaveChangesAsync();

        var usage = await QueryAsync(topicId: null, afterMessageId: 10);

        usage.Models.ShouldBe(new[] { new LlmModelUsage("haiku", 1, 50, 5), new LlmModelUsage("sonnet", 2, 130, 27) });
        usage.Calls.ShouldBe(3);
        usage.InputTokens.ShouldBe(180);
        usage.OutputTokens.ShouldBe(32);
    }

    [Fact]
    public async Task Without_a_cutoff_counts_every_counted_call_and_missing_token_counts_add_zero()
    {
        Add(1, "sonnet", 10, 1);
        Add(2, "sonnet", 20, 2);
        Add(3, "sonnet", null, null);
        Add(null, "sonnet", 1000, 1000);
        await Db.SaveChangesAsync();

        var usage = await QueryAsync(topicId: null, afterMessageId: null);

        usage.Models.ShouldBe(new[] { new LlmModelUsage("sonnet", 3, 30, 3) });
    }

    [Fact]
    public async Task Topic_is_matched_null_safely()
    {
        Add(11, "sonnet", 10, 1, topicId: 7);
        Add(12, "sonnet", 20, 2, topicId: null);
        await Db.SaveChangesAsync();

        (await QueryAsync(topicId: 7, afterMessageId: null)).InputTokens.ShouldBe(10);
        (await QueryAsync(topicId: null, afterMessageId: null)).InputTokens.ShouldBe(20);
    }

    [Fact]
    public async Task No_counted_calls_returns_an_empty_summary()
    {
        Add(null, "sonnet", 1000, 1000);
        await Db.SaveChangesAsync();

        var usage = await QueryAsync(topicId: null, afterMessageId: null);

        usage.Models.ShouldBeEmpty();
        usage.Calls.ShouldBe(0);
    }
}
