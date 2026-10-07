using Assistant.Application.Common;
using Assistant.Application.Llm;
using Assistant.Domain.Llm;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;

namespace Assistant.IntegrationTests.Llm;

public sealed class ImageGatewayTests : IntegrationTestBase
{
    private sealed class ImageClient : IChatClient, IImageChatClient
    {
        public bool SupportsImages { get; init; } = true;
        public int Calls { get; private set; }
        public Func<IEnumerable<ChatMessage>, CancellationToken, Task>? BeforeResponse { get; set; }
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (BeforeResponse is not null) await BeforeResponse(messages, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthetic extraction"))
            { Usage = new UsageDetails { InputTokenCount = 17, OutputTokenCount = 3 } };
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NoopNotice : IBudgetNoticeDispatcher
    { public Task Dispatch() => Task.CompletedTask; }

    private sealed class FailingAdmission(DbContextOptions<AssistantDbContext> options) : AssistantDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("synthetic admission failure");
    }

    private sealed class FailFinalization : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            command.CommandText.StartsWith("UPDATE llm_calls", StringComparison.Ordinal)
                ? throw new InvalidOperationException("synthetic finalization failure")
                : base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private static LlmRequest Request(Guid? key = null) => new(111, 222, LlmConfig.FastTier, null,
        "synthetic image role", [new(LlmMessageRole.User, "synthetic caption")], 333, 444, 555)
    {
        Images = [new(new byte[] {137,80,78,71,13,10,26,10,42}, "image/png")],
        AttemptKey = key ?? Guid.NewGuid()
    };

    private LlmGateway Gateway(ImageClient client, AssistantDbContext? db = null, int minuteCap = 100)
    {
        var config = new LlmConfig
        {
            Models = [new("codex-cli", "gpt-6.1-sol"), new("fake", "unproved-model")],
            FastModels = [new("codex-cli", "gpt-6.1-sol")], Prices = new Dictionary<string, ModelPrice>(),
            Budget = null, CallsPerMinute = minuteCap, CallsPerDay = 100, MaxContextMessages = 2, MaxInputChars = 1000,
            MaxOutputTokens = 100, CallTimeoutSeconds = 30, MaxConcurrentCalls = 1, ModelCooldownMinutes = 10
        };
        var clock = new SystemClock();
        return new(config, new ModelCatalog(config), new ModelAvailability(clock),
            new ChatClientProvider(new Dictionary<string, IChatClient> { ["codex-cli"] = client, ["fake"] = client }),
            db ?? Db, clock, new ConcurrentCallGate(1), new NullBudgetGuard(), new NoopNotice(),
            NullLogger<LlmGateway>.Instance);
    }

    [Fact]
    public async Task DispatchIsDurableBeforeProviderAndFinalizedInSameRowWithUsage()
    {
        var request = Request();
        var client = new ImageClient { BeforeResponse = async (messages, _) =>
        {
            var admitted = await Db.LlmCalls.AsNoTracking().SingleAsync();
            admitted.AttemptKey.ShouldBe(request.AttemptKey);
            admitted.Outcome.ShouldBe(LlmCallOutcome.Dispatching);
            admitted.TriggerMessageId.ShouldBe(555);
            messages.Last().Contents.OfType<DataContent>().Single().Data.ToArray().ShouldBe(request.Images![0].Data.ToArray());
        }};
        var gateway = Gateway(client);
        var result = await gateway.CompleteAsync(request, default);
        result.Text.ShouldBe("synthetic extraction");
        result.TraceAttemptId.ShouldBe(request.AttemptKey);
        var row = await Db.LlmCalls.AsNoTracking().SingleAsync();
        row.Outcome.ShouldBe(LlmCallOutcome.Ok);
        row.InputTokens.ShouldBe(17);
        row.OutputTokens.ShouldBe(3);
        row.Cost.ShouldBe(0);
        row.ChatId.ShouldBe(333);
        row.TopicId.ShouldBe(444);
        (await gateway.CompleteAsync(request, default)).RefusalReason.ShouldBe(LlmRefusalReason.OutcomeUnknown);
        client.Calls.ShouldBe(1);
        (await Db.LlmCalls.CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData("family")]
    [InlineData("bot")]
    [InlineData("chat")]
    [InlineData("topic")]
    [InlineData("source")]
    [InlineData("tier")]
    [InlineData("model")]
    public async Task AttemptIdentityCannotBeReusedInDifferentScopeOrModel(string field)
    {
        var request = Request();
        var client = new ImageClient();
        var gateway = Gateway(client);
        (await gateway.CompleteAsync(request, default)).IsAnswer.ShouldBeTrue();
        var changed = field switch
        {
            "family" => request with { FamilyId = 112 },
            "bot" => request with { BotId = 223 },
            "chat" => request with { ChatId = 334 },
            "topic" => request with { TopicId = null },
            "source" => request with { TriggerMessageId = 556 },
            "tier" => request with { Tier = LlmConfig.SmartTier },
            _ => request with { PreferredModel = "unproved-model" }
        };
        (await gateway.CompleteAsync(changed, default)).RefusalReason.ShouldBe(LlmRefusalReason.Failed);
        client.Calls.ShouldBe(1);
        (await Db.LlmCalls.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task FailedAdmissionDispatchesNothingAndLeavesNoTrackedRetry()
    {
        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        await using var failing = new FailingAdmission(options.Options);
        var client = new ImageClient();
        var result = await Gateway(client, failing).CompleteAsync(Request(), default);
        result.RefusalReason.ShouldBe(LlmRefusalReason.OutcomeUnknown);
        client.Calls.ShouldBe(0);
        failing.ChangeTracker.Entries<LlmCall>().ShouldBeEmpty();
        (await Db.LlmCalls.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task FailedFinalizationDoesNotReturnUnaccountedAnswerOrRedispatch()
    {
        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        options.AddInterceptors(new FailFinalization());
        await using var failing = new AssistantDbContext(options.Options);
        var client = new ImageClient();
        var request = Request();
        var gateway = Gateway(client, failing);
        (await gateway.CompleteAsync(request, default)).RefusalReason.ShouldBe(LlmRefusalReason.OutcomeUnknown);
        (await gateway.CompleteAsync(request, default)).RefusalReason.ShouldBe(LlmRefusalReason.OutcomeUnknown);
        var row = await Db.LlmCalls.SingleAsync();
        row.Outcome.ShouldBe(LlmCallOutcome.Dispatching);
        row.InputTokens.ShouldBeNull();
        client.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ProviderTimeoutIsUnknownAndRetryDoesNotDispatchAgain()
    {
        var client = new ImageClient { BeforeResponse = (_, _) => throw new TimeoutException("synthetic timeout") };
        var request = Request();
        var gateway = Gateway(client);
        (await gateway.CompleteAsync(request, default)).RefusalReason.ShouldBe(LlmRefusalReason.OutcomeUnknown);
        (await gateway.CompleteAsync(request, default)).RefusalReason.ShouldBe(LlmRefusalReason.OutcomeUnknown);
        (await Db.LlmCalls.SingleAsync()).Outcome.ShouldBe(LlmCallOutcome.OutcomeUnknown);
        client.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task CallerCancellationRetainsUnknownDispatchAndPropagates()
    {
        using var cancelled = new CancellationTokenSource();
        var client = new ImageClient { BeforeResponse = (_, token) => { cancelled.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        await Should.ThrowAsync<OperationCanceledException>(() => Gateway(client).CompleteAsync(Request(), cancelled.Token));
        (await Db.LlmCalls.SingleAsync()).Outcome.ShouldBe(LlmCallOutcome.OutcomeUnknown);
        client.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task UnsupportedImagesNeverFallBackOrCreateAttempt()
    {
        var client = new ImageClient { SupportsImages = false };
        var result = await Gateway(client).CompleteAsync(Request(), default);
        result.RefusalReason.ShouldBe(LlmRefusalReason.UnsupportedInput);
        client.Calls.ShouldBe(0);
        (await Db.LlmCalls.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task InvalidImageBytesAreRejectedBeforeAdmission()
    {
        var client = new ImageClient();
        var request = Request() with { Images = [new(new byte[] {1,2,3}, "image/png")] };
        (await Gateway(client).CompleteAsync(request, default)).RefusalReason.ShouldBe(LlmRefusalReason.UnsupportedInput);
        client.Calls.ShouldBe(0);
        (await Db.LlmCalls.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task RateGuardCreatesNoImageAttemptAndLegacyTextKeepsNullKey()
    {
        var client = new ImageClient();
        (await Gateway(client, minuteCap: 0).CompleteAsync(Request(), default)).RefusalReason.ShouldBe(LlmRefusalReason.RateLimited);
        client.Calls.ShouldBe(0);
        (await Db.LlmCalls.CountAsync()).ShouldBe(0);
        var legacy = Request() with { Images = null, AttemptKey = null };
        (await Gateway(client).CompleteAsync(legacy, default)).Text.ShouldBe("synthetic extraction");
        (await Db.LlmCalls.SingleAsync()).AttemptKey.ShouldBeNull();
    }
}
