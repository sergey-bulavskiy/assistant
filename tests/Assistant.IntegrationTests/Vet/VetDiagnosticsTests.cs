using Assistant.Application.Diagnostics;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.Infrastructure.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Assistant.IntegrationTests.Vet;

public sealed class VetDiagnosticsTests : VetTestBase
{
    [Fact]
    public async Task Trace_dispositions_use_existing_allowlist_and_original_source_link_with_confirmation_actor()
    {
        await SeedAsync(); var trace = new RecordingTrace(); await using var s = Open(trace: trace);
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"insulin","intent":"unsure","dose":"0.125","time_evidence":"current"}]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic pending source")), CancellationToken.None);
        var prompt = s.Telegram.ButtonMessages.Single();
        await s.Assistant.HandleCallbackAsync(Bot, s.Telegram, new("synthetic", 222, prompt.Buttons[0].CallbackData,
            -100, prompt.MessageId, 7, "supergroup"), CancellationToken.None);
        var row = await s.Context.VetEvents.SingleAsync();
        var requested = trace.Events.Single(e => e.Stage == "confirmation" && e.Outcome == "requested");
        requested.ActorId.ShouldBe(111); requested.RelatedSourceMessageId.ShouldBe(row.SourceMessageDbId);
        var accepted = trace.Events.Single(e => e.Stage == "confirmation" && e.Outcome == "accepted");
        accepted.ActorId.ShouldBe(222); accepted.PendingRecordId.ShouldBe(requested.PendingRecordId);
        accepted.RelatedSourceMessageId.ShouldBe(row.SourceMessageDbId);
        foreach (var item in trace.Events) TraceRedactor.Validate(item);
        trace.Events.Where(e => e.Stage is "extraction" or "confirmation").ShouldAllBe(e => e.Text == null);
    }

    [Fact]
    public async Task Send_exception_content_stays_out_of_operational_logs_and_trace_failure_does_not_revert_action()
    {
        await SeedAsync(); var logger = new RecordingLogger();
        await using var s = Open(trace: new RecordingTrace { Fail = true }, logger: logger);
        s.Telegram.ThrowOnSendToChatId = -100;
        s.Chat.EnqueueResponse("""{"needs_reply":false,"events":[{"type":"glucose","intent":"record","value":"6.4","time_evidence":"current"}]}""");
        await s.Handler.HandleAsync(Bot, s.Telegram, new(1, Text("synthetic private source marker")), CancellationToken.None);
        (await s.Context.VetEvents.SingleAsync()).Value.ShouldBe(6.4m);
        (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(1);
        logger.Messages.ShouldNotBeEmpty();
        var output = string.Join("\n", logger.Messages);
        output.ShouldNotContain("synthetic private source marker"); output.ShouldNotContain("6.4");
        output.ShouldNotContain("test-manager-token"); output.ShouldContain("Exception");
    }

    private sealed class RecordingTrace : ITraceSession
    {
        public bool Enabled => true;
        public Guid? TraceId { get; private set; }
        public bool Fail { get; init; }
        public List<TraceEventData> Events { get; } = [];
        public Task StartAsync(TraceStart start, CancellationToken cancellationToken)
        { TraceId = start.TraceId; return Task.CompletedTask; }
        public Task RecordAsync(TraceEventData data, CancellationToken cancellationToken)
        {
            if (Fail) throw new IOException("synthetic trace failure");
            Events.Add(data); return Task.CompletedTask;
        }
        public ITelegramClient Wrap(ITelegramClient inner) => inner;
    }
    private sealed class RecordingLogger : ILogger<VetAssistant>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
