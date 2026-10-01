using Assistant.Application.Common;
using Assistant.Domain.Families;
using Assistant.Domain.Llm;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Manager;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Assistant.IntegrationTests.Manager;

/// <summary>/usage: owner-only, platform totals + state visible to any owner, per-bot/per-model
/// breakdown isolated to the caller's own family, zero-price calls shown as "подписка", and the
/// "budgets aren't configured" text while still showing call counts.</summary>
public class UsageCommandHandlerTests : IntegrationTestBase
{
    private const string Provider = "anthropic";
    private const string FreeProvider = "claude-cli";

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

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
        return new UsageCommandHandler(Db, guard, clock);
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

    private async Task SeedLlmCallAsync(long familyId, string provider, string model, decimal cost, int inputTokens = 100, int outputTokens = 50)
    {
        Db.LlmCalls.Add(new LlmCall
        {
            FamilyId = familyId,
            BotId = 1,
            Tier = LlmConfig.SmartTier,
            Provider = provider,
            Model = model,
            Outcome = LlmCallOutcome.Ok,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = cost,
            DurationMs = 1,
            CreatedAt = Now
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
        await SeedLlmCallAsync(familyId, FreeProvider, "sonnet", cost: 0m);
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
        var familyBId = await SeedFamilyAsync();
        await SeedLlmCallAsync(familyBId, Provider, "claude-haiku-4-5", cost: 5m);
        await SeedLlmCallAsync(familyAId, FreeProvider, "sonnet", cost: 0m);
        var handler = CreateHandler(budget: new BudgetConfig(2m, 30m, 80, 120));

        var reply = await handler.BuildReplyAsync(ownerAId, CancellationToken.None);

        reply.ShouldContain("Сегодня:"); // platform totals visible
        reply.ShouldContain("Этот месяц:");
        reply.ShouldContain("claude-cli:sonnet"); // own family's breakdown
        reply.ShouldContain("подписка"); // zero-price call shown as "подписка"
        reply.ShouldNotContain("claude-haiku-4-5"); // NOT family B's breakdown
    }

    [Fact]
    public async Task Calls_outside_the_current_month_are_excluded_from_the_breakdown()
    {
        var familyId = await SeedFamilyAsync();
        var ownerId = await SeedOwnerAsync(familyId);
        await SeedLlmCallAsync(familyId, FreeProvider, "sonnet", cost: 0m);
        Db.LlmCalls.Add(new LlmCall
        {
            FamilyId = familyId, BotId = 1, Tier = LlmConfig.SmartTier, Provider = FreeProvider, Model = "old-model",
            Outcome = LlmCallOutcome.Ok, Cost = 0m, DurationMs = 1, CreatedAt = Now.AddMonths(-2)
        });
        await Db.SaveChangesAsync();
        var handler = CreateHandler(budget: null);

        var reply = await handler.BuildReplyAsync(ownerId, CancellationToken.None);

        reply.ShouldContain("1 вызовов"); // only this month's call counted
        reply.ShouldNotContain("old-model");
    }
}
