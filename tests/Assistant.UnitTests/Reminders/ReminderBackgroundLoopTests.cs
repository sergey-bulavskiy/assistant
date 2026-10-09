using Assistant.Application.Expectations;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Reminders;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.UnitTests.Reminders;

public sealed class ReminderBackgroundLoopTests
{
    private sealed class State
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockSelection; public bool Fail; public int Created; public int Disposed;
        public List<long?> Families = []; public List<Guid> Claimed = []; public List<Guid> Completed = [];
        public IReadOnlyList<NonurgentCandidate> Candidates = [];
    }

    private sealed class Store(State state, ICurrentFamily family) : INonurgentDispatchStore, IDisposable
    {
        public Task CleanupAsync(ReceivingBot bot, CancellationToken ct)
        { state.Families.Add(family.FamilyId); return Task.CompletedTask; }
        public async Task<IReadOnlyList<NonurgentCandidate>> SelectAsync(ReceivingBot bot, CancellationToken ct)
        {
            state.Entered.TrySetResult();
            if (state.Fail) throw new IOException("test-token synthetic-private-marker 111");
            if (state.BlockSelection) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return state.Candidates;
        }
        public Task<NonurgentDispatch?> ClaimAsync(ReceivingBot bot, NonurgentCandidate candidate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); state.Families.Add(family.FamilyId); state.Claimed.Add(candidate.Id);
            return Task.FromResult<NonurgentDispatch?>(new(candidate.Kind, candidate.Id, candidate.Id, -100, 7, "synthetic task"));
        }
        public Task CompleteAsync(ReceivingBot bot, NonurgentDispatch dispatch, int messageId, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); state.Families.Add(family.FamilyId); state.Completed.Add(dispatch.Id); return Task.CompletedTask; }
        public void Dispose() => state.Disposed++;
    }

    private sealed class Logs : ILogger<ReminderBackgroundLoop>
    {
        public List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> format)
            => Lines.Add(format(state, exception));
    }

    private static ServiceProvider Services(State state) => new ServiceCollection()
        .AddScoped<ICurrentFamily, CurrentFamily>()
        .AddScoped<INonurgentDispatchStore>(p => { state.Created++; return new Store(state, p.GetRequiredService<ICurrentFamily>()); })
        .BuildServiceProvider();

    [Fact]
    public async Task Cancellation_joins_inflight_pass_and_disposes_family_scoped_store()
    {
        var state = new State { BlockSelection = true }; var logs = new Logs();
        using var services = Services(state); using var stop = new CancellationTokenSource();
        var loop = new ReminderBackgroundLoop(services.GetRequiredService<IServiceScopeFactory>(), logs);
        var running = loop.RunAsync(new(22, 999, "synthetic_bot", 11, "health"), new FakeTelegramClient(), stop.Token);
        try
        {
            await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            state.Disposed.ShouldBe(0); running.IsCompleted.ShouldBeFalse(); state.Families.ShouldBe(new long?[] { 11 });
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        state.Disposed.ShouldBe(1); logs.Lines.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, "manager")]
    [InlineData(11L, "unsupported")]
    public async Task Unsupported_bots_do_not_create_background_scope(long? family, string role)
    {
        var state = new State(); using var services = Services(state);
        var loop = new ReminderBackgroundLoop(services.GetRequiredService<IServiceScopeFactory>(), new Logs());
        await loop.RunAsync(new(22, 999, "synthetic_bot", family, role), new FakeTelegramClient(), default);
        state.Created.ShouldBe(0); state.Families.ShouldBeEmpty(); state.Entered.Task.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Background_fault_logs_category_and_type_without_private_exception_details()
    {
        var state = new State { Fail = true }; var logs = new Logs(); using var services = Services(state);
        using var stop = new CancellationTokenSource();
        var loop = new ReminderBackgroundLoop(services.GetRequiredService<IServiceScopeFactory>(), logs);
        var running = loop.RunAsync(new(22, 999, "synthetic_bot", 11, "health"), new FakeTelegramClient(), stop.Token);
        try { await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        logs.Lines.ShouldBe(new[] { "Nonurgent pass failed: IOException" }); state.Disposed.ShouldBe(1);
    }

    [Fact]
    public async Task Mixed_features_send_five_total_in_selection_order_with_fresh_disposed_scopes()
    {
        var ids = Enumerable.Range(1, 6).Select(i => Guid.Parse($"10000000-0000-0000-0000-{i:000000000000}")).ToArray();
        var state = new State { Candidates = ids.Select((id, i) => new NonurgentCandidate(i % 2 == 0 ? "expectation" : "reminder",
            id, DateTimeOffset.Parse("2032-02-10T06:30:00Z").AddSeconds(i))).ToArray() };
        using var services = Services(state); var logs = new Logs(); var client = new FakeTelegramClient();
        var loop = new ReminderBackgroundLoop(services.GetRequiredService<IServiceScopeFactory>(), logs);
        await loop.RunPassAsync(new(22, 999, "synthetic_bot", 11, "health"), client, default);
        state.Claimed.ShouldBe(ids.Take(5).ToArray()); state.Completed.ShouldBe(ids.Take(5).ToArray());
        client.Sent.Count.ShouldBe(5); client.Sent.All(x => x.ChatId == -100 && x.TopicId == 7).ShouldBeTrue();
        state.Created.ShouldBe(11); state.Disposed.ShouldBe(11);
        state.Families.ShouldBe(Enumerable.Repeat<long?>(11, 11).ToArray()); logs.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rejected_transports_consume_pass_slots_and_never_run_completion()
    {
        var state = new State { Candidates = Enumerable.Range(1, 6).Select(i => new NonurgentCandidate("expectation",
            Guid.Parse($"10000000-0000-0000-0000-{i:000000000000}"), DateTimeOffset.Parse("2032-02-10T06:30:00Z"))).ToArray() };
        using var services = Services(state); var logs = new Logs();
        var client = new FakeTelegramClient { ThrowOnSend = true, SendFailure = new IOException("synthetic-private-marker test-token 111") };
        await new ReminderBackgroundLoop(services.GetRequiredService<IServiceScopeFactory>(), logs)
            .RunPassAsync(new(22, 999, "synthetic_bot", 11, "health"), client, default);
        state.Claimed.Count.ShouldBe(5); state.Completed.ShouldBeEmpty(); state.Created.ShouldBe(6); state.Disposed.ShouldBe(6);
        logs.Lines.ShouldBe(Enumerable.Repeat("Nonurgent delivery outcome unknown: IOException", 5).ToArray());
    }
}
