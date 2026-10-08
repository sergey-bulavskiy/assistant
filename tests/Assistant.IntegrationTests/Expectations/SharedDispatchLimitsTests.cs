using Assistant.Application.Expectations;
using Assistant.Application.Messages;
using Assistant.Application.Reminders;
using Assistant.Domain.Expectations;
using Assistant.Domain.Reminders;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Expectations;

public sealed class SharedDispatchLimitsTests : ExpectationTestBase
{
    private static readonly DateTimeOffset Due = DateTimeOffset.Parse("2032-02-10T06:30:00Z");

    private Reminder ReminderRow(Guid id, DateTimeOffset now, ReminderScope scope, string status = "active") => new()
    {
        Id = id, FamilyId = scope.FamilyId, BotDbId = scope.BotDbId, BotId = scope.BotId, Role = scope.Role,
        ChatId = scope.ChatId, TopicId = scope.TopicId, ChatType = scope.ChatType, ActorUserId = scope.ActorUserId,
        SourceMessageId = NextSource(), Text = "synthetic task", DueAt = now, OffsetMinutes = 180, Status = status,
        CreatedAt = Initial, UpdatedAt = Initial
    };

    private async Task SeedReminderAttemptsAsync(int count, DateTimeOffset startedAt, string outcome = "unknown")
    {
        await using var session = Open(); var reminder = ReminderRow(Guid.NewGuid(), startedAt, HealthScope, "unknown");
        session.Db.Add(reminder);
        for (var i = 0; i < count; i++)
            session.Db.Add(new ReminderAttempt { Id = Guid.NewGuid(), ReminderId = reminder.Id, FamilyId = 11,
                Role = "health", StartedAt = startedAt, OccurrenceDueAt = startedAt.AddSeconds(-i), Outcome = outcome });
        await session.Db.SaveChangesAsync();
    }

    private async Task SeedExpectationAttemptsAsync(int count, DateTimeOffset startedAt)
    {
        await using var session = Open();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            session.Db.Add(new Expectation { Id = id, FamilyId = 11, BotDbId = 22, BotId = 999, Role = "health",
                ChatId = -100, TopicId = 7, ChatType = "supergroup", ActorUserId = 111, ProfileId = 333,
                EventType = "glucose", Status = "cancelled", OffsetMinutes = 180, CurrentVersion = 1, LastVersion = 1,
                FirstDate = new(2032, 2, 10), CreatedAt = Initial, UpdatedAt = startedAt });
            session.Db.Add(new ExpectationVersion { FamilyId = 11, ExpectationId = id, Number = 1,
                DeadlineMinute = 540, GraceMinutes = 30, EffectiveFrom = new(2032, 2, 10), CreatedAt = Initial });
            session.Db.Add(new ExpectationOccurrence { FamilyId = 11, ExpectationId = id, Version = 1,
                LocalDate = new(2032, 2, 10), WindowStart = DateTimeOffset.Parse("2032-02-09T21:00:00Z"),
                DueAt = Due, ExpiresAt = DateTimeOffset.Parse("2032-02-10T21:00:00Z"),
                Outcome = "dispatch-unknown", UpdatedAt = startedAt });
            session.Db.Add(new ExpectationAttempt { Id = Guid.NewGuid(), FamilyId = 11, Role = "health",
                ExpectationId = id, LocalDate = new(2032, 2, 10), StartedAt = startedAt, Outcome = "unknown" });
        }
        await session.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Nine_existing_reminder_attempts_and_two_cross_bot_check_claims_share_one_remaining_slot()
    {
        await SeedAsync(); var first = await ActiveAsync(); var second = await ActiveAsync(OtherHealthScope);
        Clock.UtcNow = Due; await SeedReminderAttemptsAsync(9, Due.AddMinutes(-1));
        await using var left = Open(); await using var right = Open();
        var claims = await Task.WhenAll(left.Dispatch.ClaimAsync(HealthBot, new("expectation", first.Id, Due), default),
            right.Dispatch.ClaimAsync(OtherHealthBot, new("expectation", second.Id, Due), default));
        claims.Count(x => x != null).ShouldBe(1);
        var dispatch = claims.Single(x => x != null)!; dispatch.Kind.ShouldBe("expectation");
        await using var read = Open();
        (await read.Db.Set<ReminderAttempt>().CountAsync()).ShouldBe(9);
        (await read.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
        var attempt = await read.Db.Set<ExpectationAttempt>().SingleAsync();
        attempt.Id.ShouldBe(dispatch.AttemptId); attempt.Outcome.ShouldBe("unknown"); attempt.StartedAt.ShouldBe(Due);
        var occurrences = await read.Db.Set<ExpectationOccurrence>().AsNoTracking().ToArrayAsync();
        occurrences.Length.ShouldBe(2); occurrences.Count(x => x.Outcome == "dispatch-unknown").ShouldBe(1);
        occurrences.Count(x => x.Outcome == "pending").ShouldBe(1);
        (await read.Dispatch.ClaimAsync(HealthBot, new("expectation", first.Id, Due), default)).ShouldBeNull();
        (await read.Dispatch.ClaimAsync(OtherHealthBot, new("expectation", second.Id, Due), default)).ShouldBeNull();
        (await read.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Nine_expectation_attempts_allow_only_one_of_two_ordinary_reminder_claims()
    {
        await SeedAsync(); Clock.UtcNow = Due; await SeedExpectationAttemptsAsync(9, Due.AddMinutes(-1));
        var first = ReminderRow(Guid.NewGuid(), Due, HealthScope);
        var second = ReminderRow(Guid.NewGuid(), Due, OtherHealthScope);
        await using (var setup = Open()) { setup.Db.AddRange(first, second); await setup.Db.SaveChangesAsync(); }
        await using var left = Open(); await using var right = Open();
        var claims = await Task.WhenAll(left.Dispatch.ClaimAsync(HealthBot, new("reminder", first.Id, Due), default),
            right.Dispatch.ClaimAsync(OtherHealthBot, new("reminder", second.Id, Due), default));
        claims.Count(x => x != null).ShouldBe(1); claims.Single(x => x != null)!.Kind.ShouldBe("reminder");
        await using var read = Open();
        (await read.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(9);
        (await read.Db.Set<ReminderAttempt>().CountAsync()).ShouldBe(1);
        (await read.Db.Set<ReminderAttempt>().SingleAsync()).Outcome.ShouldBe("unknown");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("sent")]
    public async Task Both_unknown_and_sent_existing_attempts_consume_budget(string outcome)
    {
        await SeedAsync(); var active = await ActiveAsync(); Clock.UtcNow = Due;
        await SeedReminderAttemptsAsync(10, Due.AddMinutes(-1), outcome);
        await using var session = Open();
        (await session.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Due), default)).ShouldBeNull();
        (await session.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
        (await session.Db.Set<ExpectationOccurrence>().SingleAsync()).Outcome.ShouldBe("pending");
        (await RowAsync(active.Id)).NextDate.ShouldBe(new DateOnly(2032, 2, 10));
    }

    [Fact]
    public async Task Previous_utc_day_attempts_do_not_charge_current_day()
    {
        await SeedAsync(); var active = await ActiveAsync(); Clock.UtcNow = Due;
        await SeedReminderAttemptsAsync(10, DateTimeOffset.Parse("2032-02-09T23:59:59.999Z"));
        await using var session = Open();
        var claim = (await session.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Due), default)).ShouldNotBeNull();
        claim.Id.ShouldBe(active.Id); (await session.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
        (await session.Db.Set<ExpectationAttempt>().SingleAsync()).StartedAt.ShouldBe(Due);
    }

    [Fact]
    public async Task Current_budget_is_role_and_family_scoped_without_creator_or_bot_escape()
    {
        await SeedAsync(); var first = await ActiveAsync();
        var otherCreator = await ActiveAsync(OtherHealthScope with { ActorUserId = 222 });
        var vet = await ActiveAsync(VetScope);
        var otherFamilyScope = new ReminderScope(12, 25, 996, "health", -100, 7, "supergroup", 111);
        var otherFamily = await ActiveAsync(otherFamilyScope);
        Clock.UtcNow = Due; await SeedReminderAttemptsAsync(10, Due.AddMinutes(-1));
        await using var session = Open();
        (await session.Dispatch.ClaimAsync(HealthBot, new("expectation", first.Id, Due), default)).ShouldBeNull();
        (await session.Dispatch.ClaimAsync(OtherHealthBot, new("expectation", otherCreator.Id, Due), default)).ShouldBeNull();
        var vetBot = new ReceivingBot(23, 998, "synthetic_vet_bot", 11, "vet");
        (await session.Dispatch.ClaimAsync(vetBot, new("expectation", vet.Id, Due), default)).ShouldNotBeNull().Id.ShouldBe(vet.Id);
        await using var family12 = Open(12);
        var otherBot = new ReceivingBot(25, 996, "synthetic_other_family_bot", 12, "health");
        (await family12.Dispatch.ClaimAsync(otherBot, new("expectation", otherFamily.Id, Due), default)).ShouldNotBeNull().Id.ShouldBe(otherFamily.Id);
        (await session.Db.Set<ExpectationAttempt>().CountAsync(x => x.Role == "health")).ShouldBe(0);
        (await session.Db.Set<ExpectationAttempt>().CountAsync(x => x.Role == "vet")).ShouldBe(1);
        (await family12.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Mixed_admission_at_creator_twenty_has_one_winner()
    {
        await SeedAsync(); await using (var setup = Open())
        {
            for (var i = 0; i < 19; i++) setup.Db.Add(ReminderRow(Guid.NewGuid(), Initial.AddHours(1), HealthScope, i % 2 == 0 ? "draft" : "active"));
            await setup.Db.SaveChangesAsync();
        }
        await using var left = Open(); await using var right = Open();
        var checkTask = left.Store.ExecuteAsync(HealthScope, NextSource(),
            new("create", EventType: "glucose", DeadlineMinute: 540, GraceMinutes: 30), default);
        var reminderTask = right.Reminders.AdmitAsync(HealthScope, NextSource(),
            new("synthetic task", Initial.AddHours(1), null, 180), default);
        await Task.WhenAll(checkTask, reminderTask);
        var check = await checkTask; var reminder = await reminderTask;
        ((check.Result == "preview" ? 1 : 0) + (reminder != null ? 1 : 0)).ShouldBe(1);
        if (reminder != null) check.Result.ShouldBe("capacity");
        await using var read = Open();
        (await read.Db.Set<Reminder>().CountAsync() + await read.Db.Set<Expectation>().CountAsync()).ShouldBe(20);
    }

    [Fact]
    public async Task Mixed_admission_at_bot_two_hundred_has_one_winner_across_places_and_creators()
    {
        await SeedAsync(); await using (var setup = Open())
        {
            for (var i = 0; i < 199; i++)
                setup.Db.Add(ReminderRow(Guid.NewGuid(), Initial.AddHours(1), HealthScope with { ChatId = -1000 - i, TopicId = null }));
            await setup.Db.SaveChangesAsync();
        }
        await using var left = Open(); await using var right = Open();
        var checkTask = left.Store.ExecuteAsync(HealthScope, NextSource(),
            new("create", EventType: "glucose", DeadlineMinute: 540, GraceMinutes: 30), default);
        var otherPlace = HealthScope with { TopicId = 8, ActorUserId = 222 };
        var reminderTask = right.Reminders.AdmitAsync(otherPlace, NextSource(),
            new("synthetic task", Initial.AddHours(1), null, 180), default);
        await Task.WhenAll(checkTask, reminderTask);
        var check = await checkTask; var reminder = await reminderTask;
        ((check.Result == "preview" ? 1 : 0) + (reminder != null ? 1 : 0)).ShouldBe(1);
        if (reminder != null) check.Result.ShouldBe("capacity");
        await using var read = Open();
        (await read.Db.Set<Reminder>().CountAsync() + await read.Db.Set<Expectation>().CountAsync()).ShouldBe(200);
    }

    [Fact]
    public async Task Paused_schedule_consumes_capacity_but_edit_draft_does_not_add_second_slot()
    {
        await SeedAsync(); var active = await ActiveAsync(); await using var session = Open();
        for (var i = 0; i < 19; i++) session.Db.Add(ReminderRow(Guid.NewGuid(), Initial.AddHours(1), HealthScope));
        await session.Db.SaveChangesAsync();
        var edit = await session.Store.ExecuteAsync(HealthScope, NextSource(),
            new("edit", active.Id, DeadlineMinute: 600, GraceMinutes: 0), default);
        edit.Result.ShouldBe("preview");
        (await session.Store.ExecuteAsync(HealthScope, NextSource(),
            new("create", EventType: "insulin", DeadlineMinute: 540, GraceMinutes: 0), default)).Result.ShouldBe("capacity");
        (await session.Store.ExecuteAsync(HealthScope, NextSource(), new("pause", active.Id), default)).Result.ShouldBe("pause");
        (await session.Reminders.AdmitAsync(HealthScope, NextSource(), new("synthetic additional", Initial.AddHours(1), null, 180), default)).ShouldBeNull();
        await using var read = Open();
        (await read.Db.Set<Reminder>().CountAsync()).ShouldBe(19);
        (await read.Db.Set<Expectation>().CountAsync()).ShouldBe(1);
        (await read.Db.Set<Expectation>().SingleAsync()).Status.ShouldBe("paused");
    }

    [Fact]
    public async Task Mixed_selection_uses_due_time_then_kind_then_id_and_is_bounded_to_two_hundred()
    {
        await SeedAsync(); var check = await ActiveAsync(); Clock.UtcNow = Due;
        var earlierId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var sameId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        await using var session = Open();
        session.Db.Add(ReminderRow(earlierId, Due.AddSeconds(-1), HealthScope));
        session.Db.Add(ReminderRow(sameId, Due, HealthScope));
        for (var i = 0; i < 199; i++) session.Db.Add(ReminderRow(Guid.NewGuid(), Due.AddSeconds(-2), HealthScope));
        await session.Db.SaveChangesAsync();
        var selected = await session.Dispatch.SelectAsync(HealthBot, default);
        selected.Count.ShouldBe(200); selected.Take(199).All(x => x.DueAt == DateTimeOffset.Parse("2032-02-10T06:29:58Z")).ShouldBeTrue();
        selected[199].ShouldBe(new NonurgentCandidate("reminder", earlierId, DateTimeOffset.Parse("2032-02-10T06:29:59Z")));
        await session.Db.Set<Reminder>().Where(x => x.Id != earlierId && x.Id != sameId).ExecuteDeleteAsync();
        selected = await session.Dispatch.SelectAsync(HealthBot, default);
        selected.ShouldBe(new[] { new NonurgentCandidate("reminder", earlierId, DateTimeOffset.Parse("2032-02-10T06:29:59Z")),
            new NonurgentCandidate("expectation", check.Id, Due), new NonurgentCandidate("reminder", sameId, Due) });
    }
}
