using Assistant.Application.Common;
using System.Text.Json;
using Assistant.Application.Health;
using Assistant.Application.Llm;
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
using Assistant.Infrastructure.Roles;
using Assistant.Infrastructure.Telegram;
using Assistant.IntegrationTests.Host;
using Assistant.IntegrationTests.Infrastructure;
using Assistant.IntegrationTests.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.IntegrationTests.Messages;

/// <summary>The health assistant through UpdateHandler against the real stores: lazy profile
/// creation, the owner-only check, family scoping and event extraction through the real LlmGateway
/// (scripted chat client).</summary>
public class UpdateHandlerHealthBotTests : IntegrationTestBase
{
    private const long OwnerId = 111;
    private const long MemberId = 222;
    private const long OtherOwnerId = 333;

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-02-07T10:00:00Z");

    private const string NoEventsJson = "{\"events\":[],\"unclear\":[],\"is_question\":false}";

    private const string GlucoseAt930Json =
        "{\"events\":[{\"type\":\"glucose\",\"day\":0,\"time\":\"09:30\",\"value\":7.8,\"unit\":\"mmol/L\",\"context\":\"after_meal_1h\"}]," +
        "\"unclear\":[],\"is_question\":false}";

    private readonly ScriptedChatClient _chat = new();

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class NoopBudgetNoticeDispatcher : IBudgetNoticeDispatcher
    {
        public Task Dispatch() => Task.CompletedTask;
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
    // Far from the fake client's sent message ids (1, 2, …): an answer stored as an outgoing row must
    // not hit the unique (bot, chat, message id) key of an incoming one.
    private int _nextMessageId = 1000;

    private async Task<(UpdateHandler Handler, ReceivingBot Bot, FakeTelegramClient Telegram, MessageStore Store)> SetupAsync(
        ILlmGateway? gatewayOverride = null)
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
        // FastModels empty: the fast tier falls back to LLM_MODELS.
        var config = new LlmConfig
        {
            Models = new[] { new ModelCatalogEntry("fake", "model-a") },
            CallsPerMinute = 100,
            CallsPerDay = 1000,
            MaxContextMessages = 30,
            MaxInputChars = 8000,
            MaxOutputTokens = 1024,
            CallTimeoutSeconds = 30,
            MaxConcurrentCalls = 4,
            ModelCooldownMinutes = 15,
            Prices = new Dictionary<string, ModelPrice>(),
            Budget = null,
            FastModels = Array.Empty<ModelCatalogEntry>()
        };
        var gateway = new LlmGateway(
            config, new ModelCatalog(config), new ModelAvailability(clock),
            new ChatClientProvider(new Dictionary<string, IChatClient> { ["fake"] = _chat }), Db, clock, new ConcurrentCallGate(4),
            new BudgetGuard(config, Db, clock), new NoopBudgetNoticeDispatcher(), NullLogger<LlmGateway>.Instance);
        var healthAssistant = new HealthAssistant(
            new HealthProfileStore(Db, currentFamily, clock), new FamilyOwnership(Db), new EventStore(Db, currentFamily, clock),
            new SafetyAlertStore(Db, currentFamily, clock), messageStore, gatewayOverride ?? gateway, config,
            new RolePrompts(typeof(RolePrompts).Assembly), new FailureNoticeThrottle(), new AddressedHintThrottle(), clock, buildInfo, NullLogger<HealthAssistant>.Instance);
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

    private IncomingMessage PrivateText(long userId, string text, int? replyToMessageId = null) =>
        new(ChatId: userId, ChatType: "private", ChatTitle: null, TopicId: null, MessageId: _nextMessageId++, UserId: userId, Username: "test_user",
            Text: text, Kind: MessageKind.Text, IsEdit: false, SentAt: Now, EditedAt: null,
            MigrateToChatId: null, RawJson: "{}", ReplyToMessageId: replyToMessageId, ReplyToUserId: null);

    private IncomingMessage GroupText(long userId, string text) =>
        new(ChatId: -100, ChatType: "group", ChatTitle: "test group", TopicId: null, MessageId: _nextMessageId++, UserId: userId, Username: "test_user",
            Text: text, Kind: MessageKind.Text, IsEdit: false, SentAt: Now, EditedAt: null,
            MigrateToChatId: null, RawJson: "{}", ReplyToMessageId: null, ReplyToUserId: null);

    private static IncomingMessage PrivateEdit(long userId, int messageId, string text) =>
        new(ChatId: userId, ChatType: "private", ChatTitle: null, TopicId: null, MessageId: messageId, UserId: userId, Username: "test_user",
            Text: text, Kind: MessageKind.Text, IsEdit: true, SentAt: Now, EditedAt: Now.AddMinutes(1),
            MigrateToChatId: null, RawJson: "{}", ReplyToMessageId: null, ReplyToUserId: null);

    private Task SendAsync(UpdateHandler handler, ReceivingBot bot, FakeTelegramClient telegram, long userId, string text) =>
        handler.HandleAsync(bot, telegram, new IncomingUpdate(_nextUpdateId++, PrivateText(userId, text)), CancellationToken.None);

    private Task HandleUpdateAsync(UpdateHandler handler, ReceivingBot bot, FakeTelegramClient telegram, IncomingUpdate update) =>
        handler.HandleAsync(bot, telegram, update, CancellationToken.None);

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
    public async Task Plain_text_is_stored_and_a_private_chat_gets_only_the_hint()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(NoEventsJson);

        await SendAsync(handler, bot, telegram, OwnerId, "test message");

        var stored = await Db.Messages.IgnoreQueryFilters().SingleAsync();
        stored.Text.ShouldBe("test message");
        telegram.SentMessages.ShouldHaveSingleItem().Text
            .ShouldBe("Слушаю. Запишите показатель (например: сахар 5.8 после обеда) или задайте вопрос.");
        telegram.Reactions.ShouldBeEmpty();
        var call = await Db.LlmCalls.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        call.Tier.ShouldBe("fast");
        call.Model.ShouldBe("model-a");
        call.TriggerMessageId.ShouldBe(stored.Id);
        call.ChatId.ShouldBe(OwnerId);
        (await Db.Events.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Dangerous_reading_the_model_missed_gets_the_alert_and_nothing_is_recorded()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(NoEventsJson);

        await SendAsync(handler, bot, telegram, OwnerId, "сахар 2.5");

        var stored = await Db.Messages.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        var sent = telegram.SentMessages.ShouldHaveSingleItem();
        sent.Text.ShouldStartWith("\U0001F6A8 Глюкоза: 2.5.");
        sent.Text.ShouldEndWith("\nНичего не записано — повторите сообщение позже.");
        sent.ReplyToMessageId.ShouldBe(stored.TelegramMessageId);
        telegram.Reactions.ShouldBeEmpty();
        (await Db.Events.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        (await Db.SafetyAlerts.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Reading_is_recorded_with_a_reaction()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(GlucoseAt930Json);

        await SendAsync(handler, bot, telegram, OwnerId, "сахар 7.8 в 9:30");

        var stored = await Db.Messages.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        var profile = await Db.HealthProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        var saved = await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        saved.FamilyId.ShouldBe(bot.FamilyId!.Value);
        saved.ProfileId.ShouldBe(profile.Id);
        saved.Type.ShouldBe("glucose");
        saved.SubjectTag.ShouldBe("health");
        saved.OccurredAt.ShouldBe(DateTimeOffset.Parse("2030-02-07T09:30:00Z"));
        saved.OccurredAtSource.ShouldBe("stated");
        using (var payload = JsonDocument.Parse(saved.Payload))
        {
            payload.RootElement.GetProperty("value").GetDecimal().ShouldBe(7.8m);
            payload.RootElement.GetProperty("context").GetString().ShouldBe("after_meal_1h");
        }

        // 7.8 one hour after a meal is at or above the default one-hour target (7.0): a flag, no message.
        saved.Flags.ShouldBe(new[] { "out_of_target" });
        saved.SourceMessageId.ShouldBe(stored.Id);
        saved.BotId.ShouldBe(1001);
        saved.ChatId.ShouldBe(OwnerId);
        saved.TopicId.ShouldBeNull();
        saved.RecordedByUserId.ShouldBe(OwnerId);
        saved.DeletedAt.ShouldBeNull();
        telegram.Reactions.ShouldBe(new[] { (OwnerId, stored.TelegramMessageId, (string?)"✍") });
        telegram.SentMessages.ShouldBeEmpty();
        (await Db.SafetyAlerts.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Redelivered_message_is_recorded_and_marked_once()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        // Only one answer is scripted: a second model call would throw and send the failure notice.
        _chat.EnqueueResponse(GlucoseAt930Json);
        var message = PrivateText(OwnerId, "сахар 7.8 в 9:30");

        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId, message));
        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId, message));
        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId + 1, message));

        (await Db.Events.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        (await Db.LlmCalls.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        telegram.Reactions.Count.ShouldBe(1);
        telegram.SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unclear_number_gets_a_clarification_and_nothing_is_recorded()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse("{\"events\":[],\"unclear\":[{\"fragment\":\"18\",\"reason\":\"unit\"}],\"is_question\":false}");

        await SendAsync(handler, bot, telegram, OwnerId, "утром 18");

        var stored = await Db.Messages.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        var sent = telegram.SentMessages.ShouldHaveSingleItem();
        sent.Text.ShouldBe("Не понял «18» — уточните единицы (нужно в ммоль/л).");
        sent.ReplyToMessageId.ShouldBe(stored.TelegramMessageId);
        (await Db.Events.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        telegram.Reactions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Model_failure_sends_the_failure_notice_once_per_ten_minutes()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueException(new InvalidOperationException("simulated provider failure"));
        _chat.EnqueueException(new InvalidOperationException("simulated provider failure"));

        await SendAsync(handler, bot, telegram, OwnerId, "сахар 7.8");
        await SendAsync(handler, bot, telegram, OwnerId, "давление 128/84");

        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe(ExtractionReplies.FailureNotice);
        (await Db.Events.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        telegram.Reactions.ShouldBeEmpty();
    }

    private const string WeightJson =
        "{\"events\":[{\"type\":\"weight\",\"day\":0,\"time\":null,\"kg\":64.5}],\"unclear\":[],\"is_question\":false}";

    [Fact]
    public async Task Undo_removes_the_latest_reading_and_today_shows_the_rest()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(GlucoseAt930Json);
        _chat.EnqueueResponse(WeightJson);
        await SendAsync(handler, bot, telegram, OwnerId, "сахар 7.8 в 9:30");
        await SendAsync(handler, bot, telegram, OwnerId, "вес 64.5");
        var glucoseId = (await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Type == "glucose")).Id;
        var weightRow = await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Type == "weight");
        var weightMessage = await Db.Messages.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.Id == weightRow.SourceMessageId);

        telegram.ClearSent();
        await SendAsync(handler, bot, telegram, OwnerId, "/today");
        var lines = telegram.SentMessages.ShouldHaveSingleItem().Text.Split('\n');
        lines.Length.ShouldBe(3);
        lines[1].ShouldBe($"#{glucoseId} 09:30 глюкоза 7.8 ммоль/л (через 1 ч после еды)");
        lines[2].ShouldBe($"#{weightRow.Id} 10:00 вес 64.5 кг");

        telegram.ClearSent();
        await SendAsync(handler, bot, telegram, OwnerId, "/undo");

        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe($"Удалено: #{weightRow.Id} вес 64.5 кг.");
        telegram.Reactions.ShouldContain((OwnerId, weightMessage.TelegramMessageId, (string?)null));
        var deleted = await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == weightRow.Id);
        deleted.DeletedAt.ShouldNotBeNull();
        deleted.DeleteReason.ShouldBe("undo");

        telegram.ClearSent();
        await SendAsync(handler, bot, telegram, OwnerId, "/today");
        var after = telegram.SentMessages.ShouldHaveSingleItem().Text.Split('\n');
        after.Length.ShouldBe(2);
        after[1].ShouldStartWith($"#{glucoseId} ");
    }

    [Fact]
    public async Task Undo_and_reply_delete_never_cross_users_or_chats()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        Db.Places.Add(new Assistant.Domain.Places.Place
        {
            BotId = bot.BotDbId, ChatId = -100, TopicId = null, Title = "test group", Status = Assistant.Domain.Places.PlaceStatus.Approved,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await Db.SaveChangesAsync();

        // The owner records a reading in the group chat.
        _chat.EnqueueResponse(GlucoseAt930Json);
        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId++, GroupText(OwnerId, "сахар 7.8 в 9:30")));
        var row = await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        row.ChatId.ShouldBe(-100);
        var message = await Db.Messages.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.Id == row.SourceMessageId);

        // Another member's /undo in the same chat, the owner's /undo from another chat and a reply
        // to that message from another chat all find nothing.
        telegram.ClearSent();
        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId++, GroupText(MemberId, "/undo")));
        await SendAsync(handler, bot, telegram, OwnerId, "/undo");
        await HandleUpdateAsync(handler, bot, telegram,
            new IncomingUpdate(_nextUpdateId++, PrivateText(MemberId, "/del", replyToMessageId: message.TelegramMessageId)));

        telegram.SentMessages.Select(m => m.Text).ShouldBe(new[] { "Нечего отменять.", "Нечего отменять.", "Не нашёл такую запись." });
        telegram.Reactions.Where(r => r.Emoji == null).ShouldBeEmpty();
        (await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync()).DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Del_works_by_reply_and_never_across_families()
    {
        var (handler, botA, telegram, store) = await SetupAsync();
        _chat.EnqueueResponse(GlucoseAt930Json);
        await SendAsync(handler, botA, telegram, OwnerId, "сахар 7.8 в 9:30");
        var row = await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        var message = await Db.Messages.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.Id == row.SourceMessageId);

        var familyB = new Family { Name = "test family B", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(familyB);
        await Db.SaveChangesAsync();
        AddMember(familyB.Id, OtherOwnerId, isOwner: true);
        var dbBotB = new Bot { FamilyId = familyB.Id, TelegramBotId = 1002, Username = "test_health_bot_b", Role = "health", Status = BotStatus.Active, LastUpdateId = 0, CreatedAt = DateTimeOffset.UtcNow };
        Db.Bots.Add(dbBotB);
        await Db.SaveChangesAsync();
        await store.EnsureBotStateAsync(new BotIdentity(dbBotB.TelegramBotId, dbBotB.Username), CancellationToken.None);
        var botB = new ReceivingBot(dbBotB.Id, dbBotB.TelegramBotId, dbBotB.Username, familyB.Id, dbBotB.Role);

        telegram.ClearSent();
        await SendAsync(handler, botB, telegram, OtherOwnerId, $"/del {row.Id}");

        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe("Не нашёл такую запись.");
        (await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync()).DeletedAt.ShouldBeNull();

        telegram.ClearSent();
        await HandleUpdateAsync(handler, botA, telegram,
            new IncomingUpdate(_nextUpdateId++, PrivateText(OwnerId, "/del", replyToMessageId: message.TelegramMessageId)));

        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldStartWith($"Удалено: #{row.Id} ");
        (await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync()).DeleteReason.ShouldBe("del");
        telegram.Reactions.ShouldContain((OwnerId, message.TelegramMessageId, (string?)null));
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

    // --- Safety rules and alerts ---

    private const string LowGlucoseJson =
        "{\"events\":[{\"type\":\"glucose\",\"day\":0,\"time\":null,\"value\":2.5,\"unit\":\"mmol/L\",\"context\":\"other\"}]," +
        "\"unclear\":[],\"is_question\":false}";

    private const string LowGlucoseAt930Json =
        "{\"events\":[{\"type\":\"glucose\",\"day\":0,\"time\":\"09:30\",\"value\":2.5,\"unit\":\"mmol/L\",\"context\":\"after_meal_1h\"}]," +
        "\"unclear\":[],\"is_question\":false}";

    private const string LowGlucoseAndPressureJson =
        "{\"events\":[{\"type\":\"glucose\",\"day\":0,\"time\":null,\"value\":2.5,\"unit\":\"mmol/L\",\"context\":\"other\"}," +
        "{\"type\":\"blood_pressure\",\"day\":0,\"time\":null,\"systolic\":150,\"diastolic\":95}],\"unclear\":[],\"is_question\":false}";

    private const string NotRecorded = "\nНичего не записано — повторите сообщение позже.";

    private const string DoctorLowJson =
        "{\"events\":[{\"type\":\"glucose\",\"day\":0,\"time\":null,\"value\":3.95,\"unit\":\"mmol/L\",\"context\":\"other\"}]," +
        "\"unclear\":[],\"is_question\":false}";

    private const string PressureJson =
        "{\"events\":[{\"type\":\"blood_pressure\",\"day\":0,\"time\":null,\"systolic\":150,\"diastolic\":95}],\"unclear\":[],\"is_question\":false}";

    private const string HeadacheJson =
        "{\"events\":[{\"type\":\"symptom\",\"day\":0,\"time\":null,\"code\":\"headache\",\"text\":\"болит голова\"}],\"unclear\":[],\"is_question\":false}";

    private const string UrgentLow25 =
        "\U0001F6A8 Глюкоза: 2.5. Это может быть опасно. Срочно свяжитесь с врачом или вызовите скорую (103 или 112). " +
        "Порог 3.0 — не подтверждено врачом. Действуйте по плану врача.";

    private const string DoctorLow395 =
        "⚠️ Глюкоза: 3.95 — ниже порога 4.0 (порог от врача). Свяжитесь с врачом. " +
        "Если самочувствие ухудшается — вызовите скорую (103 или 112). Действуйте по плану врача.";

    private const string SystolicAlert150 =
        "⚠️ Верхнее давление: 150 — выше порога 140 (не подтверждено врачом). Свяжитесь с врачом. " +
        "Если самочувствие ухудшается — вызовите скорую (103 или 112).";

    private const string Combo15095 =
        "\U0001F6A8 Давление 150/95 вместе с симптомом «головная боль». Это может быть опасно. " +
        "Срочно свяжитесь с врачом или вызовите скорую (103 или 112). (не подтверждено врачом)";

    private Task<List<SafetyAlert>> AlertRowsAsync() =>
        Db.SafetyAlerts.IgnoreQueryFilters().AsNoTracking().OrderBy(a => a.Id).ToListAsync();

    private async Task<ReceivingBot> AddFamilyBAsync(MessageStore store)
    {
        var familyB = new Family { Name = "test family B", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(familyB);
        await Db.SaveChangesAsync();
        AddMember(familyB.Id, OtherOwnerId, isOwner: true);
        var dbBotB = new Bot { FamilyId = familyB.Id, TelegramBotId = 1002, Username = "test_health_bot_b", Role = "health", Status = BotStatus.Active, LastUpdateId = 0, CreatedAt = DateTimeOffset.UtcNow };
        Db.Bots.Add(dbBotB);
        await Db.SaveChangesAsync();
        await store.EnsureBotStateAsync(new BotIdentity(dbBotB.TelegramBotId, dbBotB.Username), CancellationToken.None);
        return new ReceivingBot(dbBotB.Id, dbBotB.TelegramBotId, dbBotB.Username, familyB.Id, dbBotB.Role);
    }

    [Fact]
    public async Task Dangerous_reading_gets_the_fixed_alert_and_one_alert_row()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(LowGlucoseJson);

        await SendAsync(handler, bot, telegram, OwnerId, "сахар 2.5");

        var stored = await Db.Messages.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        var saved = await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        saved.Flags.ShouldBeEmpty();
        var sent = telegram.SentMessages.ShouldHaveSingleItem();
        sent.Text.ShouldBe(UrgentLow25);
        sent.ChatId.ShouldBe(OwnerId);
        sent.ReplyToMessageId.ShouldBe(stored.TelegramMessageId);
        telegram.Reactions.ShouldBe(new[] { (OwnerId, stored.TelegramMessageId, (string?)"✍") });
        var row = (await AlertRowsAsync()).ShouldHaveSingleItem();
        row.FamilyId.ShouldBe(bot.FamilyId!.Value);
        row.EventId.ShouldBe(saved.Id);
        row.RuleKey.ShouldBe("glucose.any");
        row.Level.ShouldBe("urgent");
        row.Threshold.ShouldBe(3.00m);
        row.ThresholdSource.ShouldBe("guideline_default");
        row.ChatId.ShouldBe(OwnerId);
        row.TopicId.ShouldBeNull();
        row.CreatedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Redelivered_dangerous_reading_alerts_once()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        // Only one answer is scripted: a second model call would throw and send the failure notice.
        _chat.EnqueueResponse(LowGlucoseJson);
        var message = PrivateText(OwnerId, "сахар 2.5");

        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId, message));
        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId, message));
        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId + 1, message));

        (await Db.Events.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        (await Db.LlmCalls.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        (await AlertRowsAsync()).ShouldHaveSingleItem();
        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe(UrgentLow25);
        telegram.Reactions.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Doctor_threshold_gives_the_doctor_label_in_the_alert()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        await SendAsync(handler, bot, telegram, OwnerId, "/threshold glucose.any low_alert 4.0");
        telegram.ClearSent();
        _chat.EnqueueResponse(DoctorLowJson);

        await SendAsync(handler, bot, telegram, OwnerId, "сахар 3.95");

        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe(DoctorLow395);
        var row = (await AlertRowsAsync()).ShouldHaveSingleItem();
        row.Level.ShouldBe("alert");
        row.Threshold.ShouldBe(4.00m);
        row.ThresholdSource.ShouldBe("doctor");
    }

    [Fact]
    public async Task Pressure_then_a_symptom_in_two_messages_gives_the_combination_alert()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(PressureJson);
        _chat.EnqueueResponse(HeadacheJson);

        await SendAsync(handler, bot, telegram, OwnerId, "давление 150/95");
        await SendAsync(handler, bot, telegram, OwnerId, "болит голова");

        telegram.SentMessages.Select(m => m.Text).ShouldBe(new[] { SystolicAlert150, Combo15095 });
        var pressure = await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Type == "blood_pressure");
        var symptom = await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Type == "symptom");
        (await AlertRowsAsync()).Select(a => (a.EventId, a.RuleKey, a.Level, a.Threshold)).ShouldBe(new[]
        {
            (pressure.Id, "blood_pressure.systolic", "alert", (decimal?)140.00m),
            (symptom.Id, "combo.bp_symptoms", "urgent", (decimal?)null)
        });
    }

    [Fact]
    public async Task A_deleted_reading_is_not_used_by_the_combination()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(PressureJson);
        await SendAsync(handler, bot, telegram, OwnerId, "давление 150/95");
        await SendAsync(handler, bot, telegram, OwnerId, "/undo");
        telegram.ClearSent();
        _chat.EnqueueResponse(HeadacheJson);

        await SendAsync(handler, bot, telegram, OwnerId, "болит голова");

        telegram.SentMessages.ShouldBeEmpty();
        (await AlertRowsAsync()).ShouldHaveSingleItem().RuleKey.ShouldBe("blood_pressure.systolic");
    }

    [Fact]
    public async Task The_combination_never_uses_another_familys_readings()
    {
        var (handler, botA, telegram, store) = await SetupAsync();
        var botB = await AddFamilyBAsync(store);
        _chat.EnqueueResponse(PressureJson);
        _chat.EnqueueResponse(HeadacheJson);

        await SendAsync(handler, botB, telegram, OtherOwnerId, "давление 150/95");
        var alertB = telegram.SentMessages.ShouldHaveSingleItem();
        alertB.ChatId.ShouldBe(OtherOwnerId);
        alertB.Text.ShouldBe(SystolicAlert150);
        telegram.ClearSent();

        await SendAsync(handler, botA, telegram, OwnerId, "болит голова");

        telegram.SentMessages.ShouldBeEmpty();
        (await AlertRowsAsync()).ShouldHaveSingleItem().FamilyId.ShouldBe(botB.FamilyId!.Value);
        (await Db.Events.IgnoreQueryFilters().CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task A_removed_rule_never_fires()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        await SendAsync(handler, bot, telegram, OwnerId, "/week");
        await Db.SafetyRules.IgnoreQueryFilters().Where(r => r.RuleKey == "glucose.any").ExecuteDeleteAsync();
        telegram.ClearSent();
        _chat.EnqueueResponse(LowGlucoseJson);

        await SendAsync(handler, bot, telegram, OwnerId, "сахар 2.5");

        (await Db.Events.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        telegram.Reactions.ShouldHaveSingleItem().Emoji.ShouldBe("✍");
        telegram.SentMessages.ShouldBeEmpty();
        (await AlertRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task With_the_model_off_a_dangerous_glucose_value_still_gets_the_alert()
    {
        var (handler, bot, telegram, _) = await SetupAsync(new NullLlmGateway());

        await SendAsync(handler, bot, telegram, OwnerId, "сахар 2.5");

        var stored = await Db.Messages.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        var alert = telegram.SentMessages.ShouldHaveSingleItem();
        alert.Text.ShouldBe(UrgentLow25 + "\nНичего не записано — повторите сообщение позже.");
        alert.ChatId.ShouldBe(OwnerId);
        alert.ReplyToMessageId.ShouldBe(stored.TelegramMessageId);
        (await Db.Events.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        (await AlertRowsAsync()).ShouldBeEmpty();
        (await Db.LlmCalls.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        telegram.Reactions.ShouldBeEmpty();

        // The alert did not use the failure notice's slot: the next failure still gets the notice.
        await SendAsync(handler, bot, telegram, OwnerId, "давление 120/80");

        telegram.SentMessages.Count.ShouldBe(2);
        telegram.SentMessages[1].Text.ShouldBe(ExtractionReplies.FailureNotice);
    }

    // --- Edited messages ---

    private Task<List<HealthEvent>> EventsByIdAsync() =>
        Db.Events.IgnoreQueryFilters().AsNoTracking().OrderBy(e => e.Id).ToListAsync();

    private static decimal GlucoseValue(HealthEvent row)
    {
        using var payload = JsonDocument.Parse(row.Payload);
        return payload.RootElement.GetProperty("value").GetDecimal();
    }

    [Fact]
    public async Task Edited_reading_replaces_the_record_and_alerts_once()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(GlucoseAt930Json);
        _chat.EnqueueResponse(LowGlucoseAt930Json);
        _chat.EnqueueResponse(LowGlucoseAt930Json);
        var original = PrivateText(OwnerId, "сахар 7.8 в 9:30");
        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId++, original));
        var edit =PrivateEdit(OwnerId, original.MessageId, "сахар 2.5 в 9:30");
        var editUpdate = new IncomingUpdate(_nextUpdateId++, edit);

        await HandleUpdateAsync(handler, bot, telegram, editUpdate);

        var message = await Db.Messages.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        message.Text.ShouldBe("сахар 2.5 в 9:30");
        var events = await EventsByIdAsync();
        events.Count.ShouldBe(2);
        GlucoseValue(events[0]).ShouldBe(7.8m);
        events[0].DeletedAt.ShouldBe(Now);
        events[0].DeleteReason.ShouldBe("edit");
        GlucoseValue(events[1]).ShouldBe(2.5m);
        events[1].DeletedAt.ShouldBeNull();
        events[1].SourceMessageId.ShouldBe(message.Id);
        events[1].OccurredAt.ShouldBe(DateTimeOffset.Parse("2030-02-07T09:30:00Z"));
        (await Db.LlmCalls.IgnoreQueryFilters().CountAsync()).ShouldBe(2);
        var sent = telegram.SentMessages.ShouldHaveSingleItem();
        sent.Text.ShouldBe(UrgentLow25);
        sent.ReplyToMessageId.ShouldBe(original.MessageId);
        sent.ChatId.ShouldBe(OwnerId);
        telegram.Reactions.ShouldHaveSingleItem();
        var alert = (await AlertRowsAsync()).ShouldHaveSingleItem();
        alert.EventId.ShouldBe(events[1].Id);
        alert.RuleKey.ShouldBe("glucose.any");

        // The same update id again is skipped; the same edit under a new update id is read again
        // but changes nothing (the record is unchanged, so no new alert).
        await HandleUpdateAsync(handler, bot, telegram, editUpdate);
        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId++, edit));

        (await Db.LlmCalls.IgnoreQueryFilters().CountAsync()).ShouldBe(3);
        var after = await EventsByIdAsync();
        after.Select(e => (e.Id, e.DeletedAt)).ShouldBe(events.Select(e => (e.Id, e.DeletedAt)));
        (await AlertRowsAsync()).ShouldHaveSingleItem();
        telegram.SentMessages.ShouldHaveSingleItem();
        telegram.Reactions.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Edit_with_the_same_reading_keeps_the_record_and_does_not_alert_again()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(LowGlucoseJson);
        _chat.EnqueueResponse(LowGlucoseJson);
        var original = PrivateText(OwnerId, "сахар 2.5");
        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId++, original));
        var firstId = (await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Id;

        await HandleUpdateAsync(handler, bot, telegram,
            new IncomingUpdate(_nextUpdateId++, PrivateEdit(OwnerId, original.MessageId, "Сахар 2.5")));

        var row = await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        row.Id.ShouldBe(firstId);
        row.DeletedAt.ShouldBeNull();
        (await Db.LlmCalls.IgnoreQueryFilters().CountAsync()).ShouldBe(2);
        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe(UrgentLow25);
        (await AlertRowsAsync()).ShouldHaveSingleItem();
        telegram.Reactions.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Edit_adding_a_reading_records_and_alerts_only_the_new_one()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(LowGlucoseJson);
        _chat.EnqueueResponse(LowGlucoseAndPressureJson);
        var original = PrivateText(OwnerId, "сахар 2.5");
        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId++, original));
        var glucoseId = (await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Id;

        await HandleUpdateAsync(handler, bot, telegram,
            new IncomingUpdate(_nextUpdateId++, PrivateEdit(OwnerId, original.MessageId, "сахар 2.5, давление 150/95")));

        var events = await EventsByIdAsync();
        events.Count.ShouldBe(2);
        events.ShouldAllBe(e => e.DeletedAt == null);
        events[0].Id.ShouldBe(glucoseId);
        var pressureId = events[1].Id;
        telegram.SentMessages.Select(m => m.Text).ShouldBe(new[] { UrgentLow25, SystolicAlert150 });
        (await AlertRowsAsync()).Select(a => (a.EventId, a.RuleKey)).ShouldBe(new[]
        {
            (glucoseId, "glucose.any"), (pressureId, "blood_pressure.systolic")
        });
        telegram.Reactions.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Edit_without_readings_deletes_the_record_and_clears_the_reaction()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(GlucoseAt930Json);
        _chat.EnqueueResponse(NoEventsJson);
        var original = PrivateText(OwnerId, "сахар 7.8 в 9:30");
        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId++, original));

        await HandleUpdateAsync(handler, bot, telegram,
            new IncomingUpdate(_nextUpdateId++, PrivateEdit(OwnerId, original.MessageId, "просто разговор")));

        var row = await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        row.DeleteReason.ShouldBe("edit");
        telegram.Reactions.ShouldBe(new[]
        {
            (OwnerId, original.MessageId, (string?)"✍"), (OwnerId, original.MessageId, (string?)null)
        });
        telegram.SentMessages.ShouldBeEmpty();

        await SendAsync(handler, bot, telegram, OwnerId, "/today");

        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe("Сегодня записей нет.");
    }

    [Fact]
    public async Task Edit_of_an_undone_message_records_its_readings_again()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(WeightJson);
        _chat.EnqueueResponse(WeightJson);
        var original = PrivateText(OwnerId, "вес 64.5");
        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId++, original));
        await SendAsync(handler, bot, telegram, OwnerId, "/undo");
        (await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync()).DeleteReason.ShouldBe("undo");

        await HandleUpdateAsync(handler, bot, telegram,
            new IncomingUpdate(_nextUpdateId++, PrivateEdit(OwnerId, original.MessageId, "вес 64.5 кг")));

        var rows = await Db.Events.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(2);
        rows.Count(r => r.DeletedAt is null).ShouldBe(1);
        rows.Single(r => r.DeletedAt is not null).DeleteReason.ShouldBe("undo");
        telegram.Reactions.Last().ShouldBe((OwnerId, original.MessageId, (string?)"✍"));
    }

    [Fact]
    public async Task Edit_while_the_model_fails_keeps_the_record()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(GlucoseAt930Json);
        _chat.EnqueueException(new InvalidOperationException("simulated provider failure"));
        var original = PrivateText(OwnerId, "сахар 7.8 в 9:30");
        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId++, original));

        await HandleUpdateAsync(handler, bot, telegram,
            new IncomingUpdate(_nextUpdateId++, PrivateEdit(OwnerId, original.MessageId, "сахар 2.5 в 9:30")));

        var row = await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        row.DeletedAt.ShouldBeNull();
        GlucoseValue(row).ShouldBe(7.8m);
        var sent = telegram.SentMessages.ShouldHaveSingleItem();
        sent.Text.ShouldBe(UrgentLow25 + NotRecorded);
        sent.ReplyToMessageId.ShouldBe(original.MessageId);
        (await AlertRowsAsync()).ShouldBeEmpty();
        telegram.Reactions.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Edit_of_a_message_the_bot_never_saw_is_recorded()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(GlucoseAt930Json);

        await HandleUpdateAsync(handler, bot, telegram,
            new IncomingUpdate(_nextUpdateId++, PrivateEdit(OwnerId, 500, "сахар 7.8 в 9:30")));

        var message = await Db.Messages.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        message.TelegramMessageId.ShouldBe(500);
        message.EditedAt.ShouldNotBeNull();
        var row = await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        row.DeletedAt.ShouldBeNull();
        row.SourceMessageId.ShouldBe(message.Id);
        telegram.Reactions.ShouldBe(new[] { (OwnerId, 500, (string?)"✍") });
        telegram.SentMessages.ShouldBeEmpty();
    }

    // --- Answers to addressed questions ---

    private const string QuestionJson = "{\"events\":[],\"unclear\":[],\"is_question\":true}";
    private const string Footer = "\n\nНе заменяю врача.";
    private const string DoseRefusal = "Я не даю советов по дозам лекарств. Это вопрос к врачу — запишите его, чтобы спросить на приёме.";

    private Task<List<StoredMessage>> OutgoingRowsAsync() =>
        Db.Messages.IgnoreQueryFilters().AsNoTracking().Where(m => m.Direction == MessageDirection.Out).ToListAsync();

    [Fact]
    public async Task Addressed_question_gets_a_smart_answer_stored_as_context()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        await SendAsync(handler, bot, telegram, OwnerId, "/setstart 15.01.2030");
        _chat.EnqueueResponse(GlucoseAt930Json);
        await SendAsync(handler, bot, telegram, OwnerId, "сахар 7.8 в 9:30");
        telegram.ClearSent();
        _chat.EnqueueResponse(QuestionJson);
        _chat.EnqueueResponse("Тестовый ответ.");

        await SendAsync(handler, bot, telegram, OwnerId, "какой сахар считается нормой?");

        var sent = telegram.SentMessages.ShouldHaveSingleItem();
        sent.Text.ShouldBe("Тестовый ответ." + Footer);
        sent.ChatId.ShouldBe(OwnerId);
        sent.ReplyToMessageId.ShouldBeNull();
        var question = await Db.Messages.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.Direction == MessageDirection.In && m.Text == "какой сахар считается нормой?");
        var calls = await Db.LlmCalls.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TriggerMessageId == question.Id).OrderBy(c => c.Id).ToListAsync();
        calls.Select(c => c.Tier).ShouldBe(new[] { "fast", "smart" });
        var outgoing = (await OutgoingRowsAsync()).ShouldHaveSingleItem();
        outgoing.ChatId.ShouldBe(OwnerId);
        outgoing.Text.ShouldBe("Тестовый ответ." + Footer);
        var answerCall = _chat.RequestedMessages[^1];
        answerCall[0].Role.ShouldBe(ChatRole.System);
        answerCall[0].Text.ShouldContain("- Stage week: 3 нед. 2 дн.");
        answerCall[0].Text.ShouldContain("07.02 09:30 глюкоза 7.8 ммоль/л (через 1 ч после еды)");
        answerCall[0].Text.ShouldContain("не подтверждено врачом");
        answerCall[^1].Role.ShouldBe(ChatRole.User);
        answerCall[^1].Text.ShouldBe("какой сахар считается нормой?");
    }

    [Fact]
    public async Task Group_question_without_a_mention_gets_no_answer()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        Db.Places.Add(new Assistant.Domain.Places.Place
        {
            BotId = bot.BotDbId, ChatId = -100, TopicId = null, Title = "test group", Status = Assistant.Domain.Places.PlaceStatus.Approved,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await Db.SaveChangesAsync();
        _chat.EnqueueResponse(QuestionJson);

        await HandleUpdateAsync(handler, bot, telegram, new IncomingUpdate(_nextUpdateId++, GroupText(OwnerId, "какой сахар считается нормой?")));

        telegram.SentMessages.ShouldBeEmpty();
        (await Db.LlmCalls.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Tier.ShouldBe("fast");
        (await OutgoingRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dose_answer_is_replaced_and_only_the_refusal_is_stored()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        _chat.EnqueueResponse(QuestionJson);
        _chat.EnqueueResponse("Увеличьте дозу на 2 единицы.");

        await SendAsync(handler, bot, telegram, OwnerId, "на сколько увеличить дозу?");

        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe(DoseRefusal + Footer);
        (await OutgoingRowsAsync()).ShouldHaveSingleItem().Text.ShouldBe(DoseRefusal + Footer);
        (await Db.Messages.IgnoreQueryFilters().AsNoTracking().CountAsync(m => m.Text != null && m.Text.Contains("Увеличьте"))).ShouldBe(0);
    }

    [Fact]
    public async Task Answer_context_never_includes_another_familys_readings()
    {
        var (handler, botA, telegram, store) = await SetupAsync();
        var botB = await AddFamilyBAsync(store);
        _chat.EnqueueResponse(GlucoseAt930Json);
        await SendAsync(handler, botB, telegram, OtherOwnerId, "сахар 7.8 в 9:30");
        telegram.ClearSent();
        _chat.EnqueueResponse(QuestionJson);
        _chat.EnqueueResponse("Тестовый ответ.");

        await SendAsync(handler, botA, telegram, OwnerId, "какой сахар считается нормой?");

        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe("Тестовый ответ." + Footer);
        var answerCall = _chat.RequestedMessages[^1];
        answerCall[0].Text.ShouldContain("- Readings of the last 24 hours (local time, oldest first):\n  - none\n");
        answerCall[0].Text.ShouldNotContain("глюкоза 7.8");
        answerCall.ShouldAllBe(m => m.Text != "сахар 7.8 в 9:30");
    }

    [Fact]
    public async Task Reading_and_question_in_one_message_is_recorded_and_the_answer_context_holds_the_reading()
    {
        var (handler, bot, telegram, _) = await SetupAsync();
        await SendAsync(handler, bot, telegram, OwnerId, "/setstart 15.01.2030");
        telegram.ClearSent();
        _chat.EnqueueResponse(
            "{\"events\":[{\"type\":\"glucose\",\"day\":0,\"time\":\"09:30\",\"value\":7.8,\"unit\":\"mmol/L\",\"context\":\"after_meal_1h\"}]," +
            "\"unclear\":[],\"is_question\":true}");
        _chat.EnqueueResponse("Тестовый ответ.");

        await SendAsync(handler, bot, telegram, OwnerId, "сахар 7.8 в 9:30, это высокий?");

        (await Db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync()).DeletedAt.ShouldBeNull();
        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldBe("Тестовый ответ." + Footer);
        var answerCall = _chat.RequestedMessages[^1];
        answerCall[0].Text.ShouldContain("07.02 09:30 глюкоза 7.8 ммоль/л (через 1 ч после еды)");
    }
}
