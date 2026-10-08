using Assistant.Application.Expectations;
using Assistant.Application.Families;
using Assistant.Application.Telegram;
using Assistant.Domain.Expectations;
using Assistant.Domain.Health;
using Assistant.Domain.Reminders;
using Assistant.Domain.Vet;
using Assistant.Infrastructure.Expectations;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Reminders;
using Assistant.IntegrationTests.Host;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Assistant.IntegrationTests.Expectations;

public sealed class NonurgentDispatchStoreTests : ExpectationTestBase
{
    private static readonly DateTimeOffset Due = DateTimeOffset.Parse("2032-02-10T06:30:00Z");

    [Fact]
    public async Task Expectation_cleanup_is_child_first_bounded_and_retains_recent_spent_attempt()
    {
        await SeedAsync(); var active = await ActiveAsync();
        Clock.UtcNow = Initial.AddDays(100);
        var old = Initial.AddDays(-1);
        await using (var seed = Open())
        {
            await seed.Db.Set<Expectation>().Where(x => x.Id == active.Id).ExecuteUpdateAsync(u =>
                u.SetProperty(x => x.Status, "cancelled").SetProperty(x => x.UpdatedAt, old));
            await seed.Db.Set<ExpectationVersion>().ExecuteUpdateAsync(u => u.SetProperty(x => x.CreatedAt, old));
            await seed.Db.Set<ExpectationDraft>().ExecuteUpdateAsync(u => u.SetProperty(x => x.UpdatedAt, old));
            for (var i = 0; i < 101; i++)
            {
                var date = new DateOnly(2031, 1, 1).AddDays(i);
                seed.Db.Add(new ExpectationOccurrence { FamilyId = 11, ExpectationId = active.Id,
                    LocalDate = date, Version = 1, WindowStart = old, DueAt = old, ExpiresAt = old,
                    Outcome = "dispatch-unknown", UpdatedAt = old });
                seed.Db.Add(new ExpectationAttempt { Id = Guid.NewGuid(), FamilyId = 11, Role = "health",
                    ExpectationId = active.Id, LocalDate = date, StartedAt = i == 100 ? Clock.UtcNow : old });
            }
            await seed.Db.SaveChangesAsync();
        }
        await using var run = Open(); await run.Dispatch.CleanupAsync(HealthBot, default);
        (await run.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
        (await run.Db.Set<ExpectationOccurrence>().CountAsync()).ShouldBe(1);
        (await run.Db.Set<ExpectationVersion>().CountAsync()).ShouldBe(1);
        (await run.Db.Set<Expectation>().CountAsync()).ShouldBe(1);
        (await run.Db.Set<ExpectationAttempt>().AsNoTracking().SingleAsync()).StartedAt.ShouldBe(Clock.UtcNow);
        Clock.UtcNow = Clock.UtcNow.AddDays(61);
        await run.Dispatch.CleanupAsync(HealthBot, default);
        (await run.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
        (await run.Db.Set<ExpectationOccurrence>().CountAsync()).ShouldBe(0);
        (await run.Db.Set<ExpectationVersion>().CountAsync()).ShouldBe(0);
        (await run.Db.Set<ExpectationDraft>().CountAsync()).ShouldBe(0);
        (await run.Db.Set<Expectation>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("paused")]
    public async Task Cleanup_preserves_current_and_future_versions_and_last_summary(string status)
    {
        await SeedAsync(); var active = await ActiveAsync(); Clock.UtcNow = Initial.AddDays(100);
        await using (var seed = Open())
        {
            var e = await seed.Db.Set<Expectation>().SingleAsync();
            e.Status = status; e.CurrentVersion = 2; e.NextVersion = 3; e.LastVersion = 3;
            e.LastDate = new DateOnly(2032, 2, 10); e.LastOutcome = "satisfied";
            e.SkippedFrom = new DateOnly(2032, 2, 11); e.SkippedThrough = new DateOnly(2032, 5, 1);
            var initialVersion = await seed.Db.Set<ExpectationVersion>().SingleAsync();
            initialVersion.CreatedAt = Initial;
            seed.Db.Add(new ExpectationVersion { FamilyId = 11, ExpectationId = active.Id, Number = 2,
                DeadlineMinute = 600, GraceMinutes = 10, EffectiveFrom = new DateOnly(2032, 2, 11), CreatedAt = Initial });
            seed.Db.Add(new ExpectationVersion { FamilyId = 11, ExpectationId = active.Id, Number = 3,
                DeadlineMinute = 660, GraceMinutes = 20, EffectiveFrom = new DateOnly(2032, 5, 21), CreatedAt = Initial });
            await seed.Db.SaveChangesAsync();
        }
        await using var run = Open(); await run.Dispatch.CleanupAsync(HealthBot, default);
        (await run.Db.Set<ExpectationVersion>().OrderBy(x => x.Number).Select(x => x.Number).ToListAsync())
            .ShouldBe(new[] { 2, 3 });
        var retained = await run.Db.Set<Expectation>().SingleAsync(); retained.Status.ShouldBe(status);
        retained.CurrentVersion.ShouldBe(2); retained.NextVersion.ShouldBe(3); retained.LastVersion.ShouldBe(3);
        retained.LastDate.ShouldBe(new DateOnly(2032, 2, 10)); retained.LastOutcome.ShouldBe("satisfied");
        retained.SkippedFrom.ShouldBe(new DateOnly(2032, 2, 11)); retained.SkippedThrough.ShouldBe(new DateOnly(2032, 5, 1));
    }

    private async Task<long> FactAsync(string role, string occurred, int? topic = 7, bool deleted = false, long? profile = null,
        string? scopeMismatch = null)
    {
        await using var session = Open();
        if (role == "health")
        {
            var row = new HealthEvent { FamilyId = 11, ProfileId = profile ?? 333, Type = "glucose",
                SubjectTag = "synthetic_subject", OccurredAt = DateTimeOffset.Parse(occurred), OccurredAtSource = "stated",
                Payload = "{\"mmolL\":6.2}", BotId = 999, ChatId = -100, TopicId = topic, RecordedByUserId = 222,
                CreatedAt = Clock.UtcNow, UpdatedAt = Clock.UtcNow, DeletedAt = deleted ? Clock.UtcNow : null };
            switch (scopeMismatch)
            {
                case "family": row.FamilyId = 12; break;
                case "type": row.Type = "insulin"; break;
                case "chat": row.ChatId = -101; break;
                case "telegram-bot": row.BotId = 997; break;
                case "profile": row.ProfileId = 334; break;
            }
            session.Db.Add(row); await session.Db.SaveChangesAsync(); return row.Id;
        }
        var vet = new VetEvent { FamilyId = 11, BotDbId = 23, TelegramBotId = 998, ProfileId = profile ?? 444,
            ChatId = -100, TopicId = topic, EventType = "glucose", Value = 6.2m, Unit = "mmol/L",
            OccurredAt = DateTimeOffset.Parse(occurred), LocalTime = "2032-02-10 08:30", TimeZoneSnapshot = "UTC",
            OccurredAtSource = "stated", ValueUnitSource = "profile", SourceKind = "text", SourceId = Guid.NewGuid(),
            InputRevisionId = Guid.NewGuid(), ExtractionResultId = Guid.NewGuid(), SourceAuthorUserId = 222,
            SourceMessageDbId = 551, TelegramMessageId = 101, CreatedAt = Clock.UtcNow, UpdatedAt = Clock.UtcNow,
            DeletedAt = deleted ? Clock.UtcNow : null };
        switch (scopeMismatch)
        {
            case "family": vet.FamilyId = 12; break;
            case "type": vet.EventType = "insulin"; break;
            case "chat": vet.ChatId = -101; break;
            case "telegram-bot": vet.TelegramBotId = 997; break;
            case "internal-bot": vet.BotDbId = 24; break;
            case "profile":
                session.Db.VetProfiles.Add(new VetProfile { Id = 445, FamilyId = 11, BotDbId = 24,
                    Name = "Synthetic second animal", TimeZone = "UTC", UpdatedAt = Initial });
                vet.ProfileId = 445; break;
        }
        session.Db.Add(vet); await session.Db.SaveChangesAsync(); return vet.Id;
    }

    [Theory]
    [InlineData("health", "matching")]
    [InlineData("health", "family")]
    [InlineData("health", "type")]
    [InlineData("health", "chat")]
    [InlineData("health", "telegram-bot")]
    [InlineData("health", "profile")]
    [InlineData("vet", "matching")]
    [InlineData("vet", "family")]
    [InlineData("vet", "type")]
    [InlineData("vet", "chat")]
    [InlineData("vet", "telegram-bot")]
    [InlineData("vet", "profile")]
    [InlineData("vet", "internal-bot")]
    public async Task Each_fact_scope_predicate_independently_blocks_matching(string role, string mismatch)
    {
        await SeedAsync(); var active = await ActiveAsync(role == "health" ? HealthScope : VetScope); Clock.UtcNow = Due;
        var fact = await FactAsync(role, "2032-02-10T05:30:00Z", scopeMismatch: mismatch);
        var bot = role == "health" ? HealthBot : new Assistant.Application.Messages.ReceivingBot(23, 998, "synthetic_vet_bot", 11, "vet");
        await using var session = Open();
        var claim = await session.Dispatch.ClaimAsync(bot, new("expectation", active.Id, Due), default);
        var occurrence = await session.Db.Set<ExpectationOccurrence>().SingleAsync();
        if (mismatch == "matching")
        {
            claim.ShouldBeNull(); occurrence.Outcome.ShouldBe("satisfied"); occurrence.MatchedEventId.ShouldBe(fact);
            (await session.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
        }
        else
        {
            claim.ShouldNotBeNull().Id.ShouldBe(active.Id); occurrence.Outcome.ShouldBe("dispatch-unknown"); occurrence.MatchedEventId.ShouldBeNull();
            (await session.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
        }
    }

    [Theory]
    [InlineData("health", "2032-02-09T20:59:59.999Z", false)]
    [InlineData("health", "2032-02-09T21:00:00Z", true)]
    [InlineData("health", "2032-02-10T06:30:00Z", true)]
    [InlineData("health", "2032-02-10T06:30:00.001Z", false)]
    [InlineData("vet", "2032-02-09T20:59:59.999Z", false)]
    [InlineData("vet", "2032-02-09T21:00:00Z", true)]
    [InlineData("vet", "2032-02-10T06:30:00Z", true)]
    [InlineData("vet", "2032-02-10T06:30:00.001Z", false)]
    public async Task Inclusive_fact_window_uses_occurrence_time_for_both_roles(string role, string occurred, bool satisfies)
    {
        await SeedAsync(); var scope = role == "health" ? HealthScope : VetScope;
        var bot = role == "health" ? HealthBot : new Assistant.Application.Messages.ReceivingBot(23, 998, "synthetic_vet_bot", 11, "vet");
        var preview = await ActiveAsync(scope); Clock.UtcNow = Due.AddMinutes(15);
        var fact = await FactAsync(role, occurred); await using var session = Open();
        var dispatch = await session.Dispatch.ClaimAsync(bot, new("expectation", preview.Id, Due), default);
        var occurrence = await session.Db.Set<ExpectationOccurrence>().AsNoTracking().SingleAsync();
        occurrence.LocalDate.ShouldBe(new DateOnly(2032, 2, 10)); occurrence.Version.ShouldBe(1);
        occurrence.WindowStart.ShouldBe(DateTimeOffset.Parse("2032-02-09T21:00:00Z")); occurrence.DueAt.ShouldBe(Due);
        occurrence.ExpiresAt.ShouldBe(DateTimeOffset.Parse("2032-02-10T21:00:00Z"));
        if (satisfies)
        {
            dispatch.ShouldBeNull(); occurrence.Outcome.ShouldBe("satisfied"); occurrence.MatchedEventId.ShouldBe(fact);
            occurrence.MatchedEventKind.ShouldBe(role); (await session.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
        }
        else
        {
            dispatch.ShouldNotBeNull().Id.ShouldBe(preview.Id); occurrence.Outcome.ShouldBe("dispatch-unknown");
            occurrence.MatchedEventId.ShouldBeNull(); (await session.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
        }
        (await session.Db.Set<Reminder>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, 7, false)]
    [InlineData(7, null, false)]
    [InlineData(7, 8, false)]
    public async Task Null_topic_is_exact_and_another_approved_author_can_satisfy(int? scheduleTopic, int? factTopic, bool satisfies)
    {
        await SeedAsync(); var active = await ActiveAsync(HealthScope with { TopicId = scheduleTopic });
        Clock.UtcNow = Due; var id = await FactAsync("health", "2032-02-10T05:30:00Z", factTopic);
        await using var session = Open();
        var claim = await session.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Due), default);
        var occurrence = await session.Db.Set<ExpectationOccurrence>().SingleAsync();
        if (satisfies) { claim.ShouldBeNull(); occurrence.MatchedEventId.ShouldBe(id); occurrence.Outcome.ShouldBe("satisfied"); }
        else { claim.ShouldNotBeNull().TopicId.ShouldBe(scheduleTopic); occurrence.MatchedEventId.ShouldBeNull(); }
    }

    [Theory]
    [InlineData("wrong-profile")]
    [InlineData("deleted")]
    [InlineData("pending")]
    public async Task Wrong_profile_deleted_or_pending_health_fact_does_not_satisfy(string kind)
    {
        await SeedAsync(); var active = await ActiveAsync(); Clock.UtcNow = Due; await using var session = Open();
        if (kind == "pending")
            session.Db.PendingRecords.Add(new PendingRecord { FamilyId = 11, ProfileId = 333, BotId = 999,
                ChatId = -100, TopicId = 7, TelegramMessageId = 101, RequestedByUserId = 222,
                Events = "[{\"type\":\"glucose\",\"occurredAt\":\"2032-02-10T05:30:00Z\",\"occurredAtSource\":\"stated\",\"payloadJson\":\"{\\\"mmolL\\\":6.2}\"}]",
                Status = "pending", CreatedAt = Initial });
        else await FactAsync("health", "2032-02-10T05:30:00Z", deleted: kind == "deleted", profile: kind == "wrong-profile" ? 334 : 333);
        await session.Db.SaveChangesAsync();
        (await session.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Due), default)).ShouldNotBeNull().Id.ShouldBe(active.Id);
        (await session.Db.Set<ExpectationOccurrence>().SingleAsync()).Outcome.ShouldBe("dispatch-unknown");
        (await session.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Early_fact_does_not_close_day_and_deleted_fact_at_due_is_missing()
    {
        await SeedAsync(); var active = await ActiveAsync(); Clock.UtcNow = Due.AddMinutes(-30);
        var id = await FactAsync("health", "2032-02-10T05:30:00Z"); await using var early = Open();
        (await early.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Due), default)).ShouldBeNull();
        (await early.Db.Set<ExpectationOccurrence>().CountAsync()).ShouldBe(0);
        await early.Db.Events.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.DeletedAt, Clock.UtcNow));
        Clock.UtcNow = Due; await using var due = Open();
        (await due.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Due), default)).ShouldNotBeNull();
        (await due.Db.Set<ExpectationOccurrence>().SingleAsync()).Outcome.ShouldBe("dispatch-unknown");
    }

    [Fact]
    public async Task Quiet_deferral_rechecks_late_confirmed_fact_before_creating_attempt()
    {
        await SeedAsync(); var active = await ActiveAsync(); await using (var setup = Open())
            await setup.Reminders.SetPreferencesAsync(HealthScope, NextSource(), new(180, 540, 600), default);
        Clock.UtcNow = Due; await using (var first = Open())
        {
            (await first.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Due), default)).ShouldBeNull();
            (await first.Db.Set<ExpectationOccurrence>().SingleAsync()).Outcome.ShouldBe("pending");
            (await first.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
        }
        Clock.UtcNow = DateTimeOffset.Parse("2032-02-10T06:45:00Z");
        var id = await FactAsync("health", "2032-02-10T05:30:00Z");
        Clock.UtcNow = DateTimeOffset.Parse("2032-02-10T07:00:00Z"); await using var second = Open();
        (await second.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Due), default)).ShouldBeNull();
        var occurrence = await second.Db.Set<ExpectationOccurrence>().SingleAsync();
        occurrence.Outcome.ShouldBe("satisfied"); occurrence.MatchedEventId.ShouldBe(id);
        (await second.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Restart_summarizes_skipped_dates_and_only_claims_current_unexpired_date()
    {
        await SeedAsync(); var active = await ActiveAsync(); Clock.UtcNow = DateTimeOffset.Parse("2032-02-20T06:30:00Z");
        await using var restart = Open();
        (await restart.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Due), default)).ShouldNotBeNull();
        var row = await RowAsync(active.Id); row.SkippedFrom.ShouldBe(new DateOnly(2032, 2, 10)); row.SkippedThrough.ShouldBe(new DateOnly(2032, 2, 19));
        row.LastDate.ShouldBe(new DateOnly(2032, 2, 20)); row.NextDate.ShouldBe(new DateOnly(2032, 2, 21));
        var occurrence = await restart.Db.Set<ExpectationOccurrence>().SingleAsync(); occurrence.LocalDate.ShouldBe(new DateOnly(2032, 2, 20));
        occurrence.Outcome.ShouldBe("dispatch-unknown"); (await restart.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_dispatch_never_retries_same_date_and_tomorrow_has_independent_identity()
    {
        await SeedAsync(); var active = await ActiveAsync(); Clock.UtcNow = Due; await using (var first = Open())
        {
            (await first.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Due), default)).ShouldNotBeNull();
        }
        await using (var restart = Open())
            (await restart.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Due), default)).ShouldBeNull();
        Clock.UtcNow = DateTimeOffset.Parse("2032-02-11T06:30:00Z"); await using var tomorrow = Open();
        (await tomorrow.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Clock.UtcNow), default)).ShouldNotBeNull();
        var attempts = await tomorrow.Db.Set<ExpectationAttempt>().OrderBy(x => x.LocalDate).ToArrayAsync();
        attempts.Length.ShouldBe(2); attempts[0].LocalDate.ShouldBe(new DateOnly(2032, 2, 10)); attempts[1].LocalDate.ShouldBe(new DateOnly(2032, 2, 11));
        attempts.Select(x => x.Id).Distinct().Count().ShouldBe(2); attempts.All(x => x.Outcome == "unknown").ShouldBeTrue();
    }

    [Fact]
    public async Task Late_completion_of_old_attempt_preserves_latest_satisfied_summary()
    {
        await SeedAsync(); var active = await ActiveAsync(); Clock.UtcNow = Due; NonurgentDispatch older;
        await using (var first = Open()) older = (await first.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Due), default)).ShouldNotBeNull();
        Clock.UtcNow = DateTimeOffset.Parse("2032-02-11T06:30:00Z"); var fact = await FactAsync("health", "2032-02-11T05:30:00Z");
        await using (var today = Open())
            (await today.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Clock.UtcNow), default)).ShouldBeNull();
        await using var completion = Open(); await completion.Dispatch.CompleteAsync(HealthBot, older, 900, default);
        var row = await RowAsync(active.Id); row.LastDate.ShouldBe(new DateOnly(2032, 2, 11)); row.LastOutcome.ShouldBe("satisfied");
        row.LastTelegramMessageId.ShouldBeNull();
        var old = await completion.Db.Set<ExpectationOccurrence>().SingleAsync(x => x.LocalDate == new DateOnly(2032, 2, 10)); old.Outcome.ShouldBe("sent");
        var current = await completion.Db.Set<ExpectationOccurrence>().SingleAsync(x => x.LocalDate == new DateOnly(2032, 2, 11));
        current.Outcome.ShouldBe("satisfied"); current.MatchedEventId.ShouldBe(fact);
        (await completion.Db.Set<ExpectationAttempt>().SingleAsync()).TelegramMessageId.ShouldBe(900);
    }

    [Fact]
    public async Task Cleanup_removes_at_most_one_hundred_old_attempts_and_keeps_parent_until_children_are_gone()
    {
        await SeedAsync(); var old = Initial.AddDays(-61); var id = Guid.NewGuid(); await using var session = Open();
        session.Db.Add(new Reminder { Id = id, FamilyId = 11, BotDbId = 22, BotId = 999, Role = "health", ChatId = -100,
            TopicId = 7, ChatType = "supergroup", ActorUserId = 111, SourceMessageId = 501, Text = "synthetic archived task",
            Status = "unknown", DueAt = old, CreatedAt = old, UpdatedAt = old });
        for (var i = 0; i < 101; i++) session.Db.Add(new ReminderAttempt { Id = Guid.NewGuid(), ReminderId = id,
            FamilyId = 11, Role = "health", OccurrenceDueAt = old.AddSeconds(i), StartedAt = old, Outcome = "unknown" });
        await session.Db.SaveChangesAsync(); session.Db.ChangeTracker.Clear();
        await session.Dispatch.CleanupAsync(HealthBot, default);
        (await session.Db.Set<ReminderAttempt>().CountAsync()).ShouldBe(1); (await session.Db.Set<Reminder>().CountAsync()).ShouldBe(1);
        session.Db.ChangeTracker.Clear(); await session.Dispatch.CleanupAsync(HealthBot, default);
        (await session.Db.Set<ReminderAttempt>().CountAsync()).ShouldBe(0);
        session.Db.ChangeTracker.Clear(); await session.Dispatch.CleanupAsync(HealthBot, default);
        (await session.Db.Set<Reminder>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Cleanup_preserves_current_day_spent_budget_even_when_parent_is_old()
    {
        await SeedAsync(); var active = await ActiveAsync(); Clock.UtcNow = Due;
        var old = Initial.AddDays(-61); var id = Guid.NewGuid(); await using var session = Open();
        session.Db.Add(new Reminder { Id = id, FamilyId = 11, BotDbId = 22, BotId = 999, Role = "health", ChatId = -100,
            TopicId = 7, ChatType = "supergroup", ActorUserId = 111, SourceMessageId = 501, Text = "synthetic archived task",
            Status = "unknown", DueAt = old, CreatedAt = old, UpdatedAt = old });
        for (var i = 0; i < 10; i++) session.Db.Add(new ReminderAttempt { Id = Guid.NewGuid(), ReminderId = id,
            FamilyId = 11, Role = "health", OccurrenceDueAt = Due.AddSeconds(-i), StartedAt = Due, Outcome = "unknown" });
        await session.Db.SaveChangesAsync(); session.Db.ChangeTracker.Clear(); await session.Dispatch.CleanupAsync(HealthBot, default);
        (await session.Db.Set<ReminderAttempt>().CountAsync()).ShouldBe(10); (await session.Db.Set<Reminder>().CountAsync()).ShouldBe(1);
        (await session.Dispatch.ClaimAsync(HealthBot, new("expectation", active.Id, Due), default)).ShouldBeNull();
        (await session.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
    }

    private sealed class ProbeTransport(Func<CancellationToken, Task> probe) : FakeTelegramClient, ITelegramClient
    {
        public new async Task<int> SendTextAsync(long chat, int? topic, string text, int? reply, CancellationToken ct)
        { await probe(ct); return await base.SendTextAsync(chat, topic, text, reply, ct); }
    }

    [Fact]
    public async Task Worker_transport_observes_committed_fence_and_no_database_ordering_lock()
    {
        await SeedAsync(); var active = await ActiveAsync(); Clock.UtcNow = Due; var probes = 0;
        using var services = new ServiceCollection().AddScoped<ICurrentFamily, CurrentFamily>()
            .AddScoped(p =>
            {
                var options = new DbContextOptionsBuilder<AssistantDbContext>(); AssistantDbContext.Configure(options, ConnectionString);
                return new AssistantDbContext(options.Options, p.GetRequiredService<ICurrentFamily>());
            }).AddScoped<INonurgentDispatchStore>(p => new NonurgentDispatchStore(p.GetRequiredService<AssistantDbContext>(),
                p.GetRequiredService<ICurrentFamily>(), Clock)).BuildServiceProvider();
        var transport = new ProbeTransport(async ct =>
        {
            probes++; await using var connection = new NpgsqlConnection(ConnectionString); await connection.OpenAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            foreach (var sql in new[] { "SELECT pg_try_advisory_xact_lock(61008, hashtext('11'))",
                "SELECT pg_try_advisory_xact_lock(999::bigint)", "SELECT pg_try_advisory_xact_lock(61009, hashtext('11:22:health'))" })
            {
                await using var command = new NpgsqlCommand(sql, connection, tx);
                ((bool)(await command.ExecuteScalarAsync(ct))!).ShouldBeTrue();
            }
            await using var read = new NpgsqlCommand("SELECT count(*) FROM expectation_attempts WHERE outcome = 'unknown'", connection, tx);
            ((long)(await read.ExecuteScalarAsync(ct))!).ShouldBe(1);
            await tx.RollbackAsync(ct);
        });
        var loop = new ReminderBackgroundLoop(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReminderBackgroundLoop>.Instance);
        await loop.RunPassAsync(HealthBot, transport, default);
        probes.ShouldBe(1); transport.SentMessages.ShouldHaveSingleItem().TopicId.ShouldBe(7);
        await using var result = Open(); var attempt = await result.Db.Set<ExpectationAttempt>().SingleAsync();
        attempt.ExpectationId.ShouldBe(active.Id); attempt.Outcome.ShouldBe("sent"); attempt.TelegramMessageId.ShouldBe(1);
    }
}
