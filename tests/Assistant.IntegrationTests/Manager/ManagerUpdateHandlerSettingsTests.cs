using Assistant.Application.Common;
using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Bots;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Manager;
using Assistant.Infrastructure.Telegram;
using Assistant.IntegrationTests.Host;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.IntegrationTests.Manager;

public class ManagerUpdateHandlerSettingsTests : IntegrationTestBase
{
    private sealed class FixedClaimCode : IClaimCodeProvider
    {
        public string Code => "424242";
    }

    private sealed class SingleClientFactory : ITelegramClientFactory
    {
        public FakeTelegramClient Client { get; } = new();
        public ITelegramClient Create(string token) => Client;
    }

    private sealed class NoopScopeFactory : IServiceScopeFactory
    {
        private readonly Assistant.Infrastructure.Persistence.AssistantDbContext _db;
        public NoopScopeFactory(Assistant.Infrastructure.Persistence.AssistantDbContext db) => _db = db;
        public IServiceScope CreateScope() => new SingleInstanceScope(_db);

        private sealed class SingleInstanceScope : IServiceScope
        {
            public SingleInstanceScope(Assistant.Infrastructure.Persistence.AssistantDbContext db) => ServiceProvider = new SingleInstanceProvider(db);
            public IServiceProvider ServiceProvider { get; }
            public void Dispose() { }
        }

        private sealed class SingleInstanceProvider : IServiceProvider
        {
            private readonly Assistant.Infrastructure.Persistence.AssistantDbContext _db;
            public SingleInstanceProvider(Assistant.Infrastructure.Persistence.AssistantDbContext db) => _db = db;
            public object? GetService(Type serviceType) => serviceType == typeof(Assistant.Infrastructure.Persistence.AssistantDbContext) ? _db : null;
        }
    }

    private static readonly ReceivingBot ManagerBot = new(BotDbId: 1, TelegramBotId: 998, Username: "test_manager_bot", FamilyId: null, Role: "manager");

    private long _familyId;
    private long _ownerMemberId;
    private long _otherMemberId;
    private long _botId;
    private long _placeId;

    private async Task<(ManagerUpdateHandler Handler, FakeTelegramClient Telegram)> SetupAsync()
    {
        var family = new Family { Name = "test family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();
        _familyId = family.Id;

        var owner = new FamilyMember { FamilyId = family.Id, TelegramUserId = 111, DisplayName = "owner one", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var other = new FamilyMember { FamilyId = family.Id, TelegramUserId = 222, DisplayName = "member two", Status = FamilyMemberStatus.Approved, IsOwner = false, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        Db.FamilyMembers.AddRange(owner, other);
        await Db.SaveChangesAsync();
        _ownerMemberId = owner.Id;
        _otherMemberId = other.Id;

        var encryptorForSetup = new Assistant.Infrastructure.Common.TokenEncryptor("MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=");
        var bot = new Bot
        {
            FamilyId = family.Id, TelegramBotId = 1001, Username = "test_role_bot", Role = "general",
            TokenEncrypted = encryptorForSetup.Encrypt("test-role-bot-token"),
            Status = BotStatus.Active, LastUpdateId = 0, CreatedAt = DateTimeOffset.UtcNow
        };
        Db.Bots.Add(bot);
        await Db.SaveChangesAsync();
        _botId = bot.Id;

        var place = new Place { BotId = bot.Id, ChatId = -100, Title = "test chat", Status = PlaceStatus.Approved, CreatedAt = DateTimeOffset.UtcNow };
        Db.Places.Add(place);
        await Db.SaveChangesAsync();
        _placeId = place.Id;

        var clients = new SingleClientFactory();
        var options = Options.Create(new BotOptions { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var clock = new SystemClock();
        var approvals = new ApprovalService(Db, clients, options, clock);
        var encryptor = new Assistant.Infrastructure.Common.TokenEncryptor(options.Value.TokenEncryptionKey);
        var coordinator = new BotPollingCoordinator(
            new NoopScopeFactory(Db), clients, encryptor, options, PollingWorkerSettings.Default,
            new PollingHealth(), clock, NullLoggerFactory.Instance);
        var handler = new ManagerUpdateHandler(
            Db, new FixedClaimCode(), new PendingBotCreations(Db, clock), clients, encryptor, coordinator, approvals, clock,
            new UsageCommandHandler(Db, new Assistant.Infrastructure.Llm.NullBudgetGuard(), clock, llmConfig: null),
            new SettingsCommandHandler(Db, coordinator, clock), NullLogger<ManagerUpdateHandler>.Instance);

        return (handler, clients.Client);
    }

    private static IncomingUpdate Command(long updateId, long userId, string text) =>
        CommandInChat(updateId, userId, text, chatType: "private", chatId: userId);

    private static IncomingUpdate CommandInChat(long updateId, long userId, string text, string chatType, long chatId) =>
        new(updateId, new IncomingMessage(
            ChatId: chatId, ChatType: chatType, ChatTitle: null, TopicId: null, MessageId: (int)updateId, UserId: userId, Username: "test_owner",
            Text: text, Kind: Assistant.Domain.Messages.MessageKind.Text, IsEdit: false,
            SentAt: DateTimeOffset.UtcNow, EditedAt: null, MigrateToChatId: null, RawJson: "{}", ReplyToMessageId: null, ReplyToUserId: null));

    private static IncomingUpdate CallbackUpdate(long updateId, long fromUserId, string data) =>
        new(updateId, null, CallbackQuery: new CallbackQueryInfo(CallbackQueryId: $"cbq-{updateId}", FromUserId: fromUserId, Data: data, MessageChatId: fromUserId, MessageId: 1));

    [Fact]
    public async Task Settings_from_a_non_owner_is_rejected()
    {
        var (handler, telegram) = await SetupAsync();

        await handler.HandleAsync(ManagerBot, telegram, Command(1, 222, "/settings"), CancellationToken.None);

        telegram.SentMessages.ShouldContain(m => m.Text.Contains("владелец"));
        telegram.SentMessages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Settings_from_the_owner_lists_the_bot_the_place_and_both_members()
    {
        var (handler, telegram) = await SetupAsync();

        await handler.HandleAsync(ManagerBot, telegram, Command(1, 111, "/settings"), CancellationToken.None);

        telegram.SentMessages.ShouldContain(m => m.Text.Contains("test_role_bot"));
        telegram.SentMessages.ShouldContain(m => m.Text.Contains("test chat"));
        telegram.SentMessages.ShouldContain(m => m.Text.Contains("owner one"));
        telegram.SentMessages.ShouldContain(m => m.Text.Contains("member two"));
    }

    private async Task<long> AddPendingMemberAsync()
    {
        var pending = new FamilyMember { FamilyId = _familyId, TelegramUserId = 333, DisplayName = "member three", Status = FamilyMemberStatus.Pending, IsOwner = false, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        Db.FamilyMembers.Add(pending);
        await Db.SaveChangesAsync();
        return pending.Id;
    }

    [Fact]
    public async Task Settings_shows_a_pending_member_as_waiting_with_allow_and_deny()
    {
        var (handler, telegram) = await SetupAsync();
        var pendingId = await AddPendingMemberAsync();

        await handler.HandleAsync(ManagerBot, telegram, Command(1, 111, "/settings"), CancellationToken.None);

        var (text, buttons) = telegram.SentButtons.Single(m => m.Text.Contains("member three"));
        text.ShouldEndWith("ожидает");
        buttons.Select(b => b.CallbackData).ShouldBe(new[] { $"member_allow:{pendingId}", $"member_deny:{pendingId}" });
        telegram.SentButtons.Single(m => m.Text.Contains("member two")).Text.ShouldEndWith("активен");
    }

    [Fact]
    public async Task Disabling_the_bot_stops_it_being_active_and_can_be_re_enabled()
    {
        var (handler, telegram) = await SetupAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, 111, $"bot_disable:{_botId}"), CancellationToken.None);
        (await Db.Bots.FindAsync(_botId))!.Status.ShouldBe(BotStatus.Disabled);

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(2, 111, $"bot_enable:{_botId}"), CancellationToken.None);
        (await Db.Bots.FindAsync(_botId))!.Status.ShouldBe(BotStatus.Active);
    }

    [Fact]
    public async Task Removing_a_bot_deletes_it_and_its_places()
    {
        var (handler, telegram) = await SetupAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, 111, $"bot_remove:{_botId}"), CancellationToken.None);

        (await Db.Bots.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == _botId)).ShouldBeNull();
        // No FK ties places.bot_id to bots.id, so this only holds if bot_remove explicitly cleans
        // up the bot's places too — otherwise the row survives, permanently orphaned.
        (await Db.Places.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == _placeId)).ShouldBeNull();
        telegram.AnsweredCallbacks.ShouldContain(c => c.CallbackQueryId == "cbq-1" && c.Text == "Бот удалён.");
    }

    [Fact]
    public async Task Bot_remove_from_a_non_owner_does_not_delete_it()
    {
        var (handler, telegram) = await SetupAsync();

        var otherFamily = new Family { Name = "other family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(otherFamily);
        await Db.SaveChangesAsync();
        Db.FamilyMembers.Add(new FamilyMember { FamilyId = otherFamily.Id, TelegramUserId = 777, DisplayName = "other owner", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, 777, $"bot_remove:{_botId}"), CancellationToken.None);

        (await Db.Bots.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == _botId)).ShouldNotBeNull();
        (await Db.Places.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == _placeId)).ShouldNotBeNull();
        telegram.AnsweredCallbacks.ShouldContain(c => c.CallbackQueryId == "cbq-1" && c.Text == "У вас нет прав.");
    }

    [Fact]
    public async Task Removing_a_place_deletes_its_row()
    {
        var (handler, telegram) = await SetupAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, 111, $"settingsplace_remove:{_placeId}"), CancellationToken.None);

        (await Db.Places.FindAsync(_placeId)).ShouldBeNull();
    }

    [Fact]
    public async Task Making_the_second_member_an_owner_lets_both_spouses_own_the_family()
    {
        var (handler, telegram) = await SetupAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, 111, $"member_makeowner:{_otherMemberId}"), CancellationToken.None);

        var ownerCount = await Db.FamilyMembers.IgnoreQueryFilters().CountAsync(m => m.FamilyId == _familyId && m.IsOwner);
        ownerCount.ShouldBe(2);
    }

    [Fact]
    public async Task Disabling_a_member_denies_them_and_enabling_approves_them_again()
    {
        var (handler, telegram) = await SetupAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, 111, $"member_disable:{_otherMemberId}"), CancellationToken.None);
        (await Db.FamilyMembers.FindAsync(_otherMemberId))!.Status.ShouldBe(FamilyMemberStatus.Denied);

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(2, 111, $"member_enable:{_otherMemberId}"), CancellationToken.None);
        (await Db.FamilyMembers.FindAsync(_otherMemberId))!.Status.ShouldBe(FamilyMemberStatus.Approved);
    }

    [Fact]
    public async Task Bot_disable_from_a_non_owner_does_not_disable_it()
    {
        var (handler, telegram) = await SetupAsync();

        // A second, unrelated family whose caller is a genuine approved owner — just not of the
        // family that owns the bot being tapped on. Proves the check is family-scoped.
        var otherFamily = new Family { Name = "other family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(otherFamily);
        await Db.SaveChangesAsync();
        Db.FamilyMembers.Add(new FamilyMember { FamilyId = otherFamily.Id, TelegramUserId = 777, DisplayName = "other owner", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, 777, $"bot_disable:{_botId}"), CancellationToken.None);

        (await Db.Bots.FindAsync(_botId))!.Status.ShouldBe(BotStatus.Active);
        telegram.AnsweredCallbacks.ShouldContain(c => c.CallbackQueryId == "cbq-1" && c.Text == "У вас нет прав.");
    }

    [Fact]
    public async Task Member_makeowner_from_a_non_owner_does_not_grant_ownership()
    {
        var (handler, telegram) = await SetupAsync();

        // The tap comes from the non-owner member's own account — a realistic attacker who is a
        // legitimate approved member of the family but not an owner.
        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, 222, $"member_makeowner:{_otherMemberId}"), CancellationToken.None);

        (await Db.FamilyMembers.FindAsync(_otherMemberId))!.IsOwner.ShouldBeFalse();
        var ownerCount = await Db.FamilyMembers.IgnoreQueryFilters().CountAsync(m => m.FamilyId == _familyId && m.IsOwner);
        ownerCount.ShouldBe(1);
        telegram.AnsweredCallbacks.ShouldContain(c => c.CallbackQueryId == "cbq-1" && c.Text == "У вас нет прав.");
    }

    [Fact]
    public async Task Usage_from_the_owner_in_a_private_chat_gets_a_reply()
    {
        var (handler, telegram) = await SetupAsync();

        await handler.HandleAsync(ManagerBot, telegram, Command(1, 111, "/usage"), CancellationToken.None);

        telegram.SentMessages.ShouldContain(m => m.Text.Contains("вызовов"));
    }

    private async Task SetBotRoleAsync(string role)
    {
        var bot = await Db.Bots.IgnoreQueryFilters().SingleAsync(b => b.Id == _botId);
        bot.Role = role;
        await Db.SaveChangesAsync();
    }

    private async Task<bool> StoredReplyToAllAsync() =>
        (await Db.Places.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == _placeId)).ReplyToAll;

    private async Task<IReadOnlyList<InlineButton>> PlaceButtonsAsync(ManagerUpdateHandler handler, FakeTelegramClient telegram, long updateId)
    {
        await handler.HandleAsync(ManagerBot, telegram, Command(updateId, 111, "/settings"), CancellationToken.None);
        return telegram.SentButtons.Single(m => m.Text.StartsWith("Место «test chat»")).Buttons;
    }

    [Fact]
    public async Task Settings_offers_a_reply_to_all_button_that_mirrors_the_stored_state_for_a_general_bot()
    {
        var (handler, telegram) = await SetupAsync();

        var buttons = await PlaceButtonsAsync(handler, telegram, 1);

        buttons.Count.ShouldBe(3);
        buttons[2].Label.ShouldBe("Отвечать на все: выкл");
        buttons[2].CallbackData.ShouldBe($"settingsplace_autoreply_on:{_placeId}");

        var place = await Db.Places.IgnoreQueryFilters().SingleAsync(p => p.Id == _placeId);
        place.ReplyToAll = true;
        await Db.SaveChangesAsync();
        telegram.ClearSent();

        buttons = await PlaceButtonsAsync(handler, telegram, 2);

        buttons[2].Label.ShouldBe("Отвечать на все: вкл");
        buttons[2].CallbackData.ShouldBe($"settingsplace_autoreply_off:{_placeId}");
    }

    [Fact]
    public async Task Settings_treats_a_role_with_stray_spaces_and_capitals_as_general()
    {
        var (handler, telegram) = await SetupAsync();
        await SetBotRoleAsync(" GENERAL ");

        var buttons = await PlaceButtonsAsync(handler, telegram, 1);

        buttons.ShouldContain(b => b.CallbackData == $"settingsplace_autoreply_on:{_placeId}");
    }

    [Fact]
    public async Task Settings_hides_the_reply_to_all_button_when_the_bot_is_not_general()
    {
        var (handler, telegram) = await SetupAsync();
        await SetBotRoleAsync("test");

        var buttons = await PlaceButtonsAsync(handler, telegram, 1);

        buttons.Select(b => b.CallbackData).ShouldBe(new[] { $"settingsplace_disable:{_placeId}", $"settingsplace_remove:{_placeId}" });
    }

    [Fact]
    public async Task Settings_adds_the_topic_number_to_a_topic_places_line_only()
    {
        var (handler, telegram) = await SetupAsync();
        Db.Places.Add(new Place { BotId = _botId, ChatId = -100, TopicId = 7, Title = "test chat", Status = PlaceStatus.Approved, CreatedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();

        await handler.HandleAsync(ManagerBot, telegram, Command(1, 111, "/settings"), CancellationToken.None);

        var lines = telegram.SentButtons.Select(m => m.Text).ToList();
        lines.ShouldContain("Место «test chat»: активно");
        lines.ShouldContain("Место «test chat» (тема 7): активно");
    }

    [Fact]
    public async Task Owner_can_switch_reply_to_all_on_then_off_and_repeating_a_tap_does_not_flip_it()
    {
        var (handler, telegram) = await SetupAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, 111, $"settingsplace_autoreply_on:{_placeId}"), CancellationToken.None);
        (await StoredReplyToAllAsync()).ShouldBeTrue();
        telegram.AnsweredCallbacks.ShouldContain(c => c.CallbackQueryId == "cbq-1" && c.Text == "Готово: отвечаю на все сообщения.");

        // The same (now stale) button tapped again means the same target state.
        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(2, 111, $"settingsplace_autoreply_on:{_placeId}"), CancellationToken.None);
        (await StoredReplyToAllAsync()).ShouldBeTrue();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(3, 111, $"settingsplace_autoreply_off:{_placeId}"), CancellationToken.None);
        (await StoredReplyToAllAsync()).ShouldBeFalse();
        telegram.AnsweredCallbacks.ShouldContain(c => c.CallbackQueryId == "cbq-3" && c.Text == "Готово: отвечаю только на обращения.");
    }

    [Fact]
    public async Task Reply_to_all_tap_by_a_non_owner_member_changes_nothing()
    {
        var (handler, telegram) = await SetupAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, 222, $"settingsplace_autoreply_on:{_placeId}"), CancellationToken.None);

        (await StoredReplyToAllAsync()).ShouldBeFalse();
        telegram.AnsweredCallbacks.ShouldContain(c => c.CallbackQueryId == "cbq-1" && c.Text == "У вас нет прав.");
    }

    [Fact]
    public async Task Reply_to_all_tap_by_the_owner_of_a_different_family_changes_nothing()
    {
        var (handler, telegram) = await SetupAsync();
        var otherFamily = new Family { Name = "other family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(otherFamily);
        await Db.SaveChangesAsync();
        Db.FamilyMembers.Add(new FamilyMember { FamilyId = otherFamily.Id, TelegramUserId = 777, DisplayName = "other owner", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, 777, $"settingsplace_autoreply_on:{_placeId}"), CancellationToken.None);

        (await StoredReplyToAllAsync()).ShouldBeFalse();
        telegram.AnsweredCallbacks.ShouldContain(c => c.CallbackQueryId == "cbq-1" && c.Text == "У вас нет прав.");
    }

    [Fact]
    public async Task Reply_to_all_tap_for_a_non_general_bot_is_refused_even_from_the_owner()
    {
        var (handler, telegram) = await SetupAsync();
        await SetBotRoleAsync("test");

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, 111, $"settingsplace_autoreply_on:{_placeId}"), CancellationToken.None);

        (await StoredReplyToAllAsync()).ShouldBeFalse();
        telegram.AnsweredCallbacks.ShouldContain(c => c.CallbackQueryId == "cbq-1" && c.Text == "Доступно только для бота general.");
    }

    [Fact]
    public async Task Usage_in_a_group_chat_gets_no_reply()
    {
        // /usage can show the caller's own family's per-bot/per-model spend breakdown -- unlike
        // /settings, that is not something to post where every member of the group can read it.
        var (handler, telegram) = await SetupAsync();

        await handler.HandleAsync(ManagerBot, telegram, CommandInChat(1, 111, "/usage", chatType: "group", chatId: -500), CancellationToken.None);

        telegram.SentMessages.ShouldBeEmpty();
    }
}
