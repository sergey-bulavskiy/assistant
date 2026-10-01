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

    // This file's own concern is candidate filtering/cost recording, not notices (those are
    // BudgetNoticeSenderTests' job) -- a no-op fake keeps these tests decoupled from the sender's
    // own DB/Telegram wiring even though Budget is non-null here, so LlmGateway's
    // "budgetStatus is not null" guard would otherwise call the real sender on every test.
    private sealed class NoopBudgetNoticeSender : IBudgetNoticeSender
    {
        public Task NotifyAsync(BudgetStatus status, CancellationToken cancellationToken) => Task.CompletedTask;
    }

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
            new NoopBudgetNoticeSender(),
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
    public async Task December_rolls_over_into_a_january_month_reset()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero));
        var status = await new BudgetGuard(MakeConfig(new[] { SmartPaid }, Budget), Db, clock).EvaluateAsync(CancellationToken.None);

        status.ShouldNotBeNull();
        status.Monthly.PeriodStart.ShouldBe(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        status.Monthly.PeriodEnd.ShouldBe(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
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
    public async Task Day_hard_month_soft_with_no_fast_models_reports_the_month_reset()
    {
        // Day hard (125/100 = 125% >= 120%) but month only soft (1025/1000 = 102.5%): once the day
        // resets, the month's own state (Soft) is still in force, and with no fast tier configured
        // Soft leaves no candidate either -- so the day's reset alone would not actually help; the
        // real reset is the month's.
        await SeedCostAsync(OtherFamily, 900m, new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        await SeedCostAsync(OtherFamily, 125m, Now.AddHours(-1));
        var client = new ScriptedChatClient();
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid }, Budget, fast: Array.Empty<string>()), client);

        var result = await gateway.CompleteAsync(MakeRequest(60), CancellationToken.None);

        result.RefusalReason.ShouldBe(LlmRefusalReason.BudgetExhausted);
        result.RetryAt.ShouldBe(NextMonth);
        client.RequestedModelIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Day_hard_month_soft_with_a_fast_model_reports_the_day_reset()
    {
        // Same spend split as above, but a fast model IS configured: once the day resets, the
        // month's own Soft state still allows the fast tier, so the day's reset genuinely helps.
        await SeedCostAsync(OtherFamily, 900m, new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        await SeedCostAsync(OtherFamily, 125m, Now.AddHours(-1));
        var client = new ScriptedChatClient();
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid, FastPaid }, Budget), client);

        var result = await gateway.CompleteAsync(MakeRequest(61), CancellationToken.None);

        result.RefusalReason.ShouldBe(LlmRefusalReason.BudgetExhausted);
        result.RetryAt.ShouldBe(NextDay);
        client.RequestedModelIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_remaining_unavailable_candidate_wins_over_a_budget_skip()
    {
        // SmartPaid is filtered out by Soft; Free is budget-allowed (zero price) but only
        // temporarily unavailable -- that must surface as AllModelsUnavailable with Free's own
        // retry time, not as BudgetExhausted, since Free could still answer once it comes back.
        await SeedCostAsync(OtherFamily, 100m, Now.AddHours(-1)); // Soft: SmartPaid filtered out
        var availability = new ModelAvailability(new FixedClock(Now));
        availability.MarkUnavailable(Free, Now.AddMinutes(10));
        var client = new ScriptedChatClient();
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid, Free }, Budget), client, availability);

        var result = await gateway.CompleteAsync(MakeRequest(43), CancellationToken.None);

        result.RefusalReason.ShouldBe(LlmRefusalReason.AllModelsUnavailable);
        result.RetryAt.ShouldBe(Now.AddMinutes(10));
        client.RequestedModelIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Paid_skipped_on_estimate_and_the_free_model_on_a_two_hour_limit_is_AllModelsUnavailable()
    {
        // SmartPaid's estimate alone would cross the hard cap (skipped, no row); Free is budget
        // allowed but under a 2h limit mark -- still only temporarily unavailable, so the answer
        // must be AllModelsUnavailable with the 2h retry, not BudgetExhausted.
        await SeedCostAsync(OtherFamily, 70m, Now.AddHours(-1));
        var prices = new Dictionary<string, ModelPrice> { [SmartPaid] = new(0m, 50_000m) };
        var availability = new ModelAvailability(new FixedClock(Now));
        var twoHourRetry = Now.AddHours(2);
        availability.MarkUnavailable(Free, twoHourRetry);
        var client = new ScriptedChatClient();
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid, Free }, Budget, prices), client, availability);

        var result = await gateway.CompleteAsync(MakeRequest(62), CancellationToken.None);

        result.RefusalReason.ShouldBe(LlmRefusalReason.AllModelsUnavailable);
        result.RetryAt.ShouldBe(twoHourRetry);
        client.RequestedModelIds.ShouldBeEmpty();
        (await RowsAsync(62)).ShouldBeEmpty();
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
    public async Task Missing_input_token_count_falls_back_to_the_estimated_input_tokens()
    {
        // Output reported (2000 @ $5/M = 0.01); input missing -> falls back to the 5.5 estimated
        // input tokens (not 0): 5.5/1e6 * $1/M = 0.0000055, total 0.0100055 -> rounds up to 0.0101.
        var client = new ScriptedChatClient();
        client.EnqueueResponsePartialUsage("answer", inputTokens: null, outputTokens: 2000);
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid }, Budget), client);

        await gateway.CompleteAsync(MakeRequest(57), CancellationToken.None);

        (await RowsAsync(57)).ShouldHaveSingleItem().Cost.ShouldBe(0.0101m);
    }

    [Fact]
    public async Task Missing_output_token_count_is_billed_as_zero_output()
    {
        // Input reported (1000 @ $1/M = 0.001); output missing -> billed as 0 output, not the
        // configured max -- the call already returned a response, so there is no metered output.
        var client = new ScriptedChatClient();
        client.EnqueueResponsePartialUsage("answer", inputTokens: 1000, outputTokens: null);
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid }, Budget), client);

        await gateway.CompleteAsync(MakeRequest(58), CancellationToken.None);

        (await RowsAsync(58)).ShouldHaveSingleItem().Cost.ShouldBe(0.0010m);
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

    [Fact]
    public async Task Caller_cancellation_during_a_paid_call_records_the_estimate_and_rethrows()
    {
        // The caller (not the gateway's own per-call timeout) cancels mid-call: for a paid entry
        // the provider may already be doing billable work, so the attempt is still charged the
        // pre-call estimate -- using CancellationToken.None so the cancelled caller token doesn't
        // also abort the bookkeeping write -- before the cancellation is rethrown to the caller.
        var client = new ScriptedChatClient();
        client.EnqueueHang();
        var gateway = CreateGateway(MakeConfig(new[] { SmartPaid }, Budget), client);

        using var cts = new CancellationTokenSource();
        var callTask = gateway.CompleteAsync(MakeRequest(55), cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await callTask);

        var row = (await RowsAsync(55)).ShouldHaveSingleItem();
        row.Outcome.ShouldBe(LlmCallOutcome.Failed);
        row.Cost.ShouldBe(SmartPaidEstimate);
    }

    [Fact]
    public async Task Caller_cancellation_during_a_zero_price_call_writes_no_row()
    {
        var client = new ScriptedChatClient();
        client.EnqueueHang();
        var gateway = CreateGateway(MakeConfig(new[] { Free }, Budget), client);

        using var cts = new CancellationTokenSource();
        var callTask = gateway.CompleteAsync(MakeRequest(56), cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await callTask);

        (await RowsAsync(56)).ShouldBeEmpty();
    }
}
