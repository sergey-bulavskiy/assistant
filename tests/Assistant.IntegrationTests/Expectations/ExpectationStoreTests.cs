using Assistant.Application.Expectations;
using Assistant.Application.Reminders;
using Assistant.Domain.Expectations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assistant.IntegrationTests.Expectations;

public sealed class ExpectationStoreTests : ExpectationTestBase
{
    [Theory]
    [InlineData("health", 333L, "synthetic_subject")]
    [InlineData("vet", 444L, "Synthetic animal")]
    public async Task Delivered_creation_binds_profile_and_activates_tomorrow_only(string role, long profile, string subject)
    {
        await SeedAsync(); var scope = role == "health" ? HealthScope : VetScope;
        var intake = await CreateAsync(scope); var preview = await DeliverAsync(intake);
        preview.ShouldBe(new ExpectationPreview(intake.Id!.Value, intake.DraftId!.Value, subject,
            "glucose", 540, 30, 180, new(2032, 2, 10), new ReminderPreferences(180, 1320, 480)));
        await using var session = Open();
        (await session.Store.ResolveAsync(scope, preview.DraftId, 700, true, default)).ShouldBe("saved");
        var row = await RowAsync(preview.Id);
        row.ProfileId.ShouldBe(profile); row.Status.ShouldBe("active"); row.CurrentVersion.ShouldBe(1);
        row.LastVersion.ShouldBe(1); row.NextVersion.ShouldBeNull(); row.Revision.ShouldBe(2);
        row.FirstDate.ShouldBe(new DateOnly(2032, 2, 10)); row.NextDate.ShouldBe(new DateOnly(2032, 2, 10));
        row.DueAt.ShouldBe(DateTimeOffset.Parse("2032-02-10T06:30:00Z")); row.OffsetMinutes.ShouldBe(180);
        var version = await session.Db.Set<ExpectationVersion>().AsNoTracking().SingleAsync();
        version.ExpectationId.ShouldBe(preview.Id); version.Number.ShouldBe(1);
        version.DeadlineMinute.ShouldBe(540); version.GraceMinutes.ShouldBe(30);
        version.EffectiveFrom.ShouldBe(new DateOnly(2032, 2, 10));
        (await session.Db.Set<ExpectationOccurrence>().CountAsync()).ShouldBe(0);
        (await session.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Redelivered_command_keeps_first_request_identity_and_creator()
    {
        await SeedAsync(); await using var first = Open();
        var original = await first.Store.ExecuteAsync(HealthScope, 101,
            new("create", EventType: "glucose", DeadlineMinute: 540, GraceMinutes: 30), default);
        await using var replay = Open();
        var duplicate = await replay.Store.ExecuteAsync(HealthScope, 101,
            new("create", EventType: "insulin", DeadlineMinute: 600, GraceMinutes: 0), default);
        duplicate.ShouldBe(original);
        var row = await RowAsync(original.Id!.Value); row.EventType.ShouldBe("glucose"); row.ActorUserId.ShouldBe(111);
        var draft = await DraftRowAsync(original.DraftId!.Value); draft.DeadlineMinute.ShouldBe(540); draft.GraceMinutes.ShouldBe(30);
        (await replay.Db.Set<Expectation>().CountAsync()).ShouldBe(1);
        (await replay.Db.Set<ExpectationDraft>().CountAsync()).ShouldBe(1);
        (await replay.Db.Set<ExpectationReceipt>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_creators_can_create_only_one_live_scope_and_cannot_steal_management()
    {
        await SeedAsync(); await using var left = Open(); await using var right = Open();
        var other = HealthScope with { ActorUserId = 222 };
        var results = await Task.WhenAll(left.Store.ExecuteAsync(HealthScope, 101,
            new("create", EventType: "glucose", DeadlineMinute: 540, GraceMinutes: 30), default),
            right.Store.ExecuteAsync(other, 102,
            new("create", EventType: "glucose", DeadlineMinute: 600, GraceMinutes: 15), default));
        results.Count(x => x.Result == "preview").ShouldBe(1); results.Count(x => x.Result == "duplicate").ShouldBe(1);
        var winner = results.Single(x => x.Result == "preview"); var loserScope = winner.Scope.ActorUserId == 111 ? other : HealthScope;
        await using var read = Open();
        (await read.Db.Set<Expectation>().CountAsync()).ShouldBe(1); (await read.Db.Set<ExpectationDraft>().CountAsync()).ShouldBe(1);
        (await RowAsync(winner.Id!.Value)).ActorUserId.ShouldBe(winner.Scope.ActorUserId);
        (await read.Store.ListAsync(loserScope, default)).ShouldBeEmpty();
        (await read.Store.ExecuteAsync(loserScope, 103, new("cancel", winner.Id), default)).Result.ShouldBe("unavailable");
        (await RowAsync(winner.Id.Value)).Status.ShouldBe("draft");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    public async Task Database_unique_scope_rejects_second_live_row_including_null_topic(int? topic)
    {
        await SeedAsync(); var intake = await CreateAsync(HealthScope with { TopicId = topic });
        var original = await RowAsync(intake.Id!.Value);
        await using var insert = Open();
        insert.Db.Add(new Expectation { Id = Guid.NewGuid(), FamilyId = 11, BotDbId = 22, BotId = 999,
            Role = "health", ChatId = -100, TopicId = topic, ChatType = "supergroup", ActorUserId = 222,
            ProfileId = 333, EventType = "glucose", Status = "paused", OffsetMinutes = 180,
            CreatedAt = Initial, UpdatedAt = Initial });
        var ex = await Should.ThrowAsync<DbUpdateException>(() => insert.Db.SaveChangesAsync());
        ((PostgresException)ex.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        await using var read = Open(); (await read.Db.Set<Expectation>().CountAsync()).ShouldBe(1);
        (await RowAsync(original.Id)).ActorUserId.ShouldBe(111);
    }

    [Fact]
    public async Task Another_exact_topic_and_type_allow_independent_checks()
    {
        await SeedAsync(); var first = await CreateAsync();
        var second = await CreateAsync(HealthScope with { TopicId = 8 });
        var third = await CreateAsync(type: "insulin");
        first.Result.ShouldBe("preview"); second.Result.ShouldBe("preview"); third.Result.ShouldBe("preview");
        new[] { first.Id, second.Id, third.Id }.Distinct().Count().ShouldBe(3);
        await using var read = Open(); (await read.Db.Set<Expectation>().CountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Unbound_preview_fence_survives_restart_and_cannot_silently_resend_or_save()
    {
        await SeedAsync(); var intake = await CreateAsync();
        await using (var first = Open())
        {
            (await first.Store.BeginPreviewAsync(HealthScope, intake.DraftId!.Value, default)).ShouldNotBeNull();
            (await first.Store.ResolveAsync(HealthScope, intake.DraftId.Value, 700, true, default)).ShouldBe("unavailable");
        }
        await using var restarted = Open();
        (await restarted.Store.BeginPreviewAsync(HealthScope, intake.DraftId!.Value, default)).ShouldBeNull();
        (await restarted.Store.ResolveAsync(HealthScope, intake.DraftId.Value, 700, true, default)).ShouldBe("unavailable");
        (await RowAsync(intake.Id!.Value)).Status.ShouldBe("draft");
        (await restarted.Db.Set<ExpectationVersion>().CountAsync()).ShouldBe(0);
        var draft = await DraftRowAsync(intake.DraftId.Value); draft.PreviewStarted.ShouldBeTrue(); draft.PreviewMessageId.ShouldBeNull();
    }

    [Fact]
    public async Task Binding_requires_begun_preview_and_cannot_replace_first_delivered_message()
    {
        await SeedAsync(); var intake = await CreateAsync(); await using var session = Open();
        await session.Store.BindPreviewAsync(HealthScope, intake.DraftId!.Value, 700, default);
        (await DraftRowAsync(intake.DraftId.Value)).PreviewMessageId.ShouldBeNull();
        (await session.Store.BeginPreviewAsync(HealthScope, intake.DraftId.Value, default)).ShouldNotBeNull();
        await session.Store.BindPreviewAsync(HealthScope, intake.DraftId.Value, 700, default);
        await session.Store.BindPreviewAsync(HealthScope, intake.DraftId.Value, 701, default);
        (await DraftRowAsync(intake.DraftId.Value)).PreviewMessageId.ShouldBe(700);
        (await session.Store.ResolveAsync(HealthScope, intake.DraftId.Value, 701, true, default)).ShouldBe("unavailable");
        (await RowAsync(intake.Id!.Value)).Status.ShouldBe("draft");
        (await session.Store.ResolveAsync(HealthScope, intake.DraftId.Value, 700, true, default)).ShouldBe("saved");
    }

    [Fact]
    public async Task Concurrent_and_repeated_save_create_one_version_only()
    {
        await SeedAsync(); var preview = await DeliverAsync(await CreateAsync());
        await using var left = Open(); await using var right = Open();
        (await Task.WhenAll(left.Store.ResolveAsync(HealthScope, preview.DraftId, 700, true, default),
            right.Store.ResolveAsync(HealthScope, preview.DraftId, 700, true, default))).ShouldBe(new[] { "saved", "saved" });
        await using var restarted = Open();
        (await restarted.Store.ResolveAsync(HealthScope, preview.DraftId, 700, true, default)).ShouldBe("saved");
        (await restarted.Db.Set<ExpectationVersion>().CountAsync()).ShouldBe(1);
        (await RowAsync(preview.Id)).LastVersion.ShouldBe(1); (await RowAsync(preview.Id)).Revision.ShouldBe(2);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("topic")]
    [InlineData("null-topic")]
    [InlineData("chat")]
    [InlineData("bot")]
    [InlineData("role")]
    public async Task Another_approved_scope_cannot_begin_bind_resolve_list_or_manage(string mismatch)
    {
        await SeedAsync(); var intake = await CreateAsync();
        var other = mismatch switch
        {
            "actor" => HealthScope with { ActorUserId = 222 },
            "topic" => HealthScope with { TopicId = 8 },
            "null-topic" => HealthScope with { TopicId = null },
            "chat" => HealthScope with { ChatId = -101 },
            "bot" => OtherHealthScope,
            _ => VetScope
        };
        await using var session = Open();
        (await session.Store.BeginPreviewAsync(other, intake.DraftId!.Value, default)).ShouldBeNull();
        await DeliverAsync(intake);
        await session.Store.BindPreviewAsync(other, intake.DraftId.Value, 701, default);
        (await session.Store.ResolveAsync(other, intake.DraftId.Value, 700, true, default)).ShouldBe("unavailable");
        (await session.Store.ResolveAsync(other, Guid.NewGuid(), 700, true, default)).ShouldBe("unavailable");
        (await session.Store.ListAsync(other, default)).ShouldBeEmpty();
        foreach (var verb in new[] { "edit", "pause", "resume", "cancel" })
            (await session.Store.ExecuteAsync(other, NextSource(), new(verb, intake.Id, DeadlineMinute: 600, GraceMinutes: 0), default))
                .Result.ShouldBe("unavailable");
        (await RowAsync(intake.Id!.Value)).Status.ShouldBe("draft");
        (await DraftRowAsync(intake.DraftId.Value)).PreviewMessageId.ShouldBe(700);
        (await session.Db.Set<ExpectationVersion>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(12L)]
    public async Task Unset_or_wrong_current_family_fails_closed_without_mutation(long? family)
    {
        await SeedAsync(); await using var session = Open(family);
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => session.Store.ExecuteAsync(HealthScope, 101,
            new("create", EventType: "glucose", DeadlineMinute: 540, GraceMinutes: 30), default));
        ex.Message.ShouldBe("Nonurgent scope unavailable.");
        await using var read = Open(); (await read.Db.Set<Expectation>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("health")]
    [InlineData("vet")]
    public async Task Missing_profile_is_rejected_without_creating_one(string role)
    {
        await SeedAsync(); await using var session = Open();
        if (role == "health") await session.Db.HealthProfiles.Where(x => x.Id == 333).ExecuteDeleteAsync();
        else await session.Db.VetProfiles.Where(x => x.Id == 444).ExecuteDeleteAsync();
        var result = await CreateAsync(role == "health" ? HealthScope : VetScope);
        result.Kind.ShouldBe("result"); result.Result.ShouldBe("profile_required"); result.Id.ShouldBeNull();
        (await session.Db.Set<Expectation>().CountAsync()).ShouldBe(0);
        (await session.Db.Set<ExpectationDraft>().CountAsync()).ShouldBe(0);
        if (role == "health") (await session.Db.HealthProfiles.CountAsync(x => x.BotId == 22)).ShouldBe(0);
        else (await session.Db.VetProfiles.CountAsync(x => x.BotDbId == 23)).ShouldBe(0);
    }

    [Fact]
    public async Task Replaced_profile_invalidates_bound_preview_without_retargeting_subject()
    {
        await SeedAsync(); var preview = await DeliverAsync(await CreateAsync()); await using var session = Open();
        await session.Db.HealthProfiles.Where(x => x.Id == 333).ExecuteDeleteAsync();
        session.Db.HealthProfiles.Add(new() { Id = 336, FamilyId = 11, BotId = 22, SubjectTag = "synthetic_replacement",
            CreatedAt = Initial, UpdatedAt = Initial }); await session.Db.SaveChangesAsync();
        (await session.Store.ResolveAsync(HealthScope, preview.DraftId, 700, true, default)).ShouldBe("expired");
        (await RowAsync(preview.Id)).ProfileId.ShouldBe(333); (await RowAsync(preview.Id)).Status.ShouldBe("draft");
        (await session.Db.Set<ExpectationVersion>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Renaming_profile_changes_list_label_without_changing_bound_identity()
    {
        await SeedAsync(); var preview = await ActiveAsync(); await using var session = Open();
        await session.Db.HealthProfiles.Where(x => x.Id == 333).ExecuteUpdateAsync(u => u.SetProperty(x => x.SubjectTag, "synthetic_renamed"));
        var item = (await session.Store.ListAsync(HealthScope, default)).ShouldHaveSingleItem();
        item.Subject.ShouldBe("synthetic_renamed"); item.Id.ShouldBe(preview.Id);
        (await RowAsync(preview.Id)).ProfileId.ShouldBe(333); (await RowAsync(preview.Id)).Status.ShouldBe("active");
    }

    [Fact]
    public async Task Creation_cancel_is_terminal_and_recreation_starts_tomorrow_with_new_identity()
    {
        await SeedAsync(); var preview = await DeliverAsync(await CreateAsync()); await using var session = Open();
        (await session.Store.ResolveAsync(HealthScope, preview.DraftId, 700, false, default)).ShouldBe("cancelled");
        (await session.Store.ResolveAsync(HealthScope, preview.DraftId, 700, true, default)).ShouldBe("expired");
        var recreated = await ActiveAsync(); recreated.Id.ShouldNotBe(preview.Id);
        recreated.EffectiveFrom.ShouldBe(new DateOnly(2032, 2, 10));
        (await RowAsync(preview.Id)).Status.ShouldBe("cancelled");
        (await session.Db.Set<ExpectationVersion>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Saved_edit_keeps_current_version_and_cursor_until_next_frozen_midnight()
    {
        await SeedAsync(); var active = await ActiveAsync();
        Clock.UtcNow = DateTimeOffset.Parse("2032-02-10T05:00:00Z");
        await using var session = Open();
        var edit = await session.Store.ExecuteAsync(HealthScope, NextSource(), new("edit", active.Id, DeadlineMinute: 600, GraceMinutes: 15), default);
        var preview = await DeliverAsync(edit, 701); preview.EffectiveFrom.ShouldBe(new DateOnly(2032, 2, 11));
        session.Db.ChangeTracker.Clear();
        (await session.Store.ResolveAsync(HealthScope, preview.DraftId, 701, true, default)).ShouldBe("saved");
        var row = await RowAsync(active.Id); row.CurrentVersion.ShouldBe(1); row.NextVersion.ShouldBe(2);
        row.LastVersion.ShouldBe(2); row.NextDate.ShouldBe(new DateOnly(2032, 2, 10));
        row.DueAt.ShouldBe(DateTimeOffset.Parse("2032-02-10T06:30:00Z"));
        var versions = await session.Db.Set<ExpectationVersion>().AsNoTracking().OrderBy(x => x.Number).ToArrayAsync();
        versions.Length.ShouldBe(2); versions[0].DeadlineMinute.ShouldBe(540); versions[0].GraceMinutes.ShouldBe(30);
        versions[0].EffectiveFrom.ShouldBe(new DateOnly(2032, 2, 10));
        versions[1].DeadlineMinute.ShouldBe(600); versions[1].GraceMinutes.ShouldBe(15);
        versions[1].EffectiveFrom.ShouldBe(new DateOnly(2032, 2, 11));
        var item = (await session.Store.ListAsync(HealthScope, default)).ShouldHaveSingleItem();
        item.DeadlineMinute.ShouldBe(600); item.GraceMinutes.ShouldBe(15); item.EffectiveFrom.ShouldBe(new DateOnly(2032, 2, 11));
    }

    [Fact]
    public async Task New_explicit_edit_retires_prior_proposal_and_only_latest_save_can_win()
    {
        await SeedAsync(); var active = await ActiveAsync(); await using var session = Open();
        var old = await session.Store.ExecuteAsync(HealthScope, NextSource(), new("edit", active.Id, DeadlineMinute: 600, GraceMinutes: 0), default);
        await DeliverAsync(old, 701);
        var latest = await session.Store.ExecuteAsync(HealthScope, NextSource(), new("edit", active.Id, DeadlineMinute: 660, GraceMinutes: 5), default);
        await DeliverAsync(latest, 702);
        await using var left = Open(); await using var right = Open();
        var outcomes = await Task.WhenAll(left.Store.ResolveAsync(HealthScope, old.DraftId!.Value, 701, true, default),
            right.Store.ResolveAsync(HealthScope, latest.DraftId!.Value, 702, true, default));
        outcomes.ShouldBe(new[] { "expired", "saved" });
        (await DraftRowAsync(old.DraftId.Value)).Status.ShouldBe("retired");
        (await DraftRowAsync(latest.DraftId.Value)).Status.ShouldBe("saved");
        await using var read = Open(); (await read.Db.Set<ExpectationVersion>().CountAsync()).ShouldBe(2);
        var next = await read.Db.Set<ExpectationVersion>().SingleAsync(x => x.Number == 2);
        next.DeadlineMinute.ShouldBe(660); next.GraceMinutes.ShouldBe(5);
    }

    [Theory]
    [InlineData("pause", "paused", "suppressed-paused")]
    [InlineData("cancel", "cancelled", "suppressed-cancelled")]
    public async Task Management_before_fence_retires_edit_and_suppresses_pending_occurrence(string verb, string status, string outcome)
    {
        await SeedAsync(); var active = await ActiveAsync(); await using var session = Open();
        session.Db.Add(new ExpectationOccurrence { FamilyId = 11, ExpectationId = active.Id, LocalDate = new(2032, 2, 10),
            Version = 1, WindowStart = DateTimeOffset.Parse("2032-02-09T21:00:00Z"),
            DueAt = DateTimeOffset.Parse("2032-02-10T06:30:00Z"), ExpiresAt = DateTimeOffset.Parse("2032-02-10T21:00:00Z"), UpdatedAt = Initial });
        await session.Db.SaveChangesAsync();
        var edit = await session.Store.ExecuteAsync(HealthScope, NextSource(), new("edit", active.Id, DeadlineMinute: 600, GraceMinutes: 0), default);
        await DeliverAsync(edit, 701);
        (await session.Store.ExecuteAsync(HealthScope, NextSource(), new(verb, active.Id), default)).Result.ShouldBe(verb);
        session.Db.ChangeTracker.Clear();
        (await session.Store.ResolveAsync(HealthScope, edit.DraftId!.Value, 701, true, default)).ShouldBe("expired");
        var row = await RowAsync(active.Id); row.Status.ShouldBe(status); row.NextDate.ShouldBeNull(); row.DueAt.ShouldBeNull();
        (await DraftRowAsync(edit.DraftId.Value)).Status.ShouldBe("retired");
        await using var read = Open(); (await read.Db.Set<ExpectationOccurrence>().SingleAsync()).Outcome.ShouldBe(outcome);
        (await read.Db.Set<ExpectationVersion>().CountAsync()).ShouldBe(1);
        (await read.Db.Set<ExpectationAttempt>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Pause_is_durable_and_confirmed_resume_starts_tomorrow_without_replaying_paused_dates()
    {
        await SeedAsync(); var active = await ActiveAsync();
        await using (var session = Open())
            (await session.Store.ExecuteAsync(HealthScope, NextSource(), new("pause", active.Id), default)).Result.ShouldBe("pause");
        Clock.UtcNow = DateTimeOffset.Parse("2032-02-15T12:00:00Z"); await using var resumed = Open();
        var intake = await resumed.Store.ExecuteAsync(HealthScope, NextSource(), new("resume", active.Id), default);
        var preview = await DeliverAsync(intake, 701); preview.EffectiveFrom.ShouldBe(new DateOnly(2032, 2, 16));
        (await RowAsync(active.Id)).Status.ShouldBe("paused");
        resumed.Db.ChangeTracker.Clear();
        (await resumed.Store.ResolveAsync(HealthScope, preview.DraftId, 701, true, default)).ShouldBe("saved");
        var row = await RowAsync(active.Id); row.Status.ShouldBe("active"); row.FirstDate.ShouldBe(new DateOnly(2032, 2, 10));
        row.NextDate.ShouldBe(new DateOnly(2032, 2, 16)); row.DueAt.ShouldBe(DateTimeOffset.Parse("2032-02-16T06:30:00Z"));
        row.CurrentVersion.ShouldBe(2); row.NextVersion.ShouldBeNull();
        (await resumed.Db.Set<ExpectationOccurrence>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Cancelled_schedule_cannot_resume_and_old_saved_callback_cannot_reactivate_it()
    {
        await SeedAsync(); var active = await ActiveAsync(); await using var session = Open();
        (await session.Store.ExecuteAsync(HealthScope, NextSource(), new("cancel", active.Id), default)).Result.ShouldBe("cancel");
        (await session.Store.ExecuteAsync(HealthScope, NextSource(), new("resume", active.Id), default)).Result.ShouldBe("unavailable");
        (await session.Store.ResolveAsync(HealthScope, active.DraftId, 700, true, default)).ShouldBe("unavailable");
        var row = await RowAsync(active.Id); row.Status.ShouldBe("cancelled"); row.DueAt.ShouldBeNull(); row.NextDate.ShouldBeNull();
        row.LastVersion.ShouldBe(1); (await session.Db.Set<ExpectationVersion>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Local_midnight_invalidates_old_tomorrow_preview_without_versions()
    {
        await SeedAsync(); var preview = await DeliverAsync(await CreateAsync());
        Clock.UtcNow = DateTimeOffset.Parse("2032-02-09T21:00:00Z"); await using var session = Open();
        (await session.Store.ResolveAsync(HealthScope, preview.DraftId, 700, true, default)).ShouldBe("expired");
        (await RowAsync(preview.Id)).Status.ShouldBe("draft");
        (await session.Db.Set<ExpectationVersion>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Late_evening_preview_expires_at_midnight_and_fresh_command_can_recreate_immediately()
    {
        await SeedAsync(); Clock.UtcNow = DateTimeOffset.Parse("2032-02-09T20:50:00Z");
        var original = await DeliverAsync(await CreateAsync());
        (await RowAsync(original.Id)).DraftExpiresAt.ShouldBe(DateTimeOffset.Parse("2032-02-09T21:00:00Z"));
        (await DraftRowAsync(original.DraftId)).ExpiresAt.ShouldBe(DateTimeOffset.Parse("2032-02-09T21:00:00Z"));
        Clock.UtcNow = DateTimeOffset.Parse("2032-02-09T21:01:00Z"); await using var callback = Open();
        (await callback.Store.ResolveAsync(HealthScope, original.DraftId, 700, true, default)).ShouldBe("expired");
        var replacement = await CreateAsync(); replacement.Result.ShouldBe("preview"); replacement.Id.ShouldNotBe(original.Id);
        (await RowAsync(original.Id)).Status.ShouldBe("expired");
        (await DraftRowAsync(original.DraftId)).Status.ShouldBe("expired");
        (await DraftRowAsync(replacement.DraftId!.Value)).EffectiveFrom.ShouldBe(new DateOnly(2032, 2, 11));
        (await callback.Db.Set<ExpectationVersion>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Exactly_twenty_four_hour_old_creation_releases_duplicate_scope_for_new_tomorrow_preview()
    {
        await SeedAsync(); var original = await CreateAsync(); Clock.UtcNow = Initial.AddHours(24);
        var replacement = await CreateAsync(); replacement.Result.ShouldBe("preview"); replacement.Id.ShouldNotBe(original.Id);
        (await RowAsync(original.Id!.Value)).Status.ShouldBe("expired");
        (await DraftRowAsync(original.DraftId!.Value)).Status.ShouldBe("expired");
        (await DraftRowAsync(replacement.DraftId!.Value)).EffectiveFrom.ShouldBe(new DateOnly(2032, 2, 11));
    }

    [Fact]
    public async Task Preference_offset_change_does_not_move_saved_schedule_or_edit_preview()
    {
        await SeedAsync(); var active = await ActiveAsync(); await using var session = Open();
        await session.Reminders.SetPreferencesAsync(HealthScope, NextSource(), new(-240, 600, 660), default);
        var edit = await session.Store.ExecuteAsync(HealthScope, NextSource(), new("edit", active.Id, DeadlineMinute: 600, GraceMinutes: 15), default);
        var preview = await DeliverAsync(edit, 701);
        preview.OffsetMinutes.ShouldBe(180); preview.Preferences.ShouldBe(new ReminderPreferences(-240, 600, 660));
        preview.EffectiveFrom.ShouldBe(new DateOnly(2032, 2, 10));
        session.Db.ChangeTracker.Clear();
        (await session.Store.ResolveAsync(HealthScope, preview.DraftId, 701, true, default)).ShouldBe("saved");
        var row = await RowAsync(active.Id); row.OffsetMinutes.ShouldBe(180);
        row.DueAt.ShouldBe(DateTimeOffset.Parse("2032-02-10T07:15:00Z"));
    }
}
