using Npgsql;

namespace Assistant.IntegrationTests.Infrastructure;

/// <summary>
/// One test's IntegreSQL database. Dispose it when the test finishes (pass or fail) — after every
/// <c>DbContext</c>/host using it has been disposed — so the database goes back to the pool instead
/// of piling up as "dirty" until the pool is exhausted.
/// </summary>
public sealed class TestDatabaseLease : IAsyncDisposable
{
    private readonly IntegreSqlClient _client;
    private readonly TestDatabaseCheckout _checkout;
    private int _released;

    internal TestDatabaseLease(IntegreSqlClient client, TestDatabaseCheckout checkout)
    {
        _client = client;
        _checkout = checkout;
    }

    public string ConnectionString => _checkout.ConnectionString;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
        {
            return;
        }

        // Npgsql keeps idle physical connections open in its client-side pool (for up to
        // ConnectionIdleLifetime, 300s by default) after every DbContext/host is disposed. IntegreSQL
        // recreates a returned database with a plain `DROP DATABASE`, which Postgres refuses while any
        // connection is open — IntegreSQL then just retries until those idle connections age out, so
        // the pool stalls. Clearing this database's Npgsql pool closes them first. (Only this
        // connection string's pool: other tests running in parallel keep theirs.)
        await using (var connection = new NpgsqlConnection(_checkout.ConnectionString))
        {
            NpgsqlConnection.ClearPool(connection);
        }

        await _client.RecreateTestDatabaseAsync(_checkout.TemplateHash, _checkout.Id, CancellationToken.None);
    }
}
