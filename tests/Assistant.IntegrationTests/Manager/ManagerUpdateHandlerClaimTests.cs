using Assistant.Application.Common;
using Assistant.Application.Families;
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

public class ManagerUpdateHandlerClaimTests : IntegrationTestBase
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

    private static readonly ReceivingBot ManagerBot = new(BotDbId: 1, TelegramBotId: 998, Username: "test_manager_bot", FamilyId: null, Role: "manager");

    private ManagerUpdateHandler CreateHandler()
    {
        var clients = new SingleClientFactory();
        var options = Options.Create(new BotOptions { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var encryptor = new Assistant.Infrastructure.Common.TokenEncryptor(options.Value.TokenEncryptionKey);
        var clock = new SystemClock();
        var coordinator = new BotPollingCoordinator(
            new NoopScopeFactory(Db), clients, encryptor, options, PollingWorkerSettings.Default,
            new PollingHealth(), clock, NullLoggerFactory.Instance);
        var approvals = new ApprovalService(Db, clients, options, clock);
        return new ManagerUpdateHandler(Db, new FixedClaimCode(), new PendingBotCreations(Db, clock), clients, encryptor, coordinator, approvals, clock, NullLogger<ManagerUpdateHandler>.Instance);
    }

    private static IncomingUpdate ClaimCommand(long updateId, long userId, string? username, string args) =>
        new(updateId, new IncomingMessage(
            ChatId: userId, ChatType: "private", ChatTitle: null, TopicId: null, MessageId: (int)updateId, UserId: userId, Username: username,
            Text: $"/claim {args}", Kind: Assistant.Domain.Messages.MessageKind.Text, IsEdit: false,
            SentAt: DateTimeOffset.UtcNow, EditedAt: null, MigrateToChatId: null, RawJson: "{}", ReplyToMessageId: null, ReplyToUserId: null));

    [Fact]
    public async Task Correct_code_creates_the_first_family_with_the_sender_as_owner()
    {
        var handler = CreateHandler();
        var telegram = new FakeTelegramClient();

        await handler.HandleAsync(ManagerBot, telegram, ClaimCommand(1, 111, "test_owner", "424242"), CancellationToken.None);

        (await Db.Families.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        var member = await Db.FamilyMembers.IgnoreQueryFilters().SingleAsync();
        member.TelegramUserId.ShouldBe(111);
        member.IsOwner.ShouldBeTrue();
        member.Status.ShouldBe(FamilyMemberStatus.Approved);
        telegram.SentMessages.ShouldContain(m => m.Text.Contains("Готово"));
    }

    [Fact]
    public async Task Wrong_code_creates_nothing()
    {
        var handler = CreateHandler();
        var telegram = new FakeTelegramClient();

        await handler.HandleAsync(ManagerBot, telegram, ClaimCommand(1, 111, "test_owner", "000000"), CancellationToken.None);

        (await Db.Families.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        telegram.SentMessages.ShouldContain(m => m.Text.Contains("Неверный"));
    }

    [Fact]
    public async Task Re_claiming_after_a_family_already_exists_is_rejected()
    {
        var handler = CreateHandler();
        var telegram = new FakeTelegramClient();
        await handler.HandleAsync(ManagerBot, telegram, ClaimCommand(1, 111, "test_owner", "424242"), CancellationToken.None);

        await handler.HandleAsync(ManagerBot, telegram, ClaimCommand(2, 222, "test_other", "424242"), CancellationToken.None);

        (await Db.Families.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        (await Db.FamilyMembers.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        telegram.SentMessages.ShouldContain(m => m.Text.Contains("уже активирована"));
    }

    [Fact]
    public async Task Two_concurrent_claims_against_the_same_database_create_only_one_family()
    {
        // Two independent DbContext instances on two independent connections, so the two claims
        // genuinely race at the Postgres level (a single DbContext is not thread-safe and would
        // just serialize the calls, proving nothing about the database-level fix).
        var optionsA = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(optionsA, ConnectionString);
        await using var dbA = new AssistantDbContext(optionsA.Options);
        var clientsA = new SingleClientFactory();
        var botOptionsA = Options.Create(new BotOptions { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var encryptorA = new Assistant.Infrastructure.Common.TokenEncryptor(botOptionsA.Value.TokenEncryptionKey);
        var clockA = new SystemClock();
        var coordinatorA = new BotPollingCoordinator(
            new NoopScopeFactory(dbA), clientsA, encryptorA, botOptionsA, PollingWorkerSettings.Default,
            new PollingHealth(), clockA, NullLoggerFactory.Instance);
        var approvalsA = new ApprovalService(dbA, clientsA, botOptionsA, clockA);
        var handlerA = new ManagerUpdateHandler(dbA, new FixedClaimCode(), new PendingBotCreations(dbA, clockA), clientsA, encryptorA, coordinatorA, approvalsA, clockA, NullLogger<ManagerUpdateHandler>.Instance);
        var telegramA = new FakeTelegramClient();

        var optionsB = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(optionsB, ConnectionString);
        await using var dbB = new AssistantDbContext(optionsB.Options);
        var clientsB = new SingleClientFactory();
        var botOptionsB = Options.Create(new BotOptions { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var encryptorB = new Assistant.Infrastructure.Common.TokenEncryptor(botOptionsB.Value.TokenEncryptionKey);
        var clockB = new SystemClock();
        var coordinatorB = new BotPollingCoordinator(
            new NoopScopeFactory(dbB), clientsB, encryptorB, botOptionsB, PollingWorkerSettings.Default,
            new PollingHealth(), clockB, NullLoggerFactory.Instance);
        var approvalsB = new ApprovalService(dbB, clientsB, botOptionsB, clockB);
        var handlerB = new ManagerUpdateHandler(dbB, new FixedClaimCode(), new PendingBotCreations(dbB, clockB), clientsB, encryptorB, coordinatorB, approvalsB, clockB, NullLogger<ManagerUpdateHandler>.Instance);
        var telegramB = new FakeTelegramClient();

        await Task.WhenAll(
            handlerA.HandleAsync(ManagerBot, telegramA, ClaimCommand(1, 111, "test_owner_a", "424242"), CancellationToken.None),
            handlerB.HandleAsync(ManagerBot, telegramB, ClaimCommand(2, 222, "test_owner_b", "424242"), CancellationToken.None));

        (await Db.Families.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        var owners = await Db.FamilyMembers.IgnoreQueryFilters().Where(m => m.IsOwner).ToListAsync();
        owners.Count.ShouldBe(1);

        var allReplies = telegramA.SentMessages.Concat(telegramB.SentMessages).ToList();
        allReplies.ShouldContain(m => m.Text.Contains("Готово"));
        allReplies.ShouldContain(m => m.Text.Contains("уже активирована"));
    }
}
