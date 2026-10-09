using Assistant.Domain.Expectations;
using Assistant.Domain.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Assistant.IntegrationTests.Expectations;

public sealed class ExpectationMigrationTests : ExpectationTestBase
{
    [Fact]
    public async Task Upgrade_from_delivered_m6a_preserves_spent_budget_without_backfill_or_reset()
    {
        // This is the disposable leased test database, never a production connection.
        await Db.GetService<IMigrator>().MigrateAsync("20261008151855_AddUserReminders");
        await SeedAsync();
        var due = DateTimeOffset.Parse("2032-02-10T06:30:00Z");
        var reminderId = Guid.NewGuid();
        var originalIds = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToArray();
        await using (var before = Open())
        {
            before.Db.Add(new Reminder { Id = reminderId, FamilyId = 11, BotDbId = 22, BotId = 999,
                Role = "health", ChatId = -100, TopicId = 7, ChatType = "supergroup", ActorUserId = 111,
                SourceMessageId = 600, Text = "synthetic task", Status = "unknown", OffsetMinutes = 180,
                DueAt = due, CreatedAt = Initial, UpdatedAt = due });
            for (var i = 0; i < originalIds.Length; i++)
                before.Db.Add(new ReminderAttempt { Id = originalIds[i], ReminderId = reminderId, FamilyId = 11,
                    Role = "health", StartedAt = due.AddMinutes(-1), OccurrenceDueAt = due.AddDays(-i), Outcome = "unknown" });
            await before.Db.SaveChangesAsync();
        }
        await Db.GetService<IMigrator>().MigrateAsync();
        var first = await ActiveAsync();
        var second = await ActiveAsync(OtherHealthScope);
        Clock.UtcNow = due;
        await using var left = Open(); await using var right = Open();
        var claims = await Task.WhenAll(
            left.Dispatch.ClaimAsync(HealthBot, new("expectation", first.Id, due), default),
            right.Dispatch.ClaimAsync(OtherHealthBot, new("expectation", second.Id, due), default));
        claims.Count(x => x != null).ShouldBe(1);
        await using var after = Open();
        var rows = await after.Db.Set<ReminderAttempt>().AsNoTracking().ToArrayAsync();
        rows.Select(x => x.Id).Order().ShouldBe(originalIds.Order());
        rows.ShouldAllBe(x => x.Outcome == "unknown" && x.StartedAt == due.AddMinutes(-1));
        (await after.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
        (await after.Db.Set<Reminder>().CountAsync()).ShouldBe(1);
    }
}
