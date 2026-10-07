using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Domain.Vet;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Vet;

public sealed class VetDiaryStoreTests : VetTestBase
{
    [Fact]
    public async Task Mixed_save_and_key_replay_keep_exact_fractional_values_one_action_and_original_author()
    {
        await SeedAsync(); await using var s = Open();
        var e = await EvidenceAsync(s, type: "insulin", value: "0.125");
        var glucose = e.State with { EventType = "glucose", Value = 6.4m, Unit = "mmol/L" };
        var mutation = Save(Scope, e.Source, e.Profile, e.State, glucose) with { ActorUserId = 222 };
        var saved = await s.Diary.ApplyAsync(mutation, CancellationToken.None);
        var replay = await s.Diary.ApplyAsync(mutation, CancellationToken.None);
        saved.Status.ShouldBe(VetMutationStatus.Applied); replay.Status.ShouldBe(VetMutationStatus.AlreadyApplied);
        replay.ActionId.ShouldBe(saved.ActionId); replay.EventIds.ShouldBe(saved.EventIds);
        var rows = await s.Context.VetEvents.OrderBy(x => x.EventType).ToListAsync();
        rows.Select(x => x.Value).ShouldBe(new[] { 6.4m, 0.125m });
        rows.ShouldAllBe(x => x.Revision == 1 && x.SourceAuthorUserId == 111 && x.DeletedAt == null);
        var action = await s.Context.VetDiaryActions.SingleAsync();
        action.ActorUserId.ShouldBe(222); action.Kind.ShouldBe("save");
        (await s.Context.VetDiaryActionChanges.CountAsync()).ShouldBe(2);
        var mismatch = await s.Diary.ApplyAsync(mutation with { Changes = [new(null, null, e.State with { Value = 0.3m })] }, CancellationToken.None);
        mismatch.Status.ShouldBe(VetMutationStatus.Refused);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task One_stale_event_revision_rolls_back_entire_mixed_correction()
    {
        await SeedAsync(); await using var s = Open();
        var e = await EvidenceAsync(s);
        var saved = await s.Diary.ApplyAsync(Save(Scope, e.Source, e.Profile, e.State,
            e.State with { EventType = "insulin", Value = 0.3m, Unit = "U" }), CancellationToken.None);
        var rows = await s.Context.VetEvents.AsNoTracking().OrderBy(x => x.EventType).ToListAsync();
        var mutation = new VetDiaryMutation(Scope, Guid.NewGuid(), 111, "manual", e.Profile.Id,
            [new(rows[0].Id, 1, e.State with { Value = 6.8m }),
                new(rows[1].Id, 9, e.State with { EventType = "insulin", Value = 0.125m, Unit = "U" })]);
        (await s.Diary.ApplyAsync(mutation, CancellationToken.None)).Status.ShouldBe(VetMutationStatus.Stale);
        (await s.Context.VetEvents.AsNoTracking().OrderBy(x => x.EventType).Select(x => x.Value).ToListAsync()).ShouldBe(new[] { 6.4m, 0.3m });
        (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(1);
        saved.EventIds.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_confirmation_has_one_winner_and_records_accepting_member_as_actor()
    {
        await SeedAsync();
        VetPendingDecision pending; VetDiaryMutation mutation;
        await using (var setup = Open())
        {
            var e = await EvidenceAsync(setup);
            var changes = new[] { new VetEventChange(null, null, e.State) };
            pending = await setup.Diary.PutPendingAsync(Scope, e.Source.Source.Id, e.Source.Revision.Id,
                e.State.ExtractionResultId, 111, JsonSerializer.Serialize(new VetProposal([], changes, [], "confirm")), CancellationToken.None);
            await setup.Diary.SetPromptAsync(Scope, pending.Id, pending.ReviewRevision, 700, CancellationToken.None);
            mutation = new(Scope, pending.OperationKey, 222, "confirm", e.Profile.Id, changes,
                pending.SourceId, pending.InputRevisionId, pending.Id, pending.ReviewRevision);
        }
        await using var first = Open(); await using var second = Open();
        var results = await Task.WhenAll(first.Diary.ApplyAsync(mutation, CancellationToken.None),
            second.Diary.ApplyAsync(mutation, CancellationToken.None));
        results.Count(r => r.Status == VetMutationStatus.Applied).ShouldBe(1);
        results.Count(r => r.Status == VetMutationStatus.AlreadyApplied).ShouldBe(1);
        var row = await first.Context.VetEvents.AsNoTracking().SingleAsync();
        row.SourceAuthorUserId.ShouldBe(111); row.Revision.ShouldBe(1); row.Value.ShouldBe(6.4m);
        var action = await first.Context.VetDiaryActions.AsNoTracking().SingleAsync();
        action.ActorUserId.ShouldBe(222);
        var resolved = await first.Diary.GetPendingAsync(Scope, pending.Id, CancellationToken.None);
        resolved!.State.ShouldBe("accepted"); resolved.ResolvedByUserId.ShouldBe(222);
    }

    [Fact]
    public async Task Acceptance_and_decline_race_cannot_overwrite_first_valid_decision()
    {
        await SeedAsync(); await using var setup = Open();
        var e = await EvidenceAsync(setup);
        var changes = new[] { new VetEventChange(null, null, e.State) };
        var p = await setup.Diary.PutPendingAsync(Scope, e.Source.Source.Id, e.Source.Revision.Id,
            e.State.ExtractionResultId, 111, JsonSerializer.Serialize(new VetProposal([], changes, [], "confirm")), CancellationToken.None);
        await setup.Diary.SetPromptAsync(Scope, p.Id, p.ReviewRevision, 700, CancellationToken.None);
        await using var accept = Open(); await using var decline = Open();
        var write = accept.Diary.ApplyAsync(new(Scope, p.OperationKey, 222, "confirm", e.Profile.Id, changes,
            p.SourceId, p.InputRevisionId, p.Id, p.ReviewRevision), CancellationToken.None);
        var refusal = decline.Diary.DeclineAsync(Scope, p.Id, p.ReviewRevision, 111, CancellationToken.None);
        await Task.WhenAll(write, refusal);
        var current = (await setup.Diary.GetPendingAsync(Scope, p.Id, CancellationToken.None))!;
        var saved = await setup.Context.VetEvents.CountAsync();
        if (current.State == "accepted")
        {
            saved.ShouldBe(1); (await refusal).ShouldBeFalse(); (await write).Status.ShouldBe(VetMutationStatus.Applied);
        }
        else
        {
            current.State.ShouldBe("declined"); saved.ShouldBe(0); (await refusal).ShouldBeTrue();
            (await write).Status.ShouldBe(VetMutationStatus.Stale);
        }
    }

    [Fact]
    public async Task Review_mismatch_or_unseen_content_is_refused_without_resolving_pending()
    {
        await SeedAsync(); await using var s = Open();
        var e = await EvidenceAsync(s);
        var reviewed = new[] { new VetEventChange(null, null, e.State) };
        var p = await s.Diary.PutPendingAsync(Scope, e.Source.Source.Id, e.Source.Revision.Id,
            e.State.ExtractionResultId, 111, JsonSerializer.Serialize(new VetProposal([], reviewed, [], "confirm")), CancellationToken.None);
        var mutation = new VetDiaryMutation(Scope, p.OperationKey, 111, "confirm", e.Profile.Id, reviewed,
            p.SourceId, p.InputRevisionId, p.Id, p.ReviewRevision);
        (await s.Diary.ApplyAsync(mutation, CancellationToken.None)).Status.ShouldBe(VetMutationStatus.Stale);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0);
        (await s.Context.VetDiaryActions.CountAsync()).ShouldBe(0);
        await s.Diary.SetPromptAsync(Scope, p.Id, p.ReviewRevision, 700, CancellationToken.None);
        (await s.Diary.ApplyAsync(mutation with { ReviewRevision = 99 }, CancellationToken.None)).Status.ShouldBe(VetMutationStatus.Stale);
        (await s.Diary.ApplyAsync(mutation with { Changes = [new(null, null, e.State with { Value = 7.1m })] }, CancellationToken.None))
            .Status.ShouldBe(VetMutationStatus.Refused);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0);
        (await s.Diary.GetPendingAsync(Scope, p.Id, CancellationToken.None))!.State.ShouldBe("pending");
    }

    [Fact]
    public async Task Undo_uses_action_actor_and_protects_independent_changes_and_replays_same_subset()
    {
        await SeedAsync(); await using var s = Open();
        var e = await EvidenceAsync(s);
        await s.Diary.ApplyAsync(Save(Scope, e.Source, e.Profile, e.State,
            e.State with { EventType = "insulin", Value = 0.125m, Unit = "U" }), CancellationToken.None);
        var rows = await s.Context.VetEvents.AsNoTracking().OrderBy(x => x.EventType).ToListAsync();
        Clock.UtcNow = Now.AddMinutes(1);
        await s.Diary.ApplyAsync(new(Scope, Guid.NewGuid(), 222, "manual", e.Profile.Id,
            [new(rows[0].Id, 1, e.State with { Value = 6.8m })]), CancellationToken.None);
        var key = Guid.NewGuid();
        var undone = await s.Diary.UndoAsync(Scope, 111, key, CancellationToken.None);
        undone.EventIds.ShouldBe(new[] { rows[1].Id }); undone.ProtectedIds.ShouldBe(new[] { rows[0].Id });
        var glucose = (await s.Diary.GetEventAsync(Scope, rows[0].Id, CancellationToken.None))!;
        glucose.Value.ShouldBe(6.8m); glucose.Revision.ShouldBe(2); glucose.DeletedAt.ShouldBeNull();
        var insulin = (await s.Diary.GetEventAsync(Scope, rows[1].Id, CancellationToken.None))!;
        insulin.Value.ShouldBe(0.125m); insulin.Revision.ShouldBe(2); insulin.DeleteReason.ShouldBe("undo");
        Clock.UtcNow = Now.AddMinutes(2);
        var correctionUndo = await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), CancellationToken.None);
        correctionUndo.EventIds.ShouldBe(new[] { rows[0].Id });
        var replay = await s.Diary.UndoAsync(Scope, 111, key, CancellationToken.None);
        replay.EventIds.ShouldBe(undone.EventIds); replay.ProtectedIds.ShouldBe(undone.ProtectedIds);
        glucose = (await s.Diary.GetEventAsync(Scope, rows[0].Id, CancellationToken.None))!;
        glucose.Value.ShouldBe(6.4m); glucose.Revision.ShouldBe(3);
        (await s.Context.VetDiaryActions.CountAsync(a => a.Kind == "undo")).ShouldBe(2);
    }

    [Fact]
    public async Task Delete_and_undo_restore_same_identity_with_increasing_revision_and_keep_source()
    {
        await SeedAsync(); await using var s = Open();
        var e = await EvidenceAsync(s);
        var saved = await s.Diary.ApplyAsync(Save(Scope, e.Source, e.Profile, e.State), CancellationToken.None);
        var id = saved.EventIds.Single();
        Clock.UtcNow = Now.AddMinutes(1);
        var deleted = await s.Diary.ApplyAsync(new(Scope, Guid.NewGuid(), 222, "delete", e.Profile.Id,
            [new(id, 1, e.State with { DeletedAt = Clock.UtcNow, DeletedByUserId = 222, DeleteReason = "manual" })]), CancellationToken.None);
        deleted.EventIds.ShouldBe(new[] { id });
        Clock.UtcNow = Now.AddMinutes(2);
        (await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), CancellationToken.None)).EventIds.ShouldBe(new[] { id });
        var restored = (await s.Diary.GetEventAsync(Scope, id, CancellationToken.None))!;
        restored.Revision.ShouldBe(3); restored.DeletedAt.ShouldBeNull(); restored.SourceAuthorUserId.ShouldBe(111);
        (await s.Context.VetTextSources.CountAsync()).ShouldBe(1);
        (await s.Context.VetTextSourceRevisions.CountAsync()).ShouldBe(1);
        (await s.Context.VetDiaryActionChanges.OrderBy(c => c.Id).Select(c => c.AfterRevision).ToListAsync()).ShouldBe(new[] { 1, 2, 3 });
    }

    [Theory]
    [InlineData(24, true)]
    [InlineData(25, false)]
    public async Task Undo_window_is_action_time_not_old_measurement_time(int hours, bool allowed)
    {
        await SeedAsync(); await using var s = Open();
        var e = await EvidenceAsync(s);
        var old = e.State with { OccurredAt = DateTimeOffset.Parse("2001-04-03T10:00:00Z"), LocalTime = "2001-04-03 10:00:00" };
        await s.Diary.ApplyAsync(Save(Scope, e.Source, e.Profile, old), CancellationToken.None);
        Clock.UtcNow = Now.AddHours(hours);
        var result = await s.Diary.UndoAsync(Scope, 111, Guid.NewGuid(), CancellationToken.None);
        result.Status.ShouldBe(allowed ? VetMutationStatus.Applied : VetMutationStatus.NotFound);
        var row = await s.Context.VetEvents.AsNoTracking().SingleAsync();
        row.Revision.ShouldBe(allowed ? 2 : 1);
        (row.DeletedAt is not null).ShouldBe(allowed);
    }

    [Fact]
    public async Task Source_edits_are_immutable_preserve_author_time_and_reject_stale_write()
    {
        await SeedAsync(); await using var s = Open();
        var e = await EvidenceAsync(s);
        var pending = await s.Diary.PutPendingAsync(Scope, e.Source.Source.Id, e.Source.Revision.Id,
            e.State.ExtractionResultId, 111, JsonSerializer.Serialize(new VetProposal([], [new(null, null, e.State)], [], "confirm")), CancellationToken.None);
        var edit = Text("synthetic corrected report") with { IsEdit = true, EditedAt = Now.AddMinutes(1), SentAt = Now.AddHours(2) };
        var newest = await s.Diary.AdmitAsync(Scope, edit, 2, CancellationToken.None);
        newest.Source.Id.ShouldBe(e.Source.Source.Id); newest.Source.SentAt.ShouldBe(Now);
        newest.Source.SourceAuthorUserId.ShouldBe(111); newest.Revision.Ordinal.ShouldBe(2);
        (await s.Diary.AdmitAsync(Scope, edit, 2, CancellationToken.None)).Revision.Id.ShouldBe(newest.Revision.Id);
        (await s.Context.VetTextSourceRevisions.OrderBy(r => r.Ordinal).Select(r => r.Text).ToListAsync())
            .ShouldBe(new[] { "synthetic actual report", "synthetic corrected report" });
        (await s.Diary.ApplyAsync(Save(Scope, e.Source, e.Profile, e.State), CancellationToken.None)).Status.ShouldBe(VetMutationStatus.Stale);
        (await s.Diary.GetPendingAsync(Scope, pending.Id, CancellationToken.None))!.State.ShouldBe("superseded");
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Profile_changes_require_owner_and_revision_and_keep_attributed_field_provenance()
    {
        await SeedAsync(); await using var s = Open();
        var p = await s.Profiles.GetOrCreateAsync(FamilyId, Bot.BotDbId, CancellationToken.None);
        var refused = await s.Profiles.UpdateAsync(FamilyId, Bot.BotDbId, 222, p.Revision,
            [new("ReportedVetGuidance", "synthetic reported guidance")], CancellationToken.None);
        refused.Applied.ShouldBeFalse();
        var updated = await s.Profiles.UpdateAsync(FamilyId, Bot.BotDbId, 111, p.Revision,
            [new("OwnerContextNote", "synthetic owner context"), new("ReportedVetGuidance", "synthetic reported guidance")], CancellationToken.None);
        updated.Applied.ShouldBeTrue(); updated.Profile.Revision.ShouldBe(3);
        var provenance = JsonDocument.Parse(updated.Profile.FieldProvenanceJson).RootElement.GetProperty("ReportedVetGuidance");
        provenance.GetProperty("ActorUserId").GetInt64().ShouldBe(111);
        provenance.GetProperty("Source").GetString().ShouldBe("owner-reported veterinarian");
        var stale = await s.Profiles.UpdateAsync(FamilyId, Bot.BotDbId, 111, 2,
            [new("ReportedVetGuidance", "unseen conflicting guidance")], CancellationToken.None);
        stale.Stale.ShouldBeTrue();
        (await s.Context.VetProfiles.AsNoTracking().SingleAsync()).ReportedVetGuidance.ShouldBe("synthetic reported guidance");
    }

    [Fact]
    public async Task Botwide_history_pages_facts_without_other_place_handles_and_exact_place_mutations_refuse()
    {
        await SeedAsync(); await using var s = Open();
        var e = await EvidenceAsync(s);
        var saved = await s.Diary.ApplyAsync(Save(Scope, e.Source, e.Profile, e.State,
            e.State with { EventType = "insulin", Value = 0.3m, Unit = "U" }), CancellationToken.None);
        var other = Scope with { TopicId = 8 };
        var first = await s.Diary.QueryAsync(other, e.Profile.Id, Now.AddDays(-1), Now.AddDays(1), 0, 1, CancellationToken.None);
        first.Total.ShouldBe(2); first.HasMore.ShouldBeTrue(); first.Facts.Single().EventId.ShouldBeNull();
        first.Facts.Single().EventType.ShouldBe("glucose"); first.Facts.Single().Value.ShouldBe(6.4m);
        var last = await s.Diary.QueryAsync(other, e.Profile.Id, Now.AddDays(-1), Now.AddDays(1), 1, 1, CancellationToken.None);
        last.Facts.Single().EventType.ShouldBe("insulin"); last.HasMore.ShouldBeFalse();
        (await s.Diary.GetEventAsync(other, saved.EventIds[0], CancellationToken.None)).ShouldBeNull();
        (await s.Diary.GetSourceAsync(other, e.Source.Source.Id, CancellationToken.None)).ShouldBeNull();
        (await s.Diary.UndoAsync(other, 111, Guid.NewGuid(), CancellationToken.None)).Status.ShouldBe(VetMutationStatus.NotFound);
        await using var unscoped = Unscoped();
        await Should.ThrowAsync<InvalidOperationException>(() => unscoped.Diary.GetEventAsync(Scope, saved.EventIds[0], CancellationToken.None));
        (await unscoped.Context.VetEvents.CountAsync()).ShouldBe(0);
        await using var wrongFamily = Open(FamilyId + 10);
        await Should.ThrowAsync<InvalidOperationException>(() => wrongFamily.Diary.GetEventAsync(Scope, saved.EventIds[0], CancellationToken.None));
    }

    [Fact]
    public async Task Revoked_actor_cannot_write_or_resolve_an_existing_review()
    {
        await SeedAsync(); await using var s = Open();
        var e = await EvidenceAsync(s);
        await Db.FamilyMembers.Where(m => m.FamilyId == FamilyId && m.TelegramUserId == 111)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, Assistant.Domain.Families.FamilyMemberStatus.Denied));
        (await s.Diary.ApplyAsync(Save(Scope, e.Source, e.Profile, e.State), CancellationToken.None)).Status.ShouldBe(VetMutationStatus.Refused);
        (await s.Context.VetEvents.CountAsync()).ShouldBe(0);
    }
}
