using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Infrastructure;

public abstract class IntegrationTestBase : IAsyncLifetime
{
    private TestDatabaseLease? _database;

    protected AssistantDbContext Db { get; private set; } = null!;

    /// <summary>
    /// Connection string for <see cref="Db"/>'s database. Exposed so a test can open additional,
    /// independent <see cref="AssistantDbContext"/> instances against the same database — e.g. to
    /// exercise real DB-level concurrency, which a single (non-thread-safe) <see cref="Db"/> cannot.
    /// </summary>
    protected string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _database = await IntegreSqlPool.CreateTestDatabaseAsync();
        ConnectionString = _database.ConnectionString;

        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        Db = new AssistantDbContext(options.Options);
    }

    // xUnit calls this after every test, pass or fail. Contexts the test opened on ConnectionString
    // itself must be disposed by the test (`await using`) — the lease clears the Npgsql pool, but a
    // connection still checked out would keep IntegreSQL from recreating the database.
    public async Task DisposeAsync()
    {
        try
        {
            if (Db is not null)
            {
                await Db.DisposeAsync();
            }
        }
        finally
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }
    }
}
