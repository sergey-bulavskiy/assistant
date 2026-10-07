using System.Text.Json;
using Assistant.Application.Llm;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.IntegrationTests.Vet.Photos;

public sealed class VetPhotoCancellationStoreTests : VetPhotoPresentationFixture
{
    private sealed class ForbiddenGateway : ILlmGateway
    {
        public int Calls;
        public bool IsEnabled => false;
        public IReadOnlyList<ModelStatus> DescribeModels() => [];
        public bool IsKnownModel(string name) => false;
        public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("Unexpected image call in archive work."); }
    }
    private sealed class NoPrompts : IRolePrompts
    {
        public IReadOnlyList<string> Missing => [];
        public string? Find(string role, string file) => null;
    }
    private VetPhotoProcessor Processor(VetTestSession s, ForbiddenGateway gateway) =>
        new(Photos(s), Photos(s), Photos(s), new VetPhotoImageDecoder(), gateway, new NoPrompts(),
            new(false), NullLogger<VetPhotoProcessor>.Instance);
    private async Task Retain(VetTestSession s, Item item, int color = 1)
    {
        var claim = (await Photos(s).ReserveDownloadAsync(Scope, item.Source.Id, item.Input.Id, 111, Ct)).Claim.ShouldNotBeNull();
        var bytes = Png(color);
        (await Photos(s).CommitOriginalAsync(new(Scope, 111, claim.Attempt.Id, claim.ClaimToken,
            item.Input.Id, bytes, new VetPhotoImageDecoder().Decode(bytes, Ct).Image.ShouldNotBeNull()), Ct)).Status.ShouldBe(VetPhotoArchiveStatus.Retained);
    }
    private async Task Protect(VetTestSession s, Item item, string state)
    {
        await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == item.Candidate.Id).ExecuteUpdateAsync(u => u
            .SetProperty(c => c.State, state == "restoration" ? "pending" : state)
            .SetProperty(c => c.RequiresExplicitRestoration, state == "restoration")
            .SetProperty(c => c.Revision, c => c.Revision + 1), Ct);
    }
    private async Task<Item> Edit(VetTestSession s, Item item)
    {
        var message = Text("synthetic changed caption", item.Source.TelegramMessageId) with
            { Kind = MessageKind.Photo, IsEdit = true, EditedAt = Now.AddSeconds(1) };
        var update = await s.Context.Bots.AsNoTracking().Select(b => b.LastUpdateId).SingleAsync(Ct) + 1;
        var admitted = await Photos(s).AdmitAsync(Scope, message, update,
            new($"synthetic-file-{item.Source.TelegramMessageId}", $"synthetic-unique-{item.Source.TelegramMessageId}",
                "synthetic.png", "image/png", Png(1).Length, 32, 24), null, Ct);
        admitted.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        admitted.Input!.ReusesImageInputId.ShouldBe(item.Input.Id);
        var stored = await s.Messages.StoreAsync(Bot.TelegramBotId, update, message, Ct);
        (await Photos(s).BindMessageAsync(Scope, admitted.Source!.Id, stored.MessageDbId!.Value, Ct)).ShouldBeTrue();
        return new(admitted.Source!, admitted.Input!, await s.Context.Set<VetPhotoCandidate>().AsNoTracking()
            .SingleAsync(c => c.SourceId == admitted.Source.Id, Ct), null, null);
    }
    private static async Task<string> Attempts(VetTestSession s) => JsonSerializer.Serialize(
        await s.Context.Set<VetPhotoAttempt>().AsNoTracking().OrderBy(a => a.Id).ToArrayAsync(Ct), Json);

    [Theory]
    [InlineData("cancelled", false)] [InlineData("excluded", false)] [InlineData("deleted", false)] [InlineData("restoration", false)]
    [InlineData("cancelled", true)] [InlineData("excluded", true)] [InlineData("deleted", true)] [InlineData("restoration", true)]
    public async Task Protected_automatic_claim_and_already_claimed_mark_refuse_without_result_or_charge_changes(string state, bool mark)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, image: false); await Retain(s, item);
        VetPhotoImageClaim? claim = null;
        if (mark) claim = (await Photos(s).ClaimCurrentImageAsync(Scope, item.Source.Id, item.Input.Id, 111, Ct)).Claim.ShouldNotBeNull();
        await Protect(s, item, state); var before = await Attempts(s); var snapshot = await Snapshot(s);
        var totals = await Photos(s).GetCapacityAsync(Scope, 111, Ct);
        if (mark) (await Photos(s).MarkImageDispatchedAsync(Scope, claim!.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeFalse();
        else (await Photos(s).ClaimCurrentImageAsync(Scope, item.Source.Id, item.Input.Id, 111, Ct)).Status.ShouldBe(VetPhotoImageStatus.Stale);
        await using var v = Open(); (await Attempts(v)).ShouldBe(before); (await Snapshot(v)).ShouldBe(snapshot);
        (await Photos(v).GetCapacityAsync(Scope, 111, Ct)).ShouldBe(totals);
        (await Photos(v).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        (await v.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(0);
        s.Chat.RequestedMessages.ShouldBeEmpty(); s.Telegram.DownloadedFiles.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("role")] [InlineData("member")] [InlineData("place")]
    public async Task Archive_queue_and_automatic_claim_mark_recheck_fresh_role_member_and_place(string fence)
    {
        await SeedAsync(); await using var s = Open(); var archived = await Prepare(s, image: false); await Retain(s, archived);
        var pending = await Prepare(s, 2, image: false);
        var claim = (await Photos(s).ClaimCurrentImageAsync(Scope, archived.Source.Id, archived.Input.Id, 111, Ct)).Claim.ShouldNotBeNull();
        if (fence == "role") await s.Context.Bots.ExecuteUpdateAsync(u => u.SetProperty(b => b.Role, "general"), Ct);
        if (fence == "member") await s.Context.FamilyMembers.Where(m => m.TelegramUserId == 111)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        if (fence == "place") await s.Context.Places.Where(p => p.TopicId == 7)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlaceStatus.Denied), Ct);
        var attempts = await Attempts(s);
        if (fence == "role")
        {
            await Should.ThrowAsync<InvalidOperationException>(() => Photos(s).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct));
            await Should.ThrowAsync<InvalidOperationException>(() => Photos(s).ClaimCurrentImageAsync(Scope, archived.Source.Id, archived.Input.Id, 111, Ct));
            await Should.ThrowAsync<InvalidOperationException>(() => Photos(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct));
        }
        else
        {
            (await Photos(s).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
            (await Photos(s).ClaimCurrentImageAsync(Scope, archived.Source.Id, archived.Input.Id, 111, Ct)).Status.ShouldBe(VetPhotoImageStatus.Refused);
            (await Photos(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeFalse();
        }
        (await Attempts(s)).ShouldBe(attempts);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(Ct)).ShouldBe(1);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(0);
        pending.Source.SourceAuthorUserId.ShouldBe(111);
    }

    [Theory]
    [InlineData("cancelled")] [InlineData("excluded")] [InlineData("deleted")] [InlineData("restoration")]
    [InlineData("human")]
    public async Task Admitted_protected_or_human_result_original_is_honestly_waiting_then_archive_only_retained_with_zero_image_work(string state)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, image: false);
        if (state == "cancelled")
        {
            var batch = await Batch(s, item.Source.BatchId!.Value);
            (await Photos(s).CancelRemainderAsync(Scope, batch.Batch.Id, batch.Batch.ReviewRevision, 222, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        }
        else if (state == "human")
            (await Photos(s).ProposeHumanCorrectionAsync(await Human(s, item), Ct)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        else await Protect(s, item, state);
        var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct);
        var batchBefore = await Batch(s, item.Source.BatchId!.Value); batchBefore.Counts.Retained.ShouldBe(0);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(Ct)).ShouldBe(0);
        var queue = await Photos(s).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct);
        queue.Single().ShouldBe(new(Scope, item.Source.Id, item.Input.Id, 111) { ArchiveOnly = true });
        var resultCount = await s.Context.Set<VetPhotoExtraction>().CountAsync(Ct);
        var bytes = Png(1); s.Telegram.Files[item.Input.FileId] = bytes; var gateway = new ForbiddenGateway();
        var result = await Processor(s, gateway).ProcessAsync(queue.Single(), s.Telegram, Ct);
        result.ShouldBe(new(VetPhotoProcessStatus.Deferred, "archive_retained")); gateway.Calls.ShouldBe(0);
        s.Telegram.DownloadedFiles.ToArray().ShouldBe(new[] { item.Input.FileId });
        await using var v = Open(); var reference = await v.Context.Set<VetPhotoOriginalReference>().SingleAsync(Ct);
        reference.State.ShouldBe("retained"); reference.ActualBytes.ShouldBe(bytes.Length);
        (await v.Context.Set<VetPhotoBlob>().SingleAsync(Ct)).Content.ShouldBe(bytes);
        JsonSerializer.Serialize(await v.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct), Json)
            .ShouldBe(JsonSerializer.Serialize(candidate, Json));
        (await v.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(resultCount);
        (await v.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image", Ct)).ShouldBe(0);
        (await Batch(v, item.Source.BatchId.Value)).Counts.Retained.ShouldBe(1);
        (await Photos(v).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(0); (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
    }

    [Theory]
    [InlineData("admitted")] [InlineData("dispatching")] [InlineData("ready")]
    public async Task Archive_queue_is_independent_of_unfinished_TEXT_while_automatic_vision_remains_held(string textState)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, image: false, captionState: textState);
        var textBefore = JsonSerializer.Serialize(await s.Context.Set<VetTextSourceRevision>().AsNoTracking().SingleAsync(Ct), Json);
        var due = await Photos(s).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct); due.Single().ArchiveOnly.ShouldBeTrue();
        (await Photos(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        s.Telegram.Files[item.Input.FileId] = Png(1); var gateway = new ForbiddenGateway();
        (await Processor(s, gateway).ProcessAsync(due.Single(), s.Telegram, Ct)).Category.ShouldBe("archive_retained");
        (await Photos(s).GetDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty(); gateway.Calls.ShouldBe(0);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image", Ct)).ShouldBe(0);
        JsonSerializer.Serialize(await s.Context.Set<VetTextSourceRevision>().AsNoTracking().SingleAsync(Ct), Json).ShouldBe(textBefore);
        (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Superseded_unretained_original_is_archived_after_cancellation_without_moving_current_source_or_candidate_pointer()
    {
        await SeedAsync(); await using var s = Open(); var old = await Prepare(s, image: false); var current = await Edit(s, old);
        var batch = await Batch(s, old.Source.BatchId!.Value);
        (await Photos(s).CancelRemainderAsync(Scope, batch.Batch.Id, batch.Batch.ReviewRevision, 222, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var candidateBefore = JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct), Json);
        var due = await Photos(s).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct);
        due.Select(w => w.InputRevisionId).Order().ShouldBe(new[] { old.Input.Id, current.Input.Id }.Order());
        s.Telegram.Files[old.Input.FileId] = Png(1); var gateway = new ForbiddenGateway();
        (await Processor(s, gateway).ProcessAsync(due.Single(w => w.InputRevisionId == old.Input.Id), s.Telegram, Ct)).Category.ShouldBe("archive_retained");
        var source = await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(Ct);
        source.CurrentInputRevisionId.ShouldBe(current.Input.Id); source.CurrentOrdinal.ShouldBe(2);
        (await s.Context.Set<VetPhotoOriginalReference>().SingleAsync(Ct)).InputRevisionId.ShouldBe(old.Input.Id);
        (await Batch(s, batch.Batch.Id)).Counts.Retained.ShouldBe(0);
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct), Json).ShouldBe(candidateBefore);
        var next = await Photos(s).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct); next.Single().InputRevisionId.ShouldBe(current.Input.Id);
        (await Processor(s, gateway).ProcessAsync(next.Single(), s.Telegram, Ct)).Category.ShouldBe("archive_retained");
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(Ct)).ShouldBe(2); gateway.Calls.ShouldBe(0);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image", Ct)).ShouldBe(0);
        (await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(Ct)).CurrentInputRevisionId.ShouldBe(current.Input.Id);
    }

    [Fact]
    public async Task Cancel_remainder_preserves_download_queue_live_reservation_and_unknown_image_charge_and_cancels_only_queued_vision()
    {
        await SeedAsync(); await using var s = Open();
        (await Photos(s).StartCollectionAsync(Scope, 111, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var queued = await Prepare(s, 1, image: false); var live = await Prepare(s, 2, image: false);
        var unknown = await Prepare(s, 3, image: false); await Retain(s, unknown, 3);
        var liveClaim = (await Photos(s).ReserveDownloadAsync(Scope, live.Source.Id, live.Input.Id, 111, Ct)).Claim.ShouldNotBeNull();
        var image = (await Photos(s).ClaimCurrentImageAsync(Scope, unknown.Source.Id, unknown.Input.Id, 111, Ct)).Claim.ShouldNotBeNull();
        (await Photos(s).MarkImageDispatchedAsync(Scope, image.AttemptKey, image.ClaimToken, 111, Ct)).ShouldBeTrue();
        (await Photos(s).RecordImageFailureAsync(Scope, image.AttemptKey, image.ClaimToken, 111,
            "outcome_unknown", VetPhotoImageFailureDisposition.OutcomeUnknown, Ct)).ShouldBeTrue();
        var queuedImage = new VetPhotoAttempt { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = Scope.ChatId, TopicId = Scope.TopicId, SourceId = queued.Source.Id,
            InputRevisionId = queued.Input.Id, ExpectedCurrentInputId = queued.Input.Id, ExpectedSourceOrdinal = 1,
            Kind = "image", State = "queued", ActorUserId = 111, CreatedAt = Now, UpdatedAt = Now };
        s.Context.Add(queuedImage); await s.Context.SaveChangesAsync(Ct); s.Context.ChangeTracker.Clear();
        var liveBefore = JsonSerializer.Serialize(await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == liveClaim.Attempt.Id, Ct), Json);
        var unknownBefore = JsonSerializer.Serialize(await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == image.AttemptKey, Ct), Json);
        var capacity = await Photos(s).GetCapacityAsync(Scope, 111, Ct);
        capacity.ReservedInputs.ShouldBe(1); capacity.ReservedResults.ShouldBe(1); capacity.ReservedBytes.ShouldBe(liveClaim.Attempt.ReservedBytes);
        var batch = await Batch(s, queued.Source.BatchId!.Value);
        (await Photos(s).CancelRemainderAsync(Scope, batch.Batch.Id, batch.Batch.ReviewRevision, 222, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        await using var v = Open();
        (await v.Context.Set<VetPhotoAttempt>().SingleAsync(a => a.Id == queuedImage.Id, Ct)).State.ShouldBe("cancelled");
        (await v.Context.Set<VetPhotoAttempt>().SingleAsync(a => a.InputRevisionId == queued.Input.Id && a.Kind == "download", Ct)).State.ShouldBe("queued");
        JsonSerializer.Serialize(await v.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == liveClaim.Attempt.Id, Ct), Json).ShouldBe(liveBefore);
        JsonSerializer.Serialize(await v.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == image.AttemptKey, Ct), Json).ShouldBe(unknownBefore);
        (await Photos(v).GetCapacityAsync(Scope, 111, Ct)).ShouldBe(capacity);
        (await Photos(v).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).Single().InputRevisionId.ShouldBe(queued.Input.Id);
        (await v.Context.Set<VetPhotoOriginalReference>().CountAsync(Ct)).ShouldBe(1);
        (await v.Context.Set<VetPhotoCandidate>().CountAsync(c => c.State == "cancelled", Ct)).ShouldBe(3);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Known_expired_current_or_superseded_download_releases_only_abandoned_reservation_then_second_expiry_is_terminal(bool superseded)
    {
        await SeedAsync(); await using var s = Open(); var old = await Prepare(s, image: false);
        var first = (await Photos(s).ReserveDownloadAsync(Scope, old.Source.Id, old.Input.Id, 111, Ct)).Claim.ShouldNotBeNull();
        first.Attempt.DownloadAttemptCount.ShouldBe(1);
        Item? edited = superseded ? await Edit(s, old) : null;
        Clock.UtcNow = Now.AddMinutes(2);
        var due = await Photos(s).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct);
        due.ShouldContain(w => w.InputRevisionId == old.Input.Id && w.ArchiveOnly);
        var repaired = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == first.Attempt.Id, Ct);
        repaired.State.ShouldBe("retry_wait"); repaired.FailureCategory.ShouldBe("download_timeout");
        repaired.ReservedBytes.ShouldBe(0); repaired.ReservedInputSlot.ShouldBeFalse(); repaired.ClaimToken.ShouldBeNull(); repaired.LeaseUntil.ShouldBeNull();
        repaired.DownloadAttemptCount.ShouldBe(1); repaired.RetryNotBefore.ShouldBe(Clock.UtcNow);
        (await Photos(s).GetCapacityAsync(Scope, 111, Ct)).ReservedInputs.ShouldBe(0);
        var second = (await Photos(s).ReserveDownloadAsync(Scope, old.Source.Id, old.Input.Id, 111, Ct)).Claim.ShouldNotBeNull();
        second.Attempt.Id.ShouldBe(first.Attempt.Id); second.Attempt.DownloadAttemptCount.ShouldBe(2);
        Clock.UtcNow = Now.AddMinutes(4);
        var next = await Photos(s).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct); next.ShouldNotContain(w => w.InputRevisionId == old.Input.Id);
        var terminal = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Id == first.Attempt.Id, Ct);
        terminal.State.ShouldBe("failed"); terminal.FailureCategory.ShouldBe("download_timeout"); terminal.DownloadAttemptCount.ShouldBe(2);
        terminal.ReservedBytes.ShouldBe(0); terminal.ReservedInputSlot.ShouldBeFalse(); terminal.RetryNotBefore.ShouldBeNull();
        (await Photos(s).GetCapacityAsync(Scope, 111, Ct)).ReservedBytes.ShouldBe(0);
        (await Photos(s).ReserveDownloadAsync(Scope, old.Source.Id, old.Input.Id, 111, Ct)).Status.ShouldBe(VetPhotoArchiveStatus.Refused);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(Ct)).ShouldBe(0);
        (await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(Ct)).CurrentInputRevisionId.ShouldBe(edited?.Input.Id ?? old.Input.Id);
    }

    [Fact]
    public async Task Deleted_original_and_caption_reuse_are_terminal_without_cached_Telegram_reacquisition_or_eviction_of_other_bytes()
    {
        await SeedAsync(); await using var s = Open(); var old = await Prepare(s, image: false); await Retain(s, old);
        var unrelated = await Prepare(s, 2, image: false); await Retain(s, unrelated, 2);
        var untouched = JsonSerializer.Serialize(await s.Context.Set<VetPhotoBlob>().AsNoTracking().OrderBy(b => b.Id).ToArrayAsync(Ct), Json);
        var reference = await s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().SingleAsync(r => r.InputRevisionId == old.Input.Id, Ct);
        await s.Context.Set<VetPhotoOriginalReference>().Where(r => r.Id == reference.Id).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.State, "deleted").SetProperty(r => r.Revision, r => r.Revision + 1).SetProperty(r => r.DeletedAt, Now), Ct);
        var current = await Edit(s, old);
        (await Photos(s).ReserveDownloadAsync(Scope, old.Source.Id, old.Input.Id, 111, Ct)).Status.ShouldBe(VetPhotoArchiveStatus.OriginalDeleted);
        (await Photos(s).ReserveDownloadAsync(Scope, current.Source.Id, current.Input.Id, 111, Ct)).Status.ShouldBe(VetPhotoArchiveStatus.OriginalDeleted);
        (await Photos(s).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        var attempt = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.InputRevisionId == current.Input.Id, Ct);
        attempt.State.ShouldBe("failed"); attempt.FailureCategory.ShouldBe("original_deleted"); attempt.DownloadAttemptCount.ShouldBe(0);
        var gateway = new ForbiddenGateway(); var result = await Processor(s, gateway)
            .ProcessAsync(new(Scope, current.Source.Id, current.Input.Id, 111) { ArchiveOnly = true }, s.Telegram, Ct);
        result.Category.ShouldBe("reupload_required"); gateway.Calls.ShouldBe(0); s.Telegram.DownloadedFiles.ShouldBeEmpty();
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoBlob>().AsNoTracking().OrderBy(b => b.Id).ToArrayAsync(Ct), Json).ShouldBe(untouched);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(Ct)).ShouldBe(2);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(0);
    }

    [Theory]
    [InlineData("cancelled")] [InlineData("excluded")] [InlineData("deleted")] [InlineData("restoration")]
    public async Task Explicit_complete_scheduled_reprocess_of_protected_candidate_still_claims_and_marks_without_fact_or_pointer_advancement(string state)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, image: false); await Retain(s, item);
        await Protect(s, item, state);
        if (state == "cancelled") await s.Context.Set<VetPhotoBatch>().ExecuteUpdateAsync(u => u.SetProperty(b => b.State, "cancelled"), Ct);
        var profile = await s.Context.Set<VetProfile>().AsNoTracking().SingleAsync(Ct);
        var staged = await Photos(s).StageRunAsync(new(Scope, Guid.NewGuid(), 111, profile.Revision,
            VetPhotoRunPurpose.Reprocess, VetPhotoRunSelectionMode.AllOriginals, "gpt-6.1-sol", "codex-cli"), Ct);
        staged.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); var review = staged.Review.ShouldNotBeNull();
        var handle = new VetPhotoReviewHandle(Scope, review.Id, review.Revision, review.OperationKey, 111);
        var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!;
        for (var i = 0; i < pages.Length; i++)
            (await Photos(s).RecordPageDeliveryAsync(handle, i, 1000 + i, Hash(pages[i]), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Photos(s).CompleteDeliveryAsync(handle, 999 + pages.Length, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var run = new VetPhotoRunHandle(Scope, staged.Run!.Id, 111);
        (await Photos(s).ApproveRunAsync(run, handle, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Photos(s).ContinueRunAsync(run, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var scheduled = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Kind == "image", Ct);
        var before = await Snapshot(s);
        var claim = (await Photos(s).ClaimScheduledImageAsync(Scope, scheduled.Id, 111, Ct)).Claim.ShouldNotBeNull();
        (await Photos(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
        (await Snapshot(s)).ShouldBe(before);
        (await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct)).RequiresExplicitRestoration.ShouldBe(state == "restoration");
        (await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(Ct)).CurrentInputRevisionId.ShouldBe(item.Input.Id);
        (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0); (await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Archive_queue_is_bounded_to_five_and_restart_retains_all_six_cancelled_sources_once()
    {
        await SeedAsync(); await using var s = Open(); (await Photos(s).StartCollectionAsync(Scope, 111, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var items = new List<Item>(); for (var i = 1; i <= 6; i++) { Clock.UtcNow = Now.AddSeconds(i); items.Add(await Prepare(s, i, image: false)); }
        var batch = await Batch(s, items[0].Source.BatchId!.Value);
        (await Photos(s).CancelRemainderAsync(Scope, batch.Batch.Id, batch.Batch.ReviewRevision, 222, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var first = await Photos(s).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct);
        first.Count.ShouldBe(5); first.Select(w => w.SourceId).ShouldBe(items.Take(5).Select(i => i.Source.Id));
        var gateway = new ForbiddenGateway();
        foreach (var work in first) { s.Telegram.Files[items.Single(i => i.Input.Id == work.InputRevisionId).Input.FileId] = Png(1);
            (await Processor(s, gateway).ProcessAsync(work, s.Telegram, Ct)).Category.ShouldBe("archive_retained"); }
        await using var restart = Open(); var last = await Photos(restart).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct);
        last.Single().SourceId.ShouldBe(items[5].Source.Id); restart.Telegram.Files[items[5].Input.FileId] = Png(1);
        (await Processor(restart, gateway).ProcessAsync(last.Single(), restart.Telegram, Ct)).Category.ShouldBe("archive_retained");
        (await Photos(restart).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        (await restart.Context.Set<VetPhotoOriginalReference>().CountAsync(Ct)).ShouldBe(6);
        (await restart.Context.Set<VetPhotoBlob>().CountAsync(Ct)).ShouldBe(1);
        (await restart.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image", Ct)).ShouldBe(0); gateway.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(0)] [InlineData(6)]
    public async Task Invalid_archive_queue_limits_fail_before_partial_results_or_pointer_changes(int limit)
    {
        await SeedAsync(); await using var s = Open(); await Prepare(s, image: false); var before = await Snapshot(s); var attempts = await Attempts(s);
        await Should.ThrowAsync<InvalidOperationException>(() => Photos(s).GetArchiveDueAsync(FamilyId, Bot.BotDbId, limit, Ct));
        (await Snapshot(s)).ShouldBe(before); (await Attempts(s)).ShouldBe(attempts);
    }

    [Theory]
    [InlineData("bot")] [InlineData("topic")] [InlineData("author")] [InlineData("kind")]
    public async Task Archive_queue_rejects_a_mismatched_immutable_bound_transport_before_returning_private_work(string mismatch)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, image: false);
        var message = await s.Context.Messages.SingleAsync(Ct);
        if (mismatch == "bot") message.BotId = Bot.BotDbId;
        if (mismatch == "topic") message.TopicId = 8;
        if (mismatch == "author") message.UserId = 222;
        if (mismatch == "kind") message.Kind = MessageKind.Text;
        await s.Context.SaveChangesAsync(Ct); s.Context.ChangeTracker.Clear(); var before = await Snapshot(s); var attempts = await Attempts(s);
        (await Photos(s).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty();
        (await Snapshot(s)).ShouldBe(before); (await Attempts(s)).ShouldBe(attempts);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(Ct)).ShouldBe(0);
        item.Source.SourceMessageDbId.ShouldBe(message.Id);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Archive_queue_unset_or_foreign_family_scope_throws_without_releasing_or_revealing_another_family_work(bool foreign)
    {
        await SeedAsync(); await using var setup = Open(); await Prepare(setup, image: false); var before = await Attempts(setup);
        await using var wrong = foreign ? Open(familyId: FamilyId + 1000) : Unscoped();
        await Should.ThrowAsync<InvalidOperationException>(() => Photos(wrong).GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct));
        await using var verify = Open(); (await Attempts(verify)).ShouldBe(before);
        (await verify.Context.Set<VetPhotoOriginalReference>().CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task Full_global_input_capacity_keeps_cancelled_metadata_waiting_and_never_evicts_retained_original_or_downloads_again()
    {
        await SeedAsync(); await using var s = Open(); var retained = await Prepare(s, image: false); await Retain(s, retained);
        var waiting = await Prepare(s, 2, image: false); var batch = await Batch(s, waiting.Source.BatchId!.Value);
        (await Photos(s).CancelRemainderAsync(Scope, batch.Batch.Id, batch.Batch.ReviewRevision, 222, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var blobs = JsonSerializer.Serialize(await s.Context.Set<VetPhotoBlob>().AsNoTracking().ToArrayAsync(Ct), Json);
        var limited = Photos(s, new(MaxInputRevisions: 1)); var gateway = new ForbiddenGateway();
        var processor = new VetPhotoProcessor(limited, limited, limited, new VetPhotoImageDecoder(), gateway, new NoPrompts(),
            new(false), NullLogger<VetPhotoProcessor>.Instance);
        var due = await limited.GetArchiveDueAsync(FamilyId, Bot.BotDbId, 5, Ct); due.Single().SourceId.ShouldBe(waiting.Source.Id);
        var result = await processor.ProcessAsync(due.Single(), s.Telegram, Ct); result.Category.ShouldBe("archive_full");
        s.Telegram.DownloadedFiles.ShouldBeEmpty(); gateway.Calls.ShouldBe(0);
        (await Batch(s, batch.Batch.Id)).Counts.Retained.ShouldBe(0);
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoBlob>().AsNoTracking().ToArrayAsync(Ct), Json).ShouldBe(blobs);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(Ct)).ShouldBe(1);
        (await limited.GetCapacityAsync(Scope, 111, Ct)).ReservedInputs.ShouldBe(0);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image", Ct)).ShouldBe(0);
    }
}
