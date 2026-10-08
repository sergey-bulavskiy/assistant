using Assistant.Application.Messages;
using Assistant.Application.Reminders;
using Assistant.Application.Telegram;
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
        public bool Disposed; public ITelegramClient? Client; public ReceivingBot? Bot; public bool Fail;
    }
    private sealed class Assistant(State state) : IReminderAssistant, IDisposable
    {
        public Task<ReminderIntake?> AdmitAsync(ReceivingBot b, IncomingMessage m, bool all,CancellationToken ct) => throw new NotSupportedException();
        public Task HandleAsync(ReminderIntake i,ITelegramClient c,IncomingMessage m,StoreResult s,CancellationToken ct) => throw new NotSupportedException();
        public Task HandleCallbackAsync(ReceivingBot b,ITelegramClient c,CallbackQueryInfo q,CancellationToken ct) => throw new NotSupportedException();
        public async Task TickAsync(ReceivingBot bot, ITelegramClient client,CancellationToken ct)
        {
            state.Bot=bot; state.Client=client; state.Entered.TrySetResult();
            if (state.Fail) throw new IOException("test-token synthetic-private-text");
            await Task.Delay(Timeout.InfiniteTimeSpan,ct);
        }
        public void Dispose() => state.Disposed=true;
    }
    private sealed class Logs : ILogger<ReminderBackgroundLoop>
    {
        public List<string> Lines=[];
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull => null;
        public bool IsEnabled(LogLevel l)=>true;
        public void Log<TState>(LogLevel l,EventId id,TState state,Exception? ex,Func<TState,Exception?,string> f)=>Lines.Add(f(state,ex));
    }

    [Fact]
    public async Task Cancellation_joins_inflight_pass_disposes_scope_and_reuses_owned_client()
    {
        var state=new State(); var logs=new Logs(); var client=new FakeTelegramClient();
        var bot=new ReceivingBot(22,999,"test_bot",11,"general");
        using var services=new ServiceCollection().AddScoped<IReminderAssistant>(_=>new Assistant(state)).BuildServiceProvider();
        using var stop=new CancellationTokenSource();
        var loop=new ReminderBackgroundLoop(services.GetRequiredService<IServiceScopeFactory>(),logs);
        var running=loop.RunAsync(bot,client,stop.Token);
        await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.Disposed.ShouldBeFalse(); running.IsCompleted.ShouldBeFalse();
        stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5));
        state.Disposed.ShouldBeTrue(); state.Client.ShouldBeSameAs(client); state.Bot.ShouldBe(bot); logs.Lines.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null,"manager")]
    [InlineData(11L,"unsupported")]
    public async Task Unsupported_bots_do_not_start_background_pass(long? family,string role)
    {
        var state=new State(); using var services=new ServiceCollection()
            .AddScoped<IReminderAssistant>(_=>new Assistant(state)).BuildServiceProvider();
        var loop=new ReminderBackgroundLoop(services.GetRequiredService<IServiceScopeFactory>(),new Logs());
        await loop.RunAsync(new(22,999,"test_bot",family,role),new FakeTelegramClient(),default);
        state.Entered.Task.IsCompleted.ShouldBeFalse(); state.Client.ShouldBeNull();
    }

    [Fact]
    public async Task Background_fault_logs_fixed_category_type_and_no_private_exception_details()
    {
        var state=new State {Fail=true};var logs=new Logs();
        using var services=new ServiceCollection().AddScoped<IReminderAssistant>(_=>new Assistant(state)).BuildServiceProvider();
        using var stop=new CancellationTokenSource();
        var loop=new ReminderBackgroundLoop(services.GetRequiredService<IServiceScopeFactory>(),logs);
        var running=loop.RunAsync(new(22,999,"test_bot",11,"general"),new FakeTelegramClient(),stop.Token);
        await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();await running.WaitAsync(TimeSpan.FromSeconds(5));
        logs.Lines.ShouldBe(["Reminder background pass failed: IOException"]);state.Disposed.ShouldBeTrue();
    }
}
