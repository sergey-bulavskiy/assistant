using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.UnitTests.Application.Messages;

public class UpdateHandlerTests
{
    private static UpdateHandler CreateHandler(FakeMessageStore store, FakeTelegramClient telegram, string allowedUserIds = "111,222")
    {
        var options = Options.Create(new BotOptions { Token = "test-token", AllowedUserIdsRaw = allowedUserIds });
        var buildInfo = new BuildInfo("abcdef1", null, DateTimeOffset.UtcNow);
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        return new UpdateHandler(store, telegram, options, buildInfo, clock, NullLogger<UpdateHandler>.Instance);
    }

    private static IncomingMessage Message(long userId = 111, string chatType = "private", string? text = "тест 1") =>
        new(
            ChatId: 111,
            ChatType: chatType,
            TopicId: null,
            MessageId: 1,
            UserId: userId,
            Username: "test_user",
            Text: text,
            Kind: MessageKind.Text,
            IsEdit: false,
            SentAt: DateTimeOffset.UtcNow,
            EditedAt: null,
            MigrateToChatId: null,
            RawJson: "{}");

    [Fact]
    public async Task Non_allowed_user_is_stored_as_offset_only_and_not_replied_to()
    {
        var store = new FakeMessageStore();
        store.SetNextResult(new StoreResult(StoreOutcome.OffsetOnly, null));
        var telegram = new FakeTelegramClient();
        var handler = CreateHandler(store, telegram, allowedUserIds: "222");

        await handler.HandleAsync(999, "test_bot", new IncomingUpdate(1, Message(userId: 111)), CancellationToken.None);

        store.Calls.ShouldHaveSingleItem();
        store.Calls[0].Message.ShouldBeNull();
        telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Allowed_private_message_is_stored_and_acknowledged()
    {
        var store = new FakeMessageStore();
        store.SetNextResult(new StoreResult(StoreOutcome.Stored, 7));
        var telegram = new FakeTelegramClient();
        var handler = CreateHandler(store, telegram);

        await handler.HandleAsync(999, "test_bot", new IncomingUpdate(2, Message(userId: 111)), CancellationToken.None);

        store.Calls.Single().Message.ShouldNotBeNull();
        telegram.Sent.Single().Text.ShouldBe("Получил ✅ #7");
    }

    [Fact]
    public async Task Failed_reply_send_is_swallowed_and_does_not_throw()
    {
        var store = new FakeMessageStore();
        store.SetNextResult(new StoreResult(StoreOutcome.Stored, 7));
        var telegram = new FakeTelegramClient { ThrowOnSend = true };
        var handler = CreateHandler(store, telegram);

        await Should.NotThrowAsync(() =>
            handler.HandleAsync(999, "test_bot", new IncomingUpdate(3, Message(userId: 111)), CancellationToken.None));
    }

    [Fact]
    public async Task Null_message_update_is_stored_as_offset_only()
    {
        var store = new FakeMessageStore();
        store.SetNextResult(new StoreResult(StoreOutcome.OffsetOnly, null));
        var telegram = new FakeTelegramClient();
        var handler = CreateHandler(store, telegram);

        await handler.HandleAsync(999, "test_bot", new IncomingUpdate(4, null), CancellationToken.None);

        store.Calls.Single().Message.ShouldBeNull();
        telegram.Sent.ShouldBeEmpty();
    }
}
