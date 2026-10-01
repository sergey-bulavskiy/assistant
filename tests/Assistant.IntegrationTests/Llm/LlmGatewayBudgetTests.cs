using Assistant.Application.Common;
using Assistant.Application.Llm;
using Assistant.Domain.Llm;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.IntegrationTests.Llm;

/// <summary>The platform-wide money budget in the gateway pipeline: states, candidate filtering,
/// the pre-call estimate and cost recording, against real llm_calls rows.</summary>
public class LlmGatewayBudgetTests : IntegrationTestBase
{
    private const string Provider = "fake";
    private const string SmartPaid = "smart-paid";   // paid, not in the fast tier
    private const string FastPaid = "fast-paid";     // paid, in the fast tier
    private const string Free = "free";              // zero price (like claude-cli)
    private const long OtherFamily = 900;

    // Mid-day, mid-month: the UTC day runs Oct 15 00:00 .. Oct 16 00:00, the month Oct 1 .. Nov 1.
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextDay = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextMonth = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    // Request: "system" (6 chars) + "hello" (5 chars) = 5.5 estimated input tokens; max output 1024.
    // At 1/5 USD per million: 5.5e-6 + 1024 * 5e-6 = 0.0051255 -> 0.0052 (rounded up).
    private const decimal SmartPaidEstimate = 0.0052m;

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private static readonly ModelPrice OneFive = new(1m, 5m);

    private static LlmConfig MakeConfig(
        string[] chain,
        BudgetConfig? budget,
        Dictionary<string, ModelPrice>? prices = null,
        string[]? fast = null,
        int callTimeoutSeconds = 30) => new()
    {
        Models = chain.Select(n => new ModelCatalogEntry(Provider, n)).ToArray(),
        CallsPerMinute = 100,
        CallsPerDay = 1000,
        MaxContextMessages = 30,
        MaxInputChars = 8000,
        MaxOutputTokens = 1024,
        CallTimeoutSeconds = callTimeoutSeconds,
        MaxConcurrentCalls = 4,
        ModelCooldownMinutes = 15,
        Prices = prices ?? new Dictionary<string, ModelPrice> { [SmartPaid] = OneFive, [FastPaid] = OneFive },
        Budget = budget,
        FastModels = (fast ?? new[] { FastPaid }).Select(n => new ModelCatalogEntry(Provider, n)).ToArray()
    };

    // Daily $100, monthly $1000, warn 80%, hard 120%: daily hard cap $120, monthly $1200.
    private static readonly BudgetConfig Budget = new(DailyUsd: 100m, MonthlyUsd: 1000m, WarnPercent: 80, HardPercent: 120);

    private LlmGateway CreateGateway(LlmConfig config, ScriptedChatClient client, ModelAvailability? availability = null)
    {
        var clock = new FixedClock(Now);
        return new LlmGateway(
            config,
            new ModelCatalog(config),
            availability ?? new ModelAvailability(clock),
            new ChatClientProvider(new Dictionary<string, IChatClient> { [Provider] = client }),
            Db,
            clock,
            new ConcurrentCallGate(config.MaxConcurrentCalls),
            new BudgetGuard(config, Db, clock),
            NullLogger<LlmGateway>.Instance);
    }

    private static LlmRequest MakeRequest(long familyId, string? preferredModel = null) => new(
        FamilyId: familyId,
        BotId: 1,
        Tier: LlmConfig.SmartTier,
        PreferredModel: preferredModel,
        SystemPrompt: "system",
        Messages: new[] { new LlmMessage(LlmMessageRole.User, "hello") });

    private async Task SeedCostAsync(long familyId, decimal cost, DateTimeOffset createdAt)
    {
        Db.LlmCalls.Add(new LlmCall
        {
            FamilyId = familyId,
            BotId = 1,
            Tier = LlmConfig.SmartTier,
            Provider = Provider,
            Model = SmartPaid,
            Outcome = LlmCallOutcome.Ok,
            Cost = cost,
            DurationMs = 1,
            CreatedAt = createdAt
        });
        await Db.SaveChangesAsync();
    }

    private Task<List<LlmCall>> RowsAsync(long familyId) =>
        Db.LlmCalls.Where(c => c.FamilyId == familyId).OrderBy(c => c.Id).ToListAsync();

    // ---- Spend query ----------------------------------------------------------------------

    [Fact]
    public async Task Spend_is_summed_across_every_family_while_family_scoped_queries_stay_scoped()
    {
        await SeedCostAsync(familyId: 31, cost: 2m, Now.AddHours(-1));
        await SeedCostAsync(familyId: OtherFamily, cost: 3m, Now.AddHours(-2));

        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        var currentFamily = new CurrentFamily();
        currentFamily.Set(31);
        await using var scopedDb = new AssistantDbContext(options.Options, currentFamily);

        var status = await new BudgetGuard(MakeConfig(new[] { SmartPaid }, Budget), scopedDb, new FixedClock(Now)).EvaluateAsync(CancellationToken.None);

        status.ShouldNotBeNull();
        status.Daily.Spend.ShouldBe(5m);
        status.Monthly.Spend.ShouldBe(5m);
        // The same context's ordinary (filtered) query sees only its own family's spend.
        (await scopedDb.LlmCalls.SumAsync(c => c.Cost)).ShouldBe(2m);
    }

    [Fact]
    public async Task Spend_periods_are_the_UTC_day_and_the_calendar_month()
    {
        await SeedCostAsync(OtherFamily, 1m, new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));     // today, first instant
        await SeedCostAsync(OtherFamily, 10m, new DateTimeOffset(2026, 10, 14, 23, 59, 59, TimeSpan.Zero)); // yesterday
        await SeedCostAsync(OtherFamily, 100m, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));    // month, first instant
        await SeedCostAsync(OtherFamily, 1000m, new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero)); // last month

        var status = await new BudgetGuard(MakeConfig(new[] { SmartPaid }, Budget), Db, new FixedClock(Now)).EvaluateAsync(CancellationToken.None);

        status.ShouldNotBeNull();
        status.Daily.Spend.ShouldBe(1m);
        status.Monthly.Spend.ShouldBe(111m);
        status.Daily.PeriodEnd.ShouldBe(NextDay);
        status.Monthly.PeriodEnd.ShouldBe(NextMonth);
    }

    [Fact]
    public async Task Without_a_budget_nothing_is_restricted_whatever_the_spend()
    {
        await SeedCostAsync(OtherFamily, 100_000m, Now.AddHours(-1));
        var client = new ScriptedChatClient();
        client.EnqueueResponse("answer");
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid, Free }, budget: null), client);

        var result = await gateway.CompleteAsync(MakeRequest(32), CancellationToken.None);

        result.ModelName.ShouldBe(SmartPaid);
    }

    // ---- States and candidate filtering ---------------------------------------------------

    [Fact]
    public async Task Warn_state_leaves_candidates_unchanged()
    {
        await SeedCostAsync(OtherFamily, 80m, Now.AddHours(-1)); // exactly warn%
        var client = new ScriptedChatClient();
        client.EnqueueResponse("answer");
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid, FastPaid, Free }, Budget), client);

        var result = await gateway.CompleteAsync(MakeRequest(33), CancellationToken.None);

        result.ModelName.ShouldBe(SmartPaid);
    }

    [Fact]
    public async Task Just_below_100_percent_paid_models_are_still_used()
    {
        await SeedCostAsync(OtherFamily, 99.99m, Now.AddHours(-1));
        var client = new ScriptedChatClient();
        client.EnqueueResponse("answer");
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid, Free }, Budget), client);

        var result = await gateway.CompleteAsync(MakeRequest(34), CancellationToken.None);

        result.ModelName.ShouldBe(SmartPaid);
    }

    [Fact]
    public async Task Soft_state_restricts_to_fast_tier_and_zero_price_entries_in_chain_order()
    {
        // Another family's spend alone puts the platform at exactly 100% (Soft) -- proving the
        // gateway sees it at all (a per-family query would see $0 and never restrict).
        await SeedCostAsync(OtherFamily, 100m, Now.AddHours(-1));
        var client = new ScriptedChatClient();
        client.EnqueueResponse("answer");
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid, FastPaid, Free }, Budget), client);

        var result = await gateway.CompleteAsync(MakeRequest(35), CancellationToken.None);

        result.ModelName.ShouldBe(FastPaid);
        client.RequestedModelIds.ShouldBe(new[] { FastPaid });
    }

    [Fact]
    public async Task Soft_state_keeps_a_zero_price_entry_that_comes_first_in_the_chain()
    {
        await SeedCostAsync(OtherFamily, 100m, Now.AddHours(-1));
        var client = new ScriptedChatClient();
        client.EnqueueResponse("answer");
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid, Free, FastPaid }, Budget), client);

        var result = await gateway.CompleteAsync(MakeRequest(36), CancellationToken.None);

        result.ModelName.ShouldBe(Free);
    }

    [Fact]
    public async Task Soft_state_honours_a_chat_preference_only_when_it_is_allowed()
    {
        await SeedCostAsync(OtherFamily, 100m, Now.AddHours(-1));
        var config = MakeConfig(new[] { SmartPaid, Free, FastPaid }, Budget);

        var allowedClient = new ScriptedChatClient();
        allowedClient.EnqueueResponse("answer");
        (await CreateGateway(config, allowedClient).CompleteAsync(MakeRequest(37, preferredModel: FastPaid), CancellationToken.None))
            .ModelName.ShouldBe(FastPaid);

        var disallowedClient = new ScriptedChatClient();
        disallowedClient.EnqueueResponse("answer");
        (await CreateGateway(config, disallowedClient).CompleteAsync(MakeRequest(37, preferredModel: SmartPaid), CancellationToken.None))
            .ModelName.ShouldBe(Free);
    }

    [Fact]
    public async Task Soft_state_with_no_fast_or_zero_price_entry_is_BudgetExhausted_until_the_next_day()
    {
        await SeedCostAsync(OtherFamily, 100m, Now.AddHours(-1));
        var client = new ScriptedChatClient();
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid }, Budget), client);

        var result = await gateway.CompleteAsync(MakeRequest(38), CancellationToken.None);

        result.RefusalReason.ShouldBe(LlmRefusalReason.BudgetExhausted);
        result.RetryAt.ShouldBe(NextDay);
        client.RequestedModelIds.ShouldBeEmpty();
        (await RowsAsync(38)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Hard_state_allows_only_zero_price_entries()
    {
        await SeedCostAsync(OtherFamily, 120m, Now.AddHours(-1)); // exactly hard%
        var client = new ScriptedChatClient();
        client.EnqueueResponse("answer");
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid, FastPaid, Free }, Budget), client);

        var result = await gateway.CompleteAsync(MakeRequest(39), CancellationToken.None);

        result.ModelName.ShouldBe(Free);
        client.RequestedModelIds.ShouldBe(new[] { Free });
    }

    [Fact]
    public async Task Just_below_hard_percent_the_fast_tier_is_still_allowed()
    {
        await SeedCostAsync(OtherFamily, 118m, Now.AddHours(-1)); // Soft; estimate keeps it under $120
        var client = new ScriptedChatClient();
        client.EnqueueResponse("answer");
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid, FastPaid }, Budget), client);

        var result = await gateway.CompleteAsync(MakeRequest(40), CancellationToken.None);

        result.ModelName.ShouldBe(FastPaid);
    }

    [Fact]
    public async Task Hard_state_with_no_zero_price_candidate_returns_BudgetExhausted_and_writes_no_row()
    {
        await SeedCostAsync(OtherFamily, 120m, Now.AddHours(-1));
        var rowsBefore = await Db.LlmCalls.CountAsync();
        var client = new ScriptedChatClient();
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid, FastPaid }, Budget), client);

        var result = await gateway.CompleteAsync(MakeRequest(41), CancellationToken.None);

        result.IsAnswer.ShouldBeFalse();
        result.RefusalReason.ShouldBe(LlmRefusalReason.BudgetExhausted);
        result.RetryAt.ShouldBe(NextDay);
        client.RequestedModelIds.ShouldBeEmpty();
        (await Db.LlmCalls.CountAsync()).ShouldBe(rowsBefore);
    }

    [Fact]
    public async Task Monthly_hard_state_reports_the_month_reset()
    {
        // Monthly hard cap $1200 reached on an earlier day; today's spend is 0.
        await SeedCostAsync(OtherFamily, 1200m, new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid }, Budget), new ScriptedChatClient());

        var result = await gateway.CompleteAsync(MakeRequest(42), CancellationToken.None);

        result.RefusalReason.ShouldBe(LlmRefusalReason.BudgetExhausted);
        result.RetryAt.ShouldBe(NextMonth);
    }

    [Fact]
    public async Task A_budget_skip_wins_over_an_unavailable_remaining_candidate()
    {
        await SeedCostAsync(OtherFamily, 100m, Now.AddHours(-1)); // Soft: SmartPaid filtered out
        var availability = new ModelAvailability(new FixedClock(Now));
        availability.MarkUnavailable(Free, Now.AddMinutes(10));
        var client = new ScriptedChatClient();
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid, Free }, Budget), client, availability);

        var result = await gateway.CompleteAsync(MakeRequest(43), CancellationToken.None);

        result.RefusalReason.ShouldBe(LlmRefusalReason.BudgetExhausted);
        result.RetryAt.ShouldBe(NextDay);
        client.RequestedModelIds.ShouldBeEmpty();
    }

    // ---- Pre-call estimate ----------------------------------------------------------------

    [Fact]
    public async Task Estimate_that_would_cross_the_hard_cap_skips_the_candidate_even_in_normal_state()
    {
        // Normal state (70%). SmartPaid priced so its estimate is 1024 * 50000e-6 = $51.20:
        // 70 + 51.20 > 120 -> skipped (no row); FastPaid's tiny estimate fits and answers.
        await SeedCostAsync(OtherFamily, 70m, Now.AddHours(-1));
        var prices = new Dictionary<string, ModelPrice> { [SmartPaid] = new(0m, 50_000m), [FastPaid] = OneFive };
        var client = new ScriptedChatClient();
        client.EnqueueResponse("answer");
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid, FastPaid }, Budget, prices), client);

        var result = await gateway.CompleteAsync(MakeRequest(44), CancellationToken.None);

        result.ModelName.ShouldBe(FastPaid);
        client.RequestedModelIds.ShouldBe(new[] { FastPaid });
        (await RowsAsync(44)).Select(r => r.Model).ShouldBe(new[] { FastPaid });
    }

    [Theory]
    [InlineData("118.976", true)]  // 118.976 + 1.024 = exactly 120: not ABOVE the cap -> called
    [InlineData("118.977", false)] // 118.977 + 1.024 > 120 -> skipped
    public async Task Estimate_reaching_exactly_the_hard_cap_is_allowed_above_it_is_skipped(string spend, bool called)
    {
        // FastPaid estimate: 5.5 input tokens at $0 + 1024 output tokens at $1000/M = $1.024.
        await SeedCostAsync(OtherFamily, decimal.Parse(spend, System.Globalization.CultureInfo.InvariantCulture), Now.AddHours(-1));
        var prices = new Dictionary<string, ModelPrice> { [FastPaid] = new(0m, 1000m) };
        var client = new ScriptedChatClient();
        client.EnqueueResponse("answer");
        var gateway = CreateGateway(MakeConfig(new[] { FastPaid, Free }, Budget, prices), client);

        var result = await gateway.CompleteAsync(MakeRequest(45), CancellationToken.None);

        result.ModelName.ShouldBe(called ? FastPaid : Free);
    }

    [Fact]
    public async Task Estimate_against_the_monthly_cap_skips_every_paid_candidate_until_the_month_reset()
    {
        // Monthly spend $1199.999 (Soft monthly, daily 0): any paid estimate crosses $1200.
        await SeedCostAsync(OtherFamily, 1199.999m, new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var client = new ScriptedChatClient();
        var gateway = CreateGateway(MakeConfig(new[] { FastPaid }, Budget), client);

        var result = await gateway.CompleteAsync(MakeRequest(46), CancellationToken.None);

        result.RefusalReason.ShouldBe(LlmRefusalReason.BudgetExhausted);
        result.RetryAt.ShouldBe(NextMonth);
        client.RequestedModelIds.ShouldBeEmpty();
        (await RowsAsync(46)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Estimate_counts_the_whole_context_not_just_the_last_message()
    {
        // Two 1,000,000-char earlier messages at $1/M input: estimate >= (6 + 2,000,005) / 2 * 1e-6
        // = ~$1.00 -- enough to cross the cap from $119.5, while the last message alone would not.
        await SeedCostAsync(OtherFamily, 119.5m, Now.AddHours(-1));
        var prices = new Dictionary<string, ModelPrice> { [FastPaid] = new(1m, 0m) };
        var client = new ScriptedChatClient();
        client.EnqueueResponse("answer");
        var gateway = CreateGateway(MakeConfig(new[] { FastPaid, Free }, Budget, prices), client);
        var big = new string('x', 1_000_000);
        var request = MakeRequest(47) with
        {
            Messages = new[]
            {
                new LlmMessage(LlmMessageRole.User, big),
                new LlmMessage(LlmMessageRole.Assistant, big),
                new LlmMessage(LlmMessageRole.User, "hello")
            }
        };

        var result = await gateway.CompleteAsync(request, CancellationToken.None);

        result.ModelName.ShouldBe(Free);
    }

    // ---- Cost recording -------------------------------------------------------------------

    [Fact]
    public async Task Ok_call_records_usage_times_price()
    {
        var client = new ScriptedChatClient();
        // 1000 input at $1/M = 0.001; 2000 output at $5/M = 0.01.
        client.EnqueueResponse("answer", inputTokens: 1000, outputTokens: 2000);
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid }, Budget), client);

        await gateway.CompleteAsync(MakeRequest(48), CancellationToken.None);

        (await RowsAsync(48)).ShouldHaveSingleItem().Cost.ShouldBe(0.011m);
    }

    [Fact]
    public async Task Zero_price_call_records_cost_zero_and_keeps_the_reported_cost()
    {
        var client = new ScriptedChatClient();
        client.EnqueueResponseWithCost("answer", 0.25m);
        var gateway = CreateGateway(MakeConfig(new[] { Free }, Budget), client);

        await gateway.CompleteAsync(MakeRequest(49), CancellationToken.None);

        var row = (await RowsAsync(49)).ShouldHaveSingleItem();
        row.Cost.ShouldBe(0m);
        row.ReportedCost.ShouldBe(0.25m);
    }

    [Fact]
    public async Task Limit_reached_records_cost_zero()
    {
        var client = new ScriptedChatClient();
        client.EnqueueException(new ModelLimitReachedException("limit", LlmLimitScope.Model));
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid }, Budget), client);

        await gateway.CompleteAsync(MakeRequest(50), CancellationToken.None);

        var row = (await RowsAsync(50)).ShouldHaveSingleItem();
        row.Outcome.ShouldBe(LlmCallOutcome.LimitReached);
        row.Cost.ShouldBe(0m);
    }

    [Fact]
    public async Task A_paid_Timeout_records_the_pre_call_estimate_as_cost()
    {
        var client = new ScriptedChatClient();
        client.EnqueueHang();
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid }, Budget, callTimeoutSeconds: 1), client);

        var result = await gateway.CompleteAsync(MakeRequest(51), CancellationToken.None);

        result.RefusalReason.ShouldBe(LlmRefusalReason.Failed);
        var row = (await RowsAsync(51)).ShouldHaveSingleItem();
        row.Outcome.ShouldBe(LlmCallOutcome.Timeout);
        row.Cost.ShouldBe(SmartPaidEstimate);
    }

    [Fact]
    public async Task A_paid_failure_without_usage_records_the_pre_call_estimate_as_cost()
    {
        var client = new ScriptedChatClient();
        client.EnqueueException(new InvalidOperationException("synthetic provider failure"));
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid }, Budget), client);

        await gateway.CompleteAsync(MakeRequest(52), CancellationToken.None);

        var row = (await RowsAsync(52)).ShouldHaveSingleItem();
        row.Outcome.ShouldBe(LlmCallOutcome.Failed);
        row.Cost.ShouldBe(SmartPaidEstimate);
    }

    [Fact]
    public async Task An_empty_answer_with_usage_records_usage_times_price()
    {
        var client = new ScriptedChatClient();
        client.EnqueueResponse("  ", inputTokens: 1000, outputTokens: 2000);
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid }, Budget), client);

        await gateway.CompleteAsync(MakeRequest(53), CancellationToken.None);

        var row = (await RowsAsync(53)).ShouldHaveSingleItem();
        row.Outcome.ShouldBe(LlmCallOutcome.Failed);
        row.Cost.ShouldBe(0.011m);
    }

    [Fact]
    public async Task A_zero_price_timeout_records_cost_zero()
    {
        var client = new ScriptedChatClient();
        client.EnqueueHang();
        var gateway = CreateGateway(MakeConfig(new[] { Free }, Budget, callTimeoutSeconds: 1), client);

        await gateway.CompleteAsync(MakeRequest(54), CancellationToken.None);

        (await RowsAsync(54)).ShouldHaveSingleItem().Cost.ShouldBe(0m);
    }
}
