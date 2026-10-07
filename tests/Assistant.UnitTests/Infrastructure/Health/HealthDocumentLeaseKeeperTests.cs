using System.Collections.Concurrent;
using Assistant.Application.Families;
using Assistant.Application.Health.Documents;
using Assistant.Infrastructure.Health.Documents;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.UnitTests.Infrastructure.Health;

public sealed class HealthDocumentLeaseKeeperTests
{
    [Fact]
    public async Task Heartbeats_use_distinct_family_scopes_and_renewal_loss_cancels_work()
    {
        var store = new FakeHealthDocumentStore();
        var time = new TickTimeProvider();
        var families = new ConcurrentQueue<ICurrentFamily>();
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddScoped<ICurrentFamily, TestFamily>();
        services.AddScoped<IHealthDocumentStore>(p =>
        {
            var family = p.GetRequiredService<ICurrentFamily>();
            family.FamilyId.ShouldBe(42);
            families.Enqueue(family);
            renewed.TrySetResult();
            return store;
        });
        using var provider = services.BuildServiceProvider();
        var keeper = new HealthDocumentLeaseKeeper(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<HealthDocumentLeaseKeeper>.Instance, time);
        var scope = new HealthDocumentScope(42, 1, 10, 999);
        var admission = new HealthDocumentAdmissionInfo(Guid.NewGuid(), scope, -100, 7, "supergroup", 33, 111,
            new(2030, 4, 10, 10, 0, 0, TimeSpan.Zero), new("synthetic-file", null, "synthetic.txt", null, null),
            null, 22, "processing", 1, null, false, false);
        var lease = new HealthDocumentLease(Guid.NewGuid(), admission, new(1, 22, admission.SentAt, "synthetic.txt", null,
            "processing", null, false, "processing", null));
        await using var owner = await keeper.StartAsync(lease, CancellationToken.None);
        time.Fire();
        await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await store.RenewalObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        owner.Token.IsCancellationRequested.ShouldBeFalse();
        var first = families.Single();
        store.Renewed = false;
        renewed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = owner.Token.Register(() => cancelled.TrySetResult());
        time.Fire();
        await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        families.Count.ShouldBe(2);
        ReferenceEquals(first, families.Last()).ShouldBeFalse();
        owner.Token.IsCancellationRequested.ShouldBeTrue();
    }

    private sealed class TestFamily : ICurrentFamily
    {
        public long? FamilyId { get; private set; }
        public void Set(long? id) => FamilyId = id;
    }
    private sealed class TickTimeProvider : TimeProvider
    {
        private ManualTimer? _timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            dueTime.ShouldBe(TimeSpan.FromSeconds(30));
            period.ShouldBe(TimeSpan.FromSeconds(30));
            return _timer = new ManualTimer(callback, state);
        }
        public void Fire() => _timer!.Fire();
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Fire() { if (!_disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
