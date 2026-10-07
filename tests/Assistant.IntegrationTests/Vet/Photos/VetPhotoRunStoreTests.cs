using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Assistant.IntegrationTests.Vet.Photos;

public sealed class VetPhotoRunStoreTests : VetTestBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private VetPhotoStore Store(VetTestSession s, VetPhotoCapacity? capacity = null) =>
        new(s.Context, s.Current, Clock, capacity ?? new(), new VetPhotoImageDecoder());
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private async Task<(VetPhotoAdmission Admission, VetPhotoOriginalReference Reference)> Original(VetTestSession s, int id,
        int topic = 7, bool edit = false, Guid? sharedBlob = null, int editOrdinal = 1)
    {
        var scope = Scope with { TopicId = topic };
        var message = Text(edit ? $"synthetic edited caption {editOrdinal}" : "synthetic caption", id, topic: topic)
            with { Kind = MessageKind.Photo, IsEdit = edit, EditedAt = edit ? Now.AddSeconds(editOrdinal) : null };
        var updateId = checked(await s.Context.Bots.AsNoTracking().Where(b => b.Id == Bot.BotDbId)
            .Select(b => b.LastUpdateId).SingleAsync() + 1);
        var admitted = await Store(s).AdmitAsync(scope, message, updateId,
            new($"synthetic-file-{id}", $"synthetic-unique-{id}", "synthetic.png", "image/png", 3, 1, 1), null, CancellationToken.None);
        admitted.Input.ShouldNotBeNull();
        var stored = await s.Messages.StoreAsync(Bot.TelegramBotId, updateId, message, CancellationToken.None);
        (await Store(s).BindMessageAsync(scope, admitted.Source!.Id, stored.MessageDbId!.Value, CancellationToken.None)).ShouldBeTrue();
        var blobId = sharedBlob ?? Guid.NewGuid();
        if (sharedBlob == null) s.Context.Add(new VetPhotoBlob { Id = blobId, FamilyId = FamilyId,
            ContentHash = Hash(blobId.ToString()), ActualBytes = 3, Format = "png", Width = 1, Height = 1,
            Content = [1, 2, 3], CreatedAt = Now });
        var reference = new VetPhotoOriginalReference { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = scope.ChatId, TopicId = scope.TopicId,
            InputRevisionId = admitted.Input!.Id, BlobId = blobId, ContentHash = Hash(blobId.ToString()),
            ActualBytes = 3, Format = "png", Width = 1, Height = 1, RetainedAt = Now };
        s.Context.Add(reference); await s.Context.SaveChangesAsync(); return (admitted, reference);
    }
    private async Task<VetPhotoRunSelectionRequest> Request(VetTestSession s, VetPhotoRunPurpose purpose = VetPhotoRunPurpose.Reprocess,
        VetPhotoRunSelectionMode mode = VetPhotoRunSelectionMode.AllOriginals)
    {
        var profile = await s.Context.Set<VetProfile>().AsNoTracking().SingleAsync();
        return new(Scope, Guid.NewGuid(), 111, profile.Revision, purpose, mode,
            purpose == VetPhotoRunPurpose.Reprocess ? "synthetic-image-model" : "", purpose == VetPhotoRunPurpose.Reprocess ? "synthetic-provider" : "");
    }
    private VetPhotoRunHandle Handle(VetPhotoRun run, long actor = 111) => new(Scope, run.Id, actor);
    private VetPhotoReviewHandle Proof(VetPhotoReview review, int? prompt = null) =>
        new(Scope, review.Id, review.Revision, review.OperationKey, 111, prompt);
    private async Task Deliver(VetTestSession s, VetPhotoReview review)
    {
        var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!;
        for (var i = 0; i < pages.Length; i++)
            (await Store(s).RecordPageDeliveryAsync(Proof(review), i, 9000 + i, Hash(pages[i]), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(s).CompleteDeliveryAsync(Proof(review), 8999 + pages.Length, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
    }
    private async Task<VetPhotoRunChange> Approved(VetTestSession s, VetPhotoRunPurpose purpose = VetPhotoRunPurpose.Reprocess)
    {
        var stage = await Store(s).StageRunAsync(await Request(s, purpose), CancellationToken.None);
        stage.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); await Deliver(s, stage.Review!);
        var approved = await Store(s).ApproveRunAsync(Handle(stage.Run!), Proof(stage.Review!), CancellationToken.None);
        approved.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); return approved;
    }
    private async Task Measurement(VetTestSession s, Guid source, DateTimeOffset at)
    {
        var input = await s.Context.Set<VetPhotoSource>().AsNoTracking().Where(x => x.Id == source).Select(x => x.CurrentInputRevisionId).SingleAsync();
        var reading = new VetPhotoEffectiveReading(6.4m, "mmol/L", at, "2031-05-12 12:00:00", "UTC",
            "human_correction", "human_correction", "human_correction", false);
        await s.Context.Set<VetPhotoCandidate>().Where(c => c.SourceId == source).ExecuteUpdateAsync(u =>
            u.SetProperty(c => c.InputRevisionId, input).SetProperty(c => c.EffectiveJson, JsonSerializer.Serialize(reading, Json)));
    }

    [Fact]
    public async Task All_originals_freezes_superseded_unknown_dates_and_more_than_one_window_without_calls()
    {
        await SeedAsync(); await using var s = Open();
        var first = await Original(s, 1); var edit = await Original(s, 1, edit: true, sharedBlob: first.Reference.BlobId);
        for (var i = 2; i <= 52; i++) await Original(s, i);
        var stage = await Store(s).StageRunAsync(await Request(s), CancellationToken.None);
        stage.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); stage.Run!.SelectedCount.ShouldBe(53);
        var inputs = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(stage.Run.SelectionJson, Json)!;
        inputs.Length.ShouldBe(53); inputs.Select(x => x.AttemptKey).Distinct().Count().ShouldBe(53);
        inputs.Where(x => x.SourceId == first.Admission.Source!.Id).Select(x => x.InputRevisionId)
            .ShouldBe([first.Admission.Input!.Id, edit.Admission.Input!.Id]);
        inputs.Single(x => x.InputRevisionId == first.Admission.Input.Id).ExpectedCurrentInputId.ShouldBe(edit.Admission.Input.Id);
        stage.Review!.SelectionJson.ShouldBe(stage.Run.SelectionJson);
        stage.Review.PreviewPagesJson.ShouldContain("53"); stage.Review.PreviewPagesJson.ShouldContain("52");
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image")).ShouldBe(0);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
        (await s.Context.Set<VetPhotoBlob>().CountAsync(b => b.Content != null)).ShouldBe(52);
        await Deliver(s, stage.Review);
        (await Store(s).ApproveRunAsync(Handle(stage.Run), Proof(stage.Review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var windows = await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().OrderBy(w => w.Ordinal).ToListAsync();
        windows.Select(w => JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(w.SelectionJson, Json)!.Length).ShouldBe([50, 3]);
        windows.Select(w => w.State).ShouldBe(["awaiting_continue", "awaiting_continue"]);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image")).ShouldBe(0);
    }

    [Fact]
    public async Task Current_policy_excludes_old_revision_but_shows_it_and_keeps_current_pointer()
    {
        await SeedAsync(); await using var s = Open(); var first = await Original(s, 1);
        var edit = await Original(s, 1, edit: true, sharedBlob: first.Reference.BlobId);
        var stage = await Store(s).StageRunAsync(await Request(s, mode: VetPhotoRunSelectionMode.Current), CancellationToken.None);
        stage.Run!.SelectedCount.ShouldBe(1);
        JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(stage.Run.SelectionJson, Json)!.Single().InputRevisionId.ShouldBe(edit.Admission.Input!.Id);
        stage.Review!.PreviewPagesJson.ShouldContain("1");
        (await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync()).CurrentInputRevisionId.ShouldBe(edit.Admission.Input.Id);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained")).ShouldBe(2);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Measurement_range_counts_unknown_while_upload_recovers_it(bool upload)
    {
        await SeedAsync(); await using var s = Open(); var known = await Original(s, 1); await Original(s, 2);
        await Measurement(s, known.Admission.Source!.Id, Now.AddDays(-40));
        var request = await Request(s, mode: VetPhotoRunSelectionMode.Current);
        request = request with { FromDate = new(2031, 5, 12), UntilDate = new(2031, 5, 12),
            DateAxis = upload ? VetPhotoRunDateAxis.Upload : VetPhotoRunDateAxis.Measurement };
        var stage = await Store(s).StageRunAsync(request, CancellationToken.None);
        if (upload) { stage.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); stage.Run!.SelectedCount.ShouldBe(2); }
        else { stage.Status.ShouldBe(VetPhotoWorkflowStatus.NotFound); stage.SelectionCounts!.UnknownMeasurementSources.ShouldBe(1); (await s.Context.Set<VetPhotoRun>().CountAsync()).ShouldBe(0); }
    }

    [Fact]
    public async Task Measurement_dates_use_profile_local_inclusive_days_and_not_UTC_date()
    {
        await SeedAsync(); await using var s = Open(); var item = await Original(s, 1);
        await Measurement(s, item.Admission.Source!.Id, DateTimeOffset.Parse("2031-05-11T23:30:00Z"));
        await s.Context.Set<VetProfile>().ExecuteUpdateAsync(u => u.SetProperty(p => p.TimeZone, "Europe/Berlin"));
        var request = await Request(s, mode: VetPhotoRunSelectionMode.Current);
        var stage = await Store(s).StageRunAsync(request with { FromDate = new(2031, 5, 12), UntilDate = new(2031, 5, 12) }, CancellationToken.None);
        stage.Run!.SelectedCount.ShouldBe(1);
        var excluded = await Store(s).StageRunAsync(request with { OperationKey = Guid.NewGuid(), FromDate = new(2031, 5, 13), UntilDate = new(2031, 5, 13) }, CancellationToken.None);
        excluded.Status.ShouldBe(VetPhotoWorkflowStatus.NotFound);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task Invalid_selection_fails_before_durable_rows(int variant)
    {
        await SeedAsync(); await using var s = Open(); var item = await Original(s, 1); var r = await Request(s);
        r = variant switch
        {
            0 => r with { OperationKey = Guid.Empty }, 1 => r with { Mode = (VetPhotoRunSelectionMode)99 },
            2 => r with { FromDate = new(2031, 5, 12) }, 3 => r with { Mode = VetPhotoRunSelectionMode.Selected, ReferenceIds = [item.Reference.Id, item.Reference.Id] },
            4 => r with { Mode = VetPhotoRunSelectionMode.Selected, ReferenceIds = [Guid.Empty] },
            5 => r with { ModelName = new string('x', 201) }, 6 => r with { ModelName = "\ud800" },
            _ => r with { UntilDate = DateOnly.MaxValue }
        };
        (await Store(s).StageRunAsync(r, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await s.Context.Set<VetPhotoRun>().CountAsync()).ShouldBe(0); (await s.Context.Set<VetPhotoReview>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Selected_foreign_approved_topic_or_unknown_reference_reads_and_writes_no_target()
    {
        await SeedAsync(); await using var s = Open(); var foreign = await Original(s, 1, topic: 8); var own = await Original(s, 2);
        var r = await Request(s, mode: VetPhotoRunSelectionMode.Selected);
        foreach (var id in new[] { foreign.Reference.Id, Guid.NewGuid() })
        {
            (await Store(s).StageRunAsync(r with { OperationKey = Guid.NewGuid(), ReferenceIds = [own.Reference.Id, id] }, CancellationToken.None))
                .Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
            (await s.Context.Set<VetPhotoRun>().CountAsync()).ShouldBe(0);
        }
    }

    [Fact]
    public async Task Capacity_refusal_never_truncates_whole_selection()
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1); await Original(s, 2);
        (await Store(s, new(MaxInputRevisions: 1)).StageRunAsync(await Request(s), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Full);
        (await s.Context.Set<VetPhotoRun>().CountAsync()).ShouldBe(0); (await s.Context.Set<VetPhotoReview>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Repeated_stage_and_concurrent_approval_reuse_full_selection_and_exact_windows()
    {
        await SeedAsync(); await using var seed = Open(); await Original(seed, 1); var request = await Request(seed);
        var stage = await Store(seed).StageRunAsync(request, CancellationToken.None);
        var replay = await Store(seed).StageRunAsync(request, CancellationToken.None);
        replay.Status.ShouldBe(VetPhotoWorkflowStatus.Existing); replay.Run!.SelectionJson.ShouldBe(stage.Run!.SelectionJson);
        (await Store(seed).StageRunAsync(request with { ModelName = "different-synthetic-model" }, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        await Deliver(seed, stage.Review!); await using var a = Open(); await using var b = Open();
        var results = await Task.WhenAll(Store(a).ApproveRunAsync(Handle(stage.Run), Proof(stage.Review!), CancellationToken.None),
            Store(b).ApproveRunAsync(Handle(stage.Run), Proof(stage.Review!), CancellationToken.None));
        results.Select(x => x.Status).Order().ShouldBe(new[] { VetPhotoWorkflowStatus.Applied, VetPhotoWorkflowStatus.Existing }.Order());
        (await seed.Context.Set<VetPhotoRunWindow>().CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData("undelivered")] [InlineData("hash")] [InlineData("prompt")] [InlineData("profile")] [InlineData("input")] [InlineData("reference")] [InlineData("revoked")]
    public async Task Approval_rechecks_complete_proof_and_current_identity(string defect)
    {
        await SeedAsync(); await using var s = Open(); var item = await Original(s, 1);
        var stage = await Store(s).StageRunAsync(await Request(s), CancellationToken.None);
        if (defect != "undelivered") await Deliver(s, stage.Review!);
        if (defect == "hash") await s.Context.Set<VetPhotoReview>().ExecuteUpdateAsync(u => u.SetProperty(r => r.Fingerprint, "wrong"));
        if (defect == "profile") await s.Context.Set<VetProfile>().ExecuteUpdateAsync(u => u.SetProperty(p => p.Revision, p => p.Revision + 1));
        if (defect == "input") await s.Context.Set<VetPhotoSource>().ExecuteUpdateAsync(u => u.SetProperty(x => x.CurrentOrdinal, x => x.CurrentOrdinal + 1));
        if (defect == "reference") await s.Context.Set<VetPhotoOriginalReference>().ExecuteUpdateAsync(u => u.SetProperty(c => c.Revision, c => c.Revision + 1));
        if (defect == "revoked") await s.Context.Set<FamilyMember>().Where(m => m.TelegramUserId == 111).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied));
        var result = await Store(s).ApproveRunAsync(Handle(stage.Run!), Proof(stage.Review!, defect == "prompt" ? 999 : null), CancellationToken.None);
        new[] { VetPhotoWorkflowStatus.Stale, VetPhotoWorkflowStatus.Refused }.ShouldContain(result.Status);
        (await s.Context.Set<VetPhotoRunWindow>().CountAsync()).ShouldBe(0); (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_continue_releases_only_next_window_and_scheduler_claim_uses_exact_frozen_key()
    {
        await SeedAsync(); await using var seed = Open(); for (var i = 1; i <= 51; i++) await Original(seed, i);
        var approved = await Approved(seed); await using var a = Open(); await using var b = Open();
        var results = await Task.WhenAll(Store(a).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None),
            Store(b).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None));
        results.Select(x => x.Status).Order().ShouldBe(new[] { VetPhotoWorkflowStatus.Applied, VetPhotoWorkflowStatus.Existing }.Order());
        var attempts = await seed.Context.Set<VetPhotoAttempt>().AsNoTracking().Where(x => x.Kind == "image").ToListAsync();
        attempts.Count.ShouldBe(50); attempts.Select(x => x.Id).Distinct().Count().ShouldBe(50);
        var key = attempts[0].Id;
        (await Store(seed).ClaimScheduledImageAsync(Scope, key, 111, CancellationToken.None)).Status.ShouldBe(VetPhotoImageStatus.Claimed);
        var windows = await seed.Context.Set<VetPhotoRunWindow>().AsNoTracking().OrderBy(x => x.Ordinal).ToListAsync();
        windows[1].State.ShouldBe("awaiting_continue");
        var nextKey = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(windows[1].SelectionJson, Json)!.Single().AttemptKey;
        (await Store(seed).ClaimScheduledImageAsync(Scope, nextKey, 111, CancellationToken.None)).Status.ShouldBe(VetPhotoImageStatus.NotFound);
    }

    [Fact]
    public async Task Cancel_preserves_unknown_and_dispatched_charges_originals_and_facts_and_blocks_new_claims()
    {
        await SeedAsync(); await using var s = Open(); for (var i = 1; i <= 4; i++) await Original(s, i);
        var approved = await Approved(s); var continued = await Store(s).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None);
        var attempts = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().Where(a => a.Kind == "image").OrderBy(a => a.Id).ToListAsync();
        var unknown = attempts[0].Id; var dispatched = attempts[1].Id; var claimed = attempts[2].Id;
        await s.Context.Set<VetPhotoAttempt>().Where(a => a.Id == unknown).ExecuteUpdateAsync(u => u.SetProperty(a => a.State, "unknown").SetProperty(a => a.ReservedResultSlot, true));
        await s.Context.Set<VetPhotoAttempt>().Where(a => a.Id == dispatched).ExecuteUpdateAsync(u => u.SetProperty(a => a.State, "dispatched").SetProperty(a => a.ReservedResultSlot, true));
        await s.Context.Set<VetPhotoAttempt>().Where(a => a.Id == claimed).ExecuteUpdateAsync(u => u.SetProperty(a => a.State, "claimed").SetProperty(a => a.ReservedResultSlot, true));
        (await Store(s).CancelRunAsync(Handle(approved.Run!), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var rows = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().Where(a => a.Kind == "image").ToListAsync();
        rows.Single(a => a.Id == unknown).State.ShouldBe("unknown"); rows.Single(a => a.Id == unknown).ReservedResultSlot.ShouldBeTrue();
        rows.Single(a => a.Id == dispatched).State.ShouldBe("dispatched"); rows.Single(a => a.Id == dispatched).ReservedResultSlot.ShouldBeTrue();
        rows.Single(a => a.Id == claimed).State.ShouldBe("cancelled"); rows.Single(a => a.Id == claimed).ReservedResultSlot.ShouldBeFalse();
        (await Store(s).ClaimScheduledImageAsync(Scope, unknown, 111, CancellationToken.None)).Status.ShouldBe(VetPhotoImageStatus.Stale);
        (await Store(s).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained")).ShouldBe(4);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Progress_pages_show_every_old_window_and_scope_actor_gates_hide_handles()
    {
        await SeedAsync(); await using var s = Open(); for (var i = 1; i <= 101; i++) await Original(s, i);
        var approved = await Approved(s);
        var first = await Store(s).GetRunAsync(Handle(approved.Run!), 0, 2, CancellationToken.None);
        first!.TotalWindows.ShouldBe(3); first.RemainingInputs.ShouldBe(101); first.Windows.Select(w => w.Ordinal).ShouldBe([0, 1]); first.NextOffset.ShouldBe(2);
        var last = await Store(s).GetRunAsync(Handle(approved.Run!), 2, 2, CancellationToken.None);
        last!.Windows.Single().Ordinal.ShouldBe(2); last.NextOffset.ShouldBeNull();
        (await Store(s).GetRunAsync(Handle(approved.Run!, 222), 0, 2, CancellationToken.None))!.Run.Id.ShouldBe(approved.Run!.Id);
        (await Store(s).GetRunAsync(Handle(approved.Run!) with { Scope = Scope with { TopicId = 8 } }, 0, 2, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Deletion_summary_acceptance_changes_zero_bytes_then_full_window_and_remaining_are_visible()
    {
        await SeedAsync(); await using var s = Open(); for (var i = 1; i <= 51; i++) await Original(s, i);
        var approved = await Approved(s, VetPhotoRunPurpose.DeleteOriginals);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained")).ShouldBe(51);
        (await s.Context.Set<VetPhotoBlob>().CountAsync(b => b.Content != null)).ShouldBe(51);
        var window = await Store(s).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None);
        window.Review!.Kind.ShouldBe("delete_originals");
        var selected = JsonSerializer.Deserialize<VetPhotoOriginalSelection[]>(window.Review.SelectionJson, Json)!;
        selected.Length.ShouldBe(50);
        var pages = JsonSerializer.Deserialize<string[]>(window.Review.PreviewPagesJson, Json)!;
        foreach (var item in selected) string.Join("\n", pages).ShouldContain(item.ReferenceId.ToString("D"));
        string.Join("\n", pages).ShouldContain("останется 1");
        await Deliver(s, window.Review);
        var result = await Store(s).ConfirmDeletionWindowAsync(Handle(approved.Run!), window.Window!.Id, Proof(window.Review), CancellationToken.None);
        result.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); result.Deletion!.ReferenceCount.ShouldBe(50);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained")).ShouldBe(1);
        (await s.Context.Set<VetPhotoSource>().CountAsync()).ShouldBe(51); (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
        var next = await Store(s).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None);
        JsonSerializer.Deserialize<VetPhotoOriginalSelection[]>(next.Review!.SelectionJson, Json)!.Length.ShouldBe(1);
    }

    [Fact]
    public async Task Shared_blob_count_refreshes_only_after_prior_window_and_restart_reconciles_committed_deletion_once()
    {
        await SeedAsync(); await using var s = Open(); var first = await Original(s, 1);
        for (var i = 2; i <= 51; i++) await Original(s, i, sharedBlob: first.Reference.BlobId);
        var approved = await Approved(s, VetPhotoRunPurpose.DeleteOriginals);
        var window = await Store(s).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None); await Deliver(s, window.Review!);
        var deletion = await Store(s).DeleteOriginalsAsync(new(Scope, window.Review!.Id, 1, window.Review.OperationKey, 111, null), CancellationToken.None);
        deletion.Status.ShouldBe(VetMutationStatus.Applied); deletion.ReclaimableBytes.ShouldBe(0);
        await using var restarted = Open();
        (await Store(restarted).ReconcileWindowAsync(Handle(approved.Run!), window.Window!.Id, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(restarted).ReconcileWindowAsync(Handle(approved.Run!), window.Window.Id, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        var next = await Store(restarted).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None);
        JsonSerializer.Deserialize<VetPhotoOriginalSelection[]>(next.Review!.SelectionJson, Json)!.Single().ExpectedBlobRetainedReferences.ShouldBe(1);
        await Deliver(restarted, next.Review);
        var final = await Store(restarted).ConfirmDeletionWindowAsync(Handle(approved.Run!), next.Window!.Id, Proof(next.Review), CancellationToken.None);
        final.Run!.State.ShouldBe("completed"); final.Deletion!.ReclaimableBytes.ShouldBe(3);
        (await Store(restarted).ReclaimAsync(FamilyId, 50, CancellationToken.None)).ShouldBe(1);
        (await Store(restarted).ReclaimAsync(FamilyId, 50, CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task Nonowner_cannot_stage_deletion_and_cancellation_between_wrapper_and_archive_writes_zero()
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1);
        var r = await Request(s, VetPhotoRunPurpose.DeleteOriginals);
        (await Store(s).StageRunAsync(r with { ActorUserId = 222 }, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        var approved = await Approved(s, VetPhotoRunPurpose.DeleteOriginals);
        var window = await Store(s).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None); await Deliver(s, window.Review!);
        var interceptor = new CancelBeforeArchive(async () =>
        {
            await using var other = Open();
            (await Store(other).CancelRunAsync(Handle(approved.Run!), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        });
        await using var raced = Open(interceptor: interceptor);
        var result = await Store(raced).ConfirmDeletionWindowAsync(Handle(approved.Run!), window.Window!.Id, Proof(window.Review!), CancellationToken.None);
        interceptor.Fired.ShouldBeTrue(); result.Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().SingleAsync()).State.ShouldBe("retained");
        (await s.Context.Set<VetPhotoBlob>().AsNoTracking().SingleAsync()).Content.ShouldBe(new byte[] { 1, 2, 3 });
        (await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(x => x.Id == window.Review!.Id)).State.ShouldBe("stale");
    }
    [Theory]
    [InlineData("pending")] [InlineData("excluded")] [InlineData("cancelled")]
    public async Task Completed_failed_window_links_comparison_but_never_restores_or_changes_manual_candidate(string state)
    {
        await SeedAsync(); await using var s = Open(); var original = await Original(s, 1);
        await s.Context.Set<VetPhotoCandidate>().ExecuteUpdateAsync(u => u.SetProperty(c => c.State, state)
            .SetProperty(c => c.ManuallyCorrected, true).SetProperty(c => c.RequiresExplicitRestoration, true));
        await s.Context.Set<VetPhotoBatch>().ExecuteUpdateAsync(u => u.SetProperty(b => b.State, "cancelled"));
        var approved = await Approved(s); var started = await Store(s).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None);
        await s.Context.Set<VetPhotoAttempt>().Where(a => a.Kind == "image").ExecuteUpdateAsync(u => u.SetProperty(a => a.State, "failed")
            .SetProperty(a => a.FailureCategory, "local_validation"));
        var profile = await s.Context.Set<VetProfile>().AsNoTracking().SingleAsync();
        var request = new VetPhotoStageReview(Scope, Guid.NewGuid(), 111, VetPhotoReviewKind.ReextractComparison, null, null,
            profile.Revision, JsonSerializer.Serialize(new[] { new { sourceId = original.Admission.Source!.Id, failed = true } }, Json),
            VetPhotoReviewFormatter.FormatBlocks("synthetic complete comparison", ["synthetic failed result; manual value unchanged; restoration requires a specific proposal"], "synthetic explicit decision"), started.Window!.Id);
        var review = (await Store(s).StageReviewAsync(request, CancellationToken.None)).Review!;
        (await Store(s).AttachComparisonAsync(Handle(approved.Run!), started.Window.Id, review.Id, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        await Deliver(s, review);
        (await Store(s).ReconcileWindowAsync(Handle(approved.Run!), started.Window.Id, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync();
        candidate.State.ShouldBe(state); candidate.ManuallyCorrected.ShouldBeTrue(); candidate.RequiresExplicitRestoration.ShouldBeTrue();
        candidate.EventId.ShouldBeNull(); candidate.Revision.ShouldBe(1);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0); (await s.Context.Set<VetDiaryAction>().CountAsync()).ShouldBe(0);
        (await s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().SingleAsync()).State.ShouldBe("retained");
    }

    [Theory]
    [InlineData("queued")] [InlineData("unknown")] [InlineData("stale_reference")] [InlineData("foreign_window")]
    public async Task Comparison_attachment_rejects_unfinished_or_stale_evidence_without_progress(string defect)
    {
        await SeedAsync(); await using var s = Open(); var original = await Original(s, 1);
        var approved = await Approved(s); var started = await Store(s).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None);
        if (defect != "queued") await s.Context.Set<VetPhotoAttempt>().Where(a => a.Kind == "image").ExecuteUpdateAsync(u =>
            u.SetProperty(a => a.State, defect == "unknown" ? "unknown" : "failed").SetProperty(a => a.ReservedResultSlot, false));
        if (defect == "stale_reference") await s.Context.Set<VetPhotoOriginalReference>().ExecuteUpdateAsync(u => u.SetProperty(c => c.Revision, c => c.Revision + 1));
        var profile = await s.Context.Set<VetProfile>().AsNoTracking().SingleAsync();
        var review = (await Store(s).StageReviewAsync(new(Scope, Guid.NewGuid(), 111, VetPhotoReviewKind.ReextractComparison,
            null, null, profile.Revision, JsonSerializer.Serialize(new[] { new { sourceId = original.Admission.Source!.Id } }, Json),
            VetPhotoReviewFormatter.FormatBlocks("synthetic comparison", ["synthetic complete evidence"], "synthetic confirm"),
            defect == "foreign_window" ? null : started.Window!.Id), CancellationToken.None)).Review!;
        var attached = await Store(s).AttachComparisonAsync(Handle(approved.Run!), started.Window!.Id, review.Id, CancellationToken.None);
        new[] { VetPhotoWorkflowStatus.Incomplete, VetPhotoWorkflowStatus.Stale }.ShouldContain(attached.Status);
        (await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().SingleAsync()).ComparisonReviewId.ShouldBeNull();
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync()).NextWindowOrdinal.ShouldBe(0);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Initial_actual_approver_owns_dispatch_but_later_member_confirmation_is_independent()
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1);
        var stage = await Store(s).StageRunAsync(await Request(s), CancellationToken.None); await Deliver(s, stage.Review!);
        var handle = Handle(stage.Run!, 222); var proof = Proof(stage.Review!) with { ActorUserId = 222 };
        var approved = await Store(s).ApproveRunAsync(handle, proof, CancellationToken.None);
        approved.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); approved.Run!.ActorUserId.ShouldBe(222);
        approved.Review!.RequesterUserId.ShouldBe(111); approved.Review.DecisionActorUserId.ShouldBe(222);
        (await Store(s).ContinueRunAsync(Handle(stage.Run!), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        var started = await Store(s).ContinueRunAsync(handle, CancellationToken.None); started.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var key = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(started.Window!.SelectionJson, Json)!.Single().AttemptKey;
        (await Store(s).ClaimScheduledImageAsync(Scope, key, 222, CancellationToken.None)).Status.ShouldBe(VetPhotoImageStatus.Claimed);
        (await Store(s).CancelRunAsync(Handle(stage.Run!), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Kind == "image")).State.ShouldBe("cancelled");
    }

    [Fact]
    public async Task Another_owner_can_confirm_a_fully_shown_deletion_window_without_rebinding_run_actor()
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1);
        await s.Context.Set<FamilyMember>().Where(m => m.TelegramUserId == 222).ExecuteUpdateAsync(u => u.SetProperty(m => m.IsOwner, true));
        var approved = await Approved(s, VetPhotoRunPurpose.DeleteOriginals);
        var window = await Store(s).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None); await Deliver(s, window.Review!);
        var result = await Store(s).ConfirmDeletionWindowAsync(Handle(approved.Run!, 222), window.Window!.Id,
            Proof(window.Review!) with { ActorUserId = 222 }, CancellationToken.None);
        result.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); result.Run!.ActorUserId.ShouldBe(111);
        (await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == window.Review!.Id)).DecisionActorUserId.ShouldBe(222);
        (await s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().SingleAsync()).DeletedByUserId.ShouldBe(222);
    }

    [Fact]
    public async Task Database_failure_rolls_back_summary_acceptance_and_all_window_creation()
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1);
        var stage = await Store(s).StageRunAsync(await Request(s), CancellationToken.None); await Deliver(s, stage.Review!);
        var interceptor = new RejectAcceptedReview(); await using var failed = Open(interceptor: interceptor);
        await Should.ThrowAsync<DbUpdateException>(() => Store(failed).ApproveRunAsync(Handle(stage.Run!), Proof(stage.Review!), CancellationToken.None));
        interceptor.Fired.ShouldBeTrue();
        (await s.Context.Set<VetPhotoRunWindow>().CountAsync()).ShouldBe(0);
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync()).State.ShouldBe("preview");
        (await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync()).State.ShouldBe("preview");
        (await Store(s).ApproveRunAsync(Handle(stage.Run!), Proof(stage.Review!), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await s.Context.Set<VetPhotoRunWindow>().CountAsync()).ShouldBe(1);
    }
    [Fact]
    public async Task Successful_comparison_waits_for_actual_external_action_marker_then_restart_advances_once()
    {
        await SeedAsync(); await using var s = Open(); var item = await Original(s, 1);
        var approved = await Approved(s); var started = await Store(s).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None);
        var snapshot = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(started.Window!.SelectionJson, Json)!.Single();
        var claim = (await Store(s).ClaimScheduledImageAsync(Scope, snapshot.AttemptKey, 111, CancellationToken.None)).Claim!;
        (await Store(s).MarkImageDispatchedAsync(Scope, snapshot.AttemptKey, claim.ClaimToken, 111, CancellationToken.None)).ShouldBeTrue();
        var structured = JsonSerializer.Serialize(new { schema_version = 1, photo_source_id = snapshot.SourceId.ToString("D"),
            input_revision_id = snapshot.InputRevisionId.ToString("D"), kind = "unreadable", displays = Array.Empty<object>(),
            reasons = new[] { "unreadable" }, notes = (string?)null }, Json);
        var extracted = await Store(s).CompleteImageAsync(new(Scope, claim.AttemptKey, claim.ClaimToken, 111,
            claim.SourceId, claim.InputRevisionId, "synthetic-image-model", structured), CancellationToken.None);
        extracted.Status.ShouldBe(VetPhotoImageStatus.ProposedDelta);
        var profile = await s.Context.Set<VetProfile>().AsNoTracking().SingleAsync();
        var review = (await Store(s).StageReviewAsync(new(Scope, Guid.NewGuid(), 111, VetPhotoReviewKind.ReextractComparison,
            null, null, profile.Revision, JsonSerializer.Serialize(new[] { new { sourceId = snapshot.SourceId, resultId = extracted.Extraction!.Id } }, Json),
            VetPhotoReviewFormatter.FormatBlocks("synthetic full comparison", ["synthetic unresolved display; retain current disposition"], "synthetic confirm keep"),
            started.Window.Id), CancellationToken.None)).Review!;
        (await Store(s).AttachComparisonAsync(Handle(approved.Run!), started.Window.Id, review.Id, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        await Deliver(s, review);
        (await Store(s).ReconcileWindowAsync(Handle(approved.Run!), started.Window.Id, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        // Seed the exact external atomic-diary boundary: an accepted all-keep review and its durable action marker.
        // This store neither creates that marker nor applies diary changes.
        var marker = new VetDiaryAction { FamilyId = FamilyId, BotDbId = Bot.BotDbId, TelegramBotId = Bot.TelegramBotId,
            ChatId = Scope.ChatId, TopicId = Scope.TopicId, ActorUserId = 222, OperationKey = review.OperationKey,
            Fingerprint = Hash("synthetic keep proof"), Kind = "photo_comparison_keep", OutcomeJson = "{}", CreatedAt = Now };
        s.Context.Add(marker); await s.Context.SaveChangesAsync();
        await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "accepted")
            .SetProperty(r => r.DecisionActorUserId, 222L).SetProperty(r => r.ActionId, marker.Id));
        await using var restarted = Open();
        var reconciled = await Store(restarted).ReconcileWindowAsync(Handle(approved.Run!), started.Window.Id, CancellationToken.None);
        reconciled.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); reconciled.Run!.State.ShouldBe("completed");
        reconciled.Window!.ActionId.ShouldBe(marker.Id);
        (await Store(restarted).ReconcileWindowAsync(Handle(approved.Run!), started.Window.Id, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        (await restarted.Context.Set<VetDiaryAction>().CountAsync()).ShouldBe(1); (await restarted.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
        (await restarted.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync()).Revision.ShouldBe(1);
        (await restarted.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync()).CurrentInputRevisionId.ShouldBe(item.Admission.Input!.Id);
    }

    [Theory]
    [InlineData("cancelled")] [InlineData("next_window")] [InlineData("link")]
    public async Task Archive_transaction_rechecks_run_fence_even_when_the_complete_preview_survives(string defect)
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1);
        var approved = await Approved(s, VetPhotoRunPurpose.DeleteOriginals);
        var window = await Store(s).ContinueRunAsync(Handle(approved.Run!), CancellationToken.None); await Deliver(s, window.Review!);
        if (defect == "cancelled") await s.Context.Set<VetPhotoRun>().ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "cancelled").SetProperty(r => r.CancelledAt, Now));
        if (defect == "next_window") await s.Context.Set<VetPhotoRun>().ExecuteUpdateAsync(u => u.SetProperty(r => r.NextWindowOrdinal, 1));
        if (defect == "link") await s.Context.Set<VetPhotoRunWindow>().ExecuteUpdateAsync(u => u.SetProperty(w => w.ComparisonReviewId, (Guid?)null));
        var result = await Store(s).DeleteOriginalsAsync(new(Scope, window.Review!.Id, 1, window.Review.OperationKey, 111, null), CancellationToken.None);
        result.Status.ShouldBe(VetMutationStatus.Stale); result.ReferenceCount.ShouldBe(0);
        (await s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().SingleAsync()).State.ShouldBe("retained");
        (await s.Context.Set<VetPhotoBlob>().AsNoTracking().SingleAsync()).Content.ShouldBe(new byte[] { 1, 2, 3 });
        (await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == window.Review.Id)).State.ShouldBe("preview");
    }

    [Fact]
    public async Task Scheduled_evidence_is_not_blocked_by_candidate_changes_from_an_earlier_historical_window()
    {
        await SeedAsync(); await using var s = Open(); var first = await Original(s, 1);
        for (var i = 1; i <= 50; i++) await Original(s, 1, edit: true, sharedBlob: first.Reference.BlobId, editOrdinal: i);
        var approved = await Approved(s);
        var full = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(approved.Run!.SelectionJson, Json)!;
        full.Length.ShouldBe(51); full.Select(x => x.SourceId).Distinct().Count().ShouldBe(1);
        full.All(x => x.ExpectedCandidateRevision == null).ShouldBeTrue();
        var before = approved.Run.SelectionJson;
        await s.Context.Set<VetPhotoCandidate>().ExecuteUpdateAsync(u => u.SetProperty(c => c.Revision, c => c.Revision + 1)
            .SetProperty(c => c.ManuallyCorrected, true).SetProperty(c => c.State, "excluded"));
        var continued = await Store(s).ContinueRunAsync(Handle(approved.Run), CancellationToken.None);
        continued.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var snapshot = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(continued.Window!.SelectionJson, Json)!.First();
        var claim = await Store(s).ClaimScheduledImageAsync(Scope, snapshot.AttemptKey, 111, CancellationToken.None);
        claim.Status.ShouldBe(VetPhotoImageStatus.Claimed); claim.Claim!.HistoricalSelection.ShouldBeTrue();
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync()).SelectionJson.ShouldBe(before);
        var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync();
        candidate.State.ShouldBe("excluded"); candidate.ManuallyCorrected.ShouldBeTrue(); candidate.Revision.ShouldBe(52);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(50)] [InlineData(51)]
    public async Task Explicit_selected_references_are_windowed_without_a_global_fifty_cap(int count)
    {
        await SeedAsync(); await using var s = Open(); var ids = new List<Guid>();
        for (var i = 1; i <= count; i++) ids.Add((await Original(s, i)).Reference.Id);
        var request = await Request(s, mode: VetPhotoRunSelectionMode.Selected);
        var stage = await Store(s).StageRunAsync(request with { ReferenceIds = ids }, CancellationToken.None);
        stage.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); stage.Run!.SelectedCount.ShouldBe(count);
        await Deliver(s, stage.Review!);
        (await Store(s).ApproveRunAsync(Handle(stage.Run), Proof(stage.Review!), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await s.Context.Set<VetPhotoRunWindow>().CountAsync()).ShouldBe((count + 49) / 50);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained")).ShouldBe(count);
    }

    [Fact]
    public async Task No_retained_originals_returns_reupload_required_scope_without_creating_empty_run()
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1);
        await s.Context.Set<VetPhotoOriginalReference>().ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "deleted").SetProperty(r => r.DeletedAt, Now));
        var result = await Store(s).StageRunAsync(await Request(s), CancellationToken.None);
        result.Status.ShouldBe(VetPhotoWorkflowStatus.NotFound); result.Run.ShouldBeNull(); result.Review.ShouldBeNull();
        result.SelectionCounts!.DeletedByteSources.ShouldBe(1);
        (await s.Context.Set<VetPhotoRun>().CountAsync()).ShouldBe(0); (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image")).ShouldBe(0);
    }

    [Fact]
    public async Task Historical_batch_filter_uses_current_profile_selection_proof_without_reopening_old_batch()
    {
        await SeedAsync(); await using var s = Open(); var old = await Original(s, 1); await Original(s, 2);
        var batchId = old.Admission.Source!.BatchId!.Value;
        await s.Context.Set<VetPhotoBatch>().Where(b => b.Id == batchId).ExecuteUpdateAsync(u => u.SetProperty(b => b.State, "completed"));
        await s.Context.Set<VetProfile>().ExecuteUpdateAsync(u => u.SetProperty(p => p.Revision, p => p.Revision + 1));
        var r = await Request(s, mode: VetPhotoRunSelectionMode.Current);
        var stage = await Store(s).StageRunAsync(r with { BatchId = batchId }, CancellationToken.None);
        stage.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); stage.Run!.SelectedCount.ShouldBe(1);
        stage.Review!.BatchId.ShouldBeNull(); stage.Review.ProfileRevision.ShouldBe(r.ExpectedProfileRevision);
        string.Join("\n", JsonSerializer.Deserialize<string[]>(stage.Review.PreviewPagesJson, Json)!).ShouldContain(batchId.ToString("D"));
        await Deliver(s, stage.Review);
        (await Store(s).ApproveRunAsync(Handle(stage.Run), Proof(stage.Review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await s.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(b => b.Id == batchId)).State.ShouldBe("completed");
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    private async Task<VetPhotoImageClaim> OldDispatched(VetTestSession s, bool uncertain)
    {
        var old = await Approved(s); var window = await Store(s).ContinueRunAsync(Handle(old.Run!), CancellationToken.None);
        var snapshot = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(window.Window!.SelectionJson, Json)!.Single();
        var claim = (await Store(s).ClaimScheduledImageAsync(Scope, snapshot.AttemptKey, 111, CancellationToken.None)).Claim!;
        (await Store(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, CancellationToken.None)).ShouldBeTrue();
        if (uncertain) (await Store(s).RecordImageFailureAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111,
            "outcome_unknown", VetPhotoImageFailureDisposition.OutcomeUnknown, CancellationToken.None)).ShouldBeTrue();
        return claim;
    }
    private static string Unreadable(VetPhotoImageClaim claim) => JsonSerializer.Serialize(new
    {
        schema_version = 1, photo_source_id = claim.SourceId.ToString("D"), input_revision_id = claim.InputRevisionId.ToString("D"),
        kind = "unreadable", displays = Array.Empty<object>(), reasons = new[] { "unreadable" }, notes = (string?)null
    }, Json);

    [Fact]
    public async Task Explicit_complete_unknown_warning_allows_new_key_and_preserves_old_unknown_charge()
    {
        await SeedAsync(); await using var s = Open(); var item = await Original(s, 1); var old = await OldDispatched(s, true);
        (await Store(s).ClaimCurrentImageAsync(Scope, item.Admission.Source!.Id, item.Admission.Input!.Id, 111, CancellationToken.None))
            .Status.ShouldBe(VetPhotoImageStatus.Unknown);
        var stage = await Store(s).StageRunAsync(await Request(s), CancellationToken.None);
        stage.SelectionCounts!.UnknownAttempts.ShouldBe(1); stage.SelectionCounts.ChargedUnknownAttempts.ShouldBe(1);
        var text = string.Join("\n", JsonSerializer.Deserialize<string[]>(stage.Review!.PreviewPagesJson, Json)!);
        text.ShouldContain("повторный расход квоты"); text.ShouldContain("1:1/1;");
        var snapshot = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(stage.Run!.SelectionJson, Json)!.Single();
        snapshot.AcknowledgedUnknownFingerprint!.Length.ShouldBe(64); snapshot.AttemptKey.ShouldNotBe(old.AttemptKey);
        JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(stage.Review.SelectionJson, Json)!.Single().ShouldBe(snapshot);
        JsonSerializer.Serialize(snapshot, Json).Length.ShouldBeLessThanOrEqualTo(VetPhotoReviewBounds.MaxRunSelectionEntryChars);
        await Deliver(s, stage.Review);
        (await Store(s).ApproveRunAsync(Handle(stage.Run), Proof(stage.Review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var continued = await Store(s).ContinueRunAsync(Handle(stage.Run), CancellationToken.None); continued.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var retried = await Store(s).ClaimScheduledImageAsync(Scope, snapshot.AttemptKey, 111, CancellationToken.None);
        retried.Status.ShouldBe(VetPhotoImageStatus.Claimed);
        (await Store(s).MarkImageDispatchedAsync(Scope, snapshot.AttemptKey, retried.Claim!.ClaimToken, 111, CancellationToken.None)).ShouldBeTrue();
        var prior = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == old.AttemptKey);
        prior.State.ShouldBe("unknown"); prior.ReservedResultSlot.ShouldBeTrue(); prior.ClaimToken.ShouldBe(old.ClaimToken);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.ReservedResultSlot)).ShouldBe(2);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("new_unknown")] [InlineData("late_result")]
    public async Task Unknown_acknowledgement_changes_after_preview_refuse_approval(string change)
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1); var old = await OldDispatched(s, true);
        var stage = await Store(s).StageRunAsync(await Request(s), CancellationToken.None); await Deliver(s, stage.Review!);
        if (change == "new_unknown") await OldDispatched(s, true);
        else (await Store(s).CompleteImageAsync(new(Scope, old.AttemptKey, old.ClaimToken, 111, old.SourceId,
            old.InputRevisionId, "synthetic-image-model", Unreadable(old)), CancellationToken.None)).Status.ShouldBe(VetPhotoImageStatus.ProposedDelta);
        (await Store(s).ApproveRunAsync(Handle(stage.Run!), Proof(stage.Review!), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await s.Context.Set<VetPhotoRunWindow>().CountAsync(w => w.RunId == stage.Run!.Id)).ShouldBe(0);
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(r => r.Id == stage.Run!.Id)).SelectionJson.ShouldBe(stage.Run!.SelectionJson);
    }

    [Fact]
    public async Task Expired_other_dispatch_becomes_durable_unknown_before_new_review_and_never_releases_its_slot()
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1); var old = await OldDispatched(s, false);
        Clock.UtcNow = old.LeaseUntil.AddSeconds(1);
        var stage = await Store(s).StageRunAsync(await Request(s), CancellationToken.None);
        stage.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); stage.SelectionCounts!.UnknownAttempts.ShouldBe(1);
        var row = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == old.AttemptKey);
        row.State.ShouldBe("unknown"); row.ReservedResultSlot.ShouldBeTrue(); row.ClaimToken.ShouldBe(old.ClaimToken);
        JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(stage.Run!.SelectionJson, Json)!.Single().AcknowledgedUnknownFingerprint.ShouldNotBeNull();
        await Deliver(s, stage.Review!);
        (await Store(s).ApproveRunAsync(Handle(stage.Run!), Proof(stage.Review!), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
    }

    [Fact]
    public async Task Dispatch_expiry_after_preview_requires_fresh_warning_instead_of_adopting_unknown_state()
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1); var old = await OldDispatched(s, false);
        var stage = await Store(s).StageRunAsync(await Request(s), CancellationToken.None); await Deliver(s, stage.Review!);
        stage.SelectionCounts!.LiveCalls.ShouldBe(1);
        (await Store(s).ApproveRunAsync(Handle(stage.Run!), Proof(stage.Review!), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        Clock.UtcNow = old.LeaseUntil.AddSeconds(1);
        (await Store(s).ApproveRunAsync(Handle(stage.Run!), Proof(stage.Review!), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == old.AttemptKey)).State.ShouldBe("unknown");
        (await s.Context.Set<VetPhotoRunWindow>().CountAsync(w => w.RunId == stage.Run!.Id)).ShouldBe(0);
    }

    [Theory]
    [InlineData("continue")] [InlineData("claim")] [InlineData("dispatch")]
    public async Task Late_definitive_evidence_stales_frozen_acknowledgement_at_each_remaining_boundary(string boundary)
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1); var old = await OldDispatched(s, true);
        var run = await Approved(s); VetPhotoRunChange? continued = null; VetPhotoImageClaim? claim = null;
        var snapshot = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(run.Run!.SelectionJson, Json)!.Single();
        if (boundary != "continue") continued = await Store(s).ContinueRunAsync(Handle(run.Run), CancellationToken.None);
        if (boundary == "dispatch") claim = (await Store(s).ClaimScheduledImageAsync(Scope, snapshot.AttemptKey, 111, CancellationToken.None)).Claim!;
        (await Store(s).CompleteImageAsync(new(Scope, old.AttemptKey, old.ClaimToken, 111, old.SourceId,
            old.InputRevisionId, "synthetic-image-model", Unreadable(old)), CancellationToken.None)).Status.ShouldBe(VetPhotoImageStatus.ProposedDelta);
        if (boundary == "continue") (await Store(s).ContinueRunAsync(Handle(run.Run), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        if (boundary == "claim") (await Store(s).ClaimScheduledImageAsync(Scope, snapshot.AttemptKey, 111, CancellationToken.None)).Status.ShouldBe(VetPhotoImageStatus.Stale);
        if (boundary == "dispatch") (await Store(s).MarkImageDispatchedAsync(Scope, snapshot.AttemptKey, claim!.ClaimToken, 111, CancellationToken.None)).ShouldBeFalse();
        var oldRow = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == old.AttemptKey);
        oldRow.State.ShouldBe("returned"); oldRow.ReservedResultSlot.ShouldBeFalse();
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Id == snapshot.AttemptKey && a.State == "dispatched")).ShouldBe(0);
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(r => r.Id == run.Run.Id)).SelectionJson.ShouldBe(run.Run.SelectionJson);
    }

    [Fact]
    public async Task New_unknown_after_continue_refuses_claim_without_freeing_prior_accounting()
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1); var run = await Approved(s);
        var continued = await Store(s).ContinueRunAsync(Handle(run.Run!), CancellationToken.None);
        var snapshot = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(continued.Window!.SelectionJson, Json)!.Single();
        var old = await OldDispatched(s, true);
        (await Store(s).ClaimScheduledImageAsync(Scope, snapshot.AttemptKey, 111, CancellationToken.None)).Status.ShouldBe(VetPhotoImageStatus.Stale);
        var prior = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == old.AttemptKey);
        prior.State.ShouldBe("unknown"); prior.ReservedResultSlot.ShouldBeTrue();
        (await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == snapshot.AttemptKey)).ReservedResultSlot.ShouldBeFalse();
    }

    [Fact]
    public async Task Two_approved_same_input_runs_concurrently_claim_only_one_live_attempt_and_dispatch_once()
    {
        await SeedAsync(); await using var seed = Open(); await Original(seed, 1);
        var first = await Approved(seed); var second = await Approved(seed);
        await Store(seed).ContinueRunAsync(Handle(first.Run!), CancellationToken.None);
        await Store(seed).ContinueRunAsync(Handle(second.Run!), CancellationToken.None);
        var key1 = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(first.Run!.SelectionJson, Json)!.Single().AttemptKey;
        var key2 = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(second.Run!.SelectionJson, Json)!.Single().AttemptKey;
        await using var a = Open(); await using var b = Open();
        var results = await Task.WhenAll(Store(a).ClaimScheduledImageAsync(Scope, key1, 111, CancellationToken.None),
            Store(b).ClaimScheduledImageAsync(Scope, key2, 111, CancellationToken.None));
        results.Select(x => x.Status).Order().ShouldBe(new[] { VetPhotoImageStatus.Claimed, VetPhotoImageStatus.Busy }.Order());
        var won = results.Single(x => x.Status == VetPhotoImageStatus.Claimed).Claim!;
        (await Store(seed).MarkImageDispatchedAsync(Scope, won.AttemptKey, won.ClaimToken, 111, CancellationToken.None)).ShouldBeTrue();
        (await seed.Context.Set<VetPhotoAttempt>().CountAsync(x => x.State == "dispatched")).ShouldBe(1);
        (await seed.Context.Set<VetPhotoAttempt>().CountAsync(x => x.ReservedResultSlot)).ShouldBe(1);
    }

    [Fact]
    public async Task Sorted_unknown_digest_is_stable_across_previews_and_counts_each_old_charge_for_its_source()
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1);
        var one = await OldDispatched(s, true); var two = await OldDispatched(s, true);
        var first = await Store(s).StageRunAsync(await Request(s), CancellationToken.None);
        var second = await Store(s).StageRunAsync(await Request(s), CancellationToken.None);
        var a = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(first.Run!.SelectionJson, Json)!.Single();
        var b = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(second.Run!.SelectionJson, Json)!.Single();
        a.AttemptKey.ShouldNotBe(b.AttemptKey); a.AcknowledgedUnknownFingerprint.ShouldBe(b.AcknowledgedUnknownFingerprint);
        a.AcknowledgedUnknownFingerprint.ShouldNotBeNull(); first.SelectionCounts!.UnknownAttempts.ShouldBe(2);
        first.SelectionCounts.ChargedUnknownAttempts.ShouldBe(2);
        string.Join("\n", JsonSerializer.Deserialize<string[]>(first.Review!.PreviewPagesJson, Json)!).ShouldContain("1:2/2;");
        var old = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().Where(x => x.Id == one.AttemptKey || x.Id == two.AttemptKey).ToListAsync();
        old.Count.ShouldBe(2); old.All(x => x.State == "unknown" && x.ReservedResultSlot).ShouldBeTrue();
    }

    [Theory]
    [InlineData(768)] [InlineData(769)]
    public async Task Populated_unknown_snapshot_entry_accepts_exact_768_and_refuses_769_without_truncation(int length)
    {
        await SeedAsync(); await using var s = Open(); await Original(s, 1); await OldDispatched(s, true);
        var stage = await Store(s).StageRunAsync(await Request(s), CancellationToken.None);
        var snapshot = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(stage.Run!.SelectionJson, Json)!.Single();
        snapshot.AcknowledgedUnknownFingerprint!.Length.ShouldBe(64);
        var entry = JsonSerializer.Serialize(snapshot, Json);
        entry.Length.ShouldBeLessThan(768);
        var padded = entry[..^1] + new string(' ', length - entry.Length) + "}";
        padded.Length.ShouldBe(length);
        var selection = "[" + padded + "]";
        JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(selection, Json)!.Single().ShouldBe(snapshot);
        await s.Context.Set<VetPhotoRun>().Where(r => r.Id == stage.Run.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.SelectionJson, selection));
        await s.Context.Set<VetPhotoReview>().Where(r => r.Id == stage.Review!.Id).ExecuteUpdateAsync(u =>
            u.SetProperty(r => r.SelectionJson, selection).SetProperty(r => r.Fingerprint, Hash(selection)));
        await Deliver(s, stage.Review!);
        var result = await Store(s).ApproveRunAsync(Handle(stage.Run), Proof(stage.Review!), CancellationToken.None);
        result.Status.ShouldBe(length == 768 ? VetPhotoWorkflowStatus.Applied : VetPhotoWorkflowStatus.Refused);
        (await s.Context.Set<VetPhotoRunWindow>().CountAsync(w => w.RunId == stage.Run.Id)).ShouldBe(length == 768 ? 1 : 0);
        (await s.Context.Set<VetPhotoRun>().AsNoTracking().SingleAsync(r => r.Id == stage.Run.Id)).SelectionJson.ShouldBe(selection);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.State == "unknown" && a.ReservedResultSlot)).ShouldBe(1);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    private sealed class RejectAcceptedReview : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE vet_photo_reviews", StringComparison.OrdinalIgnoreCase)
                && command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, "accepted")))
            { Fired = true; throw new InvalidOperationException("Synthetic accepted-review database failure."); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelBeforeArchive(Func<Task> cancel) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("vet-photo-capacity-v1", StringComparison.Ordinal))
            { Fired = true; await cancel(); }
            return result;
        }
    }
}
