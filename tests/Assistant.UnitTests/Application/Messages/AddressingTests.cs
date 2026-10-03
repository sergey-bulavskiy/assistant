using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;

namespace Assistant.UnitTests.Application.Messages;

public class AddressingTests
{
    private static readonly ReceivingBot Bot = new(BotDbId: 1, TelegramBotId: 999, Username: "test_bot", FamilyId: 42, Role: "general");
    private static readonly ReceivingBot HealthBot = Bot with { Username = "test_health_bot", Role = "health" };

    private static IncomingMessage Msg(string chatType = "group", int? topicId = null, int? replyToMessageId = null, long? replyToUserId = null) =>
        new(
            ChatId: chatType == "private" ? 111 : -100,
            ChatType: chatType,
            ChatTitle: null,
            TopicId: topicId,
            MessageId: 100,
            UserId: 555,
            Username: null,
            Text: "test question",
            Kind: MessageKind.Text,
            IsEdit: false,
            SentAt: new DateTimeOffset(2030, 2, 7, 10, 0, 0, TimeSpan.Zero),
            EditedAt: null,
            MigrateToChatId: null,
            RawJson: "{}",
            ReplyToMessageId: replyToMessageId,
            ReplyToUserId: replyToUserId);

    [Fact]
    public void Private_chat_is_always_addressed()
    {
        Assert.True(Addressing.IsAddressed(Bot, Msg("private"), "test question"));
    }

    [Theory]
    [InlineData("@test_bot test question")]
    [InlineData("test question @TEST_BOT")]
    [InlineData("hey @Test_Bot, test question")]
    public void Mentions_address_the_bot(string text)
    {
        Assert.True(Addressing.IsAddressed(Bot, Msg(), text));
    }

    [Theory]
    [InlineData("@test_bot2 test question")]
    [InlineData("@test_bot_other test question")]
    [InlineData("test_bot test question")]
    [InlineData("me@test_bot test question")]
    [InlineData("test question")]
    public void Other_mentions_do_not(string text)
    {
        Assert.False(Addressing.IsAddressed(Bot, Msg(), text));
    }

    [Fact]
    public void A_genuine_reply_to_the_bot_is_addressed()
    {
        Assert.True(Addressing.IsAddressed(Bot, Msg(replyToMessageId: 77, replyToUserId: 999), "test question"));
        Assert.True(Addressing.IsAddressed(Bot, Msg("supergroup", topicId: 42, replyToMessageId: 77, replyToUserId: 999), "test question"));
    }

    [Fact]
    public void A_reply_to_the_topic_root_is_not()
    {
        Assert.False(Addressing.IsAddressed(Bot, Msg("supergroup", topicId: 42, replyToMessageId: 42, replyToUserId: 999), "test question"));
    }

    [Fact]
    public void A_reply_to_someone_else_is_not()
    {
        Assert.False(Addressing.IsAddressed(Bot, Msg(replyToMessageId: 77, replyToUserId: 555), "test question"));
    }

    [Fact]
    public void Health_bot_uses_its_own_username()
    {
        Assert.True(Addressing.IsAddressed(HealthBot, Msg(), "@test_health_bot какой сахар считается нормой?"));
        Assert.False(Addressing.IsAddressed(HealthBot, Msg(), "@test_health_bot_b какой сахар считается нормой?"));
        Assert.False(Addressing.IsAddressed(HealthBot, Msg(), "@test_bot какой сахар считается нормой?"));
    }

    [Fact]
    public void MentionsBot_matches_whole_usernames()
    {
        Assert.True(Addressing.MentionsBot("@test_bot", "test_bot"));
        Assert.False(Addressing.MentionsBot("@test_bot2", "test_bot"));
        Assert.False(Addressing.MentionsBot("test_bot", "test_bot"));
    }
}
