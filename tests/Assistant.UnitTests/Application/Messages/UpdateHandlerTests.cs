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

        public Task<long> GetOrCreatePendingPlaceAsync(long botDbId, long chatId, int? topicId, string title, CancellationToken cancellationToken) =>
            Task.FromResult(NextPlaceId);

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

    private static IncomingMessage Message(long userId = 111, string chatType = "private", string? text = "test 1") =>
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
    public async Task Role_bot_message_from_a_pending_place_is_ignored()
    {
        var (handler, store, telegram, approvals, _) = CreateHandler();
        approvals.NextPlaceStatus = PlaceStatus.Pending;

        await handler.HandleAsync(RoleBot, telegram, new IncomingUpdate(3, Message(userId: 111)), CancellationToken.None);

        store.Calls.Single().Message.ShouldBeNull();
        telegram.Sent.ShouldBeEmpty();
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
    public async Task Failed_reply_send_is_swallowed_and_does_not_throw()
    {
        var (handler, _, telegram, _, _) = CreateHandler();
        telegram.ThrowOnSend = true;

        await Should.NotThrowAsync(() =>
            handler.HandleAsync(RoleBot, telegram, new IncomingUpdate(7, Message(userId: 111)), CancellationToken.None));
    }
}
