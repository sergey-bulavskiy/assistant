using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Persistence;

public class MigrationTests : IntegrationTestBase
{
    [Fact]
    public async Task Migrations_apply_and_model_has_no_pending_changes()
    {
        Db.Database.HasPendingModelChanges().ShouldBeFalse();
        (await Db.Database.CanConnectAsync()).ShouldBeTrue();
    }
}
