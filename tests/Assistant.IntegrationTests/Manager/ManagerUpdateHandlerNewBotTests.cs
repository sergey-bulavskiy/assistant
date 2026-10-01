using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Families;
using Assistant.Infrastructure.Bots;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Manager;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Telegram;
using Assistant.IntegrationTests.Host;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.IntegrationTests.Manager;

public class ManagerUpdateHandlerNewBotTests : IntegrationTestBase
{
    private sealed class FixedClaimCode : IClaimCodeProvider
    {
        public string Code => "424242";
    }

    private sealed class RecordingClientFactory : ITelegramClientFactory
    {
        public List<string> TokensRequested { get; } = new();
        public FakeTelegramClient NewBotClient { get; } = new();

        public ITelegramClient Create(string token)
        {
            TokensRequested.Add(token);
            return NewBotClient;
        }
    }

    private static readonly ReceivingBot ManagerBot = new(BotDbId: 1, TelegramBotId: 998, Username: "test_manager_bot", FamilyId: null, Role: "manager");

    private readonly TestClock _clock = new();

    private async Task<(ManagerUpdateHandler Handler, RecordingClientFactory Clients, IPendingBotCreations Pending, long FamilyId)> SetupAsync()
    {
        var family = new Family { Name = "test family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();
        Db.FamilyMembers.Add(new FamilyMember { FamilyId = family.Id, TelegramUserId = 111, DisplayName = "owner", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();

        var (handler, clients, pending, _) = CreateHandler(Db);
        return (handler, clients, pending, family.Id);
    }

    /// <summary>Everything a fresh process would build: a new handler, store and client factory over
    /// the given context. Calling it again with a new context stands in for a restart.</summary>
    private (ManagerUpdateHandler Handler, RecordingClientFactory Clients, IPendingBotCreations Pending, BotPollingCoordinator Coordinator) CreateHandler(AssistantDbContext db)
    {
        var clients = new RecordingClientFactory();
        var pending = new PendingBotCreations(db, _clock);
        var options = Options.Create(new BotOptions { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var encryptor = new Assistant.Infrastructure.Common.TokenEncryptor(options.Value.TokenEncryptionKey);
        var coordinator = new BotPollingCoordinator(
            new NoopScopeFactory(db), clients, encryptor, options, PollingWorkerSettings.Default,
            new PollingHealth(), _clock, NullLoggerFactory.Instance);
        var approvals = new ApprovalService(db, clients, options, _clock);

        var handler = new ManagerUpdateHandler(
            db, new FixedClaimCode(), pending, clients, encryptor, coordinator, approvals, _clock,
            new UsageCommandHandler(db, new Assistant.Infrastructure.Llm.NullBudgetGuard(), _clock, llmConfig: null), NullLogger<ManagerUpdateHandler>.Instance);
        return (handler, clients, pending, coordinator);
    }

    private AssistantDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        return new AssistantDbContext(options.Options);
    }

    /// <summary>BotPollingCoordinator.StartBotAsync opens its own DI scope to reload the bot row —
    /// tests share one already-open Db, so this hands back a scope whose AssistantDbContext is that
    /// same instance rather than spinning up a second connection.</summary>
    private sealed class NoopScopeFactory : IServiceScopeFactory
    {
        private readonly AssistantDbContext _db;

        public NoopScopeFactory(AssistantDbContext db) => _db = db;

        public IServiceScope CreateScope() => new SingleInstanceScope(_db);

        private sealed class SingleInstanceScope : IServiceScope
        {
            public SingleInstanceScope(AssistantDbContext db) => ServiceProvider = new SingleInstanceProvider(db);
            public IServiceProvider ServiceProvider { get; }
            public void Dispose() { }
        }

        private sealed class SingleInstanceProvider : IServiceProvider
        {
            private readonly AssistantDbContext _db;
            public SingleInstanceProvider(AssistantDbContext db) => _db = db;
            public object? GetService(Type serviceType) => serviceType == typeof(AssistantDbContext) ? _db : null;
        }
    }

    private static IncomingUpdate Command(long updateId, long userId, string text) =>
        new(updateId, new IncomingMessage(
            ChatId: userId, ChatType: "private", ChatTitle: null, TopicId: null, MessageId: (int)updateId, UserId: userId, Username: "test_owner",
            Text: text, Kind: Assistant.Domain.Messages.MessageKind.Text, IsEdit: false,
            SentAt: DateTimeOffset.UtcNow, EditedAt: null, MigrateToChatId: null, RawJson: "{}", ReplyToMessageId: null, ReplyToUserId: null));

    [Fact]
    public async Task Newbot_from_a_non_owner_is_rejected()
    {
        var (handler, _, _, _) = await SetupAsync();
        var telegram = new FakeTelegramClient();

        await handler.HandleAsync(ManagerBot, telegram, Command(1, 999, "/newbot general"), CancellationToken.None);

        telegram.SentMessages.ShouldContain(m => m.Text.Contains("владелец"));
    }

    [Fact]
    public async Task Newbot_from_the_owner_replies_with_a_creation_link_and_remembers_the_role()
    {
        var (handler, _, pending, _) = await SetupAsync();
        var telegram = new FakeTelegramClient();

        await handler.HandleAsync(ManagerBot, telegram, Command(1, 111, "/newbot cook"), CancellationToken.None);

        telegram.SentMessages.ShouldContain(m => m.Text.Contains("https://t.me/newbot/test_manager_bot/"));
        (await pending.GetRoleAsync(111, CancellationToken.None)).ShouldBe("cook");
    }

    [Fact]
    public async Task Managed_bot_update_creates_the_bot_row_with_the_pending_role_and_starts_it()
    {
        var (handler, clients, _, familyId) = await SetupAsync();
        var telegram = new FakeTelegramClient();
        clients.NewBotClient.EnqueueUpdate(new IncomingUpdate(0, null)); // unused, GetMeAsync doesn't consume updates

        await handler.HandleAsync(ManagerBot, telegram, Command(1, 111, "/newbot cook"), CancellationToken.None);
        var managedBotUpdate = new IncomingUpdate(2, null, ManagedBotCreatorUserId: 111, ManagedBotUserId: 555);

        await handler.HandleAsync(ManagerBot, telegram, managedBotUpdate, CancellationToken.None);

        var bot = await Db.Bots.IgnoreQueryFilters().SingleAsync(b => b.TelegramBotId == 999);
        bot.FamilyId.ShouldBe(familyId);
        bot.Role.ShouldBe("cook");
        bot.TokenEncrypted.ShouldNotBeNull();
        telegram.SentMessages.ShouldContain(m => m.ChatId == 111 && m.Text.Contains("создан и запущен"));
    }

    [Fact]
    public async Task Pending_role_survives_a_restart_between_newbot_and_the_managed_bot_update()
    {
        var (handler, _, _, _) = await SetupAsync();
        var telegram = new FakeTelegramClient();
        await handler.HandleAsync(ManagerBot, telegram, Command(1, 111, "/newbot cook"), CancellationToken.None);

        await using var dbAfterRestart = NewDbContext();
        var (handlerAfterRestart, _, _, coordinatorAfterRestart) = CreateHandler(dbAfterRestart);
        await handlerAfterRestart.HandleAsync(
            ManagerBot, telegram, new IncomingUpdate(2, null, ManagedBotCreatorUserId: 111, ManagedBotUserId: 555), CancellationToken.None);
        await coordinatorAfterRestart.StopAsync(CancellationToken.None);

        var bot = await Db.Bots.IgnoreQueryFilters().SingleAsync(b => b.TelegramBotId == 999);
        bot.Role.ShouldBe("cook");
    }

    [Fact]
    public async Task A_repeat_newbot_replaces_the_pending_role()
    {
        var (handler, _, pending, _) = await SetupAsync();
        var telegram = new FakeTelegramClient();

        await handler.HandleAsync(ManagerBot, telegram, Command(1, 111, "/newbot cook"), CancellationToken.None);
        await handler.HandleAsync(ManagerBot, telegram, Command(2, 111, "/newbot driver"), CancellationToken.None);

        (await pending.GetRoleAsync(111, CancellationToken.None)).ShouldBe("driver");
    }

    [Fact]
    public async Task A_failed_bot_creation_keeps_the_pending_role_for_the_redelivered_update()
    {
        var (handler, _, pending, _) = await SetupAsync();
        var telegram = new FakeTelegramClient();
        await handler.HandleAsync(ManagerBot, telegram, Command(1, 111, "/newbot cook"), CancellationToken.None);
        var managedBotUpdate = new IncomingUpdate(2, null, ManagedBotCreatorUserId: 111, ManagedBotUserId: 555);

        telegram.ManagedBotTokenToReturn = null; // the token fetch fails
        await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(ManagerBot, telegram, managedBotUpdate, CancellationToken.None));
        telegram.ManagedBotTokenToReturn = "test-managed-bot-token";
        await handler.HandleAsync(ManagerBot, telegram, managedBotUpdate, CancellationToken.None);

        (await Db.Bots.IgnoreQueryFilters().SingleAsync(b => b.TelegramBotId == 999)).Role.ShouldBe("cook");
        (await pending.GetRoleAsync(111, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_pending_role_older_than_a_day_is_ignored()
    {
        var (handler, _, pending, _) = await SetupAsync();
        var telegram = new FakeTelegramClient();
        await handler.HandleAsync(ManagerBot, telegram, Command(1, 111, "/newbot cook"), CancellationToken.None);

        _clock.Advance(TimeSpan.FromHours(25));

        (await pending.GetRoleAsync(111, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_role_that_does_not_fit_a_bot_row_is_rejected_without_a_link()
    {
        var (handler, _, _, _) = await SetupAsync();
        var telegram = new FakeTelegramClient();

        await handler.HandleAsync(ManagerBot, telegram, Command(1, 111, "/newbot " + new string('r', 65)), CancellationToken.None);

        telegram.SentMessages.ShouldHaveSingleItem().Text.ShouldContain("слишком длинная");
        (await Db.PendingBotCreations.CountAsync()).ShouldBe(0);
    }
}
