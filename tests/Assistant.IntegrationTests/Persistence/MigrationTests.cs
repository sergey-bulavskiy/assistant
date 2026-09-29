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

    [Fact]
    public async Task M1_tables_are_truncated_and_bot_state_is_gone()
    {
        (await Db.Messages.CountAsync()).ShouldBe(0);
        (await Db.ChatMigrations.CountAsync()).ShouldBe(0);
        (await Db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = 'bot_state'")
            .SingleAsync()).ShouldBe(0);
    }
}
