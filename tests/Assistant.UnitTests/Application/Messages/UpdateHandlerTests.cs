using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Health;
using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.UnitTests.Application.Messages;

public class UpdateHandlerTests
{
    private sealed class FakeApprovalService : IApprovalService
    {
        public PlaceStatus NextPlaceStatus { get; set; } = PlaceStatus.Approved;
        public FamilyMemberStatus NextMemberStatus { get; set; } = FamilyMemberStatus.Approved;
        public long NextPlaceId { get; set; } = 1;
        public long NextMemberId { get; set; } = 1;
        public int PlaceApprovalCalls { get; private set; }
        public string? LastPlaceTitle { get; private set; }
        public bool NextReplyToAll { get; set; }
        public int ReplyToAllCalls { get; private set; }

        public Task<bool> GetPlaceReplyToAllAsync(long placeId, CancellationToken cancellationToken)
        {
            ReplyToAllCalls++;
            return Task.FromResult(NextReplyToAll);
        }

        public Task<long> GetOrCreatePendingPlaceAsync(long botDbId, long chatId, int? topicId, string title, CancellationToken cancellationToken)
        {
            PlaceApprovalCalls++;
            LastPlaceTitle = title;
            return Task.FromResult(NextPlaceId);
        }

        public Task<ApprovalResolution> ResolvePlaceApprovalAsync(long placeId, bool approve, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalResolution.Applied);

        public Task<PlaceStatus> GetPlaceStatusAsync(long placeId, CancellationToken cancellationToken) => Task.FromResult(NextPlaceStatus);

        public Task<long> GetOrCreatePendingFamilyMemberAsync(
            long familyId, long telegramUserId, string displayName, string? username, string requestingBotUsername, CancellationToken cancellationToken) =>
            Task.FromResult(NextMemberId);

        public Task<ApprovalResolution> ResolveUserApprovalAsync(long familyMemberId, bool approve, CancellationToken cancellationToken) =>
            Task.FromResult(ApprovalResolution.Applied);

        public Task<FamilyMemberStatus> GetFamilyMemberStatusAsync(long familyMemberId, CancellationToken cancellationToken) =>
            Task.FromResult(NextMemberStatus);

        public FamilyMemberStatus? FoundMemberStatus { get; set; } = FamilyMemberStatus.Approved;

        public PlaceStatus? FoundPlaceStatus { get; set; } = PlaceStatus.Approved;

        public List<(long FamilyId, long UserId)> MemberLookups { get; } = new();

        public List<(long BotDbId, long ChatId, int? TopicId)> PlaceLookups { get; } = new();

        public Task<FamilyMemberStatus?> FindFamilyMemberStatusAsync(long familyId, long telegramUserId, CancellationToken cancellationToken)
        {
            MemberLookups.Add((familyId, telegramUserId));
            return Task.FromResult(FoundMemberStatus);
        }

        public Task<PlaceStatus?> FindPlaceStatusAsync(long botDbId, long chatId, int? topicId, CancellationToken cancellationToken)
        {
            PlaceLookups.Add((botDbId, chatId, topicId));
            return Task.FromResult(FoundPlaceStatus);
        }
    }

    private sealed class FakeCurrentFamily : ICurrentFamily
    {
        public long? FamilyId { get; private set; }

        public void Set(long? familyId) => FamilyId = familyId;
    }

    private sealed class FakeManagerUpdateHandler : IManagerUpdateHandler
    {
        public int CallCount { get; private set; }

        public Task HandleAsync(ReceivingBot managerBot, ITelegramClient telegramClient, IncomingUpdate update, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGeneralAssistant : IGeneralAssistant
    {
        public List<(ReceivingBot Bot, IncomingMessage Message, StoreResult Result, bool ReplyToAll)> Calls { get; } = new();

        public Task HandleAsync(
            ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken, bool replyToAll = false)
        {
            Calls.Add((bot, message, storeResult, replyToAll));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHealthAssistant : IHealthAssistant
    {
        public List<(ReceivingBot Bot, IncomingMessage Message, StoreResult Result)> Calls { get; } = new();

        public Task HandleAsync(ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken)
        {
            Calls.Add((bot, message, storeResult));
            return Task.CompletedTask;
        }

        public List<CallbackQueryInfo> Callbacks { get; } = new();

        public Task HandleCallbackAsync(ReceivingBot bot, ITelegramClient telegramClient, CallbackQueryInfo callback, CancellationToken cancellationToken)
        {
            Callbacks.Add(callback);
            return Task.CompletedTask;
        }
    }

    // A non-general role bot: keeps the pre-M3a store-and-acknowledge behaviour.
    private static readonly ReceivingBot RoleBot = new(BotDbId: 1, TelegramBotId: 999, Username: "test_bot", FamilyId: 42, Role: "test");
    private static readonly ReceivingBot GeneralBot = RoleBot with { Role = "general" };
    private static readonly ReceivingBot ManagerBot = new(BotDbId: 2, TelegramBotId: 998, Username: "test_manager_bot", FamilyId: null, Role: "manager");

    private static (UpdateHandler Handler, FakeMessageStore Store, FakeTelegramClient Telegram, FakeApprovalService Approvals, FakeManagerUpdateHandler Manager) CreateHandler() =>
        CreateHandler(new FakeGeneralAssistant());

    private static (UpdateHandler Handler, FakeMessageStore Store, FakeTelegramClient Telegram, FakeApprovalService Approvals, FakeManagerUpdateHandler Manager) CreateHandler(
        IGeneralAssistant generalAssistant, FakeMessageStore? messageStore = null, IHealthAssistant? healthAssistant = null)
    {
        var store = messageStore ?? new FakeMessageStore();
        var telegram = new FakeTelegramClient();
        var approvals = new FakeApprovalService();
        var currentFamily = new FakeCurrentFamily();
        var manager = new FakeManagerUpdateHandler();
        var options = Options.Create(new BotOptions { ManagerToken = "test-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var buildInfo = new BuildInfo("abcdef1", null, DateTimeOffset.UtcNow);
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var handler = new UpdateHandler(store, approvals, currentFamily, manager, generalAssistant, healthAssistant ?? new FakeHealthAssistant(), options, buildInfo, clock, NullLogger<UpdateHandler>.Instance);
        return (handler, store, telegram, approvals, manager);
    }

    private static IncomingMessage Message(long userId = 111, string chatType = "private", string? text = "test 1", string? chatTitle = null) =>
        new(
            ChatId: 111,
            ChatType: chatType,
            ChatTitle: chatTitle,
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
            RawJson: "{}", ReplyToMessageId: null, ReplyToUserId: null);

    [Fact]
    public async Task Manager_bot_updates_are_delegated_to_the_manager_handler_and_never_stored_as_a_message()
    {
        var (handler, store, telegram, _, manager) = CreateHandler();

        await handler.HandleAsync(ManagerBot, telegram, new IncomingUpdate(1, Message()), CancellationToken.None);

        manager.CallCount.ShouldBe(1);
        store.Calls.Single().Message.ShouldBeNull();
    }

    [Fact]
    public async Task Role_bot_message_from_an_approved_place_and_user_is_stored_and_acknowledged()
    {
        var (handler, store, telegram, _, _) = CreateHandler();

        await handler.HandleAsync(RoleBot, telegram, new IncomingUpdate(2, Message(userId: 111)), CancellationToken.None);

        store.Calls.Single().Message.ShouldNotBeNull();
        telegram.Sent.Single().Text.ShouldStartWith("Получил");
    }

    [Fact]
    public async Task Group_message_from_a_pending_place_is_ignored()
    {
        var (handler, store, telegram, approvals, _) = CreateHandler();
        approvals.NextPlaceStatus = PlaceStatus.Pending;

        await handler.HandleAsync(RoleBot, telegram, new IncomingUpdate(3, Message(userId: 111, chatType: "group")), CancellationToken.None);

        store.Calls.Single().Message.ShouldBeNull();
        telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Private_message_skips_the_place_approval_gate_entirely()
    {
        // Bug fix: a private DM to a role bot has no "place" to approve — approving the user
        // (below) already covers all of that family's bots. Before the fix, HandleAsync ran the
        // place-approval gate unconditionally, so a pending place status would wrongly block a
        // brand-new user's first private DM even though the user gate would have approved it.
        var (handler, store, telegram, approvals, _) = CreateHandler();
        approvals.NextPlaceStatus = PlaceStatus.Pending;

        await handler.HandleAsync(RoleBot, telegram, new IncomingUpdate(8, Message(userId: 111, chatType: "private")), CancellationToken.None);

        approvals.PlaceApprovalCalls.ShouldBe(0);
        store.Calls.Single().Message.ShouldNotBeNull();
    }

    [Fact]
    public async Task Group_message_place_approval_uses_the_real_chat_title_when_available()
    {
        // Bug fix: the place-approval title must come from Telegram's real chat title, not a
        // synthesized "chat {id}" placeholder, so owners see the actual group name in approval
        // prompts and /settings.
        var (handler, _, telegram, approvals, _) = CreateHandler();

        await handler.HandleAsync(RoleBot, telegram, new IncomingUpdate(9, Message(userId: 111, chatType: "group", chatTitle: "test group")), CancellationToken.None);

        approvals.LastPlaceTitle.ShouldBe("test group");
    }

    [Fact]
    public async Task Group_message_place_approval_falls_back_to_a_synthetic_title_when_telegram_sent_none()
    {
        var (handler, _, telegram, approvals, _) = CreateHandler();

        await handler.HandleAsync(RoleBot, telegram, new IncomingUpdate(10, Message(userId: 111, chatType: "group", chatTitle: null)), CancellationToken.None);

        approvals.LastPlaceTitle.ShouldBe("chat 111");
    }

    [Fact]
    public async Task Role_bot_message_from_a_pending_user_is_ignored_even_when_the_place_is_approved()
    {
        var (handler, store, telegram, approvals, _) = CreateHandler();
        approvals.NextMemberStatus = FamilyMemberStatus.Pending;

        await handler.HandleAsync(RoleBot, telegram, new IncomingUpdate(4, Message(userId: 111)), CancellationToken.None);

        store.Calls.Single().Message.ShouldBeNull();
        telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Bot_added_to_a_chat_requests_a_place_approval_and_stores_nothing()
    {
        var (handler, store, telegram, _, _) = CreateHandler();
        var update = new IncomingUpdate(5, null, MembershipChange: new BotMembershipChange(-100, "test group", IsNowMember: true));

        await handler.HandleAsync(RoleBot, telegram, update, CancellationToken.None);

        store.Calls.Single().Message.ShouldBeNull();
        telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Null_message_update_is_stored_as_offset_only()
    {
        var (handler, store, telegram, _, _) = CreateHandler();

        await handler.HandleAsync(RoleBot, telegram, new IncomingUpdate(6, null), CancellationToken.None);

        store.Calls.Single().Message.ShouldBeNull();
        telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failed_reply_send_keeps_the_stored_message_and_does_not_fail_the_update()
    {
        var (handler, store, telegram, _, _) = CreateHandler();
        telegram.ThrowOnSend = true;
        var message = Message(userId: 111);

        // The message is committed before the reply is sent; a send failure escaping here would
        // make the poller retry an update that is already stored.
        await Should.NotThrowAsync(() =>
            handler.HandleAsync(RoleBot, telegram, new IncomingUpdate(7, message), CancellationToken.None));

        var call = store.Calls.ShouldHaveSingleItem();
        call.UpdateId.ShouldBe(7);
        call.Message.ShouldBe(message);
    }

    [Theory]
    [InlineData("general")]
    [InlineData(" General ")]
    [InlineData("GENERAL")]
    public async Task General_role_bot_messages_are_routed_to_the_general_assistant_not_the_reply_policy(string role)
    {
        var general = new FakeGeneralAssistant();
        var (handler, store, telegram, _, _) = CreateHandler(general);
        var message = Message(userId: 111);

        await handler.HandleAsync(RoleBot with { Role = role }, telegram, new IncomingUpdate(20, message), CancellationToken.None);

        store.Calls.ShouldHaveSingleItem().Message.ShouldBe(message);
        var call = general.Calls.ShouldHaveSingleItem();
        call.Message.ShouldBe(message);
        call.Result.Outcome.ShouldBe(StoreOutcome.Stored);
        telegram.Sent.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("test")]
    [InlineData("generalist")]
    [InlineData("cook")]
    [InlineData("healthcare")]
    public async Task Other_role_bots_keep_the_acknowledgement_and_never_reach_the_general_assistant(string role)
    {
        var general = new FakeGeneralAssistant();
        var (handler, _, telegram, _, _) = CreateHandler(general);

        await handler.HandleAsync(RoleBot with { Role = role }, telegram, new IncomingUpdate(21, Message(userId: 111)), CancellationToken.None);

        general.Calls.ShouldBeEmpty();
        telegram.Sent.ShouldHaveSingleItem().Text.ShouldStartWith("Получил");
    }

    [Fact]
    public async Task General_bot_is_not_reached_when_the_user_is_not_approved()
    {
        var general = new FakeGeneralAssistant();
        var (handler, _, telegram, approvals, _) = CreateHandler(general);
        approvals.NextMemberStatus = FamilyMemberStatus.Pending;

        await handler.HandleAsync(GeneralBot, telegram, new IncomingUpdate(22, Message(userId: 111)), CancellationToken.None);

        general.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task General_bot_answers_through_the_real_general_assistant()
    {
        var store = new FakeMessageStore();
        var gateway = new FakeLlmGateway();
        var assistant = new GeneralAssistant(
            store, gateway, new FakeChatSettingsStore(), new FakeLlmUsageQuery(), config: null, new FixedClock(DateTimeOffset.UtcNow),
            new BuildInfo("abcdef1", null, DateTimeOffset.UtcNow), NullLogger<GeneralAssistant>.Instance);
        var (handler, _, telegram, _, _) = CreateHandler(assistant, store);

        await handler.HandleAsync(GeneralBot, telegram, new IncomingUpdate(23, Message(userId: 111)), CancellationToken.None);

        telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("Ассистент пока не настроен.");
        gateway.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task General_bot_group_message_carries_its_places_reply_to_all_flag(bool flag)
    {
        var general = new FakeGeneralAssistant();
        var (handler, _, telegram, approvals, _) = CreateHandler(general);
        approvals.NextReplyToAll = flag;

        await handler.HandleAsync(GeneralBot, telegram, new IncomingUpdate(30, Message(userId: 111, chatType: "group")), CancellationToken.None);

        general.Calls.ShouldHaveSingleItem().ReplyToAll.ShouldBe(flag);
    }

    [Fact]
    public async Task Private_message_to_the_general_bot_never_reads_the_flag()
    {
        var general = new FakeGeneralAssistant();
        var (handler, _, telegram, approvals, _) = CreateHandler(general);
        approvals.NextReplyToAll = true;

        await handler.HandleAsync(GeneralBot, telegram, new IncomingUpdate(31, Message(userId: 111, chatType: "private")), CancellationToken.None);

        approvals.ReplyToAllCalls.ShouldBe(0);
        general.Calls.ShouldHaveSingleItem().ReplyToAll.ShouldBeFalse();
    }

    [Fact]
    public async Task Other_role_bots_never_read_the_flag()
    {
        var (handler, _, telegram, approvals, _) = CreateHandler();
        approvals.NextReplyToAll = true;

        await handler.HandleAsync(RoleBot, telegram, new IncomingUpdate(32, Message(userId: 111, chatType: "group")), CancellationToken.None);

        approvals.ReplyToAllCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("health")]
    [InlineData(" Health ")]
    [InlineData("HEALTH")]
    public async Task Health_role_bot_messages_are_routed_to_the_health_assistant(string role)
    {
        var general = new FakeGeneralAssistant();
        var health = new FakeHealthAssistant();
        var (handler, store, telegram, _, _) = CreateHandler(general, healthAssistant: health);
        var message = Message(userId: 111);

        await handler.HandleAsync(RoleBot with { Role = role }, telegram, new IncomingUpdate(40, message), CancellationToken.None);

        store.Calls.ShouldHaveSingleItem().Message.ShouldBe(message);
        var call = health.Calls.ShouldHaveSingleItem();
        call.Message.ShouldBe(message);
        call.Result.Outcome.ShouldBe(StoreOutcome.Stored);
        general.Calls.ShouldBeEmpty();
        telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Health_bot_group_message_never_reads_the_reply_to_all_flag()
    {
        var health = new FakeHealthAssistant();
        var (handler, _, telegram, approvals, _) = CreateHandler(new FakeGeneralAssistant(), healthAssistant: health);
        approvals.NextReplyToAll = true;

        await handler.HandleAsync(RoleBot with { Role = "health" }, telegram, new IncomingUpdate(41, Message(userId: 111, chatType: "group")), CancellationToken.None);

        approvals.ReplyToAllCalls.ShouldBe(0);
        health.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task General_bot_never_reaches_the_health_assistant()
    {
        var health = new FakeHealthAssistant();
        var (handler, _, telegram, _, _) = CreateHandler(new FakeGeneralAssistant(), healthAssistant: health);

        await handler.HandleAsync(GeneralBot, telegram, new IncomingUpdate(42, Message(userId: 111)), CancellationToken.None);

        health.Calls.ShouldBeEmpty();
    }

    // --- Button taps (callback queries) of role bots ---

    private static readonly ReceivingBot HealthBot = RoleBot with { Role = "health" };

    private static CallbackQueryInfo Tap(string chatType = "group", int? topicId = 7, long userId = 222) =>
        new("cbq-1", userId, "rec_yes:5", MessageChatId: chatType == "private" ? userId : -100, MessageId: 50,
            MessageTopicId: topicId, MessageChatType: chatType);

    [Fact]
    public async Task Health_bot_tap_by_an_approved_member_in_an_approved_place_reaches_the_health_assistant()
    {
        var health = new FakeHealthAssistant();
        var (handler, store, telegram, approvals, _) = CreateHandler(new FakeGeneralAssistant(), healthAssistant: health);
        var tap = Tap();

        await handler.HandleAsync(HealthBot, telegram, new IncomingUpdate(50, null, CallbackQuery: tap), CancellationToken.None);

        health.Callbacks.ShouldHaveSingleItem().ShouldBe(tap);
        approvals.MemberLookups.ShouldBe(new[] { (42L, 222L) });
        approvals.PlaceLookups.ShouldBe(new[] { (1L, -100L, (int?)7) });
        approvals.PlaceApprovalCalls.ShouldBe(0);
        telegram.AnsweredCallbacks.ShouldBeEmpty();
        var stored = store.Calls.ShouldHaveSingleItem();
        stored.UpdateId.ShouldBe(50);
        stored.Message.ShouldBeNull();
    }

    [Theory]
    [InlineData(FamilyMemberStatus.Pending)]
    [InlineData(FamilyMemberStatus.Denied)]
    [InlineData(null)]
    public async Task Health_bot_tap_by_someone_who_is_not_an_approved_member_is_refused(FamilyMemberStatus? status)
    {
        var health = new FakeHealthAssistant();
        var (handler, store, telegram, approvals, _) = CreateHandler(new FakeGeneralAssistant(), healthAssistant: health);
        approvals.FoundMemberStatus = status;

        await handler.HandleAsync(HealthBot, telegram, new IncomingUpdate(51, null, CallbackQuery: Tap()), CancellationToken.None);

        health.Callbacks.ShouldBeEmpty();
        telegram.AnsweredCallbacks.ShouldBe(new[] { ("cbq-1", (string?)"У вас нет прав.") });
        store.Calls.ShouldHaveSingleItem().Message.ShouldBeNull();
    }

    [Theory]
    [InlineData(PlaceStatus.Pending)]
    [InlineData(PlaceStatus.Denied)]
    [InlineData(PlaceStatus.Disabled)]
    [InlineData(null)]
    public async Task Health_bot_tap_in_a_place_that_is_not_approved_is_refused(PlaceStatus? status)
    {
        var health = new FakeHealthAssistant();
        var (handler, _, telegram, approvals, _) = CreateHandler(new FakeGeneralAssistant(), healthAssistant: health);
        approvals.FoundPlaceStatus = status;

        await handler.HandleAsync(HealthBot, telegram, new IncomingUpdate(52, null, CallbackQuery: Tap()), CancellationToken.None);

        health.Callbacks.ShouldBeEmpty();
        telegram.AnsweredCallbacks.ShouldBe(new[] { ("cbq-1", (string?)"У вас нет прав.") });
    }

    [Fact]
    public async Task Health_bot_tap_in_a_private_chat_needs_no_place()
    {
        var health = new FakeHealthAssistant();
        var (handler, _, telegram, approvals, _) = CreateHandler(new FakeGeneralAssistant(), healthAssistant: health);
        approvals.FoundPlaceStatus = null;

        await handler.HandleAsync(HealthBot, telegram, new IncomingUpdate(53, null, CallbackQuery: Tap("private", null, 111)), CancellationToken.None);

        health.Callbacks.ShouldHaveSingleItem();
        approvals.PlaceLookups.ShouldBeEmpty();
        approvals.MemberLookups.ShouldBe(new[] { (42L, 111L) });
    }

    [Theory]
    [InlineData("general")]
    [InlineData("test")]
    public async Task Other_role_bots_answer_a_tap_with_no_text(string role)
    {
        var health = new FakeHealthAssistant();
        var (handler, store, telegram, approvals, _) = CreateHandler(new FakeGeneralAssistant(), healthAssistant: health);

        await handler.HandleAsync(RoleBot with { Role = role }, telegram, new IncomingUpdate(54, null, CallbackQuery: Tap()), CancellationToken.None);

        health.Callbacks.ShouldBeEmpty();
        telegram.AnsweredCallbacks.ShouldBe(new[] { ("cbq-1", (string?)null) });
        approvals.MemberLookups.ShouldBeEmpty();
        store.Calls.ShouldHaveSingleItem().Message.ShouldBeNull();
    }
}
