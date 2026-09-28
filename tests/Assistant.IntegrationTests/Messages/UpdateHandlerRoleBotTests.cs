using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Telegram;
using Assistant.IntegrationTests.Host;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.IntegrationTests.Messages;

public class UpdateHandlerRoleBotTests : IntegrationTestBase
{
    private sealed class SingleClientFactory : ITelegramClientFactory
    {
        public FakeTelegramClient Client { get; } = new();

        public ITelegramClient Create(string token) => Client;
    }

    private sealed class NoopManagerUpdateHandler : IManagerUpdateHandler
    {
        public Task HandleAsync(ReceivingBot managerBot, ITelegramClient telegramClient, IncomingUpdate update, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private async Task<(UpdateHandler Handler, ReceivingBot Bot, FakeTelegramClient Telegram)> SetupAsync()
    {
        var family = new Family { Name = "test family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();

        Db.FamilyMembers.Add(new FamilyMember { FamilyId = family.Id, TelegramUserId = 900, DisplayName = "owner", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        var bot = new Bot { FamilyId = family.Id, TelegramBotId = 1001, Username = "test_role_bot", Role = "general", Status = BotStatus.Active, LastUpdateId = 0, CreatedAt = DateTimeOffset.UtcNow };
        Db.Bots.Add(bot);
        await Db.SaveChangesAsync();

        var receivingBot = new ReceivingBot(bot.Id, bot.TelegramBotId, bot.Username, family.Id, bot.Role);

        var clients = new SingleClientFactory();
        var options = Options.Create(new BotOptions { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var clock = new SystemClock();
        var currentFamily = new CurrentFamily();
        var approvals = new ApprovalService(Db, clients, options, clock);
        var messageStore = new MessageStore(Db, clock, NullLogger<MessageStore>.Instance);
        var buildInfo = new BuildInfo("abcdef1", null, DateTimeOffset.UtcNow);
        var handler = new UpdateHandler(messageStore, approvals, currentFamily, new NoopManagerUpdateHandler(), options, buildInfo, clock, NullLogger<UpdateHandler>.Instance);

        await messageStore.EnsureBotStateAsync(new BotIdentity(bot.TelegramBotId, bot.Username), CancellationToken.None);

        return (handler, receivingBot, clients.Client);
    }

    private static IncomingMessage GroupText(int messageId, long chatId, long userId, string text) =>
        new(ChatId: chatId, ChatType: "group", TopicId: null, MessageId: messageId, UserId: userId, Username: "test_user",
            Text: text, Kind: MessageKind.Text, IsEdit: false, SentAt: DateTimeOffset.UtcNow, EditedAt: null,
            MigrateToChatId: null, RawJson: "{}");

    private static IncomingMessage GroupService(int messageId, long chatId, long userId) =>
        new(ChatId: chatId, ChatType: "group", TopicId: null, MessageId: messageId, UserId: userId, Username: "test_user",
            Text: null, Kind: MessageKind.Service, IsEdit: false, SentAt: DateTimeOffset.UtcNow, EditedAt: null,
            MigrateToChatId: null, RawJson: "{}");

    [Fact]
    public async Task First_message_from_an_unknown_chat_is_ignored_and_requests_a_place_approval()
    {
        var (handler, bot, telegram) = await SetupAsync();

        await handler.HandleAsync(bot, telegram, new IncomingUpdate(1, GroupText(1, -100, 111, "hello")), CancellationToken.None);

        (await Db.Messages.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        (await Db.Places.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        telegram.SentMessages.ShouldContain(m => m.ChatId == 900);
    }

    [Fact]
    public async Task Message_after_the_place_and_user_are_approved_is_stored_with_the_bots_family_id()
    {
        var (handler, bot, telegram) = await SetupAsync();

        // Round trip 1: first message only creates a pending place (the user-gate is never reached
        // while the place is pending — UpdateHandler returns early).
        await handler.HandleAsync(bot, telegram, new IncomingUpdate(1, GroupText(1, -100, 111, "hello")), CancellationToken.None);
        var place = await Db.Places.IgnoreQueryFilters().SingleAsync();
        place.Status = Assistant.Domain.Places.PlaceStatus.Approved;
        await Db.SaveChangesAsync();

        // Round trip 2: with the place now approved, the resent message reaches the user-gate and
        // creates a pending family member for user 111.
        await handler.HandleAsync(bot, telegram, new IncomingUpdate(2, GroupText(2, -100, 111, "hello again")), CancellationToken.None);
        var member = await Db.FamilyMembers.IgnoreQueryFilters().SingleAsync(m => m.TelegramUserId == 111);
        member.Status = FamilyMemberStatus.Approved;
        await Db.SaveChangesAsync();

        // Round trip 3: both gates are now approved, so the resent message is finally stored.
        await handler.HandleAsync(bot, telegram, new IncomingUpdate(3, GroupText(3, -100, 111, "hello again")), CancellationToken.None);

        var stored = await Db.Messages.IgnoreQueryFilters().SingleAsync();
        stored.FamilyId.ShouldBe(bot.FamilyId);
        stored.Text.ShouldBe("hello again");
    }

    [Fact]
    public async Task Service_message_from_a_denied_place_is_not_stored()
    {
        var (handler, bot, telegram) = await SetupAsync();

        // First message creates a pending place; deny it explicitly.
        await handler.HandleAsync(bot, telegram, new IncomingUpdate(1, GroupText(1, -100, 111, "hello")), CancellationToken.None);
        var place = await Db.Places.IgnoreQueryFilters().SingleAsync();
        place.Status = Assistant.Domain.Places.PlaceStatus.Denied;
        await Db.SaveChangesAsync();

        // A Service-kind update (e.g. a member joining/leaving, a pin) from the same denied chat
        // must be gated exactly like a text message — approval gating is not a text-only rule.
        await handler.HandleAsync(bot, telegram, new IncomingUpdate(2, GroupService(2, -100, 111)), CancellationToken.None);

        (await Db.Messages.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
    }
}
