using Assistant.Application.Common;
using Assistant.Application.Families;
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

    private static readonly ReceivingBot RoleBot = new(BotDbId: 1, TelegramBotId: 999, Username: "test_bot", FamilyId: 42, Role: "general");
    private static readonly ReceivingBot ManagerBot = new(BotDbId: 2, TelegramBotId: 998, Username: "test_manager_bot", FamilyId: null, Role: "manager");

    private static (UpdateHandler Handler, FakeMessageStore Store, FakeTelegramClient Telegram, FakeApprovalService Approvals, FakeManagerUpdateHandler Manager) CreateHandler()
    {
        var store = new FakeMessageStore();
        var telegram = new FakeTelegramClient();
        var approvals = new FakeApprovalService();
        var currentFamily = new FakeCurrentFamily();
        var manager = new FakeManagerUpdateHandler();
        var options = Options.Create(new BotOptions { ManagerToken = "test-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var buildInfo = new BuildInfo("abcdef1", null, DateTimeOffset.UtcNow);
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var handler = new UpdateHandler(store, approvals, currentFamily, manager, options, buildInfo, clock, NullLogger<UpdateHandler>.Instance);
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
}
