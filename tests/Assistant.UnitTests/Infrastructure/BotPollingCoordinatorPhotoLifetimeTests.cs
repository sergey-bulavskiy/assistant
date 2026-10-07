using System.Reflection;
using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Bots;
using Assistant.Infrastructure.Bots;
using Assistant.Infrastructure.Telegram;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Assistant.UnitTests.Infrastructure;

public sealed class BotPollingCoordinatorPhotoLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2031-05-12T12:00:00Z");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Await(Task task) => task.WaitAsync(Bound);
    // Only SQL-backed startup lookup is bypassed. Real coordinator registration and stop ownership,
    // real BotPollingWorker and paired run execute; fake Telegram blocks before any store access.
    private static void Start(BotPollingCoordinator coordinator, Bot bot) =>
        typeof(BotPollingCoordinator).GetMethod("StartWorkerIfAbsent", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(coordinator, [bot]);
    private static Bot Row() => new() { Id = 10, TelegramBotId = 1001, FamilyId = 1, Role = "vet",
        Username = "synthetic_vet_bot", TokenEncrypted = [1], Status = BotStatus.Active, CreatedAt = Now };
    private static BotPollingCoordinator Coordinator(ServiceProvider services, Clients clients, PhotoLoops photos) => new(
        services.GetRequiredService<IServiceScopeFactory>(), clients, new Tokens(), Options.Create(new BotOptions { ManagerToken = "synthetic-manager" }),
        PollingWorkerSettings.Default, new PollingHealth(), new FixedClock(Now), NullLoggerFactory.Instance, photos);

    [Fact]
    public async Task Photo_loop_ending_cancels_and_drains_actual_poller_before_handle_disposal_and_sequential_replacement()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var firstClient = new ControlledTelegram(); var nextClient = new ControlledTelegram();
        var firstPhotos = new PhotoRun(); var nextPhotos = new PhotoRun();
        var clients = new Clients(firstClient, nextClient); var photos = new PhotoLoops(firstPhotos, nextPhotos);
        var coordinator = Coordinator(services, clients, photos); Start(coordinator, Row());
        try
        {
            await Await(firstClient.Entered.Task); await Await(firstPhotos.Entered.Task);
            firstPhotos.ReleaseDrain.TrySetResult(); firstPhotos.End.TrySetResult();
            await Await(firstPhotos.Drained.Task); await Await(firstClient.CancellationSeen.Task);
            firstClient.Drained.Task.IsCompleted.ShouldBeFalse();
            firstClient.AssertTokenAlive();
            var stopping = coordinator.StopBotAsync(10); stopping.IsCompleted.ShouldBeFalse();
            firstClient.ReleaseDrain.TrySetResult(); await Await(stopping);
            firstClient.Drained.Task.IsCompletedSuccessfully.ShouldBeTrue();
            firstClient.AssertTokenDisposed();
            Start(coordinator, Row()); await Await(nextClient.Entered.Task); await Await(nextPhotos.Entered.Task);
            firstClient.Drained.Task.IsCompletedSuccessfully.ShouldBeTrue(); firstPhotos.Drained.Task.IsCompletedSuccessfully.ShouldBeTrue();
            nextClient.CancellationSeen.Task.IsCompleted.ShouldBeFalse(); clients.Created.ShouldBe(2);
        }
        finally
        {
            var cleanup = coordinator.StopAsync(CancellationToken.None);
            firstClient.ReleaseDrain.TrySetResult(); nextClient.ReleaseDrain.TrySetResult();
            firstPhotos.ReleaseDrain.TrySetResult(); nextPhotos.ReleaseDrain.TrySetResult(); firstPhotos.End.TrySetResult(); nextPhotos.End.TrySetResult();
            await Await(cleanup);
        }
    }

    [Fact]
    public async Task Shutdown_cancels_both_paired_tasks_and_waits_for_each_drain_before_disposal()
    {
        using var services = new ServiceCollection().BuildServiceProvider(); var client = new ControlledTelegram(); var photo = new PhotoRun();
        var coordinator = Coordinator(services, new Clients(client), new PhotoLoops(photo)); Start(coordinator, Row());
        try
        {
            await Await(client.Entered.Task); await Await(photo.Entered.Task);
            var stopping = coordinator.StopAsync(CancellationToken.None);
            await Await(client.CancellationSeen.Task); await Await(photo.CancellationSeen.Task);
            stopping.IsCompleted.ShouldBeFalse();
            Start(coordinator, Row()); await Await(coordinator.StartBotAsync(10, CancellationToken.None));
            client.AssertTokenAlive();
            client.ReleaseDrain.TrySetResult(); await Await(client.Drained.Task);
            stopping.IsCompleted.ShouldBeFalse(); photo.Drained.Task.IsCompleted.ShouldBeFalse();
            photo.ReleaseDrain.TrySetResult(); await Await(stopping);
            photo.Drained.Task.IsCompletedSuccessfully.ShouldBeTrue(); client.Drained.Task.IsCompletedSuccessfully.ShouldBeTrue();
            client.AssertTokenDisposed();
            Start(coordinator, Row()); await Await(coordinator.StartBotAsync(10, CancellationToken.None));
            client.Cancellations.ShouldBe(1);
            await Await(coordinator.StopAsync(CancellationToken.None));
        }
        finally
        {
            var cleanup = coordinator.StopAsync(CancellationToken.None);
            client.ReleaseDrain.TrySetResult(); photo.ReleaseDrain.TrySetResult(); photo.End.TrySetResult();
            await Await(cleanup);
        }
    }

    [Fact]
    public async Task Concurrent_start_is_blocked_during_shared_stop_drain_and_replacement_starts_only_after_release()
    {
        using var services = new ServiceCollection().BuildServiceProvider(); var client = new ControlledTelegram(); var replacement = new ControlledTelegram();
        var photo = new PhotoRun(); var replacementPhoto = new PhotoRun(); var clients = new Clients(client, replacement);
        var coordinator = Coordinator(services, clients, new PhotoLoops(photo, replacementPhoto)); Start(coordinator, Row());
        try
        {
            await Await(client.Entered.Task); await Await(photo.Entered.Task);
            var stop = coordinator.StopBotAsync(10); var sameStop = coordinator.StopBotAsync(10); sameStop.ShouldBeSameAs(stop);
            await Await(client.CancellationSeen.Task); await Await(photo.CancellationSeen.Task);
            Start(coordinator, Row()); await Await(coordinator.StartBotAsync(10, CancellationToken.None));
            clients.Created.ShouldBe(1); replacement.Entered.Task.IsCompleted.ShouldBeFalse(); replacementPhoto.Entered.Task.IsCompleted.ShouldBeFalse();
            stop.IsCompleted.ShouldBeFalse(); client.AssertTokenAlive(); client.Cancellations.ShouldBe(1);
            client.ReleaseDrain.TrySetResult(); await Await(client.Drained.Task); stop.IsCompleted.ShouldBeFalse();
            photo.ReleaseDrain.TrySetResult(); await Await(stop); await Await(sameStop); client.AssertTokenDisposed();
            Start(coordinator, Row()); await Await(replacement.Entered.Task); await Await(replacementPhoto.Entered.Task); clients.Created.ShouldBe(2);
            client.Cancellations.ShouldBe(1); photo.Drained.Task.IsCompletedSuccessfully.ShouldBeTrue();
        }
        finally
        {
            var cleanup = coordinator.StopAsync(CancellationToken.None);
            client.ReleaseDrain.TrySetResult(); replacement.ReleaseDrain.TrySetResult(); photo.ReleaseDrain.TrySetResult(); replacementPhoto.ReleaseDrain.TrySetResult();
            photo.End.TrySetResult(); replacementPhoto.End.TrySetResult(); await Await(cleanup);
        }
    }

    [Fact]
    public async Task Throwing_cancellation_callback_still_drains_both_tasks_disposes_and_releases_registration()
    {
        using var services = new ServiceCollection().BuildServiceProvider(); var client = new ControlledTelegram { ThrowOnCancellation = true };
        var next = new ControlledTelegram(); var photo = new PhotoRun(); var nextPhoto = new PhotoRun(); var clients = new Clients(client, next);
        var coordinator = Coordinator(services, clients, new PhotoLoops(photo, nextPhoto)); Start(coordinator, Row());
        Task stop = Task.CompletedTask;
        try
        {
            await Await(client.Entered.Task); await Await(photo.Entered.Task); stop = coordinator.StopBotAsync(10);
            await Await(client.CancellationSeen.Task); await Await(photo.CancellationSeen.Task); stop.IsCompleted.ShouldBeFalse(); client.AssertTokenAlive();
            Start(coordinator, Row()); clients.Created.ShouldBe(1);
            client.ReleaseDrain.TrySetResult(); await Await(client.Drained.Task); stop.IsCompleted.ShouldBeFalse();
            photo.ReleaseDrain.TrySetResult(); var error = await Should.ThrowAsync<AggregateException>(() => Await(stop));
            error.InnerExceptions.Single().ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("Synthetic cancellation callback failure.");
            client.AssertTokenDisposed(); photo.Drained.Task.IsCompletedSuccessfully.ShouldBeTrue(); client.Cancellations.ShouldBe(1);
            Start(coordinator, Row()); await Await(next.Entered.Task); await Await(nextPhoto.Entered.Task); clients.Created.ShouldBe(2);
        }
        finally
        {
            var cleanup = coordinator.StopAsync(CancellationToken.None); client.ReleaseDrain.TrySetResult(); next.ReleaseDrain.TrySetResult();
            photo.ReleaseDrain.TrySetResult(); nextPhoto.ReleaseDrain.TrySetResult(); photo.End.TrySetResult(); nextPhoto.End.TrySetResult();
            await Await(cleanup);
        }
    }

    [Fact]
    public async Task Photo_end_with_throwing_poller_cancellation_still_drains_before_disposal_and_replacement()
    {
        using var services = new ServiceCollection().BuildServiceProvider(); var client = new ControlledTelegram { ThrowOnCancellation = true };
        var next = new ControlledTelegram(); var photo = new PhotoRun(); var nextPhoto = new PhotoRun(); var clients = new Clients(client, next);
        var coordinator = Coordinator(services, clients, new PhotoLoops(photo, nextPhoto)); Start(coordinator, Row());
        try
        {
            await Await(client.Entered.Task); await Await(photo.Entered.Task); photo.ReleaseDrain.TrySetResult(); photo.End.TrySetResult();
            await Await(photo.Drained.Task); await Await(client.CancellationSeen.Task);
            var stop = coordinator.StopBotAsync(10); var sameStop = coordinator.StopBotAsync(10); sameStop.ShouldBeSameAs(stop);
            stop.IsCompleted.ShouldBeFalse(); client.Drained.Task.IsCompleted.ShouldBeFalse(); client.AssertTokenAlive();
            Start(coordinator, Row()); clients.Created.ShouldBe(1);
            await Await(coordinator.StartBotAsync(10, CancellationToken.None)); clients.Created.ShouldBe(1);
            next.Entered.Task.IsCompleted.ShouldBeFalse(); nextPhoto.Entered.Task.IsCompleted.ShouldBeFalse();
            client.ReleaseDrain.TrySetResult(); var error = await Should.ThrowAsync<AggregateException>(() => Await(stop));
            error.Flatten().InnerExceptions.Single().ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("Synthetic cancellation callback failure.");
            client.Drained.Task.IsCompletedSuccessfully.ShouldBeTrue(); client.AssertTokenDisposed(); client.Cancellations.ShouldBe(1);
            Start(coordinator, Row()); await Await(next.Entered.Task); await Await(nextPhoto.Entered.Task); clients.Created.ShouldBe(2);
        }
        finally
        {
            var cleanup = coordinator.StopAsync(CancellationToken.None); client.ReleaseDrain.TrySetResult(); next.ReleaseDrain.TrySetResult();
            photo.ReleaseDrain.TrySetResult(); nextPhoto.ReleaseDrain.TrySetResult(); photo.End.TrySetResult(); nextPhoto.End.TrySetResult(); await Await(cleanup);
        }
    }

    private sealed class ControlledTelegram : FakeTelegramClient, ITelegramClient
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource CancellationSeen { get; } = Signal();
        public TaskCompletionSource ReleaseDrain { get; } = Signal();
        public TaskCompletionSource Drained { get; } = Signal();
        public CancellationToken ObservedToken { get; private set; }
        public int Cancellations;
        public bool ThrowOnCancellation { get; init; }
        public void AssertTokenAlive() => Should.NotThrow(() => { _ = ObservedToken.WaitHandle; });
        public void AssertTokenDisposed() => Should.Throw<ObjectDisposedException>(() => { _ = ObservedToken.WaitHandle; });
        public new async Task<BotIdentity> GetMeAsync(CancellationToken ct)
        {
            ObservedToken = ct;
            using var registration = ct.Register(() => { Interlocked.Increment(ref Cancellations); CancellationSeen.TrySetResult();
                if (ThrowOnCancellation) throw new InvalidOperationException("Synthetic cancellation callback failure."); }); Entered.TrySetResult();
            try { await ReleaseDrain.Task; ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("Synthetic poller must be cancelled before drain."); }
            finally { Drained.TrySetResult(); }
        }
    }
    private sealed class PhotoRun
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource CancellationSeen { get; } = Signal();
        public TaskCompletionSource ReleaseDrain { get; } = Signal();
        public TaskCompletionSource End { get; } = Signal();
        public TaskCompletionSource Drained { get; } = Signal();
        public async Task Run(CancellationToken ct)
        {
            using var registration = ct.Register(() => { CancellationSeen.TrySetResult(); End.TrySetResult(); }); Entered.TrySetResult();
            try { await End.Task; await ReleaseDrain.Task; }
            finally { Drained.TrySetResult(); }
        }
    }
    private sealed class PhotoLoops(params PhotoRun[] runs) : IVetPhotoBackgroundLoop
    {
        private readonly Queue<PhotoRun> pending = new(runs);
        public Task RunAsync(ReceivingBot bot, ITelegramClient client, CancellationToken ct)
        { bot.Role.ShouldBe("vet"); return pending.Dequeue().Run(ct); }
    }
    private sealed class Clients(params ControlledTelegram[] clients) : ITelegramClientFactory
    {
        private readonly Queue<ControlledTelegram> pending = new(clients);
        public int Created { get; private set; }
        public ITelegramClient Create(string token) { token.ShouldBe("synthetic-token"); Created++; return pending.Dequeue(); }
    }
    private sealed class Tokens : ITokenEncryptor
    { public byte[] Encrypt(string token) => [1]; public string Decrypt(byte[] cipher) => "synthetic-token"; }
}
