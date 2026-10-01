using Assistant.Application.Common;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Llm;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Manager;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Assistant.IntegrationTests.Manager;

/// <summary>/usage: owner-only, platform totals + state visible to any owner, a "today" and "this
/// month" per-bot/per-model breakdown isolated to the caller's own family (bot shown by username,
/// not internal id), zero-price calls shown as "подписка", and the "budgets aren't configured" text
/// while still showing call counts.</summary>
public class UsageCommandHandlerTests : IntegrationTestBase
{
    private const string Provider = "anthropic";
    private const string FreeProvider = "claude-cli";

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TodayStart = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private UsageCommandHandler CreateHandler(BudgetConfig? budget)
    {
        var clock = new FixedClock(Now);
        var config = new LlmConfig
        {
            Models = Array.Empty<ModelCatalogEntry>(),
            CallsPerMinute = 100,
            CallsPerDay = 1000,
            MaxContextMessages = 30,
            MaxInputChars = 8000,
            MaxOutputTokens = 1024,
            CallTimeoutSeconds = 30,
            MaxConcurrentCalls = 4,
            ModelCooldownMinutes = 15,
            Prices = new Dictionary<string, ModelPrice>(),
            Budget = budget,
            FastModels = Array.Empty<ModelCatalogEntry>()
        };
        IBudgetGuard guard = budget is null ? new NullBudgetGuard() : new BudgetGuard(config, Db, clock);
        return new UsageCommandHandler(Db, guard, clock, config);
    }

    private async Task<long> SeedFamilyAsync()
    {
        var family = new Family { Name = "test family", CreatedAt = DateTimeOffset.UtcNow };
        Db.Families.Add(family);
        await Db.SaveChangesAsync();
        return family.Id;
    }

    private async Task<long> SeedOwnerAsync(long familyId)
    {
        var ownerUserId = Random.Shared.NextInt64(1000, 1_000_000);
        var owner = new FamilyMember
        {
            FamilyId = familyId, TelegramUserId = ownerUserId, DisplayName = "owner", Status = FamilyMemberStatus.Approved,
            IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        Db.FamilyMembers.Add(owner);
        await Db.SaveChangesAsync();
        return ownerUserId;
    }

    private async Task<long> SeedBotAsync(long? familyId, string username)
    {
        var bot = new Bot
        {
            FamilyId = familyId, TelegramBotId = Random.Shared.NextInt64(1000, 1_000_000), Username = username, Role = "general",
            Status = BotStatus.Active, LastUpdateId = 0, CreatedAt = DateTimeOffset.UtcNow
        };
        Db.Bots.Add(bot);
        await Db.SaveChangesAsync();
        return bot.Id;
    }

    private async Task SeedLlmCallAsync(
        long familyId, long botId, string provider, string model, decimal cost, int inputTokens = 100, int outputTokens = 50, DateTimeOffset? createdAt = null)
    {
        Db.LlmCalls.Add(new LlmCall
        {
            FamilyId = familyId,
            BotId = botId,
            Tier = LlmConfig.SmartTier,
            Provider = provider,
            Model = model,
            Outcome = LlmCallOutcome.Ok,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = cost,
            DurationMs = 1,
            CreatedAt = createdAt ?? Now
        });
        await Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Non_owner_gets_the_no_rights_text()
    {
        var handler = CreateHandler(budget: null);

        (await handler.BuildReplyAsync(callerUserId: 12345, CancellationToken.None)).ShouldBe("У вас нет прав.");
    }

    [Fact]
    public async Task An_owner_with_no_budget_configured_is_told_so_and_still_sees_call_counts()
    {
        var familyId = await SeedFamilyAsync();
        var ownerId = await SeedOwnerAsync(familyId);
        var botId = await SeedBotAsync(familyId, "test_role_bot");
        await SeedLlmCallAsync(familyId, botId, FreeProvider, "sonnet", cost: 0m);
        var handler = CreateHandler(budget: null);

        var reply = await handler.BuildReplyAsync(ownerId, CancellationToken.None);

        reply.ShouldStartWith("Бюджет не настроен.");
        reply.ShouldContain("1 вызовов");
        reply.ShouldContain("claude-cli:sonnet");
        reply.ShouldContain("подписка");
    }

    [Fact]
    public async Task An_owner_sees_platform_totals_and_only_their_own_familys_breakdown()
    {
        var familyAId = await SeedFamilyAsync();
        var ownerAId = await SeedOwnerAsync(familyAId);
        var botAId = await SeedBotAsync(familyAId, "family_a_bot");
        var familyBId = await SeedFamilyAsync();
        var botBId = await SeedBotAsync(familyBId, "family_b_bot");
        await SeedLlmCallAsync(familyBId, botBId, Provider, "claude-haiku-4-5", cost: 5m);
        await SeedLlmCallAsync(familyAId, botAId, FreeProvider, "sonnet", cost: 0m);
        var handler = CreateHandler(budget: new BudgetConfig(2m, 30m, 80, 120));

        var reply = await handler.BuildReplyAsync(ownerAId, CancellationToken.None);

        reply.ShouldContain("Сегодня:"); // platform totals visible
        reply.ShouldContain("Этот месяц:");
        reply.ShouldContain("claude-cli:sonnet"); // own family's breakdown
        reply.ShouldContain("подписка"); // zero-price call shown as "подписка"
        reply.ShouldNotContain("claude-haiku-4-5"); // NOT family B's breakdown
        reply.ShouldNotContain("family_b_bot");
    }

    [Fact]
    public async Task Calls_outside_the_current_month_are_excluded_from_the_breakdown()
    {
        var familyId = await SeedFamilyAsync();
        var ownerId = await SeedOwnerAsync(familyId);
        var botId = await SeedBotAsync(familyId, "test_role_bot");
        await SeedLlmCallAsync(familyId, botId, FreeProvider, "sonnet", cost: 0m);
        await SeedLlmCallAsync(familyId, botId, FreeProvider, "old-model", cost: 0m, createdAt: Now.AddMonths(-2));
        var handler = CreateHandler(budget: null);

        var reply = await handler.BuildReplyAsync(ownerId, CancellationToken.None);

        reply.ShouldContain("1 вызовов"); // only this month's call counted
        reply.ShouldNotContain("old-model");
    }

    [Fact]
    public async Task Shows_the_bot_username_not_its_internal_id()
    {
        var familyId = await SeedFamilyAsync();
        var ownerId = await SeedOwnerAsync(familyId);
        var botId = await SeedBotAsync(familyId, "my_named_bot");
        await SeedLlmCallAsync(familyId, botId, FreeProvider, "sonnet", cost: 0m);
        var handler = CreateHandler(budget: null);

        var reply = await handler.BuildReplyAsync(ownerId, CancellationToken.None);

        reply.ShouldContain("Бот my_named_bot:");
        reply.ShouldNotContain($"Бот {botId}:");
    }

    [Fact]
    public async Task Today_and_this_month_each_get_their_own_breakdown_section()
    {
        var familyId = await SeedFamilyAsync();
        var ownerId = await SeedOwnerAsync(familyId);
        var botId = await SeedBotAsync(familyId, "test_role_bot");
        await SeedLlmCallAsync(familyId, botId, FreeProvider, "today-model", cost: 0m, createdAt: Now);
        // Earlier this month, but before today's UTC midnight -- counted in the month section, not
        // in the today section.
        await SeedLlmCallAsync(familyId, botId, FreeProvider, "earlier-this-month-model", cost: 0m, createdAt: TodayStart.AddDays(-1));
        var handler = CreateHandler(budget: null);

        var reply = await handler.BuildReplyAsync(ownerId, CancellationToken.None);

        var todaySectionIndex = reply.IndexOf("Ваша семья (сегодня)", StringComparison.Ordinal);
        var monthSectionIndex = reply.IndexOf("Ваша семья (этот месяц)", StringComparison.Ordinal);
        todaySectionIndex.ShouldBeGreaterThanOrEqualTo(0);
        monthSectionIndex.ShouldBeGreaterThan(todaySectionIndex);

        var todaySection = reply[todaySectionIndex..monthSectionIndex];
        todaySection.ShouldContain("today-model");
        todaySection.ShouldNotContain("earlier-this-month-model");

        var monthSection = reply[monthSectionIndex..];
        monthSection.ShouldContain("today-model");
        monthSection.ShouldContain("earlier-this-month-model");
    }

    [Fact]
    public async Task A_reply_longer_than_the_Telegram_cap_is_truncated_at_a_line_boundary()
    {
        var familyId = await SeedFamilyAsync();
        var ownerId = await SeedOwnerAsync(familyId);
        var botId = await SeedBotAsync(familyId, "test_role_bot");
        for (var i = 0; i < 200; i++)
        {
            await SeedLlmCallAsync(familyId, botId, FreeProvider, $"model-{i:000}", cost: 0m);
        }

        var handler = CreateHandler(budget: null);

        var reply = await handler.BuildReplyAsync(ownerId, CancellationToken.None);

        reply.Length.ShouldBeLessThanOrEqualTo(4096);
        reply.ShouldEndWith("\n…");
        // The cut lands on a whole line boundary, not mid-line: every breakdown/header line this
        // handler generates ends with '.' or ':', so the content right before the appended marker
        // must end with one of those, never a sliced-off fragment of a model name.
        var beforeMarker = reply[..^"\n…".Length];
        (beforeMarker.EndsWith('.') || beforeMarker.EndsWith(':')).ShouldBeTrue();
    }

    [Fact]
    public async Task A_platform_wide_call_count_for_today_and_this_month_is_shown()
    {
        var familyAId = await SeedFamilyAsync();
        var ownerId = await SeedOwnerAsync(familyAId);
        var botAId = await SeedBotAsync(familyAId, "family_a_bot");
        var familyBId = await SeedFamilyAsync();
        var botBId = await SeedBotAsync(familyBId, "family_b_bot");
        await SeedLlmCallAsync(familyAId, botAId, FreeProvider, "sonnet", cost: 0m, createdAt: Now);
        await SeedLlmCallAsync(familyBId, botBId, FreeProvider, "sonnet", cost: 0m, createdAt: Now);
        // Earlier this month but not today: counted in the month figure only.
        await SeedLlmCallAsync(familyBId, botBId, FreeProvider, "sonnet", cost: 0m, createdAt: TodayStart.AddDays(-1));
        var handler = CreateHandler(budget: null);

        var reply = await handler.BuildReplyAsync(ownerId, CancellationToken.None);

        reply.ShouldContain("Вызовов на платформе: 2 сегодня, 3 в этом месяце.");
    }

    [Fact]
    public async Task A_limit_reached_attempt_on_a_priced_model_is_not_shown_as_a_subscription()
    {
        // Review fix: LlmGateway records a LimitReached attempt at Cost 0 regardless of the model's
        // real price -- labelling it "подписка" (a Cost == 0 check) would mislabel a failed attempt
        // on an expensive paid model as free. The catalog's own price decides this, not the row.
        var familyId = await SeedFamilyAsync();
        var ownerId = await SeedOwnerAsync(familyId);
        var botId = await SeedBotAsync(familyId, "test_role_bot");
        await SeedLlmCallAsync(familyId, botId, Provider, "claude-haiku-4-5", cost: 0m);
        var clock = new FixedClock(Now);
        var config = new LlmConfig
        {
            Models = Array.Empty<ModelCatalogEntry>(),
            CallsPerMinute = 100,
            CallsPerDay = 1000,
            MaxContextMessages = 30,
            MaxInputChars = 8000,
            MaxOutputTokens = 1024,
            CallTimeoutSeconds = 30,
            MaxConcurrentCalls = 4,
            ModelCooldownMinutes = 15,
            Prices = new Dictionary<string, ModelPrice> { ["claude-haiku-4-5"] = new ModelPrice(1m, 5m) },
            Budget = null,
            FastModels = Array.Empty<ModelCatalogEntry>()
        };
        var handler = new UsageCommandHandler(Db, new NullBudgetGuard(), clock, config);

        var reply = await handler.BuildReplyAsync(ownerId, CancellationToken.None);

        reply.ShouldContain("claude-haiku-4-5: 1 вызовов, 150 токенов, $0.0000.");
        reply.ShouldNotContain("подписка");
    }

    [Fact]
    public async Task A_zero_priced_paid_provider_entry_is_still_shown_as_a_subscription()
    {
        var familyId = await SeedFamilyAsync();
        var ownerId = await SeedOwnerAsync(familyId);
        var botId = await SeedBotAsync(familyId, "test_role_bot");
        await SeedLlmCallAsync(familyId, botId, Provider, "free-tier-model", cost: 0m);
        var clock = new FixedClock(Now);
        var config = new LlmConfig
        {
            Models = Array.Empty<ModelCatalogEntry>(),
            CallsPerMinute = 100,
            CallsPerDay = 1000,
            MaxContextMessages = 30,
            MaxInputChars = 8000,
            MaxOutputTokens = 1024,
            CallTimeoutSeconds = 30,
            MaxConcurrentCalls = 4,
            ModelCooldownMinutes = 15,
            Prices = new Dictionary<string, ModelPrice> { ["free-tier-model"] = new ModelPrice(0m, 0m) },
            Budget = null,
            FastModels = Array.Empty<ModelCatalogEntry>()
        };
        var handler = new UsageCommandHandler(Db, new NullBudgetGuard(), clock, config);

        var reply = await handler.BuildReplyAsync(ownerId, CancellationToken.None);

        reply.ShouldContain("подписка");
    }

    [Fact]
    public async Task A_deleted_bots_calls_are_still_counted_under_a_generic_deleted_bot_label()
    {
        var familyId = await SeedFamilyAsync();
        var ownerId = await SeedOwnerAsync(familyId);
        var botId = await SeedBotAsync(familyId, "soon_to_be_deleted");
        await SeedLlmCallAsync(familyId, botId, FreeProvider, "sonnet", cost: 0m);
        Db.Bots.Remove(await Db.Bots.FirstAsync(b => b.Id == botId));
        await Db.SaveChangesAsync();
        var handler = CreateHandler(budget: null);

        var reply = await handler.BuildReplyAsync(ownerId, CancellationToken.None);

        // The old inner-join implementation dropped this row from the breakdown entirely while the
        // total call count above still counted it -- both must now agree.
        reply.ShouldContain("1 вызовов"); // total count still includes the deleted bot's call
        reply.ShouldContain("Бот удалённый бот:");
        reply.ShouldContain("claude-cli:sonnet");
    }

    [Fact]
    public async Task An_owner_who_is_also_a_member_of_another_familys_table_row_still_gets_a_deterministic_reply()
    {
        // Guards against FirstOrDefaultAsync with no ordering: seeding two Approved-owner rows for
        // the same Telegram user id across two families (not reachable via /claim today, but cheap to
        // guard against directly) must not make the reply flaky between runs.
        var familyAId = await SeedFamilyAsync();
        var familyBId = await SeedFamilyAsync();
        var ownerUserId = Random.Shared.NextInt64(1000, 1_000_000);
        Db.FamilyMembers.AddRange(
            new FamilyMember { FamilyId = familyAId, TelegramUserId = ownerUserId, DisplayName = "owner", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow },
            new FamilyMember { FamilyId = familyBId, TelegramUserId = ownerUserId, DisplayName = "owner", Status = FamilyMemberStatus.Approved, IsOwner = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();
        var handler = CreateHandler(budget: null);

        var first = await handler.BuildReplyAsync(ownerUserId, CancellationToken.None);
        var second = await handler.BuildReplyAsync(ownerUserId, CancellationToken.None);

        first.ShouldBe(second);
    }
}
