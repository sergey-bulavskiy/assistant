using System.Reflection;
using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Reminders;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Infrastructure.Bots;
using Assistant.Infrastructure.Reminders;
using Assistant.Infrastructure.Telegram;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.UnitTests.Infrastructure;

public sealed class BotPollingCoordinatorReminderLifetimeTests
{
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Await(Task t)=>t.WaitAsync(TimeSpan.FromSeconds(10));
    private sealed class RunState
    {
        public TaskCompletionSource Entered=Signal(),Cancelled=Signal(),Release=Signal();
        public bool Disposed; public ITelegramClient? Client;
    }
    private sealed class ReminderRun(RunState state):IReminderAssistant,IDisposable
    {
        public Task<ReminderIntake?> AdmitAsync(ReceivingBot b,IncomingMessage m,bool all,CancellationToken ct)=>throw new NotSupportedException();
        public Task HandleAsync(ReminderIntake i,ITelegramClient c,IncomingMessage m,StoreResult s,CancellationToken ct)=>throw new NotSupportedException();
        public Task HandleCallbackAsync(ReceivingBot b,ITelegramClient c,CallbackQueryInfo q,CancellationToken ct)=>throw new NotSupportedException();
        public async Task TickAsync(ReceivingBot b,ITelegramClient c,CancellationToken ct)
        {
            state.Client=c; using var registration=ct.Register(()=>state.Cancelled.TrySetResult());
            state.Entered.TrySetResult(); await state.Release.Task; ct.ThrowIfCancellationRequested();
        }
        public void Dispose()=>state.Disposed=true;
    }
    private sealed class Client:FakeTelegramClient,ITelegramClient
    {
        public TaskCompletionSource Entered=Signal(),Cancelled=Signal(),Release=Signal();
        public TaskCompletionSource Drained=Signal(); public CancellationToken Token;
        public new async Task<BotIdentity> GetMeAsync(CancellationToken ct)
        {
            Token=ct; using var registration=ct.Register(()=>Cancelled.TrySetResult()); Entered.TrySetResult();
            try {await Release.Task;ct.ThrowIfCancellationRequested();return new(999,"test_bot");}
            finally {Drained.TrySetResult();}
        }
    }
    private sealed class Clients(Client client):ITelegramClientFactory
    { public int Created; public ITelegramClient Create(string token){Created++;return client;} }
    private sealed class Tokens:ITokenEncryptor
    {public byte[] Encrypt(string token)=>[1];public string Decrypt(byte[] cipher)=>"synthetic-token";}

    [Fact]
    public async Task Stop_cancels_and_joins_poller_and_reminder_before_disposal_or_replacement()
    {
        var state=new RunState();var client=new Client();var clients=new Clients(client);
        using var services=new ServiceCollection().AddScoped<IReminderAssistant>(_=>new ReminderRun(state)).BuildServiceProvider();
        var scopeFactory=services.GetRequiredService<IServiceScopeFactory>();
        var loop=new ReminderBackgroundLoop(scopeFactory,NullLogger<ReminderBackgroundLoop>.Instance);
        var coordinator=new BotPollingCoordinator(scopeFactory,clients,new Tokens(),Options.Create(new BotOptions{ManagerToken="synthetic-manager"}),
            PollingWorkerSettings.Default,new PollingHealth(),new FixedClock(DateTimeOffset.Parse("2026-01-02T12:00:00Z")),NullLoggerFactory.Instance,reminderLoop:loop);
        var row=new Bot{Id=22,TelegramBotId=999,FamilyId=11,Role="general",Username="test_bot",TokenEncrypted=[1],Status=BotStatus.Active};
        var start=typeof(BotPollingCoordinator).GetMethod("StartWorkerIfAbsent",BindingFlags.NonPublic|BindingFlags.Instance)!;
        start.Invoke(coordinator,[row]);
        try
        {
            await Await(client.Entered.Task);await Await(state.Entered.Task);state.Client.ShouldBeSameAs(client);
            var stop=coordinator.StopBotAsync(22);coordinator.StopBotAsync(22).ShouldBeSameAs(stop);
            await Await(client.Cancelled.Task);await Await(state.Cancelled.Task);
            stop.IsCompleted.ShouldBeFalse();state.Disposed.ShouldBeFalse();
            start.Invoke(coordinator,[row]);clients.Created.ShouldBe(1);
            client.Release.TrySetResult();await Await(client.Drained.Task);
            stop.IsCompleted.ShouldBeFalse();state.Disposed.ShouldBeFalse();
            state.Release.TrySetResult();await Await(stop);state.Disposed.ShouldBeTrue();client.Drained.Task.IsCompletedSuccessfully.ShouldBeTrue();
            Should.Throw<ObjectDisposedException>(()=>{_=client.Token.WaitHandle;});
        }
        finally {client.Release.TrySetResult();state.Release.TrySetResult();await Await(coordinator.StopAsync(default));}
    }
}
