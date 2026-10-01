using Assistant.Application.Common;
using Assistant.Domain.Families;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Telegram;
using Assistant.IntegrationTests.Host;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Assistant.IntegrationTests.Llm;

/// <summary>Platform-admin DMs on a budget threshold crossing: insert-before-send dedup, "platform
/// admin" isolation (first-claimed family's owners only, not every family's owners), and each
/// threshold (warn/soft/hard) firing separately.</summary>
public class BudgetNoticeSenderTests : IntegrationTestBase
{
    private sealed class SingleClientFactory : ITelegramClientFactory
    {
        public FakeTelegramClient Client { get; } = new();
        public Assistant.Application.Telegram.ITelegramClient Create(string token) => Client;
    }

    /// <summary>Builds a real, independent <see cref="AssistantDbContext"/> per call, against a given
    /// connection string -- the production shape of <c>IDbContextFactory&lt;AssistantDbContext&gt;</c>,
    /// minus DI.</summary>
    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<Assistant.Infrastructure.Persistence.AssistantDbContext>
    {
        public Assistant.Infrastructure.Persistence.AssistantDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<Assistant.Infrastructure.Persistence.AssistantDbContext>();
            Assistant.Infrastructure.Persistence.AssistantDbContext.Configure(options, connectionString);
            return new Assistant.Infrastructure.Persistence.AssistantDbContext(options.Options);
        }

        public Task<Assistant.Infrastructure.Persistence.AssistantDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    /// <summary>Every context it creates points at an address nothing listens on, with a 1s connect
    /// timeout: any <c>SaveChangesAsync</c> against it fails fast with a connection error -- a
    /// non-unique-violation failure, unlike the dedup race the other tests in this file cover.</summary>
    private sealed class BadDbContextFactory : IDbContextFactory<Assistant.Infrastructure.Persistence.AssistantDbContext>
    {
        public Assistant.Infrastructure.Persistence.AssistantDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<Assistant.Infrastructure.Persistence.AssistantDbContext>();
            Assistant.Infrastructure.Persistence.AssistantDbContext.Configure(options, "Host=127.0.0.1;Port=1;Database=doesnotexist;Username=x;Password=x;Timeout=1");
            return new Assistant.Infrastructure.Persistence.AssistantDbContext(options.Options);
        }

        public Task<Assistant.Infrastructure.Persistence.AssistantDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static readonly DateTimeOffset DayStart = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DayEnd = DayStart.AddDays(1);
    private static readonly DateTimeOffset MonthStart = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MonthEnd = MonthStart.AddMonths(1);

    private static readonly BudgetConfig Config = new(DailyUsd: 100m, MonthlyUsd: 1000m, WarnPercent: 80, HardPercent: 120);

    private SingleClientFactory _clients = null!;

    private (BudgetNoticeSender Sender, SingleClientFactory Clients) CreateSender()
    {
        _clients = new SingleClientFactory();
        var options = Options.Create(new BotOptions { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var sender = new BudgetNoticeSender(
            Db, new TestDbContextFactory(ConnectionString), _clients, options, new SystemClock(), NullLogger<BudgetNoticeSender>.Instance);
        return (sender, _clients);
    }

    private async Task<long> SeedFamilyAsync()
    {
        var family = new Family { Name = "test family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();
        return family.Id;
    }

    private async Task<long> SeedOwnerAsync(long familyId, long telegramUserId)
    {
        var owner = new FamilyMember
        {
            FamilyId = familyId, TelegramUserId = telegramUserId, DisplayName = "owner", Status = FamilyMemberStatus.Approved,
            IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        Db.FamilyMembers.Add(owner);
        await Db.SaveChangesAsync();
        return owner.Id;
    }

    private static BudgetStatus StatusAt(decimal dailySpend, decimal monthlySpend) => new(
        BudgetGuard.EvaluatePeriod("daily", DayStart, DayEnd, dailySpend, Config.DailyUsd, Config),
        BudgetGuard.EvaluatePeriod("monthly", MonthStart, MonthEnd, monthlySpend, Config.MonthlyUsd, Config));

    [Fact]
    public async Task Sends_one_DM_per_platform_admin_and_inserts_one_notice_row_at_warn()
    {
        var familyId = await SeedFamilyAsync();
        var ownerUserId = 111L;
        await SeedOwnerAsync(familyId, ownerUserId);
        var (sender, clients) = CreateSender();

        await sender.NotifyAsync(StatusAt(dailySpend: 85m, monthlySpend: 5m), CancellationToken.None);

        Db.BudgetNotices.Count().ShouldBe(1);
        clients.Client.SentMessages.ShouldContain(m => m.ChatId == ownerUserId);
    }

    [Fact]
    public async Task A_second_call_at_the_same_threshold_sends_nothing_more()
    {
        var familyId = await SeedFamilyAsync();
        await SeedOwnerAsync(familyId, 111);
        var (sender, clients) = CreateSender();
        var status = StatusAt(dailySpend: 85m, monthlySpend: 5m);
        await sender.NotifyAsync(status, CancellationToken.None);
        clients.Client.ClearSent();

        await sender.NotifyAsync(status, CancellationToken.None); // same daily state, same day

        Db.BudgetNotices.Count().ShouldBe(1);
        clients.Client.SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_second_call_still_at_warn_but_a_higher_percent_does_not_send_again()
    {
        // Pitfall the plan's own "Pitfall" paragraph warned about: dedup must key on the fixed
        // BudgetState bucket (Warn/Soft/Hard), not on the continuously-varying rounded Percent --
        // otherwise every distinct percent past the warn line would fire a fresh DM.
        var familyId = await SeedFamilyAsync();
        await SeedOwnerAsync(familyId, 111);
        var (sender, clients) = CreateSender();
        await sender.NotifyAsync(StatusAt(dailySpend: 81m, monthlySpend: 1m), CancellationToken.None); // ~81% (Warn)
        clients.Client.ClearSent();

        await sender.NotifyAsync(StatusAt(dailySpend: 95m, monthlySpend: 1m), CancellationToken.None); // ~95% (still Warn)

        Db.BudgetNotices.Count().ShouldBe(1);
        clients.Client.SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_is_sent_below_warn()
    {
        var familyId = await SeedFamilyAsync();
        await SeedOwnerAsync(familyId, 111);
        var (sender, clients) = CreateSender();

        await sender.NotifyAsync(StatusAt(dailySpend: 10m, monthlySpend: 10m), CancellationToken.None);

        Db.BudgetNotices.Count().ShouldBe(0);
        clients.Client.SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Warn_soft_and_hard_each_fire_their_own_separate_notice()
    {
        var familyId = await SeedFamilyAsync();
        await SeedOwnerAsync(familyId, 111);
        var (sender, clients) = CreateSender();

        await sender.NotifyAsync(StatusAt(dailySpend: 85m, monthlySpend: 1m), CancellationToken.None);  // Warn
        await sender.NotifyAsync(StatusAt(dailySpend: 100m, monthlySpend: 1m), CancellationToken.None); // Soft
        await sender.NotifyAsync(StatusAt(dailySpend: 125m, monthlySpend: 1m), CancellationToken.None); // Hard

        Db.BudgetNotices.Count().ShouldBe(3);
        clients.Client.SentMessages.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Two_concurrent_calls_at_the_same_threshold_only_send_once()
    {
        var familyId = await SeedFamilyAsync();
        await SeedOwnerAsync(familyId, 111);
        var options = Options.Create(new BotOptions { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var clients = new SingleClientFactory();
        var status = StatusAt(dailySpend: 85m, monthlySpend: 1m);

        // Two independent DbContexts against the same database, racing the same insert -- the real
        // DB-level concurrency ApprovalServiceTests' pattern relies on IntegreSqlPool's single
        // shared database to exercise, not a single (non-thread-safe) context.
        var optionsBuilder = new DbContextOptionsBuilder<Assistant.Infrastructure.Persistence.AssistantDbContext>();
        Assistant.Infrastructure.Persistence.AssistantDbContext.Configure(optionsBuilder, ConnectionString);
        await using var dbB = new Assistant.Infrastructure.Persistence.AssistantDbContext(optionsBuilder.Options);

        var dbFactory = new TestDbContextFactory(ConnectionString);
        var senderA = new BudgetNoticeSender(Db, dbFactory, clients, options, new SystemClock(), NullLogger<BudgetNoticeSender>.Instance);
        var senderB = new BudgetNoticeSender(dbB, dbFactory, clients, options, new SystemClock(), NullLogger<BudgetNoticeSender>.Instance);

        await Task.WhenAll(
            senderA.NotifyAsync(status, CancellationToken.None),
            senderB.NotifyAsync(status, CancellationToken.None));

        Db.BudgetNotices.Count().ShouldBe(1);
        clients.Client.SentMessages.Count(m => m.ChatId == 111).ShouldBe(1);
    }

    [Fact]
    public async Task Only_the_first_claimed_familys_owners_receive_the_DM()
    {
        var firstFamilyId = await SeedFamilyAsync();
        await SeedOwnerAsync(firstFamilyId, 111);
        var secondFamilyId = await SeedFamilyAsync();
        await SeedOwnerAsync(secondFamilyId, 222);
        var (sender, clients) = CreateSender();

        await sender.NotifyAsync(StatusAt(dailySpend: 85m, monthlySpend: 1m), CancellationToken.None);

        clients.Client.SentMessages.ShouldContain(m => m.ChatId == 111);
        clients.Client.SentMessages.ShouldNotContain(m => m.ChatId == 222);
    }

    [Fact]
    public async Task A_non_unique_insert_failure_does_not_leave_the_request_contexts_change_tracker_broken()
    {
        // The dedup insert always fails with a connection error, never a unique violation -- the old
        // code (notice added directly to the shared `Db`) would leave that failed Added entity
        // tracked on Db forever, breaking every later SaveChangesAsync on it. The fix inserts through
        // its own throwaway context, so Db must come out of this completely unaffected.
        var options = Options.Create(new BotOptions { ManagerToken = "test-manager-token", TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
        var clients = new SingleClientFactory();
        var sender = new BudgetNoticeSender(Db, new BadDbContextFactory(), clients, options, new SystemClock(), NullLogger<BudgetNoticeSender>.Instance);

        await sender.NotifyAsync(StatusAt(dailySpend: 85m, monthlySpend: 1m), CancellationToken.None);

        // Nothing was sent (the insert never succeeded) and nothing was persisted on the real DB.
        clients.Client.SentMessages.ShouldBeEmpty();
        Db.BudgetNotices.Count().ShouldBe(0);

        // The request's own shared context must still be perfectly usable for an unrelated save
        // afterward (standing in for "the gateway's next save" in the review finding).
        Db.Families.Add(new Family { Name = "after a failed notice insert", CreatedAt = DateTimeOffset.UtcNow });
        await Should.NotThrowAsync(async () => await Db.SaveChangesAsync());
    }
}
