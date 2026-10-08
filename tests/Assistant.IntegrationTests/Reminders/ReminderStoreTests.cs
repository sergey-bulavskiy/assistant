using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Reminders;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Domain.Reminders;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Reminders;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Reminders;

public sealed class ReminderStoreTests : IntegrationTestBase
{
    private static readonly DateTimeOffset Initial = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly ReminderScope Scope = new(11, 22, 999, "general", -1001111111111, 7, "supergroup", 111);
    private static readonly ReceivingBot Bot = new(22, 999, "test_bot", 11, "general");
    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = Initial; }
    private readonly Clock _clock = new();
    private readonly CurrentFamily _family = new();
    private ReminderStore Store(AssistantDbContext db) => new(db, _family, _clock);
    private AssistantDbContext Context()
    {
        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        return new(options.Options, _family);
    }
    private async Task SeedAsync()
    {
        Db.Families.Add(new Family { Id = 11, Name = "Synthetic family", CreatedAt = Initial });
        Db.Bots.AddRange(new Bot { Id = 22, FamilyId = 11, TelegramBotId = 999, Username = "test_bot",
            Role = "general", Status = BotStatus.Active, CreatedAt = Initial },
            new Bot { Id = 23, FamilyId = 11, TelegramBotId = 998, Username = "second_test_bot",
            Role = "general", Status = BotStatus.Active, CreatedAt = Initial });
        Db.FamilyMembers.AddRange(new FamilyMember { FamilyId = 11, TelegramUserId = 111,
            Status = FamilyMemberStatus.Approved, CreatedAt = Initial, UpdatedAt = Initial },
            new FamilyMember { FamilyId = 11, TelegramUserId = 222,
            Status = FamilyMemberStatus.Approved, CreatedAt = Initial, UpdatedAt = Initial });
        Db.Places.AddRange(new Place { BotId = 22, ChatId = Scope.ChatId, TopicId = 7,
            Status = PlaceStatus.Approved, CreatedAt = Initial },
            new Place { BotId = 22, ChatId = Scope.ChatId, TopicId = 8,
            Status = PlaceStatus.Approved, CreatedAt = Initial },
            new Place { BotId = 23, ChatId = Scope.ChatId, TopicId = 7,
            Status = PlaceStatus.Approved, CreatedAt = Initial });
        await Db.SaveChangesAsync(); _family.Set(11);
    }
    private async Task<ReminderItem> DraftAsync(int source = 100, ReminderScope? scope = null,
        int? daily = null, int offset = 0)
    {
        await using var db = Context();
        return (await Store(db).AdmitAsync(scope ?? Scope, source,
            new("synthetic task", _clock.UtcNow.AddMinutes(10), daily, offset), default))!;
    }
    private async Task<ReminderItem> ActiveAsync(int source = 100, ReminderScope? scope = null,
        int? daily = null, int offset = 0)
    {
        var s = scope ?? Scope; var draft = await DraftAsync(source, s, daily, offset);
        await using var db = Context(); var store = Store(db);
        (await store.BeginPreviewAsync(s, draft.Id, default))!.Id.ShouldBe(draft.Id);
        await store.BindPreviewAsync(s, draft.Id, 700 + source, default);
        (await store.SaveAsync(s, draft.Id, 700 + source, default)).ShouldBe("saved");
        return draft;
    }
    private async Task<Reminder> RowAsync(Guid id)
    {
        await using var db = Context();
        return await db.Set<Reminder>().AsNoTracking().SingleAsync(x => x.Id == id);
    }

    [Fact]
    public async Task Admission_replay_preserves_first_identity_actor_text_and_schedule()
    {
        await SeedAsync(); var first = await DraftAsync();
        await using var db = Context();
        var replay = await Store(db).AdmitAsync(Scope, 100,
            new("changed text", Initial.AddHours(1), 800, 180), default);
        replay.ShouldBe(first);
        (await db.Set<Reminder>().CountAsync()).ShouldBe(1);
        (await RowAsync(first.Id)).Status.ShouldBe("draft");
    }

    [Fact]
    public async Task Concurrent_admission_creates_one_durable_draft()
    {
        await SeedAsync();
        await using var left = Context(); await using var right = Context();
        var request = new ReminderRequest("synthetic task", Initial.AddMinutes(10), null, 0);
        var results = await Task.WhenAll(Store(left).AdmitAsync(Scope, 100, request, default),
            Store(right).AdmitAsync(Scope, 100, request, default));
        results[0]!.Id.ShouldBe(results[1]!.Id);
        await using var read = Context(); (await read.Set<Reminder>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Preview_fence_survives_restart_and_unbound_or_wrong_message_cannot_save()
    {
        await SeedAsync(); var draft = await DraftAsync();
        await using (var db = Context())
        {
            (await Store(db).BeginPreviewAsync(Scope, draft.Id, default))!.Id.ShouldBe(draft.Id);
            (await Store(db).SaveAsync(Scope, draft.Id, 700, default)).ShouldBe("unavailable");
        }
        await using (var restarted = Context())
        {
            (await Store(restarted).BeginPreviewAsync(Scope, draft.Id, default)).ShouldBeNull();
            await Store(restarted).BindPreviewAsync(Scope, draft.Id, 700, default);
            (await Store(restarted).SaveAsync(Scope, draft.Id, 701, default)).ShouldBe("unavailable");
            (await Store(restarted).SaveAsync(Scope, draft.Id, 700, default)).ShouldBe("saved");
        }
        (await RowAsync(draft.Id)).Status.ShouldBe("active");
    }

    [Fact]
    public async Task Concurrent_save_returns_one_active_identity_and_repeated_save_is_stable()
    {
        await SeedAsync(); var draft = await DraftAsync();
        await using (var db = Context())
        {
            await Store(db).BeginPreviewAsync(Scope, draft.Id, default);
            await Store(db).BindPreviewAsync(Scope, draft.Id, 700, default);
        }
        await using var left = Context(); await using var right = Context();
        (await Task.WhenAll(Store(left).SaveAsync(Scope, draft.Id, 700, default),
            Store(right).SaveAsync(Scope, draft.Id, 700, default))).ShouldBe(["saved", "saved"]);
        (await RowAsync(draft.Id)).Status.ShouldBe("active");
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("topic")]
    [InlineData("bot")]
    public async Task Other_approved_scope_cannot_list_save_or_cancel(string kind)
    {
        await SeedAsync(); var item = await ActiveAsync();
        var other = kind switch { "actor" => Scope with { ActorUserId = 222 },
            "topic" => Scope with { TopicId = 8 }, _ => Scope with { BotDbId = 23, BotId = 998 } };
        await using var db = Context(); var store = Store(db);
        (await store.ListAsync(other, default)).ShouldBeEmpty();
        (await store.SaveAsync(other, item.Id, 800, default)).ShouldBe("unavailable");
        (await store.CancelAsync(other, item.Id, default)).ShouldBe("unavailable");
        (await RowAsync(item.Id)).Status.ShouldBe("active");
    }

    [Fact]
    public async Task Unset_and_mismatched_family_fail_closed()
    {
        await SeedAsync(); await ActiveAsync();
        await using var db = Context();
        await Should.ThrowAsync<InvalidOperationException>(() => Store(db).ListAsync(Scope with { FamilyId = 12 }, default));
        var unset = new CurrentFamily();
        await Should.ThrowAsync<InvalidOperationException>(() => new ReminderStore(db, unset, _clock).ListAsync(Scope, default));
        (await Store(db).ListAsync(Scope, default)).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Save_rejects_due_now_or_past(int seconds)
    {
        await SeedAsync(); var item = await DraftAsync();
        await using var db = Context(); var store = Store(db);
        await store.BeginPreviewAsync(Scope, item.Id, default); await store.BindPreviewAsync(Scope, item.Id, 700, default);
        _clock.UtcNow = item.DueAt.AddSeconds(-seconds);
        (await store.SaveAsync(Scope, item.Id, 700, default)).ShouldBe("expired");
        (await RowAsync(item.Id)).Status.ShouldBe("draft");
    }

    [Fact]
    public async Task Cancel_before_claim_prevents_attempt_and_is_idempotent()
    {
        await SeedAsync(); var item = await ActiveAsync();
        await using var db = Context(); var store = Store(db);
        (await store.CancelAsync(Scope, item.Id, default)).ShouldBe("cancelled");
        (await store.CancelAsync(Scope, item.Id, default)).ShouldBe("cancelled");
        _clock.UtcNow = item.DueAt;
        (await store.ClaimDueAsync(Bot, default)).ShouldBeNull();
        (await db.Set<ReminderAttempt>().CountAsync()).ShouldBe(0);
        (await RowAsync(item.Id)).Status.ShouldBe("cancelled");
    }

    [Fact]
    public async Task Due_boundary_and_restart_fence_allow_one_exact_occurrence()
    {
        await SeedAsync(); var item = await ActiveAsync();
        await using (var early = Context())
        {
            _clock.UtcNow = item.DueAt.AddTicks(-1);
            (await Store(early).ClaimDueAsync(Bot, default)).ShouldBeNull();
        }
        ReminderDispatch dispatch;
        await using (var due = Context())
        {
            _clock.UtcNow = item.DueAt;
            dispatch = (await Store(due).ClaimDueAsync(Bot, default))!;
            dispatch.ChatId.ShouldBe(Scope.ChatId); dispatch.TopicId.ShouldBe(7);
            dispatch.Text.ShouldBe("⏰ Напоминание: synthetic task");
        }
        await using var restarted = Context();
        (await Store(restarted).ClaimDueAsync(Bot, default)).ShouldBeNull();
        (await restarted.Set<ReminderAttempt>().SingleAsync()).Outcome.ShouldBe("unknown");
        await Store(restarted).CompleteAsync(Bot, dispatch, 900, default);
        await Store(restarted).CompleteAsync(Bot, dispatch, 901, default);
        var row = await RowAsync(item.Id); row.Status.ShouldBe("sent"); row.LastTelegramMessageId.ShouldBe(900);
        (await restarted.Set<ReminderAttempt>().AsNoTracking().SingleAsync()).TelegramMessageId.ShouldBe(900);
    }

    [Fact]
    public async Task Concurrent_due_claims_have_one_winner_and_one_attempt()
    {
        await SeedAsync(); var item = await ActiveAsync(); _clock.UtcNow = item.DueAt;
        await using var left = Context(); await using var right = Context();
        var results = await Task.WhenAll(Store(left).ClaimDueAsync(Bot, default), Store(right).ClaimDueAsync(Bot, default));
        results.Count(x => x != null).ShouldBe(1);
        await using var read = Context();
        (await read.Set<ReminderAttempt>().SingleAsync()).ReminderId.ShouldBe(item.Id);
    }

    [Fact]
    public async Task Claim_before_cancel_reports_started_and_completion_cannot_reactivate()
    {
        await SeedAsync(); var item = await ActiveAsync(daily: 730); _clock.UtcNow = item.DueAt;
        await using var db = Context(); var store = Store(db);
        var dispatch = (await store.ClaimDueAsync(Bot, default))!;
        (await store.CancelAsync(Scope, item.Id, default)).ShouldBe("cancelled_started");
        await store.CompleteAsync(Bot, dispatch, 900, default);
        var row = await RowAsync(item.Id); row.Status.ShouldBe("cancelled"); row.LastOutcome.ShouldBe("sent");
        _clock.UtcNow = Initial.AddDays(1);
        (await store.ClaimDueAsync(Bot, default)).ShouldBeNull();
    }

    [Theory]
    [InlineData("member")]
    [InlineData("place")]
    [InlineData("bot")]
    public async Task Revoked_authority_cancels_daily_and_reenable_does_not_replay(string kind)
    {
        await SeedAsync(); var item = await ActiveAsync(daily: 730);
        if (kind == "member") await Db.FamilyMembers.Where(x => x.TelegramUserId == 111).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, FamilyMemberStatus.Denied));
        if (kind == "place") await Db.Places.Where(x => x.BotId == 22 && x.TopicId == 7).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, PlaceStatus.Denied));
        if (kind == "bot") await Db.Bots.Where(x => x.Id == 22).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, BotStatus.Disabled));
        _clock.UtcNow = item.DueAt;
        await using var db = Context();
        (await Store(db).ClaimDueAsync(Bot, default)).ShouldBeNull();
        (await RowAsync(item.Id)).Status.ShouldBe("cancelled");
        await Db.FamilyMembers.Where(x => x.TelegramUserId == 111).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, FamilyMemberStatus.Approved));
        await Db.Places.Where(x => x.BotId == 22 && x.TopicId == 7).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, PlaceStatus.Approved));
        await Db.Bots.Where(x => x.Id == 22).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, BotStatus.Active));
        (await Store(db).ClaimDueAsync(Bot, default)).ShouldBeNull();
        (await db.Set<ReminderAttempt>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_at_twenty_four_hours_skips_without_attempt_and_daily_advances(bool daily)
    {
        await SeedAsync(); var item = await ActiveAsync(daily: daily ? 730 : null);
        _clock.UtcNow = item.DueAt.AddHours(24);
        await using var db = Context(); (await Store(db).ClaimDueAsync(Bot, default)).ShouldBeNull();
        var row = await RowAsync(item.Id); row.LastOutcome.ShouldBe("skipped");
        row.Status.ShouldBe(daily ? "active" : "skipped");
        if (daily) row.DueAt.ShouldBe(new DateTimeOffset(2026, 1, 4, 12, 10, 0, TimeSpan.Zero));
        (await db.Set<ReminderAttempt>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Quiet_deferral_keeps_due_and_end_boundary_dispatches()
    {
        await SeedAsync(); var item = await ActiveAsync();
        await using var db = Context(); var store = Store(db);
        await store.SetPreferencesAsync(Scope, 200, new(0, 720, 780), default);
        _clock.UtcNow = item.DueAt;
        (await store.ClaimDueAsync(Bot, default)).ShouldBeNull();
        (await RowAsync(item.Id)).DueAt.ShouldBe(item.DueAt);
        _clock.UtcNow = Initial.AddHours(1);
        (await store.ClaimDueAsync(Bot, default))!.ReminderId.ShouldBe(item.Id);
    }

    [Fact]
    public async Task Preference_changes_do_not_move_frozen_daily_due_or_offset()
    {
        await SeedAsync(); var item = await ActiveAsync(daily: 910, offset: 180);
        await using var db = Context(); var store = Store(db);
        await store.SetPreferencesAsync(Scope, 200, new(-300, 60, 120), default);
        _clock.UtcNow = item.DueAt; var dispatch = await store.ClaimDueAsync(Bot, default);
        dispatch!.ReminderId.ShouldBe(item.Id);
        var row = await RowAsync(item.Id);
        row.OffsetMinutes.ShouldBe(180); row.DueAt.ShouldBe(new DateTimeOffset(2026, 1, 3, 12, 10, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Settings_replay_cannot_restore_old_preferences()
    {
        await SeedAsync(); await using var db = Context(); var store = Store(db);
        await store.SetPreferencesAsync(Scope, 100, new(180, 1320, 480), default);
        await store.SetPreferencesAsync(Scope, 101, new(240, 1200, 420), default);
        await store.SetPreferencesAsync(Scope, 100, new(180, 1320, 480), default);
        (await store.GetPreferencesAsync(Scope, default)).ShouldBe(new ReminderPreferences(240, 1200, 420));
        (await db.Set<ReminderSettingsReceipt>().CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_cross_bot_preferences_create_one_complete_row()
    {
        await SeedAsync(); await using var left = Context(); await using var right = Context();
        var second = Scope with { BotDbId = 23, BotId = 998 };
        await Task.WhenAll(Store(left).SetPreferencesAsync(Scope, 100, new(180, 1200, 420), default),
            Store(right).SetPreferencesAsync(second, 100, new(-300, 60, 120), default));
        await using var read = Context(); var p = await Store(read).GetPreferencesAsync(Scope, default);
        new[] { new ReminderPreferences(180, 1200, 420), new ReminderPreferences(-300, 60, 120) }.ShouldContain(p);
        (await read.Set<ReminderPreference>().CountAsync()).ShouldBe(1);
        (await read.Set<ReminderSettingsReceipt>().CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Actor_place_capacity_twenty_and_expired_drafts_release_slots()
    {
        await SeedAsync(); for (var i = 1; i <= 20; i++) await DraftAsync(i);
        await using var db = Context(); var store = Store(db);
        (await store.AdmitAsync(Scope, 21, new("overflow", Initial.AddHours(1), null, 0), default)).ShouldBeNull();
        (await store.AdmitAsync(Scope with { TopicId = 8 }, 22, new("other topic", Initial.AddHours(1), null, 0), default))!.Text.ShouldBe("other topic");
        _clock.UtcNow = Initial.AddHours(24);
        (await store.AdmitAsync(Scope, 23, new("new draft", _clock.UtcNow.AddHours(1), null, 0), default))!.Text.ShouldBe("new draft");
        (await db.Set<Reminder>().CountAsync(x => x.Status == "draft")).ShouldBe(1);
        (await db.Set<Reminder>().CountAsync(x => x.Status == "skipped")).ShouldBe(21);
    }

    [Fact]
    public async Task Concurrent_final_daily_budget_slot_is_role_wide_across_bots_and_actors()
    {
        await SeedAsync(); var first = await ActiveAsync();
        var secondScope = Scope with { BotDbId = 23, BotId = 998, ActorUserId = 222 };
        var second = await ActiveAsync(101, secondScope);
        await using (var seed = Context())
        {
            for (var i = 0; i < 9; i++) seed.Add(new ReminderAttempt { Id = Guid.NewGuid(), ReminderId = first.Id,
                FamilyId = 11, Role = "general", OccurrenceDueAt = Initial.AddMinutes(-i - 1), StartedAt = Initial });
            await seed.SaveChangesAsync();
        }
        _clock.UtcNow = first.DueAt;
        await using var left = Context(); await using var right = Context();
        var secondBot = new ReceivingBot(23, 998, "second_test_bot", 11, "general");
        var claimed = await Task.WhenAll(Store(left).ClaimDueAsync(Bot, default), Store(right).ClaimDueAsync(secondBot, default));
        claimed.Count(x => x != null).ShouldBe(1);
        await using var read = Context(); (await read.Set<ReminderAttempt>().CountAsync()).ShouldBe(10);
        var deferred = claimed[0] == null ? first : second;
        (await RowAsync(deferred.Id)).Status.ShouldBe("active");
        (await RowAsync(deferred.Id)).DueAt.ShouldBe(deferred.DueAt);
    }

    [Fact]
    public async Task Cleanup_expires_drafts_protects_active_bounds_history_and_cascades_attempts()
    {
        await SeedAsync(); var active = await ActiveAsync(); var draft = await DraftAsync(101);
        var oldIds = new List<Guid>();
        await using (var seed = Context())
        {
            for (var i = 0; i < 101; i++)
            {
                var row = new Reminder { Id = Guid.NewGuid(), FamilyId = 11, BotDbId = 22, BotId = 999,
                    Role = "general", ChatId = Scope.ChatId, TopicId = 7, ChatType = "supergroup",
                    ActorUserId = 111, SourceMessageId = 1000 + i, Text = "synthetic history", Status = "sent",
                    DueAt = Initial.AddDays(-61), CreatedAt = Initial.AddDays(-61), UpdatedAt = Initial.AddDays(-60) };
                oldIds.Add(row.Id); seed.Add(row);
                seed.Add(new ReminderAttempt { Id = Guid.NewGuid(), ReminderId = row.Id, FamilyId = 11,
                    Role = "general", OccurrenceDueAt = row.DueAt, StartedAt = Initial.AddDays(-61), Outcome = "sent" });
            }
            await seed.SaveChangesAsync();
        }
        _clock.UtcNow = Initial.AddHours(24);
        await using var db = Context(); await Store(db).CleanupAsync(Bot, default);
        (await db.Set<Reminder>().CountAsync(x => oldIds.Contains(x.Id))).ShouldBe(1);
        (await RowAsync(active.Id)).Status.ShouldBe("active");
        (await RowAsync(draft.Id)).Status.ShouldBe("skipped");
        (await db.Set<ReminderAttempt>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task List_prioritizes_live_due_order_then_newest_history_without_cross_actor()
    {
        await SeedAsync(); var active = await ActiveAsync(); var history = await ActiveAsync(101);
        var other = await ActiveAsync(102, Scope with { ActorUserId = 222 });
        await using var db = Context(); var store = Store(db);
        await store.CancelAsync(Scope, history.Id, default);
        var listed = await store.ListAsync(Scope, default);
        listed.Select(x => x.Id).ShouldBe([active.Id, history.Id]);
        listed.Select(x => x.Status).ShouldBe(["active", "cancelled"]);
        listed.ShouldNotContain(x => x.Id == other.Id);
    }

    [Theory]
    [InlineData(199, true)]
    [InlineData(200, false)]
    public async Task Bot_capacity_includes_other_creators_and_places(int existing, bool allowed)
    {
        await SeedAsync(); await using var db = Context();
        for (var i = 0; i < existing; i++) db.Add(new Reminder { Id = Guid.NewGuid(), FamilyId = 11,
            BotDbId = 22, BotId = 999, Role = "general", ChatId = Scope.ChatId, TopicId = 8,
            ChatType = "supergroup", ActorUserId = 222, SourceMessageId = 1000 + i, Text = "synthetic capacity",
            DueAt = Initial.AddHours(1), CreatedAt = Initial, UpdatedAt = Initial });
        await db.SaveChangesAsync();
        var result = await Store(db).AdmitAsync(Scope, 100, new("new task", Initial.AddMinutes(10), null, 0), default);
        if (allowed) result!.Text.ShouldBe("new task"); else result.ShouldBeNull();
        (await db.Set<Reminder>().CountAsync()).ShouldBe(200);
    }

    [Fact]
    public async Task Concurrent_last_actor_place_slot_admits_one_new_source()
    {
        await SeedAsync(); for (var i = 1; i <= 19; i++) await DraftAsync(i);
        await using var left = Context(); await using var right = Context();
        var request = new ReminderRequest("new task", Initial.AddMinutes(10), null, 0);
        var results = await Task.WhenAll(Store(left).AdmitAsync(Scope, 100, request, default),
            Store(right).AdmitAsync(Scope, 101, request, default));
        results.Count(x => x != null).ShouldBe(1);
        await using var read = Context(); (await read.Set<Reminder>().CountAsync()).ShouldBe(20);
    }

    [Fact]
    public async Task Private_destination_requires_creator_chat_and_no_topic()
    {
        await SeedAsync(); var privateScope = Scope with { ChatId = 111, TopicId = null, ChatType = "private" };
        var item = await ActiveAsync(scope: privateScope);
        await using var db = Context(); var store = Store(db);
        await Should.ThrowAsync<InvalidOperationException>(() => store.ListAsync(privateScope with { ChatId = 222 }, default));
        await Should.ThrowAsync<InvalidOperationException>(() => store.ListAsync(privateScope with { TopicId = 7 }, default));
        _clock.UtcNow = item.DueAt;
        var dispatch = (await store.ClaimDueAsync(Bot, default))!;
        dispatch.ChatId.ShouldBe(111); dispatch.TopicId.ShouldBeNull();
    }

    [Fact]
    public async Task New_daily_budget_utc_day_dispatches_deferred_occurrence_without_changing_due()
    {
        await SeedAsync(); var item = await ActiveAsync();
        await using var db = Context();
        for (var i = 0; i < 10; i++) db.Add(new ReminderAttempt { Id = Guid.NewGuid(), ReminderId = item.Id,
            FamilyId = 11, Role = "general", OccurrenceDueAt = Initial.AddMinutes(-i - 1), StartedAt = Initial });
        await db.SaveChangesAsync();
        _clock.UtcNow = Initial.AddHours(11).AddMinutes(59);
        await Store(db).SetPreferencesAsync(Scope, 200, new(840, 0, 1), default);
        (await Store(db).ClaimDueAsync(Bot, default)).ShouldBeNull();
        (await RowAsync(item.Id)).DueAt.ShouldBe(item.DueAt);
        _clock.UtcNow = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
        (await Store(db).ClaimDueAsync(Bot, default))!.ReminderId.ShouldBe(item.Id);
        (await db.Set<ReminderAttempt>().CountAsync()).ShouldBe(11);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task Draft_preview_expiry_exact_boundary(int seconds, bool begins)
    {
        await SeedAsync(); var item = await DraftAsync(); _clock.UtcNow = Initial.AddHours(24).AddSeconds(seconds);
        await using var db = Context(); var preview = await Store(db).BeginPreviewAsync(Scope, item.Id, default);
        if (begins) preview!.Id.ShouldBe(item.Id); else preview.ShouldBeNull();
        (await RowAsync(item.Id)).PreviewStarted.ShouldBe(begins);
    }

    [Theory]
    [InlineData(-1,true)]
    [InlineData(0,false)]
    [InlineData(1,false)]
    public async Task Terminal_cleanup_at_exact_sixty_day_age(int seconds,bool retained)
    {
        await SeedAsync();var item=await ActiveAsync();await using var db=Context();
        await Store(db).CancelAsync(Scope,item.Id,default);
        _clock.UtcNow=Initial.AddDays(60).AddSeconds(seconds);
        await Store(db).CleanupAsync(Bot,default);
        (await db.Set<Reminder>().AnyAsync(x=>x.Id==item.Id)).ShouldBe(retained);
    }

    [Fact]
    public async Task Active_daily_survives_old_attempt_pruning_and_keeps_current_unknown_summary()
    {
        await SeedAsync();var item=await ActiveAsync(daily:730);_clock.UtcNow=item.DueAt;
        await using var db=Context();var dispatch=(await Store(db).ClaimDueAsync(Bot,default))!;
        _clock.UtcNow=item.DueAt.AddDays(60);await Store(db).CleanupAsync(Bot,default);
        (await db.Set<ReminderAttempt>().AnyAsync(x=>x.Id==dispatch.AttemptId)).ShouldBeFalse();
        var row=await RowAsync(item.Id);row.Status.ShouldBe("active");row.LastOutcome.ShouldBe("unknown");row.LastAttemptAt.ShouldBe(item.DueAt);
    }

    [Fact]
    public async Task Cancelled_token_before_claim_leaves_occurrence_recoverable_without_attempt()
    {
        await SeedAsync();var item=await ActiveAsync();_clock.UtcNow=item.DueAt;
        await using var db=Context();using var stop=new CancellationTokenSource();stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(()=>Store(db).ClaimDueAsync(Bot,stop.Token));
        (await RowAsync(item.Id)).Status.ShouldBe("active");(await db.Set<ReminderAttempt>().CountAsync()).ShouldBe(0);
        (await Store(db).ClaimDueAsync(Bot,default))!.ReminderId.ShouldBe(item.Id);
    }
}
