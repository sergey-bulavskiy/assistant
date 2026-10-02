using Assistant.Domain.Llm;
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

    [Fact]
    public async Task New_M3a_tables_and_column_exist()
    {
        (await Db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name IN ('chat_settings', 'llm_calls')")
            .SingleAsync()).ShouldBe(2);
        (await Db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'messages' AND column_name = 'direction'")
            .SingleAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task M3b_budget_table_and_cost_column_exist()
    {
        (await Db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = 'budget_notices'")
            .SingleAsync()).ShouldBe(1);
        (await Db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'llm_calls' AND column_name = 'cost'")
            .SingleAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Budget_notice_is_unique_per_period_kind_start_and_threshold()
    {
        var periodStart = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        Db.BudgetNotices.Add(new BudgetNotice { PeriodKind = BudgetNotice.DailyPeriod, PeriodStart = periodStart, Threshold = 80, CreatedAt = periodStart });
        // Same period, different threshold / kind: allowed.
        Db.BudgetNotices.Add(new BudgetNotice { PeriodKind = BudgetNotice.DailyPeriod, PeriodStart = periodStart, Threshold = 100, CreatedAt = periodStart });
        Db.BudgetNotices.Add(new BudgetNotice { PeriodKind = BudgetNotice.MonthlyPeriod, PeriodStart = periodStart, Threshold = 80, CreatedAt = periodStart });
        await Db.SaveChangesAsync();

        Db.BudgetNotices.Add(new BudgetNotice { PeriodKind = BudgetNotice.DailyPeriod, PeriodStart = periodStart, Threshold = 80, CreatedAt = periodStart });

        await Should.ThrowAsync<DbUpdateException>(() => Db.SaveChangesAsync());
    }

    [Fact]
    public async Task M3c_reply_to_all_column_is_not_null_with_default_false()
    {
        (await Db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'places' AND column_name = 'reply_to_all' AND is_nullable = 'NO' AND column_default = 'false'")
            .SingleAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task M3c_llm_calls_chat_columns_exist_and_are_nullable()
    {
        (await Db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'llm_calls' AND column_name IN ('chat_id', 'topic_id', 'trigger_message_id') AND is_nullable = 'YES'")
            .SingleAsync()).ShouldBe(3);
    }
}
