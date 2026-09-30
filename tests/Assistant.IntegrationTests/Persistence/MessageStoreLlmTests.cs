using Assistant.Application.Common;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.IntegrationTests.Persistence;

public class MessageStoreLlmTests : IntegrationTestBase
{
    private const long BotId = 999;
    private const long OtherBotId = 998;

    private MessageStore CreateStore() => new(Db, new SystemClock(), NullLogger<MessageStore>.Instance);

    private async Task<Bot> EnsureBotAsync(long botId = BotId, long? familyId = null)
    {
        var bot = new Bot
        {
            FamilyId = familyId,
            TelegramBotId = botId,
            Username = "test_bot",
            Role = "general",
            Status = BotStatus.Active,
            LastUpdateId = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        Db.Bots.Add(bot);
        await Db.SaveChangesAsync();
        return bot;
    }

    private static IncomingMessage IncomingText(
        int messageId, long chatId, int? topicId = null, string? text = "hi", MessageKind kind = MessageKind.Text) =>
        new(
            ChatId: chatId,
            ChatType: "private",
            ChatTitle: null,
            TopicId: topicId,
            MessageId: messageId,
            UserId: 111,
            Username: "test_user",
            Text: text,
            Kind: kind,
            IsEdit: false,
            SentAt: DateTimeOffset.UtcNow,
            EditedAt: null,
            MigrateToChatId: null,
            RawJson: "{\"ok\":true}",
            ReplyToMessageId: null,
            ReplyToUserId: null);

    [Fact]
    public async Task Outgoing_messages_are_stored_with_direction_out_and_no_user()
    {
        await EnsureBotAsync();
        var store = CreateStore();

        await store.StoreOutgoingAsync(BotId, chatId: 111, topicId: null, chatType: "private", telegramMessageId: 9001, text: "test reply", CancellationToken.None);

        var stored = await Db.Messages.SingleAsync(m => m.TelegramMessageId == 9001);
        stored.Direction.ShouldBe(MessageDirection.Out);
        stored.UserId.ShouldBeNull();
        stored.Text.ShouldBe("test reply");
    }

    [Fact]
    public async Task Context_includes_both_directions_in_id_order_and_respects_the_cap()
    {
        // Interleaves real incoming rows (via StoreAsync) with outgoing rows (via
        // StoreOutgoingAsync) so the test exercises both write paths landing in one ordered
        // timeline, not just StoreOutgoingAsync in isolation.
        await EnsureBotAsync();
        var store = CreateStore();

        await store.StoreAsync(BotId, 1, IncomingText(1, chatId: 222, text: "user 1"), CancellationToken.None);
        await store.StoreOutgoingAsync(BotId, chatId: 222, topicId: null, chatType: "private", telegramMessageId: 9100, text: "bot 1", CancellationToken.None);
        await store.StoreAsync(BotId, 2, IncomingText(2, chatId: 222, text: "user 2"), CancellationToken.None);
        await store.StoreOutgoingAsync(BotId, chatId: 222, topicId: null, chatType: "private", telegramMessageId: 9101, text: "bot 2", CancellationToken.None);
        await store.StoreAsync(BotId, 3, IncomingText(3, chatId: 222, text: "user 3"), CancellationToken.None);

        var context = await store.GetRecentContextAsync(BotId, chatId: 222, topicId: null, afterMessageId: null, beforeMessageId: null, maxMessages: 3, CancellationToken.None);

        context.Select(c => c.Text).ShouldBe(new[] { "user 2", "bot 2", "user 3" });
        context.Select(c => c.Direction).ShouldBe(new[] { MessageDirection.In, MessageDirection.Out, MessageDirection.In });
    }

    [Fact]
    public async Task Context_excludes_messages_at_or_before_the_new_command_cutoff_id()
    {
        // Spec §8.3: /new is chat_settings.context_start_message_id (a messages.id, not a
        // timestamp); context includes only messages with a strictly greater id.
        await EnsureBotAsync();
        var store = CreateStore();
        await store.StoreOutgoingAsync(BotId, chatId: 333, topicId: null, chatType: "private", telegramMessageId: 9200, text: "before reset", CancellationToken.None);
        var cutoffId = (await Db.Messages.SingleAsync(m => m.TelegramMessageId == 9200)).Id;
        await store.StoreOutgoingAsync(BotId, chatId: 333, topicId: null, chatType: "private", telegramMessageId: 9201, text: "after reset", CancellationToken.None);

        var context = await store.GetRecentContextAsync(BotId, chatId: 333, topicId: null, afterMessageId: cutoffId, beforeMessageId: null, maxMessages: 30, CancellationToken.None);

        context.Select(c => c.Text).ShouldBe(new[] { "after reset" });
    }

    [Fact]
    public async Task Commands_are_excluded_from_context_even_though_their_kind_is_text()
    {
        // Blocking finding: commands like /model x are stored with Kind == Text (there is no
        // separate "command" Kind), so they must be filtered by their leading '/' instead.
        await EnsureBotAsync();
        var store = CreateStore();
        await store.StoreAsync(BotId, 1, IncomingText(1, chatId: 444, text: "hello"), CancellationToken.None);
        await store.StoreAsync(BotId, 2, IncomingText(2, chatId: 444, text: "/model x"), CancellationToken.None);
        await store.StoreAsync(BotId, 3, IncomingText(3, chatId: 444, text: "how are you"), CancellationToken.None);

        var context = await store.GetRecentContextAsync(BotId, chatId: 444, topicId: null, afterMessageId: null, beforeMessageId: null, maxMessages: 30, CancellationToken.None);

        context.Select(c => c.Text).ShouldBe(new[] { "hello", "how are you" });
    }

    [Fact]
    public async Task BeforeMessageId_excludes_the_current_message_from_its_own_context()
    {
        // Blocking finding: the incoming message is stored before context is built, so without a
        // cutoff it would duplicate itself into its own context.
        await EnsureBotAsync();
        var store = CreateStore();
        await store.StoreAsync(BotId, 1, IncomingText(1, chatId: 555, text: "earlier"), CancellationToken.None);
        var storeResult = await store.StoreAsync(BotId, 2, IncomingText(2, chatId: 555, text: "current"), CancellationToken.None);

        var context = await store.GetRecentContextAsync(
            BotId, chatId: 555, topicId: null, afterMessageId: null, beforeMessageId: storeResult.MessageDbId, maxMessages: 30, CancellationToken.None);

        context.Select(c => c.Text).ShouldBe(new[] { "earlier" });
    }

    [Fact]
    public async Task Context_is_scoped_to_bot_chat_and_topic()
    {
        await EnsureBotAsync();
        await EnsureBotAsync(OtherBotId);
        var store = CreateStore();

        await store.StoreOutgoingAsync(BotId, chatId: 666, topicId: null, chatType: "group", telegramMessageId: 1, text: "this bot, chat-wide", CancellationToken.None);
        await store.StoreOutgoingAsync(BotId, chatId: 777, topicId: null, chatType: "group", telegramMessageId: 2, text: "other chat", CancellationToken.None);
        await store.StoreOutgoingAsync(OtherBotId, chatId: 666, topicId: null, chatType: "group", telegramMessageId: 3, text: "other bot", CancellationToken.None);
        await store.StoreOutgoingAsync(BotId, chatId: 666, topicId: 5, chatType: "group", telegramMessageId: 4, text: "topic 5", CancellationToken.None);

        var chatWide = await store.GetRecentContextAsync(BotId, chatId: 666, topicId: null, afterMessageId: null, beforeMessageId: null, maxMessages: 30, CancellationToken.None);
        chatWide.Select(c => c.Text).ShouldBe(new[] { "this bot, chat-wide" });

        var topicScoped = await store.GetRecentContextAsync(BotId, chatId: 666, topicId: 5, afterMessageId: null, beforeMessageId: null, maxMessages: 30, CancellationToken.None);
        topicScoped.Select(c => c.Text).ShouldBe(new[] { "topic 5" });
    }

    [Fact]
    public async Task Non_text_rows_are_excluded_from_context()
    {
        await EnsureBotAsync();
        var store = CreateStore();
        await store.StoreAsync(BotId, 1, IncomingText(1, chatId: 888, text: null, kind: MessageKind.Photo), CancellationToken.None);
        await store.StoreAsync(BotId, 2, IncomingText(2, chatId: 888, text: "caption or plain text"), CancellationToken.None);

        var context = await store.GetRecentContextAsync(BotId, chatId: 888, topicId: null, afterMessageId: null, beforeMessageId: null, maxMessages: 30, CancellationToken.None);

        context.Select(c => c.Text).ShouldBe(new[] { "caption or plain text" });
    }

    [Fact]
    public async Task Outgoing_row_copies_family_id_from_the_bot_row()
    {
        var family = new Family { Name = "test family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();
        await EnsureBotAsync(familyId: family.Id);
        var store = CreateStore();

        await store.StoreOutgoingAsync(BotId, chatId: 111, topicId: null, chatType: "private", telegramMessageId: 9300, text: "reply", CancellationToken.None);

        var stored = await Db.Messages.SingleAsync(m => m.TelegramMessageId == 9300);
        stored.FamilyId.ShouldBe(family.Id);
    }

    [Fact]
    public async Task StoreOutgoingAsync_does_not_advance_the_bots_last_update_id()
    {
        var bot = await EnsureBotAsync();
        bot.LastUpdateId = 42;
        await Db.SaveChangesAsync();
        var store = CreateStore();

        await store.StoreOutgoingAsync(BotId, chatId: 111, topicId: null, chatType: "private", telegramMessageId: 9400, text: "reply", CancellationToken.None);

        (await store.GetLastUpdateIdAsync(BotId, CancellationToken.None)).ShouldBe(42);
    }
}
