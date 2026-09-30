using Assistant.Application.Common;
using Assistant.Application.Llm;
using Assistant.Domain.Llm;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Host;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Assistant.IntegrationTests.Llm;

public class LlmGatewayTests : IntegrationTestBase
{
    private const string Provider = "fake";
    private const string ModelA = "model-a";
    private const string ModelB = "model-b";

    private static LlmConfig MakeConfig(
        int callsPerMinute = 100,
        int callsPerDay = 1000,
        IReadOnlyList<ModelCatalogEntry>? models = null,
        int callTimeoutSeconds = 30,
        int maxConcurrentCalls = 4,
        int modelCooldownMinutes = 15) => new()
    {
        Models = models ?? new[] { new ModelCatalogEntry(Provider, ModelA), new ModelCatalogEntry(Provider, ModelB) },
        CallsPerMinute = callsPerMinute,
        CallsPerDay = callsPerDay,
        MaxContextMessages = 30,
        MaxInputChars = 8000,
        MaxOutputTokens = 1024,
        CallTimeoutSeconds = callTimeoutSeconds,
        MaxConcurrentCalls = maxConcurrentCalls,
        ModelCooldownMinutes = modelCooldownMinutes
    };

    private LlmGateway CreateGateway(
        LlmConfig config,
        ScriptedChatClient client,
        ModelAvailability? availability = null,
        Assistant.Application.Common.IClock? clock = null,
        AssistantDbContext? db = null,
        ConcurrentCallGate? concurrencyGate = null) =>
        new(
            config,
            new ModelCatalog(config),
            availability ?? new ModelAvailability(clock ?? new SystemClock()),
            new ChatClientProvider(new Dictionary<string, IChatClient> { [Provider] = client }),
            db ?? Db,
            clock ?? new SystemClock(),
            concurrencyGate ?? new ConcurrentCallGate(config.MaxConcurrentCalls),
            NullLogger<LlmGateway>.Instance);

    private LlmGateway CreateGatewayWithProviders(
        LlmConfig config,
        IReadOnlyDictionary<string, IChatClient> clientsByProvider,
        ModelAvailability? availability = null,
        Assistant.Application.Common.IClock? clock = null) =>
        new(
            config,
            new ModelCatalog(config),
            availability ?? new ModelAvailability(clock ?? new SystemClock()),
            new ChatClientProvider(clientsByProvider),
            Db,
            clock ?? new SystemClock(),
            new ConcurrentCallGate(config.MaxConcurrentCalls),
            NullLogger<LlmGateway>.Instance);

    /// <summary>An <see cref="AssistantDbContext"/> whose <c>SaveChangesAsync</c> always fails, to
    /// exercise the gateway's "recording failed, answer must still be returned" path without a real
    /// transient-DB-failure scenario.</summary>
    private sealed class FailingSaveDbContext : AssistantDbContext
    {
        public FailingSaveDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<AssistantDbContext> options) : base(options)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated DB failure recording the LLM call attempt.");
    }

    private static LlmRequest MakeRequest(long familyId = 1, string? preferredModel = null) => new(
        FamilyId: familyId,
        BotId: 1,
        Tier: LlmConfig.SmartTier,
        PreferredModel: preferredModel,
        SystemPrompt: "system",
        Messages: new[] { new LlmMessage(LlmMessageRole.User, "hello") });

    private async Task SeedCallsAsync(long familyId, int count, DateTimeOffset createdAt)
    {
        for (var i = 0; i < count; i++)
        {
            Db.LlmCalls.Add(new LlmCall
            {
                FamilyId = familyId,
                BotId = 1,
                Tier = LlmConfig.SmartTier,
                Provider = Provider,
                Model = ModelA,
                Outcome = LlmCallOutcome.Ok,
                DurationMs = 1,
                CreatedAt = createdAt
            });
        }

        await Db.SaveChangesAsync();
    }

    [Fact]
    public async Task First_candidate_success_returns_answer_and_records_one_ok_row()
    {
        var config = MakeConfig();
        var client = new ScriptedChatClient();
        client.EnqueueResponse("hi there", inputTokens: 10, outputTokens: 20);
        var gateway = CreateGateway(config, client);

        var result = await gateway.CompleteAsync(MakeRequest(familyId: 11), CancellationToken.None);

        result.IsAnswer.ShouldBeTrue();
        result.Text.ShouldBe("hi there");
        result.ModelName.ShouldBe(ModelA);

        var rows = await Db.LlmCalls.Where(c => c.FamilyId == 11).ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].Outcome.ShouldBe(LlmCallOutcome.Ok);
        rows[0].InputTokens.ShouldBe(10);
        rows[0].OutputTokens.ShouldBe(20);
    }

    [Fact]
    public async Task Limit_on_first_candidate_falls_back_to_second_and_marks_first_unavailable()
    {
        var config = MakeConfig();
        var client = new ScriptedChatClient();
        client.EnqueueException(new ModelLimitReachedException("limit", LlmLimitScope.Model));
        client.EnqueueResponse("fallback answer");
        var availability = new ModelAvailability(new SystemClock());
        var gateway = CreateGateway(config, client, availability);

        var result = await gateway.CompleteAsync(MakeRequest(familyId: 12), CancellationToken.None);

        result.IsAnswer.ShouldBeTrue();
        result.Text.ShouldBe("fallback answer");
        result.ModelName.ShouldBe(ModelB);

        var rows = await Db.LlmCalls.Where(c => c.FamilyId == 12).OrderBy(c => c.Id).ToListAsync();
        rows.Count.ShouldBe(2);
        rows[0].Outcome.ShouldBe(LlmCallOutcome.LimitReached);
        rows[0].Model.ShouldBe(ModelA);
        rows[1].Outcome.ShouldBe(LlmCallOutcome.Ok);
        rows[1].Model.ShouldBe(ModelB);

        availability.IsAvailable(ModelA).ShouldBeFalse();
    }

    [Fact]
    public async Task Every_candidate_limited_returns_all_models_unavailable_with_earliest_retry()
    {
        var config = MakeConfig();
        var client = new ScriptedChatClient();
        var now = DateTimeOffset.UtcNow;
        client.EnqueueException(new ModelLimitReachedException("limit", LlmLimitScope.Model, now.AddMinutes(30)));
        client.EnqueueException(new ModelLimitReachedException("limit", LlmLimitScope.Model, now.AddMinutes(5)));
        var gateway = CreateGateway(config, client);

        var result = await gateway.CompleteAsync(MakeRequest(familyId: 13), CancellationToken.None);

        result.IsAnswer.ShouldBeFalse();
        result.RefusalReason.ShouldBe(LlmRefusalReason.AllModelsUnavailable);
        result.RetryAt.ShouldNotBeNull();
        result.RetryAt!.Value.ShouldBe(now.AddMinutes(5), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Non_limit_exception_fails_without_trying_next_candidate()
    {
        var config = MakeConfig();
        var client = new ScriptedChatClient();
        client.EnqueueException(new InvalidOperationException("boom"));
        client.EnqueueResponse("never used");
        var gateway = CreateGateway(config, client);

        var result = await gateway.CompleteAsync(MakeRequest(familyId: 14), CancellationToken.None);

        result.IsAnswer.ShouldBeFalse();
        result.RefusalReason.ShouldBe(LlmRefusalReason.Failed);

        var rows = await Db.LlmCalls.Where(c => c.FamilyId == 14).ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].Outcome.ShouldBe(LlmCallOutcome.Failed);
        rows[0].Model.ShouldBe(ModelA);
    }

    [Fact]
    public async Task Per_minute_cap_reached_refuses_rate_limited_and_writes_no_row()
    {
        var config = MakeConfig(callsPerMinute: 3);
        await SeedCallsAsync(familyId: 15, count: 3, createdAt: DateTimeOffset.UtcNow);
        var client = new ScriptedChatClient();
        client.EnqueueResponse("should not be used");
        var gateway = CreateGateway(config, client);

        var result = await gateway.CompleteAsync(MakeRequest(familyId: 15), CancellationToken.None);

        result.IsAnswer.ShouldBeFalse();
        result.RefusalReason.ShouldBe(LlmRefusalReason.RateLimited);

        var rows = await Db.LlmCalls.Where(c => c.FamilyId == 15).ToListAsync();
        rows.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Per_day_cap_reached_refuses_daily_cap_reached_and_writes_no_row()
    {
        var config = MakeConfig(callsPerMinute: 1000, callsPerDay: 2);
        var earlierToday = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).AddHours(1);
        await SeedCallsAsync(familyId: 16, count: 2, createdAt: earlierToday);
        var client = new ScriptedChatClient();
        client.EnqueueResponse("should not be used");
        var gateway = CreateGateway(config, client);

        var result = await gateway.CompleteAsync(MakeRequest(familyId: 16), CancellationToken.None);

        result.IsAnswer.ShouldBeFalse();
        result.RefusalReason.ShouldBe(LlmRefusalReason.DailyCapReached);

        var rows = await Db.LlmCalls.Where(c => c.FamilyId == 16).ToListAsync();
        rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Preferred_model_is_tried_first()
    {
        var config = MakeConfig();
        var client = new ScriptedChatClient();
        client.EnqueueResponse("answer from preferred");
        var gateway = CreateGateway(config, client);

        var result = await gateway.CompleteAsync(MakeRequest(familyId: 17, preferredModel: ModelB), CancellationToken.None);

        result.ModelName.ShouldBe(ModelB);
        client.RequestedModelIds[0].ShouldBe(ModelB);
    }

    [Fact]
    public async Task Rate_guard_is_isolated_per_family()
    {
        var config = MakeConfig(callsPerMinute: 1);
        await SeedCallsAsync(familyId: 18, count: 1, createdAt: DateTimeOffset.UtcNow);
        var client = new ScriptedChatClient();
        client.EnqueueResponse("answer for family 19");
        var gateway = CreateGateway(config, client);

        var result = await gateway.CompleteAsync(MakeRequest(familyId: 19), CancellationToken.None);

        result.IsAnswer.ShouldBeTrue();
        result.Text.ShouldBe("answer for family 19");
    }

    [Fact]
    public async Task Provider_scope_limit_marks_every_entry_of_that_provider()
    {
        const string providerP1 = "p1";
        const string providerP2 = "p2";
        const string modelA = "model-a";
        const string modelB = "model-b";
        const string modelC = "model-c";

        var config = MakeConfig(models: new[]
        {
            new ModelCatalogEntry(providerP1, modelA),
            new ModelCatalogEntry(providerP1, modelB),
            new ModelCatalogEntry(providerP2, modelC)
        });

        var clientP1 = new ScriptedChatClient();
        clientP1.EnqueueException(new ModelLimitReachedException("account limit", LlmLimitScope.Provider));
        var clientP2 = new ScriptedChatClient();
        clientP2.EnqueueResponse("answer from C");

        var availability = new ModelAvailability(new SystemClock());
        var gateway = CreateGatewayWithProviders(
            config,
            new Dictionary<string, IChatClient> { [providerP1] = clientP1, [providerP2] = clientP2 },
            availability);

        var result = await gateway.CompleteAsync(MakeRequest(familyId: 20), CancellationToken.None);

        result.IsAnswer.ShouldBeTrue();
        result.Text.ShouldBe("answer from C");
        result.ModelName.ShouldBe(modelC);

        // Only A was ever called on the p1 client -- B must have been skipped once the provider-wide
        // mark took effect, never reaching the client at all.
        clientP1.RequestedModelIds.ShouldBe(new[] { modelA });
        clientP2.RequestedModelIds.ShouldBe(new[] { modelC });

        availability.IsAvailable(modelA).ShouldBeFalse();
        availability.IsAvailable(modelB).ShouldBeFalse();
        availability.IsAvailable(modelC).ShouldBeTrue();
    }

    [Fact]
    public async Task Unparsable_retry_time_falls_back_to_now_plus_cooldown()
    {
        var clock = new TestClock();
        var config = MakeConfig(models: new[] { new ModelCatalogEntry(Provider, ModelA) });
        var client = new ScriptedChatClient();
        client.EnqueueException(new ModelLimitReachedException("limit, no retry time given", LlmLimitScope.Model, retryAt: null));
        var gateway = CreateGateway(config, client, clock: clock);

        var before = clock.UtcNow;
        var result = await gateway.CompleteAsync(MakeRequest(familyId: 21), CancellationToken.None);

        result.IsAnswer.ShouldBeFalse();
        result.RefusalReason.ShouldBe(LlmRefusalReason.AllModelsUnavailable);
        result.RetryAt.ShouldNotBeNull();
        result.RetryAt!.Value.ShouldBe(before.AddMinutes(config.ModelCooldownMinutes), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Mark_expires_after_the_clock_advances_past_it()
    {
        var clock = new TestClock();
        var config = MakeConfig(models: new[] { new ModelCatalogEntry(Provider, ModelA) });
        var availability = new ModelAvailability(clock);
        var client = new ScriptedChatClient();
        client.EnqueueException(new ModelLimitReachedException("limit", LlmLimitScope.Model, retryAt: clock.UtcNow.AddMinutes(1)));
        var gateway = CreateGateway(config, client, availability, clock);

        var first = await gateway.CompleteAsync(MakeRequest(familyId: 22), CancellationToken.None);
        first.IsAnswer.ShouldBeFalse();
        availability.IsAvailable(ModelA).ShouldBeFalse();

        clock.Advance(TimeSpan.FromMinutes(2));
        availability.IsAvailable(ModelA).ShouldBeTrue();

        client.EnqueueResponse("back online");
        var second = await gateway.CompleteAsync(MakeRequest(familyId: 22), CancellationToken.None);

        second.IsAnswer.ShouldBeTrue();
        second.Text.ShouldBe("back online");
    }

    [Fact]
    public async Task Semaphore_is_released_after_an_exception_so_the_next_call_can_proceed()
    {
        var config = MakeConfig(models: new[] { new ModelCatalogEntry(Provider, ModelA) }, maxConcurrentCalls: 1);
        var client = new ScriptedChatClient();
        client.EnqueueException(new InvalidOperationException("boom"));
        client.EnqueueResponse("second call answer");
        var gate = new ConcurrentCallGate(config.MaxConcurrentCalls);
        var gateway = CreateGateway(config, client, concurrencyGate: gate);

        var failing = await gateway.CompleteAsync(MakeRequest(familyId: 23), CancellationToken.None);
        failing.IsAnswer.ShouldBeFalse();

        var succeeding = await gateway.CompleteAsync(MakeRequest(familyId: 23), CancellationToken.None);
        succeeding.IsAnswer.ShouldBeTrue();
        succeeding.Text.ShouldBe("second call answer");
    }

    [Fact]
    public async Task Waiting_for_the_concurrency_slot_beyond_the_call_timeout_is_rate_limited()
    {
        var gate = new ConcurrentCallGate(maxConcurrentCalls: 1);

        // The holding call gets a long call timeout of its own (it is ended explicitly, by
        // cancelling its own token, once the assertions below are done) so it cannot race the
        // assertion by releasing the slot on its own timeout first.
        var holdingConfig = MakeConfig(models: new[] { new ModelCatalogEntry(Provider, ModelA) }, maxConcurrentCalls: 1, callTimeoutSeconds: 30);
        var holdingClient = new ScriptedChatClient();
        holdingClient.EnqueueHang();
        var holdingGateway = CreateGateway(holdingConfig, holdingClient, concurrencyGate: gate);
        using var holdingCts = new CancellationTokenSource();
        var holdingCall = holdingGateway.CompleteAsync(MakeRequest(familyId: 24), holdingCts.Token);

        // Give the holding call a moment to actually take the one slot before the second call tries.
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        var waitingConfig = MakeConfig(models: new[] { new ModelCatalogEntry(Provider, ModelA) }, maxConcurrentCalls: 1, callTimeoutSeconds: 1);
        var waitingClient = new ScriptedChatClient();
        waitingClient.EnqueueResponse("never reached");
        var waitingGateway = CreateGateway(waitingConfig, waitingClient, concurrencyGate: gate);

        var waitingResult = await waitingGateway.CompleteAsync(MakeRequest(familyId: 24), CancellationToken.None);

        waitingResult.IsAnswer.ShouldBeFalse();
        waitingResult.RefusalReason.ShouldBe(LlmRefusalReason.RateLimited);
        waitingClient.RequestedModelIds.ShouldBeEmpty();

        // Release the held slot deterministically (rather than racing its own 30s call timeout) so
        // the test doesn't leave a dangling task.
        holdingCts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await holdingCall);
    }

    [Fact]
    public async Task Own_call_timeout_records_a_timeout_row_and_returns_failed()
    {
        var config = MakeConfig(models: new[] { new ModelCatalogEntry(Provider, ModelA) }, callTimeoutSeconds: 1);
        var client = new ScriptedChatClient();
        client.EnqueueDelayedResponse(TimeSpan.FromSeconds(5), "too slow");
        var gateway = CreateGateway(config, client);

        var result = await gateway.CompleteAsync(MakeRequest(familyId: 25), CancellationToken.None);

        result.IsAnswer.ShouldBeFalse();
        result.RefusalReason.ShouldBe(LlmRefusalReason.Failed);

        var rows = await Db.LlmCalls.Where(c => c.FamilyId == 25).ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].Outcome.ShouldBe(LlmCallOutcome.Timeout);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_writes_no_row()
    {
        var config = MakeConfig(models: new[] { new ModelCatalogEntry(Provider, ModelA) });
        var client = new ScriptedChatClient();
        client.EnqueueHang();
        var gateway = CreateGateway(config, client);

        using var cts = new CancellationTokenSource();
        var callTask = gateway.CompleteAsync(MakeRequest(familyId: 26), cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await callTask);

        var rows = await Db.LlmCalls.Where(c => c.FamilyId == 26).ToListAsync();
        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unavailable_candidate_is_skipped_without_calling_its_client()
    {
        var config = MakeConfig();
        var availability = new ModelAvailability(new SystemClock());
        availability.MarkUnavailable(ModelA, DateTimeOffset.UtcNow.AddMinutes(30));
        var client = new ScriptedChatClient();
        client.EnqueueResponse("from B only");
        var gateway = CreateGateway(config, client, availability);

        var result = await gateway.CompleteAsync(MakeRequest(familyId: 27), CancellationToken.None);

        result.IsAnswer.ShouldBeTrue();
        result.ModelName.ShouldBe(ModelB);
        client.RequestedModelIds.ShouldBe(new[] { ModelB });
    }

    [Fact]
    public async Task Empty_or_whitespace_answer_is_treated_as_failed()
    {
        var config = MakeConfig(models: new[] { new ModelCatalogEntry(Provider, ModelA) });
        var client = new ScriptedChatClient();
        client.EnqueueResponse("   ");
        var gateway = CreateGateway(config, client);

        var result = await gateway.CompleteAsync(MakeRequest(familyId: 28), CancellationToken.None);

        result.IsAnswer.ShouldBeFalse();
        result.RefusalReason.ShouldBe(LlmRefusalReason.Failed);

        var rows = await Db.LlmCalls.Where(c => c.FamilyId == 28).ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].Outcome.ShouldBe(LlmCallOutcome.Failed);
    }

    [Fact]
    public async Task Db_failure_recording_a_successful_answer_still_returns_answered()
    {
        var config = MakeConfig(models: new[] { new ModelCatalogEntry(Provider, ModelA) });
        var client = new ScriptedChatClient();
        client.EnqueueResponse("answer despite db failure");

        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        await using var failingDb = new FailingSaveDbContext(options.Options);

        var gateway = CreateGateway(config, client, db: failingDb);

        var result = await gateway.CompleteAsync(MakeRequest(familyId: 29), CancellationToken.None);

        result.IsAnswer.ShouldBeTrue();
        result.Text.ShouldBe("answer despite db failure");

        // The real Db (this test's own context) confirms nothing actually got persisted -- the save
        // failed, as scripted -- while the gateway still returned the answer to the caller.
        var rows = await Db.LlmCalls.Where(c => c.FamilyId == 29).ToListAsync();
        rows.ShouldBeEmpty();
    }
}
