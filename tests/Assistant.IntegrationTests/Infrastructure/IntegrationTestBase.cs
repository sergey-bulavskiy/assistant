using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Infrastructure;

public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected AssistantDbContext Db { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await IntegreSqlPool.CreateTestDatabaseAsync();

        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, connectionString);
        Db = new AssistantDbContext(options.Options);
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
    }
}
