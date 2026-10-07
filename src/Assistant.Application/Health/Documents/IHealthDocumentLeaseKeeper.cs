namespace Assistant.Application.Health.Documents;

public interface IHealthDocumentLeaseOwner : IAsyncDisposable
{
    CancellationToken Token { get; }
}

public interface IHealthDocumentLeaseKeeper
{
    Task<IHealthDocumentLeaseOwner> StartAsync(HealthDocumentLease lease, CancellationToken token);
}
