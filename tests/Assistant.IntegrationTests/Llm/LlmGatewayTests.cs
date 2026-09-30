using Assistant.Application.Common;
using Assistant.Application.Llm;
using Assistant.Domain.Llm;
using Assistant.Infrastructure.Llm;
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
        IReadOnlyList<ModelCatalogEntry>? models = null) => new()
    {
        Models = models ?? new[] { new ModelCatalogEntry(Provider, ModelA), new ModelCatalogEntry(Provider, ModelB) },
        CallsPerMinute = callsPerMinute,
        CallsPerDay = callsPerDay,
        MaxContextMessages = 30,
        MaxInputChars = 8000,
        MaxOutputTokens = 1024,
        CallTimeoutSeconds = 30,
        MaxConcurrentCalls = 4,
        ModelCooldownMinutes = 15
    };

    private LlmGateway CreateGateway(LlmConfig config, ScriptedChatClient client, ModelAvailability? availability = null) =>
        new(
            config,
            new ModelCatalog(config),
            availability ?? new ModelAvailability(new SystemClock()),
            new ChatClientProvider(new Dictionary<string, IChatClient> { [Provider] = client }),
            Db,
            new SystemClock(),
            new ConcurrentCallGate(config.MaxConcurrentCalls),
            NullLogger<LlmGateway>.Instance);

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
}
