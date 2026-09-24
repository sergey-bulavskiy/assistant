using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;

namespace Assistant.UnitTests.Application.Telegram;

public class ReplyPolicyTests
{
    private static IncomingMessage Message(
        string chatType = "private",
        MessageKind kind = MessageKind.Text,
        bool isEdit = false,
        string? text = "hello") =>
        new(
            ChatId: 111,
            ChatType: chatType,
            TopicId: null,
            MessageId: 1,
            UserId: 111,
            Username: "test_user",
            Text: text,
            Kind: kind,
            IsEdit: isEdit,
            SentAt: DateTimeOffset.UtcNow,
            EditedAt: null,
            MigrateToChatId: null,
            RawJson: "{}");

    [Fact]
    public void Returns_null_when_message_is_null()
    {
        ReplyPolicy.Decide(null, new StoreResult(StoreOutcome.Stored, 1), "test_bot", () => "v").ShouldBeNull();
    }

    [Theory]
    [InlineData(StoreOutcome.AlreadyProcessed)]
    [InlineData(StoreOutcome.Duplicate)]
    [InlineData(StoreOutcome.OffsetOnly)]
    public void Returns_null_for_outcomes_that_never_reply(StoreOutcome outcome)
    {
        ReplyPolicy.Decide(Message(), new StoreResult(outcome, 1), "test_bot", () => "v").ShouldBeNull();
    }

    [Fact]
    public void Returns_null_for_service_messages()
    {
        var message = Message(kind: MessageKind.Service);
        ReplyPolicy.Decide(message, new StoreResult(StoreOutcome.Stored, 1), "test_bot", () => "v").ShouldBeNull();
    }

    [Fact]
    public void Returns_null_for_edits()
    {
        var message = Message(isEdit: true);
        ReplyPolicy.Decide(message, new StoreResult(StoreOutcome.Updated, 1), "test_bot", () => "v").ShouldBeNull();
    }

    [Theory]
    [InlineData("private")]
    [InlineData("group")]
    public void Version_command_replies_anywhere(string chatType)
    {
        var message = Message(chatType: chatType, text: "/version");
        var reply = ReplyPolicy.Decide(
            message, new StoreResult(StoreOutcome.Stored, 1), "test_bot", () => "abc1234 · built unknown · up 0d 0h 1m");
        reply.ShouldBe("abc1234 · built unknown · up 0d 0h 1m");
    }

    [Fact]
    public void Version_command_addressed_to_another_bot_is_ignored()
    {
        var message = Message(text: "/version@other_bot");
        var reply = ReplyPolicy.Decide(message, new StoreResult(StoreOutcome.Stored, 1), "test_bot", () => "v");
        reply.ShouldBeNull();
    }

    [Fact]
    public void Start_command_replies_in_private_only()
    {
        var privateMsg = Message(chatType: "private", text: "/start");
        ReplyPolicy.Decide(privateMsg, new StoreResult(StoreOutcome.Stored, 1), "test_bot", () => "v")
            .ShouldBe("Привет! Я сохраняю сообщения. /version — какая версия запущена.");

        var groupMsg = Message(chatType: "group", text: "/start");
        ReplyPolicy.Decide(groupMsg, new StoreResult(StoreOutcome.Stored, 1), "test_bot", () => "v").ShouldBeNull();
    }

    [Fact]
    public void Private_stored_message_gets_acknowledgement_with_id()
    {
        var message = Message(chatType: "private", text: "тест 1");
        ReplyPolicy.Decide(message, new StoreResult(StoreOutcome.Stored, 42), "test_bot", () => "v")
            .ShouldBe("Получил ✅ #42");
    }

    [Fact]
    public void Group_stored_message_gets_no_reply()
    {
        var message = Message(chatType: "group", text: "тест 1");
        ReplyPolicy.Decide(message, new StoreResult(StoreOutcome.Stored, 42), "test_bot", () => "v").ShouldBeNull();
    }
}
