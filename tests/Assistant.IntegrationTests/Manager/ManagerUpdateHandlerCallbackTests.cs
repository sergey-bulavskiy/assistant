using Assistant.Application.Common;
using Assistant.Application.Families;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.IntegrationTests.Manager;

public class ManagerUpdateHandlerCallbackTests : IntegrationTestBase
{
    private sealed class FixedClaimCode : Assistant.Application.Manager.IClaimCodeProvider
    {
        public string Code => "424242";
    }

    private sealed class SingleClientFactory : ITelegramClientFactory
    {
        public FakeTelegramClient Client { get; } = new();
        public ITelegramClient Create(string token) => Client;
    }

    private static readonly ReceivingBot ManagerBot = new(BotDbId: 1, TelegramBotId: 998, Username: "test_manager_bot", FamilyId: null, Role: "manager");

    private long _familyId;
    private long _botId;

    private async Task<(ManagerUpdateHandler Handler, FakeTelegramClient Telegram)> SetupAsync()
    {
        var family = new Family { Name = "test family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();
        _familyId = family.Id;
        Db.FamilyMembers.Add(new FamilyMember { FamilyId = family.Id, TelegramUserId = 111, DisplayName = "owner", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        var bot = new Bot { FamilyId = family.Id, TelegramBotId = 1001, Username = "test_role_bot", Role = "general", Status = BotStatus.Active, LastUpdateId = 0, CreatedAt = DateTimeOffset.UtcNow };
        Db.Bots.Add(bot);
        await Db.SaveChangesAsync();
        _botId = bot.Id;

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
            new UsageCommandHandler(Db, new Assistant.Infrastructure.Llm.NullBudgetGuard(), clock), NullLogger<ManagerUpdateHandler>.Instance);

        return (handler, clients.Client);
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

    private static IncomingUpdate CallbackUpdate(long updateId, string data) => CallbackUpdate(updateId, data, fromUserId: 111);

    private static IncomingUpdate CallbackUpdate(long updateId, string data, long fromUserId) =>
        new(updateId, null, CallbackQuery: new CallbackQueryInfo(CallbackQueryId: $"cbq-{updateId}", FromUserId: fromUserId, Data: data, MessageChatId: fromUserId, MessageId: 1));

    [Fact]
    public async Task Approving_a_place_marks_it_approved_and_answers_recorded()
    {
        var (handler, telegram) = await SetupAsync();
        var place = new Place { BotId = _botId, ChatId = -100, Title = "test chat", Status = PlaceStatus.Pending, CreatedAt = DateTimeOffset.UtcNow };
        Db.Places.Add(place);
        await Db.SaveChangesAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, $"place_approve:{place.Id}"), CancellationToken.None);

        (await Db.Places.FindAsync(place.Id))!.Status.ShouldBe(PlaceStatus.Approved);
        telegram.AnsweredCallbacks.ShouldContain(c => c.Text == "Записано.");
    }

    [Fact]
    public async Task Denying_a_place_marks_it_denied()
    {
        var (handler, telegram) = await SetupAsync();
        var place = new Place { BotId = _botId, ChatId = -100, Title = "test chat", Status = PlaceStatus.Pending, CreatedAt = DateTimeOffset.UtcNow };
        Db.Places.Add(place);
        await Db.SaveChangesAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, $"place_deny:{place.Id}"), CancellationToken.None);

        (await Db.Places.FindAsync(place.Id))!.Status.ShouldBe(PlaceStatus.Denied);
    }

    [Fact]
    public async Task Second_tap_after_resolution_answers_already_decided()
    {
        var (handler, telegram) = await SetupAsync();
        var place = new Place { BotId = _botId, ChatId = -100, Title = "test chat", Status = PlaceStatus.Pending, CreatedAt = DateTimeOffset.UtcNow };
        Db.Places.Add(place);
        await Db.SaveChangesAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, $"place_approve:{place.Id}"), CancellationToken.None);
        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(2, $"place_deny:{place.Id}"), CancellationToken.None);

        (await Db.Places.FindAsync(place.Id))!.Status.ShouldBe(PlaceStatus.Approved);
        telegram.AnsweredCallbacks.ShouldContain(c => c.CallbackQueryId == "cbq-2" && c.Text == "Уже решено.");
    }

    [Fact]
    public async Task Allowing_a_user_marks_the_member_approved()
    {
        var (handler, telegram) = await SetupAsync();
        var member = new FamilyMember { FamilyId = _familyId, TelegramUserId = 333, DisplayName = "test user", Status = FamilyMemberStatus.Pending, IsOwner = false, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        Db.FamilyMembers.Add(member);
        await Db.SaveChangesAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, $"member_allow:{member.Id}"), CancellationToken.None);

        (await Db.FamilyMembers.FindAsync(member.Id))!.Status.ShouldBe(FamilyMemberStatus.Approved);
    }

    [Fact]
    public async Task Tap_from_a_non_owner_does_not_approve_a_place()
    {
        var (handler, telegram) = await SetupAsync();
        var place = new Place { BotId = _botId, ChatId = -100, Title = "test chat", Status = PlaceStatus.Pending, CreatedAt = DateTimeOffset.UtcNow };
        Db.Places.Add(place);
        await Db.SaveChangesAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, $"place_approve:{place.Id}", fromUserId: 999), CancellationToken.None);

        (await Db.Places.FindAsync(place.Id))!.Status.ShouldBe(PlaceStatus.Pending);
        telegram.AnsweredCallbacks.ShouldContain(c => c.CallbackQueryId == "cbq-1" && c.Text != "Записано.");
    }

    [Fact]
    public async Task Tap_from_a_non_owner_does_not_approve_a_member()
    {
        var (handler, telegram) = await SetupAsync();
        var member = new FamilyMember { FamilyId = _familyId, TelegramUserId = 333, DisplayName = "test user", Status = FamilyMemberStatus.Pending, IsOwner = false, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        Db.FamilyMembers.Add(member);
        await Db.SaveChangesAsync();

        // The non-owner tap comes from the pending member's own account — a realistic attacker who
        // has messaged the manager bot but holds no owner membership in any family.
        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, $"member_allow:{member.Id}", fromUserId: member.TelegramUserId), CancellationToken.None);

        (await Db.FamilyMembers.FindAsync(member.Id))!.Status.ShouldBe(FamilyMemberStatus.Pending);
        telegram.AnsweredCallbacks.ShouldContain(c => c.CallbackQueryId == "cbq-1" && c.Text != "Записано.");
    }

    [Fact]
    public async Task Tap_from_an_owner_of_a_different_family_does_not_approve_a_place()
    {
        var (handler, telegram) = await SetupAsync();
        var place = new Place { BotId = _botId, ChatId = -100, Title = "test chat", Status = PlaceStatus.Pending, CreatedAt = DateTimeOffset.UtcNow };
        Db.Places.Add(place);

        // A second, unrelated family whose caller is a genuine approved owner — just not of the
        // family that owns the place being tapped on. Proves the authorization check is
        // family-scoped, not "is an owner of any family".
        var otherFamily = new Family { Name = "other family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(otherFamily);
        await Db.SaveChangesAsync();
        Db.FamilyMembers.Add(new FamilyMember { FamilyId = otherFamily.Id, TelegramUserId = 777, DisplayName = "other owner", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();

        await handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, $"place_approve:{place.Id}", fromUserId: 777), CancellationToken.None);

        (await Db.Places.FindAsync(place.Id))!.Status.ShouldBe(PlaceStatus.Pending);
        telegram.AnsweredCallbacks.ShouldContain(c => c.CallbackQueryId == "cbq-1" && c.Text != "Записано.");
    }

    [Fact]
    public async Task Unknown_callback_data_answers_not_implemented_without_throwing()
    {
        var (handler, telegram) = await SetupAsync();

        await Should.NotThrowAsync(() =>
            handler.HandleAsync(ManagerBot, telegram, CallbackUpdate(1, "not_a_real_action:123"), CancellationToken.None));

        telegram.AnsweredCallbacks.ShouldContain(c => c.Text == "Пока не реализовано");
    }
}
