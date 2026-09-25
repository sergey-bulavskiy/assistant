using System.Text.Json;
using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assistant.IntegrationTests.Persistence;

public class MessageStoreTests : IntegrationTestBase
{
    private const long BotId = 999;

    private MessageStore CreateStore() => new(Db, new SystemClock());

    private static IncomingMessage TextMessage(
        int messageId,
        long chatId = 111,
        long userId = 111,
        string? text = "тест 1",
        bool isEdit = false,
        long? migrateTo = null,
        MessageKind kind = MessageKind.Text) =>
        new(
            ChatId: chatId,
            ChatType: "private",
            TopicId: null,
            MessageId: messageId,
            UserId: userId,
            Username: "test_user",
            Text: text,
            Kind: kind,
            IsEdit: isEdit,
            SentAt: DateTimeOffset.UtcNow,
            EditedAt: isEdit ? DateTimeOffset.UtcNow : null,
            MigrateToChatId: migrateTo,
            RawJson: "{\"ok\":true}");

    private async Task EnsureBotAsync()
    {
        await CreateStore().EnsureBotStateAsync(new BotIdentity(BotId, "test_bot"), CancellationToken.None);
    }

    [Fact]
    public async Task New_message_is_stored()
    {
        await EnsureBotAsync();
        var store = CreateStore();

        var result = await store.StoreAsync(BotId, 1, TextMessage(1), CancellationToken.None);

        result.Outcome.ShouldBe(StoreOutcome.Stored);
        result.MessageDbId.ShouldNotBeNull();
        (await Db.Messages.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Same_update_id_seen_twice_is_already_processed()
    {
        await EnsureBotAsync();
        var store = CreateStore();

        await store.StoreAsync(BotId, 10, TextMessage(10), CancellationToken.None);
        var second = await store.StoreAsync(BotId, 10, TextMessage(10), CancellationToken.None);

        second.Outcome.ShouldBe(StoreOutcome.AlreadyProcessed);
        (await Db.Messages.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Same_message_id_resent_under_a_new_update_id_is_a_duplicate()
    {
        await EnsureBotAsync();
        var store = CreateStore();

        await store.StoreAsync(BotId, 20, TextMessage(20), CancellationToken.None);
        var result = await store.StoreAsync(BotId, 21, TextMessage(20), CancellationToken.None);

        result.Outcome.ShouldBe(StoreOutcome.Duplicate);
        (await Db.Messages.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Edit_of_a_known_message_updates_the_row()
    {
        await EnsureBotAsync();
        var store = CreateStore();

        await store.StoreAsync(BotId, 30, TextMessage(30, text: "original"), CancellationToken.None);
        var result = await store.StoreAsync(BotId, 31, TextMessage(30, text: "edited", isEdit: true), CancellationToken.None);

        result.Outcome.ShouldBe(StoreOutcome.Updated);
        var stored = await Db.Messages.SingleAsync();
        stored.Text.ShouldBe("edited");
        stored.EditedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Edit_of_an_unknown_message_is_inserted_as_stored()
    {
        await EnsureBotAsync();
        var store = CreateStore();

        var result = await store.StoreAsync(BotId, 40, TextMessage(40, text: "edited but never seen", isEdit: true), CancellationToken.None);

        result.Outcome.ShouldBe(StoreOutcome.Stored);
        (await Db.Messages.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Null_message_advances_offset_only_and_stores_nothing()
    {
        await EnsureBotAsync();
        var store = CreateStore();

        var result = await store.StoreAsync(BotId, 50, null, CancellationToken.None);

        result.Outcome.ShouldBe(StoreOutcome.OffsetOnly);
        (await Db.Messages.CountAsync()).ShouldBe(0);
        (await store.GetLastUpdateIdAsync(BotId, CancellationToken.None)).ShouldBe(50);
    }

    [Fact]
    public async Task Migration_to_new_chat_id_is_recorded_once_even_if_seen_twice()
    {
        await EnsureBotAsync();
        var store = CreateStore();

        await store.StoreAsync(BotId, 60, TextMessage(60, chatId: -100, text: null, migrateTo: -200, kind: MessageKind.Service), CancellationToken.None);
        await store.StoreAsync(BotId, 61, TextMessage(61, chatId: -100, text: null, migrateTo: -200, kind: MessageKind.Service), CancellationToken.None);

        var migrations = await Db.ChatMigrations.Where(m => m.FromChatId == -100).ToListAsync();
        migrations.ShouldHaveSingleItem();
        migrations[0].ToChatId.ShouldBe(-200);
    }

    [Fact]
    public async Task Sanitized_text_stores_cleanly_with_no_nul_characters()
    {
        await EnsureBotAsync();
        var store = CreateStore();

        var sanitizedText = TextSanitizer.SanitizeText("caption 1\0 note");
        var result = await store.StoreAsync(BotId, 70, TextMessage(70, text: sanitizedText), CancellationToken.None);

        result.Outcome.ShouldBe(StoreOutcome.Stored);
        var stored = await Db.Messages.SingleAsync(m => m.TelegramMessageId == 70);
        stored.Text.ShouldBe("caption 1 note");
        stored.Text.ShouldNotBeNull();
        stored.Text.ShouldNotContain('\0');
    }

    [Fact]
    public async Task Unsanitized_nul_character_in_text_is_rejected_by_postgres()
    {
        await EnsureBotAsync();
        var store = CreateStore();

        var exception = await Should.ThrowAsync<DbUpdateException>(() =>
            store.StoreAsync(BotId, 80, TextMessage(80, text: "caption 1\0 note"), CancellationToken.None));

        exception.InnerException.ShouldBeOfType<PostgresException>();
    }

    [Fact]
    public async Task Message_with_literal_backslash_u0000_text_is_stored_and_raw_json_round_trips()
    {
        // C1: the literal 6 characters `\u0000` (one backslash, not an escape) must survive the
        // raw-JSON sanitizer intact — a naive substring removal turns it into invalid JSON that
        // Postgres would reject, wedging polling. Build rawJson the same way the real mapper does:
        // serialize, then sanitize.
        await EnsureBotAsync();
        var store = CreateStore();

        var literalText = "note \\u0000 here";
        var rawJson = TextSanitizer.SanitizeRawJson(JsonSerializer.Serialize(new { text = literalText }));

        var message = TextMessage(90, text: literalText) with { RawJson = rawJson };
        var result = await store.StoreAsync(BotId, 90, message, CancellationToken.None);

        result.Outcome.ShouldBe(StoreOutcome.Stored);

        var stored = await Db.Messages.SingleAsync(m => m.TelegramMessageId == 90);
        stored.Text.ShouldBe(literalText);

        using var doc = JsonDocument.Parse(stored.Raw);
        doc.RootElement.GetProperty("text").GetString().ShouldBe(literalText);
    }
}
