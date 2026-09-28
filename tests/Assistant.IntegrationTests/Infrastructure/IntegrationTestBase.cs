using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Infrastructure;

public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected AssistantDbContext Db { get; private set; } = null!;

    /// <summary>
    /// Connection string for <see cref="Db"/>'s database. Exposed so a test can open additional,
    /// independent <see cref="AssistantDbContext"/> instances against the same database — e.g. to
    /// exercise real DB-level concurrency, which a single (non-thread-safe) <see cref="Db"/> cannot.
    /// </summary>
    protected string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = await IntegreSqlPool.CreateTestDatabaseAsync();

        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        Db = new AssistantDbContext(options.Options);
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
    }
}
