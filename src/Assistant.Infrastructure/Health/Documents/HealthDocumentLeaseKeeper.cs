using Assistant.Application.Families;
using Assistant.Application.Health.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.Infrastructure.Health.Documents;

public sealed class HealthDocumentLeaseKeeper(
    IServiceScopeFactory scopes, ILogger<HealthDocumentLeaseKeeper> logger, TimeProvider timeProvider)
    : IHealthDocumentLeaseKeeper
{
    public Task<IHealthDocumentLeaseOwner> StartAsync(HealthDocumentLease lease, CancellationToken token) =>
        Task.FromResult<IHealthDocumentLeaseOwner>(new Owner(scopes, logger, timeProvider, lease, token));

    private sealed class Owner : IHealthDocumentLeaseOwner
    {
        private readonly CancellationTokenSource _work;
        private readonly CancellationTokenSource _heartbeat = new();
        private readonly Task _task;
        public CancellationToken Token => _work.Token;

        public Owner(IServiceScopeFactory scopes, ILogger logger, TimeProvider timeProvider,
            HealthDocumentLease lease, CancellationToken token)
        {
            _work = CancellationTokenSource.CreateLinkedTokenSource(token);
            _task = RunAsync(scopes, logger, timeProvider, lease);
        }

        private async Task RunAsync(IServiceScopeFactory scopes, ILogger logger, TimeProvider timeProvider, HealthDocumentLease lease)
        {
            try
            {
                using var timer = new PeriodicTimer(HealthDocumentLimits.HeartbeatInterval, timeProvider);
                while (await timer.WaitForNextTickAsync(_heartbeat.Token))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    scope.ServiceProvider.GetRequiredService<ICurrentFamily>().Set(lease.Admission.Scope.FamilyId);
                    var store = scope.ServiceProvider.GetRequiredService<IHealthDocumentStore>();
                    if (!await store.RenewAsync(lease, _heartbeat.Token))
                    {
                        await _work.CancelAsync();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (_heartbeat.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning("Health document lease renewal failed: {ExceptionType}", ex.GetType().Name);
                await _work.CancelAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _heartbeat.CancelAsync();
            await _task;
            _heartbeat.Dispose();
            _work.Dispose();
        }
    }
}
