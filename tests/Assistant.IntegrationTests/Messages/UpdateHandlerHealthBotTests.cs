using Assistant.Application.Common;
using Assistant.Application.Health;
using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Health;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Telegram;
using Assistant.IntegrationTests.Host;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.IntegrationTests.Messages;

/// <summary>The health assistant through UpdateHandler against the real stores: lazy profile
/// creation, the owner-only check and family scoping.</summary>
public class UpdateHandlerHealthBotTests : IntegrationTestBase
{
    private const long OwnerId = 111;
    private const long MemberId = 222;
    private const long OtherOwnerId = 333;

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.Parse("2030-02-07T10:00:00Z");
    }

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

    private sealed class NoopGeneralAssistant : IGeneralAssistant
    {
        public Task HandleAsync(
            ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken, bool replyToAll = false) =>
            Task.CompletedTask;
    }

    private int _nextUpdateId = 1;
    private int _nextMessageId = 1;

    private async Task<(UpdateHandler Handler, ReceivingBot Bot, FakeTelegramClient Telegram, MessageStore Store)> SetupAsync()
    {
        var family = new Family { Name = "test family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();

        AddMember(family.Id, OwnerId, isOwner: true);
        AddMember(family.Id, MemberId, isOwner: false);

        var bot = new Bot { FamilyId = family.Id, TelegramBotId = 1001, Username = "test_health_bot", Role = " Health ", Status = BotStatus.Active, LastUpdateId = 0, CreatedAt = DateTimeOffset.UtcNow };
        Db.Bots.Add(bot);
        await Db.SaveChangesAsync();

        var clients = new SingleClientFactory();
        var options = Options.Create(new BotOptions { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var clock = new FixedClock();
        var currentFamily = new CurrentFamily();
        var approvals = new ApprovalService(Db, clients, options, clock);
        var messageStore = new MessageStore(Db, clock, NullLogger<MessageStore>.Instance);
        var buildInfo = new BuildInfo("abcdef1", null, DateTimeOffset.UtcNow);
        var healthAssistant = new HealthAssistant(
            new HealthProfileStore(Db, currentFamily, clock), new FamilyOwnership(Db), clock, buildInfo, NullLogger<HealthAssistant>.Instance);
        var handler = new UpdateHandler(
            messageStore, approvals, currentFamily, new NoopManagerUpdateHandler(), new NoopGeneralAssistant(), healthAssistant, options, buildInfo, clock,
            NullLogger<UpdateHandler>.Instance);

        await messageStore.EnsureBotStateAsync(new BotIdentity(bot.TelegramBotId, bot.Username), CancellationToken.None);

        return (handler, new ReceivingBot(bot.Id, bot.TelegramBotId, bot.Username, family.Id, bot.Role), clients.Client, messageStore);
    }

    private void AddMember(long familyId, long userId, bool isOwner)
    {
        Db.FamilyMembers.Add(new FamilyMember
        {
            FamilyId = familyId, TelegramUserId = userId, DisplayName = "test user", Status = FamilyMemberStatus.Approved, IsOwner = isOwner,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private IncomingMessage PrivateText(long userId, string text) =>
        new(ChatId: userId, ChatType: "private", ChatTitle: null, TopicId: null, MessageId: _nextMessageId++, UserId: userId, Username: "test_user",
            Text: text, Kind: MessageKind.Text, IsEdit: false, SentAt: DateTimeOffset.UtcNow, EditedAt: null,
            MigrateToChatId: null, RawJson: "{}", ReplyToMessageId: null, ReplyToUserId: null);

    private Task SendAsync(UpdateHandler handler, ReceivingBot bot, FakeTelegramClient telegram, long userId, string text) =>
        handler.HandleAsync(bot, telegram, new IncomingUpdate(_nextUpdateId++, PrivateText(userId, text)), CancellationToken.None);

    [Fact]
    public async Task First_message_creates_the_profile_with_the_default_rules_once()
    {
        var (handler, bot, telegram, _) = await SetupAsync();

        await SendAsync(handler, bot, telegram, OwnerId, "/week");
        await SendAsync(handler, bot, telegram, OwnerId, "/week");

        telegram.SentMessages.Select(m => m.Text).ShouldBe(new[] { "Неделя: не задана (/setstart)", "Неделя: не задана (/setstart)" });
        var profile = await Db.HealthProfiles.IgnoreQueryFilters().SingleAsync();
        profile.BotId.ShouldBe(bot.BotDbId);
        profile.FamilyId.ShouldBe(bot.FamilyId!.Value);
        var rules = await Db.SafetyRules.IgnoreQueryFilters().ToListAsync();
        rules.Count.ShouldBe(11);
        rules.ShouldAllBe(r => r.FamilyId == bot.FamilyId.Value);
    }

    [Fact]
    public async Task Owner_sets_the_start_date_and_week_follows()
    {
        var (handler, bot, telegram, _) = await SetupAsync();

        await SendAsync(handler, bot, telegram, OwnerId, "/setstart 15.01.2030");

        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe("Начало отсчёта: 15.01.2030. Неделя: 3 нед. 2 дн.");
        var profile = await Db.HealthProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        profile.StageStartDate.ShouldBe(new DateOnly(2030, 1, 15));
        profile.UpdatedByUserId.ShouldBe(OwnerId);

        telegram.ClearSent();
        await SendAsync(handler, bot, telegram, OwnerId, "/week");
        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe("Неделя: 3 нед. 2 дн.");
    }

    [Fact]
    public async Task Non_owner_cannot_change_the_profile()
    {
        var (handler, bot, telegram, _) = await SetupAsync();

        await SendAsync(handler, bot, telegram, MemberId, "/setstart 15.01.2030");

        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe("Только владелец семьи может менять профиль.");
        var profile = await Db.HealthProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        profile.StageStartDate.ShouldBeNull();
    }

    [Fact]
    public async Task Doctor_threshold_is_saved_shown_and_restored()
    {
        var (handler, bot, telegram, _) = await SetupAsync();

        await SendAsync(handler, bot, telegram, OwnerId, "/threshold glucose.any low_alert 4.0");

        async Task<SafetyRule> GlucoseAnyAsync() =>
            await Db.SafetyRules.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.RuleKey == "glucose.any");

        var changed = await GlucoseAnyAsync();
        changed.LowAlert.ShouldBe(4.00m);
        changed.Source.ShouldBe("doctor");
        changed.UpdatedByUserId.ShouldBe(OwnerId);

        telegram.ClearSent();
        await SendAsync(handler, bot, telegram, OwnerId, "/thresholds");
        var lines = telegram.SentMessages.ShouldHaveSingleItem().Text.Split('\n');
        lines.ShouldContain("glucose.any: low_urgent 3.0, low_alert 4.0, high_alert 11.0, high_urgent 13.9 — врач");
        lines.ShouldContain("glucose.fasting: target_high 5.1 — не подтверждено врачом");

        telegram.ClearSent();
        await SendAsync(handler, bot, telegram, MemberId, "/threshold glucose.any low_alert 5.0");
        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe("Только владелец семьи может менять профиль.");
        (await GlucoseAnyAsync()).LowAlert.ShouldBe(4.00m);

        await SendAsync(handler, bot, telegram, OwnerId, "/threshold glucose.any default");
        var restored = await GlucoseAnyAsync();
        restored.LowAlert.ShouldBe(3.90m);
        restored.Source.ShouldBe("guideline_default");
    }

    [Fact]
    public async Task Plain_text_is_stored_without_a_reply()
    {
        var (handler, bot, telegram, _) = await SetupAsync();

        await SendAsync(handler, bot, telegram, OwnerId, "test message");

        var stored = await Db.Messages.IgnoreQueryFilters().SingleAsync();
        stored.Text.ShouldBe("test message");
        telegram.SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_families_health_bots_keep_separate_profiles()
    {
        var (handler, botA, telegram, store) = await SetupAsync();

        var familyB = new Family { Name = "test family B", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(familyB);
        await Db.SaveChangesAsync();
        AddMember(familyB.Id, OtherOwnerId, isOwner: true);
        var dbBotB = new Bot { FamilyId = familyB.Id, TelegramBotId = 1002, Username = "test_health_bot_b", Role = "health", Status = BotStatus.Active, LastUpdateId = 0, CreatedAt = DateTimeOffset.UtcNow };
        Db.Bots.Add(dbBotB);
        await Db.SaveChangesAsync();
        await store.EnsureBotStateAsync(new BotIdentity(dbBotB.TelegramBotId, dbBotB.Username), CancellationToken.None);
        var botB = new ReceivingBot(dbBotB.Id, dbBotB.TelegramBotId, dbBotB.Username, familyB.Id, dbBotB.Role);

        await SendAsync(handler, botA, telegram, OwnerId, "/setstart 15.01.2030");
        telegram.ClearSent();
        await SendAsync(handler, botB, telegram, OtherOwnerId, "/week");

        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe("Неделя: не задана (/setstart)");
        var profiles = await Db.HealthProfiles.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        profiles.Count.ShouldBe(2);
        var profileB = profiles.Single(p => p.BotId == botB.BotDbId);
        profileB.FamilyId.ShouldBe(familyB.Id);
        profileB.StageStartDate.ShouldBeNull();
        var rules = await Db.SafetyRules.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rules.Count.ShouldBe(22);
        foreach (var rule in rules)
        {
            rule.FamilyId.ShouldBe(profiles.Single(p => p.Id == rule.ProfileId).FamilyId);
        }
    }
}
