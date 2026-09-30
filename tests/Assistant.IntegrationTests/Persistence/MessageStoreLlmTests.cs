using Assistant.Application.Common;
using Assistant.Domain.Bots;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.IntegrationTests.Persistence;

public class MessageStoreLlmTests : IntegrationTestBase
{
    private const long BotId = 999;

    private MessageStore CreateStore() => new(Db, new SystemClock(), NullLogger<MessageStore>.Instance);

    private async Task EnsureBotAsync()
    {
        Db.Bots.Add(new Bot
        {
            TelegramBotId = BotId,
            Username = "test_bot",
            Role = "general",
            Status = BotStatus.Active,
            LastUpdateId = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Outgoing_messages_are_stored_with_direction_out_and_no_user()
    {
        await EnsureBotAsync();
        var store = CreateStore();

        await store.StoreOutgoingAsync(BotId, chatId: 111, topicId: null, chatType: "private", telegramMessageId: 9001, text: "test reply", CancellationToken.None);

        var stored = await Db.Messages.IgnoreQueryFilters().SingleAsync(m => m.TelegramMessageId == 9001);
        stored.Direction.ShouldBe(MessageDirection.Out);
        stored.UserId.ShouldBeNull();
        stored.Text.ShouldBe("test reply");
    }

    [Fact]
    public async Task Context_includes_both_directions_oldest_first_and_respects_the_cap()
    {
        await EnsureBotAsync();
        var store = CreateStore();
        for (var i = 0; i < 5; i++)
        {
            await store.StoreOutgoingAsync(BotId, chatId: 222, topicId: null, chatType: "private", telegramMessageId: 9100 + i, text: $"out {i}", CancellationToken.None);
        }

        var context = await store.GetRecentContextAsync(BotId, chatId: 222, topicId: null, afterMessageId: null, maxMessages: 3, CancellationToken.None);

        context.Count.ShouldBe(3);
        context.Select(c => c.Text).ShouldBe(new[] { "out 2", "out 3", "out 4" });
    }

    [Fact]
    public async Task Context_excludes_messages_at_or_before_the_new_command_cutoff_id()
    {
        // Spec §8.3: /new is chat_settings.context_start_message_id (a messages.id, not a
        // timestamp); context includes only messages with a strictly greater id.
        await EnsureBotAsync();
        var store = CreateStore();
        await store.StoreOutgoingAsync(BotId, chatId: 333, topicId: null, chatType: "private", telegramMessageId: 9200, text: "before reset", CancellationToken.None);
        var cutoffId = (await Db.Messages.IgnoreQueryFilters().SingleAsync(m => m.TelegramMessageId == 9200)).Id;
        await store.StoreOutgoingAsync(BotId, chatId: 333, topicId: null, chatType: "private", telegramMessageId: 9201, text: "after reset", CancellationToken.None);

        var context = await store.GetRecentContextAsync(BotId, chatId: 333, topicId: null, afterMessageId: cutoffId, maxMessages: 30, CancellationToken.None);

        context.Select(c => c.Text).ShouldBe(new[] { "after reset" });
    }
}
