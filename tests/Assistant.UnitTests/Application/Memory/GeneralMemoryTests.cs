using Assistant.Application.Common;
using Assistant.Application.Llm;
using Assistant.Application.Memory;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.UnitTests.Application.Memory;

public sealed class GeneralMemoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly ReceivingBot Bot = new(22, 999, "test_bot", 11, "general");
    private static IncomingMessage Message(string text) => new(111, "private", null, null, 55, 111,
        "test_user", text, MessageKind.Text, false, Now, null, null, "{}", null, null);
    private static LlmConfig Config(int maxChars = 10000) => new()
    {
        Models = [new("codex-cli", "test")], FastModels = [], CallsPerMinute = 10, CallsPerDay = 100,
        MaxContextMessages = 10, MaxInputChars = maxChars, MaxOutputTokens = 1000, CallTimeoutSeconds = 60,
        MaxConcurrentCalls = 2, ModelCooldownMinutes = 5, Prices = new Dictionary<string, ModelPrice>(), Budget = null
    };
    private static GeneralSummaryFold Fold() => new(0, 0, 0, "", Enumerable.Range(1, 20)
        .Select(i => new GeneralSummarySource(i, MessageDirection.In, $"invented turn {i}", null)).ToArray());

    [Fact]
    public void Context_includes_facts_summary_and_current_once_within_budget()
    {
        var memory = new GeneralMemorySnapshot([new(17, "invented persistent fact")], new("invented older context", 20));
        var messages = GeneralMemoryContext.Build([], "unique current turn", null, false, 2000, memory);
        messages.Select(x => x.Text).ShouldContain(x => x.Contains("invented persistent fact", StringComparison.Ordinal));
        messages.Select(x => x.Text).ShouldContain(x => x.Contains("invented older context", StringComparison.Ordinal));
        messages.Count(x => x.Text == "unique current turn").ShouldBe(1);
        messages.Sum(x => x.Text.Length).ShouldBeLessThanOrEqualTo(2000);
        messages[0].Role.ShouldBe(LlmMessageRole.User);
    }

    [Fact]
    public void Oversized_current_drops_optional_memory_before_truncating_current()
    {
        var messages = GeneralMemoryContext.Build([], "12345678901", null, false, 10,
            new([new(17, "fact")], new("summary", 20)));
        messages.ShouldHaveSingleItem().Text.ShouldBe("1234567890");
    }

    [Fact]
    public void Oversized_summary_is_dropped_but_saved_fact_can_remain()
    {
        var messages = GeneralMemoryContext.Build([], "current", null, false, 1800,
            new([new(17, "small saved fact")], new(new string('x', 3000), 20)));
        string.Join("\n", messages.Select(x => x.Text)).ShouldContain("small saved fact");
        string.Join("\n", messages.Select(x => x.Text)).ShouldNotContain(new string('x', 100));
        messages[^1].Text.ShouldBe("current");
    }

    [Fact]
    public async Task Search_is_SQL_only_and_formats_source_without_inventing_private_URL()
    {
        var memory = new FakeGeneralMemoryStore { Hits = [new(19, 27, "invented source", Now)] };
        var gateway = new FakeLlmGateway();
        var response = await new GeneralMemoryService(memory, gateway).CommandAsync("search", Bot, Message("/search source"), 55, default);
        response!.ShouldContain("invented source");
        response!.ShouldContain("сообщение 27");
        response!.ShouldNotContain("https://");
        gateway.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/forget 0")]
    [InlineData("/forget -1")]
    [InlineData("/forget abc")]
    public async Task Invalid_forget_never_mutates(string command)
    {
        var memory = new FakeGeneralMemoryStore();
        var response = await new GeneralMemoryService(memory, new FakeLlmGateway()).CommandAsync("forget", Bot, Message(command), 55, default);
        response.ShouldBe("Введите /forget и номер из /memory.");
        memory.ForgetCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(true, "oversized")]
    public async Task Refused_empty_or_oversized_summary_keeps_saved_facts_and_installs_nothing(bool answered, string mode)
    {
        var memory = new FakeGeneralMemoryStore { Fold = Fold(), Snapshot = new([new(17, "kept fact")], null) };
        var gateway = new FakeLlmGateway { NextResult = answered
            ? LlmResult.Answered(mode == "oversized" ? new string('x', 3001) : "", "test")
            : LlmResult.Refused(LlmRefusalReason.Failed) };
        var result = await new GeneralMemoryService(memory, gateway, Config()).BeforeAnswerAsync(Bot, Message("question"), 55, default);
        result.Facts.ShouldHaveSingleItem().Text.ShouldBe("kept fact");
        result.Summary.ShouldBeNull();
        memory.CommitCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(200, "Найденные сообщения (после /new):\n2026-01-02 03:04 UTC · сообщение 27\nvisible hit")]
    [InlineData(201, "Введите /search и запрос от 1 до 200 символов.")]
    public async Task Search_query_length_boundary_has_visible_result(int length, string expected)
    {
        var memory = new FakeGeneralMemoryStore { Hits = [new(19, 27, "visible hit", Now)] };
        var response = await new GeneralMemoryService(memory, new FakeLlmGateway()).CommandAsync("search",
            Bot, Message("/search " + new string('x', length)), 55, default);
        response.ShouldBe(expected);
    }

    [Theory]
    [InlineData(500, "Сохранён факт 17. /forget 17 — забыть.")]
    [InlineData(501, "Введите /remember и текст от 1 до 500 символов.")]
    public async Task Remember_text_length_boundary_has_visible_outcome(int length, string expected)
    {
        var response = await new GeneralMemoryService(new FakeGeneralMemoryStore(), new FakeLlmGateway())
            .CommandAsync("remember", Bot, Message("/remember " + new string('x', length)), 55, default);
        response.ShouldBe(expected);
    }

    [Fact]
    public async Task Summary_payload_reduces_window_to_fit_multilingual_character_bound()
    {
        var fold = Fold() with { Sources = Enumerable.Range(1, 20)
            .Select(i => new GeneralSummarySource(i, MessageDirection.In, "многоязычный выдуманный текст", null)).ToArray() };
        var memory = new FakeGeneralMemoryStore { Fold = fold };
        var gateway = new FakeLlmGateway { NextResult = LlmResult.Answered("краткое резюме", "test") };
        await new GeneralMemoryService(memory, gateway, Config(1000)).BeforeAnswerAsync(Bot, Message("question"), 55, default);
        gateway.Requests.ShouldHaveSingleItem().Messages.ShouldHaveSingleItem().Text.Length.ShouldBeLessThanOrEqualTo(1000);
        memory.CommittedFold!.Sources.Count.ShouldBeLessThan(20);
        memory.CommittedFold!.Sources.Count.ShouldBeGreaterThan(0);
        memory.CommittedFold!.Sources[0].Id.ShouldBe(1);
    }

    [Fact]
    public async Task Deliberate_non_subscription_config_does_not_enable_extra_summary_calls()
    {
        var config = Config();
        var legacy = new LlmConfig { Models = [new("openai", "test")], FastModels = [], CallsPerMinute = config.CallsPerMinute,
            CallsPerDay = config.CallsPerDay, MaxContextMessages = config.MaxContextMessages,
            MaxInputChars = config.MaxInputChars, MaxOutputTokens = config.MaxOutputTokens,
            CallTimeoutSeconds = config.CallTimeoutSeconds, MaxConcurrentCalls = config.MaxConcurrentCalls,
            ModelCooldownMinutes = config.ModelCooldownMinutes, Prices = config.Prices, Budget = null };
        var memory = new FakeGeneralMemoryStore { Fold = Fold(), Snapshot = new([new(17, "saved fact")], null) };
        var gateway = new FakeLlmGateway();
        var snapshot = await new GeneralMemoryService(memory, gateway, legacy).BeforeAnswerAsync(Bot, Message("question"), 55, default);
        snapshot.Facts.ShouldHaveSingleItem().Text.ShouldBe("saved fact");
        memory.PrepareCalls.ShouldBe(0);
        gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Summary_refusal_still_allows_normal_answer_in_same_turn()
    {
        var gateway = new FakeLlmGateway();
        gateway.Results.Enqueue(LlmResult.Refused(LlmRefusalReason.Failed));
        gateway.Results.Enqueue(LlmResult.Answered("ordinary answer after refusal", "test"));
        var memory = new FakeGeneralMemoryStore { Fold = Fold() };
        var telegram = new FakeTelegramClient();
        var assistant = new GeneralAssistant(new FakeMessageStore(), gateway, new FakeChatSettingsStore(),
            new FakeLlmUsageQuery(), Config(), new FixedClock(Now), new BuildInfo("abcdef0", null, Now),
            NullLogger<GeneralAssistant>.Instance, memory: new GeneralMemoryService(memory, gateway, Config()));
        await assistant.HandleAsync(Bot, telegram, Message("unique question"), new(StoreOutcome.Stored, 55), default);
        telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("ordinary answer after refusal");
        gateway.Requests.Count.ShouldBe(2);
        gateway.Requests[^1].Messages[^1].Text.ShouldBe("unique question");
        memory.CommitCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Memory_commands_work_with_LLM_disabled_and_other_bot_commands_stay_silent()
    {
        var gateway = new FakeLlmGateway { IsEnabled = false };
        var telegram = new FakeTelegramClient();
        var assistant = new GeneralAssistant(new FakeMessageStore(), gateway, new FakeChatSettingsStore(),
            new FakeLlmUsageQuery(), null, new FixedClock(Now), new BuildInfo("abcdef0", null, Now),
            NullLogger<GeneralAssistant>.Instance, memory: new GeneralMemoryService(new FakeGeneralMemoryStore(), gateway));
        await assistant.HandleAsync(Bot, telegram, Message("/remember explicit text"), new(StoreOutcome.Stored, 55), default);
        telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("Сохранён факт 17. /forget 17 — забыть.");
        await assistant.HandleAsync(Bot, telegram, Message("/memory@another_bot"), new(StoreOutcome.Stored, 56), default);
        telegram.Sent.Count.ShouldBe(1);
        gateway.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Successful_fold_uses_one_subscription_call_and_persists_its_exact_source_window()
    {
        var memory = new FakeGeneralMemoryStore { Fold = Fold() };
        var gateway = new FakeLlmGateway { NextResult = LlmResult.Answered("bounded summary", "test") };
        var result = await new GeneralMemoryService(memory, gateway, Config()).BeforeAnswerAsync(Bot, Message("question"), 55, default);
        result.Summary!.Text.ShouldBe("bounded summary");
        gateway.Requests.ShouldHaveSingleItem().Messages.ShouldHaveSingleItem();
        memory.CommitCalls.ShouldBe(1);
        memory.CommittedFold!.Sources.Select(x => x.Id).ShouldBe(Enumerable.Range(1, 20).Select(x => (long)x));
    }

    [Fact]
    public async Task Unavailable_store_preserves_ordinary_General_answer()
    {
        var gateway = new FakeLlmGateway { NextResult = LlmResult.Answered("ordinary answer", "test") };
        var memory = new FakeGeneralMemoryStore { Failure = new InvalidOperationException("synthetic failure") };
        var telegram = new FakeTelegramClient();
        var assistant = new GeneralAssistant(new FakeMessageStore(), gateway, new FakeChatSettingsStore(),
            new FakeLlmUsageQuery(), Config(), new FixedClock(Now), new BuildInfo("abcdef0", null, Now),
            NullLogger<GeneralAssistant>.Instance, memory: new GeneralMemoryService(memory, gateway, Config()));
        await assistant.HandleAsync(Bot, telegram, Message("question"), new(StoreOutcome.Stored, 55), default);
        telegram.Sent.ShouldHaveSingleItem().Text.ShouldBe("ordinary answer");
        gateway.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Duplicate_update_cannot_launch_summary_or_answer()
    {
        var memory = new FakeGeneralMemoryStore { Fold = Fold() };
        var gateway = new FakeLlmGateway();
        var telegram = new FakeTelegramClient();
        var assistant = new GeneralAssistant(new FakeMessageStore(), gateway, new FakeChatSettingsStore(),
            new FakeLlmUsageQuery(), Config(), new FixedClock(Now), new BuildInfo("abcdef0", null, Now),
            NullLogger<GeneralAssistant>.Instance, memory: new GeneralMemoryService(memory, gateway, Config()));
        await assistant.HandleAsync(Bot, telegram, Message("question"), new(StoreOutcome.Duplicate, 55), default);
        memory.PrepareCalls.ShouldBe(0);
        gateway.Requests.ShouldBeEmpty();
        telegram.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancellation_propagates_without_installing_a_summary()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var memory = new FakeGeneralMemoryStore { Failure = new OperationCanceledException(cts.Token) };
        await Should.ThrowAsync<OperationCanceledException>(() => new GeneralMemoryService(memory, new FakeLlmGateway(), Config())
            .BeforeAnswerAsync(Bot, Message("question"), 55, cts.Token));
        memory.CommitCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Cancellation_during_provider_wait_never_installs_summary()
    {
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new FakeGeneralMemoryStore { Fold = Fold(), Snapshot = new([new(17, "kept fact")], null) };
        var gateway = new FakeLlmGateway { WaitBeforeAnswering = gate.Task };
        var pending = new GeneralMemoryService(memory, gateway, Config())
            .BeforeAnswerAsync(Bot, Message("question"), 55, cts.Token);
        gateway.Requests.ShouldHaveSingleItem();
        cts.Cancel();
        gate.SetResult();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        memory.CommitCalls.ShouldBe(0);
        memory.Snapshot.Summary.ShouldBeNull();
        memory.Snapshot.Facts.ShouldHaveSingleItem().Text.ShouldBe("kept fact");
    }
}
