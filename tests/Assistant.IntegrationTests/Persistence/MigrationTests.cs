using Assistant.Domain.Health;
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
    public async Task Health_tables_exist_with_numeric_8_2_thresholds()
    {
        (await Db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name IN ('health_profiles', 'safety_rules')")
            .SingleAsync()).ShouldBe(2);
        (await Db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'safety_rules' AND data_type = 'numeric' AND numeric_precision = 8 AND numeric_scale = 2")
            .SingleAsync()).ShouldBe(5);
    }

    [Fact]
    public async Task Safety_rule_is_unique_per_profile_and_key_and_is_deleted_with_its_profile()
    {
        var now = new DateTimeOffset(2030, 2, 7, 10, 0, 0, TimeSpan.Zero);
        var profile = new HealthProfile { FamilyId = 1, BotId = 10, CreatedAt = now, UpdatedAt = now };
        Db.HealthProfiles.Add(profile);
        await Db.SaveChangesAsync();

        SafetyRule NewRule() => new()
        {
            FamilyId = 1,
            ProfileId = profile.Id,
            RuleKey = "glucose.any",
            Source = "guideline_default",
            UpdatedAt = now
        };

        Db.SafetyRules.Add(NewRule());
        Db.SafetyRules.Add(NewRule());
        await Should.ThrowAsync<DbUpdateException>(() => Db.SaveChangesAsync());
        Db.ChangeTracker.Clear();

        Db.SafetyRules.Add(NewRule());
        await Db.SaveChangesAsync();

        var toDelete = await Db.HealthProfiles.SingleAsync();
        Db.HealthProfiles.Remove(toDelete);
        await Db.SaveChangesAsync();

        (await Db.SafetyRules.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task M3c_llm_calls_chat_columns_exist_and_are_nullable()
    {
        (await Db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'llm_calls' AND column_name IN ('chat_id', 'topic_id', 'trigger_message_id') AND is_nullable = 'YES'")
            .SingleAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Events_table_has_jsonb_payload_flags_default_and_the_partial_index()
    {
        (await Db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'events' AND column_name = 'payload' AND data_type = 'jsonb'")
            .SingleAsync()).ShouldBe(1);
        (await Db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'events' AND column_name = 'flags' AND data_type = 'ARRAY' AND column_default LIKE '%{{}}%'")
            .SingleAsync()).ShouldBe(1);
        (await Db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE tablename = 'events' AND indexdef LIKE '%WHERE (deleted_at IS NULL)%'")
            .SingleAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_profile_with_events_cannot_be_deleted()
    {
        var now = new DateTimeOffset(2030, 2, 7, 10, 0, 0, TimeSpan.Zero);
        var profile = new HealthProfile { FamilyId = 1, BotId = 10, CreatedAt = now, UpdatedAt = now };
        Db.HealthProfiles.Add(profile);
        await Db.SaveChangesAsync();
        Db.Events.Add(new HealthEvent
        {
            FamilyId = 1,
            ProfileId = profile.Id,
            Type = "weight",
            SubjectTag = "health",
            OccurredAt = now,
            OccurredAtSource = "message",
            Payload = "{\"kg\":60}",
            BotId = 1001,
            ChatId = -100,
            CreatedAt = now,
            UpdatedAt = now
        });
        await Db.SaveChangesAsync();

        // Otherwise EF sees the tracked event and refuses before the database does.
        Db.ChangeTracker.Clear();
        var toDelete = await Db.HealthProfiles.SingleAsync();
        Db.HealthProfiles.Remove(toDelete);

        await Should.ThrowAsync<DbUpdateException>(() => Db.SaveChangesAsync());
    }
}
