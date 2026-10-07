using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using SkiaSharp;

namespace Assistant.IntegrationTests.Vet.Photos;

public sealed class VetPhotoUndoTests : VetTestBase
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Measured = DateTimeOffset.Parse("2031-05-11T10:20:00Z");
    private static readonly VetPhotoEffectiveReading Reading = new(5.6m, "mmol/L", Measured,
        "2031-05-11 10:20:00", "+00:00", "image", "image", "image_or_caption", false);
    private VetPhotoStore Photos(VetTestSession s) => new(s.Context, s.Current, Clock, new(), new VetPhotoImageDecoder());
    private sealed record Evidence(VetDiaryScope Scope, VetPhotoSource Source, VetPhotoInputRevision Input,
        VetPhotoCandidate Candidate, VetPhotoOriginalReference? Original, VetPhotoExtraction? Result, VetProfile Profile);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static byte[] Png(int color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(32, 24, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor((byte)color, 32, 64)); using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    private static string ImageJson(Guid source, Guid input) => JsonSerializer.Serialize(new
    {
        schema_version = 1, photo_source_id = source.ToString("D"), input_revision_id = input.ToString("D"), kind = "meter",
        displays = new[] { new { value_text = "5.6", decimal_value = 5.6m, unit = "mmol/L", year = 2031,
            year_displayed = true, month = 5, day = 11, time = "10:20", offset = "+00:00" } },
        reasons = Array.Empty<string>(), notes = (string?)null
    }, Json);
    private async Task<Evidence> Prepare(VetTestSession s, int id = 1, bool image = true, int topic = 7,
        bool sameBytes = false, bool collection = false, bool privateChat = false, bool caption = false)
    {
        var scope = privateChat ? Scope with { ChatId = 111, TopicId = null } : Scope with { TopicId = topic };
        if (collection) (await Photos(s).StartCollectionAsync(scope, 111, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var bytes = Png(sameBytes ? 1 : id);
        var message = Text("synthetic image caption", id, 111, topic) with { Kind = MessageKind.Photo,
            ChatId = scope.ChatId, TopicId = scope.TopicId, ChatType = privateChat ? "private" : "supergroup" };
        var text = caption ? await s.Diary.AdmitAsync(scope, message, id, Ct) : null;
        var admitted = await Photos(s).AdmitAsync(scope, message, id,
            new($"synthetic-file-{id}", $"synthetic-unique-{id}", "synthetic.png", "image/png", bytes.Length, 32, 24), text?.Revision.Id, Ct);
        admitted.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        var transport = await s.Messages.StoreAsync(scope.TelegramBotId, id, message, Ct);
        (await Photos(s).BindMessageAsync(scope, admitted.Source!.Id, transport.MessageDbId.ShouldNotBeNull(), Ct)).ShouldBeTrue();
        if (text != null)
        {
            await s.Diary.LinkMessageAsync(scope, text.Source.Id, transport.MessageDbId!.Value, Ct);
            await s.Diary.SetProcessingAsync(scope, text.Revision.Id, "admitted", "dispatching", null, Ct);
            (await s.Diary.SaveResultAsync(scope, text.Revision.Id,
                "{\"needs_reply\":false,\"events\":[{\"type\":\"insulin\",\"intent\":\"record\",\"dose\":\"0.125\",\"unit\":\"U\",\"time_evidence\":\"current\"}],\"unclear\":[]}",
                "synthetic-text-model", null, Ct)).ShouldBeTrue();
        }
        VetPhotoExtraction? result = null;
        if (image)
        {
            var reservation = await Photos(s).ReserveDownloadAsync(scope, admitted.Source.Id, admitted.Input!.Id, 111, Ct);
            reservation.Status.ShouldBe(VetPhotoArchiveStatus.Reserved); var download = reservation.Claim.ShouldNotBeNull();
            var decoded = new VetPhotoImageDecoder().Decode(bytes, Ct).Image.ShouldNotBeNull();
            (await Photos(s).CommitOriginalAsync(new(scope, 111, download.Attempt.Id, download.ClaimToken,
                admitted.Input.Id, bytes, decoded), Ct)).Status.ShouldBe(VetPhotoArchiveStatus.Retained);
            var claimed = await Photos(s).ClaimCurrentImageAsync(scope, admitted.Source.Id, admitted.Input.Id, 111, Ct);
            claimed.Status.ShouldBe(VetPhotoImageStatus.Claimed); var claim = claimed.Claim.ShouldNotBeNull();
            (await Photos(s).MarkImageDispatchedAsync(scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
            var completed = await Photos(s).CompleteImageAsync(new(scope, claim.AttemptKey, claim.ClaimToken, 111,
                claim.SourceId, claim.InputRevisionId, "synthetic-model", ImageJson(claim.SourceId, claim.InputRevisionId)), Ct);
            completed.Status.ShouldBe(VetPhotoImageStatus.Installed); result = completed.Extraction.ShouldNotBeNull();
            await s.Context.Set<VetPhotoCandidate>().Where(c => c.SourceId == admitted.Source.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, "clear")
                    .SetProperty(c => c.EffectiveJson, JsonSerializer.Serialize(Reading, Json)).SetProperty(c => c.ReasonsJson, "[]"), Ct);
        }
        s.Context.ChangeTracker.Clear();
        return new(scope, await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(x => x.Id == admitted.Source.Id, Ct),
            await s.Context.Set<VetPhotoInputRevision>().AsNoTracking().SingleAsync(x => x.Id == admitted.Input!.Id, Ct),
            await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(x => x.SourceId == admitted.Source.Id, Ct),
            await s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().SingleOrDefaultAsync(x => x.InputRevisionId == admitted.Input!.Id, Ct),
            result, await s.Context.Set<VetProfile>().AsNoTracking().SingleAsync(Ct));
    }
    private static VetEventState Fact(Evidence e) => new("glucose", 5.6m, "mmol/L", null, Measured,
        "2031-05-11 10:20:00", "+00:00", "image_or_caption", "image/image", "photo", e.Source.Id, 0,
        null, e.Source.Id, e.Source.BatchId, e.Input.Id, e.Result!.Id, 111, e.Source.SourceMessageDbId!.Value, e.Source.TelegramMessageId);
    private async Task<VetPhotoDiarySelection> Select(VetTestSession s, Evidence e, string disposition = "save",
        VetEventState? state = null, string duplicate = "unresolved", bool restore = false)
    {
        var source = await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(x => x.Id == e.Source.Id, Ct);
        var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(x => x.Id == e.Candidate.Id, Ct);
        var batch = await s.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(x => x.Id == source.BatchId, Ct);
        if (state == null && disposition is "save" or "correct" or "link") state = Fact(e);
        var proof = (await s.Diary.GetPhotoCollisionProofAsync(e.Scope, e.Profile.Id, candidate.Id, state, Ct)).ShouldNotBeNull();
        return new(e.Profile.Id, e.Profile.Revision, batch.Id, batch.ReviewRevision, candidate.Id, candidate.Revision,
            source.Id, source.CurrentInputRevisionId, source.CurrentOrdinal, e.Input.Id, e.Result?.Id, candidate.ExtractionResultId,
            e.Original?.Id, e.Original?.Revision, e.Original?.State, candidate.EventId, candidate.EventRevision,
            disposition, duplicate, null, null, null, restore, new(), state, proof);
    }
    private async Task<VetPhotoReview> Review(VetTestSession s, VetDiaryScope scope, VetPhotoDiarySelection[] selection,
        VetPhotoReviewKind kind = VetPhotoReviewKind.Save, Guid? window = null, bool deliver = true, Guid? batchOverride = null)
    {
        var profile = await s.Context.Set<VetProfile>().AsNoTracking().SingleAsync(Ct);
        var batchId = batchOverride ?? (selection.Select(x => x.BatchId).Distinct().Count() == 1 ? selection[0].BatchId : (Guid?)null);
        var batchRevision = batchId == null ? null : (int?)await s.Context.Set<VetPhotoBatch>().Where(b => b.Id == batchId).Select(b => b.ReviewRevision).SingleAsync(Ct);
        var formatted = VetPhotoReviewFormatter.FormatBlocks("synthetic exact selected evidence",
            selection.Select(x => JsonSerializer.Serialize(x, Json)).ToArray(), "synthetic complete selection");
        formatted.Success.ShouldBeTrue();
        var preview = new VetPhotoPreviewResult(formatted.Pages.Concat(new[] { "synthetic acceptance" }).ToArray(), null);
        var staged = await Photos(s).StageReviewAsync(new(scope, Guid.NewGuid(), 111, kind, batchId, batchRevision,
            profile.Revision, JsonSerializer.Serialize(selection, Json), preview, window), Ct);
        staged.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); var review = staged.Review.ShouldNotBeNull();
        if (deliver)
        {
            var handle = new VetPhotoReviewHandle(scope, review.Id, review.Revision, review.OperationKey, 111);
            for (var i = 0; i < preview.Pages.Count; i++)
                (await Photos(s).RecordPageDeliveryAsync(handle, i, i == preview.Pages.Count - 1 ? 701 : 500 + i,
                    Hash(preview.Pages[i]), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
            (await Photos(s).CompleteDeliveryAsync(handle, 701, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        }
        return await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == review.Id, Ct);
    }
    private static VetPhotoDiaryAcceptance Accept(VetDiaryScope scope, VetPhotoReview review, long actor = 222) =>
        new(scope, review.Id, review.Revision, review.OperationKey, actor, 701);

    private async Task<VetMutationResult> SavePhoto(VetTestSession s, Evidence e, long actor = 222)
    {
        var review = await Review(s, e.Scope, [await Select(s, e, duplicate: "separate")]);
        var result = await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review, actor), Ct);
        result.Status.ShouldBe(VetMutationStatus.Applied); return result;
    }
    private static async Task<string> Retention(VetTestSession s) => JsonSerializer.Serialize(new
    {
        Originals = await s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Blobs = await s.Context.Set<VetPhotoBlob>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Inputs = await s.Context.Set<VetPhotoInputRevision>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Results = await s.Context.Set<VetPhotoExtraction>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        TextResults = await s.Context.VetExtractionResults.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Messages = await s.Context.Messages.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct)
    }, Json);
    private static async Task<string> Mutable(VetTestSession s) => JsonSerializer.Serialize(new
    {
        Events = await s.Context.VetEvents.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Candidates = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Actions = await s.Context.VetDiaryActions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Changes = await s.Context.VetDiaryActionChanges.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Reviews = await s.Context.Set<VetPhotoReview>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Batches = await s.Context.Set<VetPhotoBatch>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct)
    }, Json);
    private static VetPhotoCandidateState CandidateState(VetPhotoCandidate c) => new(c.State,
        c.RequiresExplicitRestoration, c.ManuallyCorrected, c.CorrectionProvenanceJson, c.EffectiveJson,
        c.ReasonsJson, c.DuplicateDecision, c.DuplicateSourceId, c.DuplicateEventId, c.DuplicateEventRevision,
        c.EventId, c.EventRevision, c.InputRevisionId, c.ExtractionResultId, c.LastReviewId);
    private static async Task<VetPhotoActionOutcome> Outcome(VetTestSession s, long id) =>
        JsonSerializer.Deserialize<VetPhotoActionOutcome>((await s.Context.VetDiaryActions.AsNoTracking()
            .SingleAsync(a => a.Id == id, Ct)).OutcomeJson).ShouldNotBeNull();

    [Fact]
    public async Task Forty_photo_creation_undo_is_one_scoped_action_with_stable_identity_increasing_revisions_and_restart_replay()
    {
        await SeedAsync(); await using var s = Open(); var items = new List<Evidence>();
        for (var i = 1; i <= 40; i++) items.Add(await Prepare(s, i, collection: i == 1));
        var batch = await s.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(Ct);
        (await Photos(s).CloseCollectionAsync(Scope, batch.Id, batch.ReviewRevision, 111, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var selection = new List<VetPhotoDiarySelection>();
        foreach (var item in items) selection.Add(await Select(s, item, duplicate: "separate"));
        var review = await Review(s, Scope, selection.ToArray());
        var saved = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct); saved.Status.ShouldBe(VetMutationStatus.Applied);
        saved.EventIds.Count.ShouldBe(40); var retained = await Retention(s); var key = Guid.NewGuid();
        var undone = await s.Diary.UndoAsync(Scope, 222, key, Ct);
        undone.Status.ShouldBe(VetMutationStatus.Applied); undone.EventIds.Order().ShouldBe(saved.EventIds.Order());
        undone.ProtectedIds.ShouldBeEmpty(); undone.ProtectedCandidateIds.ShouldBeEmpty(); undone.Revisions.Count.ShouldBe(40);
        await using var v = Open(); var outcome = await Outcome(v, undone.ActionId!.Value);
        outcome.PhotoChanges.Count.ShouldBe(40); outcome.ProtectedCandidateIds.ShouldBeEmpty();
        var events = await v.Context.VetEvents.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct);
        events.Length.ShouldBe(40);
        foreach (var item in items)
        {
            var fact = events.Single(e => e.PhotoSourceId == item.Source.Id);
            fact.Value.ShouldBe(5.6m); fact.OccurredAt.ShouldBe(Measured); fact.SourceAuthorUserId.ShouldBe(111);
            fact.InputRevisionId.ShouldBe(item.Input.Id); fact.ExtractionResultId.ShouldBe(item.Result!.Id);
            fact.DeletedAt.ShouldBe(Clock.UtcNow); fact.DeleteReason.ShouldBe("undo"); fact.DeletedByUserId.ShouldBe(222);
            fact.Revision.ShouldBe(2); fact.LastMutationKind.ShouldBe("undo");
            var candidate = await v.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == item.Candidate.Id, Ct);
            candidate.State.ShouldBe("deleted"); candidate.RequiresExplicitRestoration.ShouldBeTrue();
            candidate.EventId.ShouldBe(fact.Id); candidate.EventRevision.ShouldBe(2);
            candidate.InputRevisionId.ShouldBe(item.Input.Id); candidate.ExtractionResultId.ShouldBe(item.Result.Id);
            candidate.Revision.ShouldBe(selection.Single(x => x.CandidateId == candidate.Id).CandidateRevision + 2);
            candidate.LastReviewId.ShouldBeNull();
            var change = outcome.PhotoChanges.Single(c => c.CandidateId == candidate.Id);
            change.Before.State.ShouldBe("saved"); change.After.ShouldBe(CandidateState(candidate));
            change.AfterRevision.ShouldBe(change.BeforeRevision + 1);
        }
        (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(2);
        (await v.Context.VetDiaryActionChanges.CountAsync(c => c.ActionId == undone.ActionId, Ct)).ShouldBe(40);
        (await v.Context.VetDiaryActions.SingleAsync(a => a.Id == saved.ActionId, Ct)).ReversedByActionId.ShouldBe(undone.ActionId);
        var inverse = await v.Context.VetDiaryActions.SingleAsync(a => a.Id == undone.ActionId, Ct);
        inverse.ReversesActionId.ShouldBe(saved.ActionId); inverse.ActorUserId.ShouldBe(222); inverse.PhotoBatchId.ShouldBe(batch.Id);
        (await Retention(v)).ShouldBe(retained); var snapshot = await Mutable(v);
        var replay = await v.Diary.UndoAsync(Scope, 222, key, Ct); replay.Status.ShouldBe(VetMutationStatus.AlreadyApplied);
        replay.ActionId.ShouldBe(undone.ActionId); replay.EventIds.ShouldBe(undone.EventIds); replay.Revisions.ShouldBe(undone.Revisions); replay.ProtectedCandidateIds.ShouldBe(undone.ProtectedCandidateIds);
        (await Mutable(v)).ShouldBe(snapshot);
        (await v.Diary.UndoAsync(Scope, 111, Guid.NewGuid(), Ct)).Status.ShouldBe(VetMutationStatus.NotFound);
        (await Mutable(v)).ShouldBe(snapshot);
    }

    [Theory]
    [InlineData("exclude")]
    [InlineData("cancel")]
    public async Task Candidate_only_exclude_and_cancel_undo_restore_exact_prior_disposition_and_provenance_without_facts(string disposition)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s);
        var before = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct);
        var selected = await Select(s, item, disposition);
        var review = await Review(s, Scope, [selected], VetPhotoReviewKind.Correction);
        var accepted = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct); accepted.Status.ShouldBe(VetMutationStatus.Applied);
        accepted.EventIds.ShouldBeEmpty(); var retained = await Retention(s);
        var result = await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct); result.Status.ShouldBe(VetMutationStatus.Applied);
        result.EventIds.ShouldBeEmpty(); result.ProtectedIds.ShouldBeEmpty(); result.ProtectedCandidateIds.ShouldBeEmpty();
        await using var v = Open(); var restored = await v.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct);
        CandidateState(restored).ShouldBe(CandidateState(before) with { LastReviewId = null });
        restored.Revision.ShouldBe(before.Revision + 2);
        var outcome = await Outcome(v, result.ActionId!.Value); outcome.PhotoChanges.Count.ShouldBe(1);
        outcome.PhotoChanges.Single().Before.State.ShouldBe(disposition == "exclude" ? "excluded" : "cancelled");
        outcome.PhotoChanges.Single().After.ShouldBe(CandidateState(restored));
        (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(0); (await v.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(0);
        (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(2); (await Retention(v)).ShouldBe(retained);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Correction_and_explicit_soft_delete_restoration_undo_restore_exact_prior_fact_and_candidate_with_new_revisions(bool restoration)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s); var saved = await SavePhoto(s, item);
        if (restoration)
        {
            await s.Context.VetEvents.Where(e => e.Id == saved.EventIds.Single()).ExecuteUpdateAsync(u => u
                .SetProperty(e => e.DeletedAt, Now.AddMinutes(-1)).SetProperty(e => e.DeleteReason, "synthetic explicit deletion")
                .SetProperty(e => e.DeletedByUserId, (long?)111).SetProperty(e => e.Revision, 2), Ct);
            await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == item.Candidate.Id).ExecuteUpdateAsync(u => u
                .SetProperty(c => c.State, "deleted").SetProperty(c => c.RequiresExplicitRestoration, true)
                .SetProperty(c => c.EventRevision, (int?)2).SetProperty(c => c.Revision, c => c.Revision + 1), Ct);
        }
        var beforeFact = await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct);
        var beforeCandidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct);
        var context = new VetPhotoContext("7.25", "mmol/L", 2031, 5, 11, "10:20", "+00:00", CorrectionApproved: true);
        var selected = (await Select(s, item, "correct", Fact(item) with { Value = 7.25m,
            OccurredAtSource = "human_correction", ValueUnitSource = "human_correction" }, "separate", restoration)) with { Context = context };
        var review = await Review(s, Scope, [selected], VetPhotoReviewKind.Correction);
        var corrected = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct); corrected.Status.ShouldBe(VetMutationStatus.Applied);
        corrected.EventIds.ShouldBe(saved.EventIds); var retained = await Retention(s);
        var result = await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct); result.EventIds.ShouldBe(saved.EventIds);
        await using var v = Open(); var fact = await v.Context.VetEvents.AsNoTracking().SingleAsync(Ct);
        fact.Value.ShouldBe(beforeFact.Value); fact.Unit.ShouldBe(beforeFact.Unit); fact.OccurredAt.ShouldBe(beforeFact.OccurredAt);
        fact.DeletedAt.ShouldBe(beforeFact.DeletedAt); fact.DeleteReason.ShouldBe(beforeFact.DeleteReason);
        fact.DeletedByUserId.ShouldBe(beforeFact.DeletedByUserId); fact.Revision.ShouldBe(beforeFact.Revision + 2);
        fact.InputRevisionId.ShouldBe(beforeFact.InputRevisionId); fact.ExtractionResultId.ShouldBe(beforeFact.ExtractionResultId);
        fact.ValueUnitSource.ShouldBe(beforeFact.ValueUnitSource); fact.OccurredAtSource.ShouldBe(beforeFact.OccurredAtSource);
        var candidate = await v.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct);
        CandidateState(candidate).ShouldBe(CandidateState(beforeCandidate) with { EventRevision = fact.Revision, LastReviewId = null });
        candidate.Revision.ShouldBe(beforeCandidate.Revision + 2);
        (await Outcome(v, result.ActionId!.Value)).PhotoChanges.Single().After.ShouldBe(CandidateState(candidate));
        (await Retention(v)).ShouldBe(retained);
    }

    [Theory]
    [InlineData("candidate_revision")]
    [InlineData("candidate_state")]
    [InlineData("event_revision")]
    public async Task Later_candidate_or_event_change_protects_exact_subset_and_records_durable_inverse_without_touching_protected_row(string protection)
    {
        await SeedAsync(); await using var s = Open(); var a = await Prepare(s, 1, collection: true); var b = await Prepare(s, 2);
        var review = await Review(s, Scope, [await Select(s, a, duplicate: "separate"), await Select(s, b, duplicate: "separate")]);
        var saved = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct); saved.Status.ShouldBe(VetMutationStatus.Applied);
        var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == a.Candidate.Id, Ct);
        if (protection == "candidate_revision") await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == candidate.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Revision, c => c.Revision + 1), Ct);
        if (protection == "candidate_state") await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == candidate.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.ManuallyCorrected, true), Ct);
        if (protection == "event_revision") await s.Context.VetEvents.Where(e => e.Id == candidate.EventId)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.Revision, e => e.Revision + 1).SetProperty(e => e.Value, 8.125m), Ct);
        var protectedCandidate = JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == candidate.Id, Ct), Json);
        var protectedFact = JsonSerializer.Serialize(await s.Context.VetEvents.AsNoTracking().SingleAsync(e => e.Id == candidate.EventId, Ct), Json);
        var retained = await Retention(s); var key = Guid.NewGuid(); var result = await s.Diary.UndoAsync(Scope, 222, key, Ct);
        result.Status.ShouldBe(VetMutationStatus.Applied); result.ProtectedIds.ShouldBe(new[] { candidate.EventId!.Value });
        result.ProtectedCandidateIds.ShouldBe(new[] { candidate.Id });
        result.EventIds.ShouldBe(saved.EventIds.Where(id => id != candidate.EventId).ToArray());
        await using var v = Open(); var outcome = await Outcome(v, result.ActionId!.Value);
        outcome.ProtectedCandidateIds.ShouldBe(new[] { candidate.Id }); outcome.PhotoChanges.Single().CandidateId.ShouldBe(b.Candidate.Id);
        JsonSerializer.Serialize(await v.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == candidate.Id, Ct), Json).ShouldBe(protectedCandidate);
        JsonSerializer.Serialize(await v.Context.VetEvents.AsNoTracking().SingleAsync(e => e.Id == candidate.EventId, Ct), Json).ShouldBe(protectedFact);
        (await v.Context.VetEvents.SingleAsync(e => e.Id == result.EventIds.Single(), Ct)).DeletedAt.ShouldBe(Now);
        (await v.Context.VetDiaryActionChanges.CountAsync(c => c.ActionId == result.ActionId, Ct)).ShouldBe(1);
        (await Retention(v)).ShouldBe(retained); var snapshot = await Mutable(v);
        var replay = await v.Diary.UndoAsync(Scope, 222, key, Ct); replay.Status.ShouldBe(VetMutationStatus.AlreadyApplied);
        replay.ProtectedCandidateIds.ShouldBe(result.ProtectedCandidateIds);
        (await Mutable(v)).ShouldBe(snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_and_linked_creation_undo_preserve_unique_ownership_and_protect_links_when_canonical_candidate_changed(bool protect)
    {
        await SeedAsync(); await using var s = Open(); var canonical = await Prepare(s, 1, collection: true); var linked = await Prepare(s, 2, sameBytes: true);
        var first = await Select(s, canonical, duplicate: "canonical");
        var second = (await Select(s, linked, "link", duplicate: "same")) with { LinkCandidateId = canonical.Candidate.Id };
        var review = await Review(s, Scope, [first, second]); var saved = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct);
        saved.Status.ShouldBe(VetMutationStatus.Applied); saved.EventIds.Count.ShouldBe(1);
        if (protect) await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == canonical.Candidate.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Revision, c => c.Revision + 1), Ct);
        var before = await Mutable(s); var retained = await Retention(s);
        var result = await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct); result.Status.ShouldBe(VetMutationStatus.Applied);
        await using var v = Open(); var outcome = await Outcome(v, result.ActionId!.Value);
        var c = await v.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(x => x.Id == canonical.Candidate.Id, Ct);
        var l = await v.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(x => x.Id == linked.Candidate.Id, Ct);
        l.EventId.ShouldBeNull(); l.DuplicateEventId.ShouldBe(saved.EventIds.Single()); l.DuplicateSourceId.ShouldBe(canonical.Source.Id);
        if (protect)
        {
            result.EventIds.ShouldBeEmpty(); result.ProtectedIds.ShouldBe(saved.EventIds);
            result.ProtectedCandidateIds.Order().ShouldBe(new[] { c.Id, l.Id }.Order());
            outcome.ProtectedCandidateIds.Order().ShouldBe(new[] { c.Id, l.Id }.Order()); outcome.PhotoChanges.ShouldBeEmpty();
            c.State.ShouldBe("saved"); l.State.ShouldBe("linked");
            (await v.Context.VetEvents.SingleAsync(Ct)).DeletedAt.ShouldBeNull();
            var original = JsonSerializer.Deserialize<JsonElement>(before);
            JsonSerializer.Serialize(await v.Context.Set<VetPhotoCandidate>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct), Json)
                .ShouldBe(original.GetProperty("candidates").GetRawText());
        }
        else
        {
            result.EventIds.ShouldBe(saved.EventIds); result.ProtectedCandidateIds.ShouldBeEmpty(); outcome.PhotoChanges.Count.ShouldBe(2);
            c.State.ShouldBe("deleted"); l.State.ShouldBe("deleted"); l.RequiresExplicitRestoration.ShouldBeTrue();
            c.EventRevision.ShouldBe(2); l.DuplicateEventRevision.ShouldBe(2);
            (await v.Context.VetEvents.SingleAsync(Ct)).DeletedAt.ShouldBe(Now);
        }
        (await Retention(v)).ShouldBe(retained);
    }

    [Fact]
    public async Task Link_only_undo_marks_explicit_restoration_without_mutating_the_existing_canonical_event()
    {
        await SeedAsync(); await using var s = Open(); var canonical = await Prepare(s, 1); var saved = await SavePhoto(s, canonical, 111);
        var linked = await Prepare(s, 2, sameBytes: true);
        var selected = (await Select(s, linked, "link", duplicate: "same")) with { LinkEventId = saved.EventIds.Single(), LinkEventRevision = 1 };
        var review = await Review(s, Scope, [selected]);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Applied);
        var canonicalFact = JsonSerializer.Serialize(await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct), Json);
        var retained = await Retention(s); var undone = await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct);
        undone.EventIds.ShouldBeEmpty(); undone.ProtectedIds.ShouldBeEmpty();
        await using var v = Open(); JsonSerializer.Serialize(await v.Context.VetEvents.AsNoTracking().SingleAsync(Ct), Json).ShouldBe(canonicalFact);
        var link = await v.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == linked.Candidate.Id, Ct);
        link.EventId.ShouldBeNull(); link.State.ShouldBe("deleted"); link.RequiresExplicitRestoration.ShouldBeTrue();
        link.DuplicateEventId.ShouldBe(saved.EventIds.Single()); link.DuplicateEventRevision.ShouldBe(1);
        link.Revision.ShouldBe(selected.CandidateRevision + 2); link.InputRevisionId.ShouldBe(linked.Input.Id);
        link.ExtractionResultId.ShouldBe(linked.Result!.Id);
        (await Outcome(v, undone.ActionId!.Value)).PhotoChanges.Single().CandidateId.ShouldBe(link.Id);
        (await Retention(v)).ShouldBe(retained);
    }

    [Theory]
    [InlineData(-10, VetMutationStatus.Applied)]
    [InlineData(0, VetMutationStatus.Applied)]
    [InlineData(10, VetMutationStatus.NotFound)]
    public async Task Twenty_four_hour_boundary_uses_action_time_inclusive_at_exact_deadline_not_measurement_time(int ticks, VetMutationStatus expected)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s); await SavePhoto(s, item);
        Clock.UtcNow = Now.AddHours(24).AddTicks(ticks); var snapshot = await Mutable(s); var retained = await Retention(s);
        var result = await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct); result.Status.ShouldBe(expected);
        await using var v = Open();
        if (expected == VetMutationStatus.NotFound) (await Mutable(v)).ShouldBe(snapshot);
        else { (await v.Context.VetEvents.SingleAsync(Ct)).DeletedAt.ShouldBe(Clock.UtcNow); result.Revisions.Single().Revision.ShouldBe(2); }
        (await Retention(v)).ShouldBe(retained);
    }

    [Fact]
    public async Task Latest_own_exact_place_action_wins_over_other_actor_newer_topic_and_non_applicable_keep_action()
    {
        await SeedAsync(); await using var s = Open(); var first = await Prepare(s, 1); var a = await SavePhoto(s, first);
        Clock.UtcNow = Now.AddMinutes(1); var second = await Prepare(s, 2); var b = await SavePhoto(s, second);
        Clock.UtcNow = Now.AddMinutes(2); var otherActor = await Prepare(s, 3); var c = await SavePhoto(s, otherActor, 111);
        Clock.UtcNow = Now.AddMinutes(3); var otherTopic = await Prepare(s, 4, topic: 8); var d = await SavePhoto(s, otherTopic);
        // Actual evidence-only keep acceptance emits a durable marker but is not an applicable diary undo action.
        var profile = await s.Profiles.GetOrCreateAsync(FamilyId, Bot.BotDbId, Ct);
        var stage = await Photos(s).StageRunAsync(new(Scope, Guid.NewGuid(), 111, profile.Revision,
            VetPhotoRunPurpose.Reprocess, VetPhotoRunSelectionMode.Selected, "gpt-6.1-sol", "codex-cli",
            ReferenceIds: [second.Original!.Id]), Ct);
        stage.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); var approval = stage.Review.ShouldNotBeNull();
        var initialPages = JsonSerializer.Deserialize<string[]>(approval.PreviewPagesJson, Json)!;
        var initialHandle = new VetPhotoReviewHandle(Scope, approval.Id, approval.Revision, approval.OperationKey, 111);
        for (var i = 0; i < initialPages.Length; i++)
            (await Photos(s).RecordPageDeliveryAsync(initialHandle, i, 8000 + i, Hash(initialPages[i]), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Photos(s).CompleteDeliveryAsync(initialHandle, 7999 + initialPages.Length, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        approval = (await Photos(s).ReadPreviewAsync(Scope, approval.Id, 111, Ct)).ShouldNotBeNull();
        var run = new VetPhotoRunHandle(Scope, stage.Run!.Id, 111);
        (await Photos(s).ApproveRunAsync(run, new(Scope, approval.Id, approval.Revision, approval.OperationKey, 111,
            approval.AcceptancePromptMessageId), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var window = (await Photos(s).ContinueRunAsync(run, Ct)).Window.ShouldNotBeNull();
        var snapshot = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(window.SelectionJson, Json)!.Single();
        var claim = (await Photos(s).ClaimScheduledImageAsync(Scope, snapshot.AttemptKey, 111, Ct)).Claim.ShouldNotBeNull();
        (await Photos(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
        var complete = await Photos(s).CompleteImageAsync(new(Scope, claim.AttemptKey, claim.ClaimToken, 111,
            claim.SourceId, claim.InputRevisionId, "gpt-6.1-sol", ImageJson(claim.SourceId, claim.InputRevisionId)), Ct);
        complete.Status.ShouldBe(VetPhotoImageStatus.ProposedDelta);
        var keep = (await Select(s, second, "keep", Fact(second))) with {
            State = Fact(second), ExtractionResultId = complete.Extraction.ShouldNotBeNull().Id };
        var review = await Review(s, Scope, [keep], VetPhotoReviewKind.ReextractComparison, window.Id);
        (await Photos(s).AttachComparisonAsync(run, window.Id, review.Id, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var kept = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct); kept.Status.ShouldBe(VetMutationStatus.NoChange);
        (await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(x => x.Id == kept.ActionId, Ct)).Kind.ShouldBe("photo_comparison_keep");
        var result = await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct); result.EventIds.ShouldBe(b.EventIds);
        await using var v = Open();
        (await v.Context.VetDiaryActions.SingleAsync(x => x.Id == result.ActionId, Ct)).ReversesActionId.ShouldBe(b.ActionId);
        foreach (var id in a.EventIds.Concat(c.EventIds).Concat(d.EventIds))
            (await v.Context.VetEvents.SingleAsync(x => x.Id == id, Ct)).DeletedAt.ShouldBeNull();
        (await v.Context.VetDiaryActions.SingleAsync(x => x.Id == kept.ActionId, Ct)).ReversedByActionId.ShouldBeNull();
    }

    [Theory]
    [InlineData("unknown_actor", VetMutationStatus.Refused)]
    [InlineData("revoked_member", VetMutationStatus.Refused)]
    [InlineData("revoked_place", VetMutationStatus.Refused)]
    [InlineData("disabled_bot", VetMutationStatus.Refused)]
    [InlineData("foreign_chat", VetMutationStatus.Refused)]
    [InlineData("other_topic", VetMutationStatus.NotFound)]
    [InlineData("empty_key", VetMutationStatus.Refused)]
    public async Task Undo_rechecks_fresh_actor_scope_and_nonempty_operation_before_any_inverse(string fence, VetMutationStatus expected)
    {
        await SeedAsync(); await using var s = Open(); await SavePhoto(s, await Prepare(s));
        var actor = fence == "unknown_actor" ? 333 : 222; var scope = Scope;
        if (fence == "revoked_member") await s.Context.FamilyMembers.Where(m => m.TelegramUserId == 222)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        if (fence == "revoked_place") await s.Context.Places.Where(p => p.TopicId == 7)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlaceStatus.Denied), Ct);
        if (fence == "disabled_bot") await s.Context.Bots.ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BotStatus.Disabled), Ct);
        if (fence == "foreign_chat") scope = scope with { ChatId = -101 };
        if (fence == "other_topic") scope = scope with { TopicId = 8 };
        var snapshot = await Mutable(s); var retained = await Retention(s);
        (await s.Diary.UndoAsync(scope, actor, fence == "empty_key" ? Guid.Empty : Guid.NewGuid(), Ct)).Status.ShouldBe(expected);
        await using var v = Open(); (await Mutable(v)).ShouldBe(snapshot); (await Retention(v)).ShouldBe(retained);
    }

    [Fact]
    public async Task Operation_replay_cannot_change_actor_or_place_and_revocation_refuses_even_a_successful_replay()
    {
        await SeedAsync(); await using var s = Open(); await SavePhoto(s, await Prepare(s)); var key = Guid.NewGuid();
        var result = await s.Diary.UndoAsync(Scope, 222, key, Ct); result.Status.ShouldBe(VetMutationStatus.Applied);
        var snapshot = await Mutable(s);
        (await s.Diary.UndoAsync(Scope, 111, key, Ct)).Status.ShouldBe(VetMutationStatus.Refused);
        (await s.Diary.UndoAsync(Scope with { TopicId = 8 }, 222, key, Ct)).Status.ShouldBe(VetMutationStatus.Refused);
        await s.Context.FamilyMembers.Where(m => m.TelegramUserId == 222)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        (await s.Diary.UndoAsync(Scope, 222, key, Ct)).Status.ShouldBe(VetMutationStatus.Refused);
        await using var v = Open(); (await Mutable(v)).ShouldBe(snapshot);
        (await v.Context.VetDiaryActions.CountAsync(a => a.Kind == "undo", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Photo_undo_preserves_independently_saved_actual_TEXT_insulin_caption_and_all_original_bytes()
    {
        await SeedAsync(); await using var s = Open();
        var text = await EvidenceAsync(s, id: 1000, update: 1, type: "insulin", value: "0.125");
        var insulin = await s.Diary.ApplyAsync(Save(Scope, text.Source, text.Profile, text.State), Ct);
        insulin.Status.ShouldBe(VetMutationStatus.Applied); Clock.UtcNow = Now.AddMinutes(1);
        var photo = await Prepare(s, 2); await SavePhoto(s, photo);
        var textFact = JsonSerializer.Serialize(await s.Context.VetEvents.AsNoTracking().SingleAsync(e => e.Id == insulin.EventIds.Single(), Ct), Json);
        var retained = await Retention(s); var undone = await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct);
        undone.EventIds.Count.ShouldBe(1); undone.EventIds.ShouldNotContain(insulin.EventIds.Single());
        await using var v = Open(); JsonSerializer.Serialize(await v.Context.VetEvents.AsNoTracking()
            .SingleAsync(e => e.Id == insulin.EventIds.Single(), Ct), Json).ShouldBe(textFact);
        (await Retention(v)).ShouldBe(retained);
        (await v.Context.VetDiaryActions.SingleAsync(a => a.Id == insulin.ActionId, Ct)).ReversedByActionId.ShouldBeNull();
        (await v.Context.VetEvents.SingleAsync(e => e.Id == insulin.EventIds.Single(), Ct)).Value.ShouldBe(0.125m);
    }

    [Fact]
    public async Task Undo_invalidates_complete_pending_preview_and_does_not_implicitly_restore_created_fact()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s); await SavePhoto(s, item);
        var preview = await Review(s, Scope, [await Select(s, item, "correct")], VetPhotoReviewKind.Correction);
        preview.CompletePreviewDelivered.ShouldBeTrue();
        await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct);
        var stale = await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == preview.Id, Ct);
        stale.State.ShouldBe("stale"); stale.CompletePreviewDelivered.ShouldBeFalse(); stale.AcceptancePromptMessageId.ShouldBeNull();
        var snapshot = await Mutable(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, preview), Ct)).Status.ShouldBe(VetMutationStatus.Stale);
        (await Mutable(s)).ShouldBe(snapshot);
        var withoutRestoration = await Review(s, Scope, [await Select(s, item, "correct")], VetPhotoReviewKind.Correction);
        var beforeRefusal = await Mutable(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, withoutRestoration), Ct)).Status.ShouldBe(VetMutationStatus.Refused);
        (await Mutable(s)).ShouldBe(beforeRefusal);
        var selected = await Select(s, item, "correct", restore: true);
        var explicitReview = await Review(s, Scope, [selected], VetPhotoReviewKind.Correction);
        var restored = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, explicitReview), Ct);
        restored.Status.ShouldBe(VetMutationStatus.Applied);
        await using var v = Open(); var fact = await v.Context.VetEvents.AsNoTracking().SingleAsync(Ct);
        fact.Revision.ShouldBe(3); fact.DeletedAt.ShouldBeNull(); fact.PhotoSourceId.ShouldBe(item.Source.Id);
        var candidate = await v.Context.Set<VetPhotoCandidate>().SingleAsync(Ct);
        candidate.State.ShouldBe("saved"); candidate.RequiresExplicitRestoration.ShouldBeFalse(); candidate.EventRevision.ShouldBe(3);
    }

    private sealed class FailUndoWrite(string table) : DbCommandInterceptor
    {
        public int Hits { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Hits == 0 && command.CommandText.Replace("\"", "", StringComparison.Ordinal)
                .Contains("UPDATE " + table, StringComparison.OrdinalIgnoreCase))
            { Hits++; throw new InvalidOperationException("synthetic undo write failure"); }
            return ValueTask.FromResult(result);
        }
    }
    [Theory]
    [InlineData("vet_photo_candidates")]
    [InlineData("vet_diary_actions")]
    public async Task Undo_database_failure_rolls_back_facts_candidates_action_and_proof_then_same_context_retry_and_restart_replay_succeed(string table)
    {
        await SeedAsync(); await using var setup = Open(); var item = await Prepare(setup); await SavePhoto(setup, item);
        var before = await Mutable(setup); var retained = await Retention(setup); var fail = new FailUndoWrite(table); var key = Guid.NewGuid();
        await using var s = Open(interceptor: fail);
        var failure = await Should.ThrowAsync<DbUpdateException>(() => s.Diary.UndoAsync(Scope, 222, key, Ct));
        failure.InnerException.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("synthetic undo write failure");
        fail.Hits.ShouldBe(1);
        await using (var verify = Open()) { (await Mutable(verify)).ShouldBe(before); (await Retention(verify)).ShouldBe(retained); }
        var result = await s.Diary.UndoAsync(Scope, 222, key, Ct); result.Status.ShouldBe(VetMutationStatus.Applied);
        result.Revisions.Single().Revision.ShouldBe(2); fail.Hits.ShouldBe(1);
        await using var restart = Open(); var snapshot = await Mutable(restart);
        (await restart.Diary.UndoAsync(Scope, 222, key, Ct)).Status.ShouldBe(VetMutationStatus.AlreadyApplied);
        (await Mutable(restart)).ShouldBe(snapshot); (await Retention(restart)).ShouldBe(retained);
        (await restart.Context.VetDiaryActions.CountAsync(a => a.Kind == "undo", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Same_timestamp_latest_action_uses_increasing_action_identity_and_never_undoes_both()
    {
        await SeedAsync(); await using var s = Open(); var first = await SavePhoto(s, await Prepare(s, 1));
        var last = await SavePhoto(s, await Prepare(s, 2));
        var actions = await s.Context.VetDiaryActions.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync(Ct);
        actions[0].CreatedAt.ShouldBe(actions[1].CreatedAt); last.ActionId!.Value.ShouldBeGreaterThan(first.ActionId!.Value);
        var result = await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct); result.EventIds.ShouldBe(last.EventIds);
        await using var v = Open(); (await v.Context.VetEvents.SingleAsync(e => e.Id == first.EventIds.Single(), Ct)).DeletedAt.ShouldBeNull();
        (await v.Context.VetEvents.SingleAsync(e => e.Id == last.EventIds.Single(), Ct)).DeletedAt.ShouldBe(Now);
    }
    [Theory]
    [InlineData(111)]
    [InlineData(222)]
    public async Task Empty_history_returns_not_found_without_creating_a_durable_inverse(long actor)
    {
        await SeedAsync(); await using var s = Open(); var before = await Mutable(s);
        (await s.Diary.UndoAsync(Scope, actor, Guid.NewGuid(), Ct)).Status.ShouldBe(VetMutationStatus.NotFound);
        (await Mutable(s)).ShouldBe(before);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unset_or_foreign_current_family_scope_throws_before_any_action_or_candidate_write(bool foreign)
    {
        await SeedAsync(); await using var setup = Open(); await SavePhoto(setup, await Prepare(setup)); var before = await Mutable(setup);
        await using var wrong = foreign ? Open(familyId: FamilyId + 1000) : Unscoped();
        await Should.ThrowAsync<InvalidOperationException>(() => wrong.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct));
        await using var verify = Open(); (await Mutable(verify)).ShouldBe(before);
    }

    [Fact]
    public async Task Same_bound_photo_caption_TEXT_insulin_action_and_immutable_interpretation_survive_photo_subset_undo()
    {
        await SeedAsync(); await using var s = Open(); var photo = await Prepare(s, caption: true);
        var textSource = (await s.Diary.FindSourceAsync(Scope, photo.Source.TelegramMessageId, Ct)).ShouldNotBeNull();
        var result = (await s.Diary.GetResultAsync(Scope, textSource.Revision.Id, Ct)).ShouldNotBeNull();
        var parsed = VetInterpretationParser.Parse(result.Json).ShouldNotBeNull();
        parsed.Events.Single().EventType.ShouldBe("insulin"); parsed.Events.Single().RawValue.ShouldBe("0.125");
        var validated = VetEventValidation.Validate(parsed.Events.Single(), photo.Profile, textSource, result.Id);
        var insulin = await s.Diary.ApplyAsync(Save(Scope, textSource, photo.Profile, validated.State.ShouldNotBeNull()), Ct);
        insulin.Status.ShouldBe(VetMutationStatus.Applied);
        await s.Diary.SetProcessingAsync(Scope, textSource.Revision.Id, "ready", "written", null, Ct);
        (await s.Context.VetTextSourceRevisions.AsNoTracking().SingleAsync(r => r.Id == textSource.Revision.Id, Ct)).State.ShouldBe("written");
        var saved = await SavePhoto(s, photo);
        var textFact = await s.Context.VetEvents.AsNoTracking().SingleAsync(e => e.Id == insulin.EventIds.Single(), Ct);
        textFact.SourceMessageDbId.ShouldBe(photo.Source.SourceMessageDbId!.Value);
        textFact.TelegramMessageId.ShouldBe(photo.Source.TelegramMessageId); textFact.SourceKind.ShouldBe("text");
        textFact.Value.ShouldBe(0.125m); textFact.Unit.ShouldBe("U"); textFact.PhotoSourceId.ShouldBeNull();
        var textBefore = JsonSerializer.Serialize(new {
            Sources = await s.Context.VetTextSources.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
            Revisions = await s.Context.VetTextSourceRevisions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
            Results = await s.Context.VetExtractionResults.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct)
        }, Json);
        var retained = await Retention(s); var undo = await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct);
        undo.EventIds.ShouldBe(saved.EventIds); undo.EventIds.ShouldNotContain(insulin.EventIds.Single());
        await using var v = Open(); JsonSerializer.Serialize(await v.Context.VetEvents.AsNoTracking()
            .SingleAsync(e => e.Id == textFact.Id, Ct), Json).ShouldBe(JsonSerializer.Serialize(textFact, Json));
        JsonSerializer.Serialize(new {
            Sources = await v.Context.VetTextSources.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
            Revisions = await v.Context.VetTextSourceRevisions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
            Results = await v.Context.VetExtractionResults.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct)
        }, Json).ShouldBe(textBefore);
        (await v.Context.VetDiaryActions.SingleAsync(a => a.Id == insulin.ActionId, Ct)).ReversedByActionId.ShouldBeNull();
        (await Outcome(v, undo.ActionId!.Value)).PhotoChanges.Single().SourceId.ShouldBe(photo.Source.Id);
        (await Retention(v)).ShouldBe(retained);
    }

    [Fact]
    public async Task Protected_candidate_only_disposition_undo_records_exact_skipped_candidate_without_restoring_it_or_creating_facts()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s);
        var review = await Review(s, Scope, [await Select(s, item, "exclude")], VetPhotoReviewKind.Correction);
        var excluded = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct); excluded.Status.ShouldBe(VetMutationStatus.Applied);
        await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == item.Candidate.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Revision, c => c.Revision + 1), Ct);
        var before = JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct), Json);
        var retained = await Retention(s); var key = Guid.NewGuid();
        var undone = await s.Diary.UndoAsync(Scope, 222, key, Ct); undone.Status.ShouldBe(VetMutationStatus.Applied);
        undone.EventIds.ShouldBeEmpty(); undone.ProtectedIds.ShouldBeEmpty();
        undone.ProtectedCandidateIds.ShouldBe(new[] { item.Candidate.Id });
        await using var v = Open(); var outcome = await Outcome(v, undone.ActionId!.Value);
        outcome.ProtectedCandidateIds.ShouldBe(new[] { item.Candidate.Id }); outcome.PhotoChanges.ShouldBeEmpty();
        JsonSerializer.Serialize(await v.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct), Json).ShouldBe(before);
        (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(0); (await v.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(0);
        (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(2);
        (await v.Context.VetDiaryActions.SingleAsync(a => a.Id == excluded.ActionId, Ct)).ReversedByActionId.ShouldBe(undone.ActionId);
        (await Retention(v)).ShouldBe(retained);
        var snapshot = await Mutable(v); var replay = await v.Diary.UndoAsync(Scope, 222, key, Ct);
        replay.Status.ShouldBe(VetMutationStatus.AlreadyApplied); replay.ProtectedCandidateIds.ShouldBe(undone.ProtectedCandidateIds);
        (await Mutable(v)).ShouldBe(snapshot);
    }
    [Theory]
    [InlineData("target_revision")]
    [InlineData("linked_revision")]
    public async Task Changed_link_target_or_link_candidate_is_protected_without_touching_canonical_fact_or_link_handles(string protection)
    {
        await SeedAsync(); await using var s = Open(); var canonical = await Prepare(s, 1); var saved = await SavePhoto(s, canonical, 111);
        var linked = await Prepare(s, 2, sameBytes: true);
        var selected = (await Select(s, linked, "link", duplicate: "same")) with { LinkEventId = saved.EventIds.Single(), LinkEventRevision = 1 };
        var review = await Review(s, Scope, [selected]);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Applied);
        if (protection == "target_revision") await s.Context.VetEvents.ExecuteUpdateAsync(u =>
            u.SetProperty(e => e.Revision, e => e.Revision + 1).SetProperty(e => e.Value, 8.125m), Ct);
        else await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == linked.Candidate.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Revision, c => c.Revision + 1), Ct);
        var candidates = JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().OrderBy(c => c.Id).ToArrayAsync(Ct), Json);
        var canonicalFact = JsonSerializer.Serialize(await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct), Json);
        var retained = await Retention(s); var undone = await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct);
        undone.EventIds.ShouldBeEmpty(); undone.ProtectedIds.ShouldBeEmpty();
        undone.ProtectedCandidateIds.ShouldBe(new[] { linked.Candidate.Id });
        await using var v = Open(); var outcome = await Outcome(v, undone.ActionId!.Value);
        outcome.ProtectedCandidateIds.ShouldBe(new[] { linked.Candidate.Id }); outcome.PhotoChanges.ShouldBeEmpty();
        JsonSerializer.Serialize(await v.Context.VetEvents.AsNoTracking().SingleAsync(Ct), Json).ShouldBe(canonicalFact);
        JsonSerializer.Serialize(await v.Context.Set<VetPhotoCandidate>().AsNoTracking().OrderBy(c => c.Id).ToArrayAsync(Ct), Json).ShouldBe(candidates);
        (await Retention(v)).ShouldBe(retained);
        var reversed = (await v.Context.VetDiaryActions.SingleAsync(a => a.Id == undone.ActionId, Ct)).ReversesActionId;
        reversed.ShouldNotBeNull(); reversed.Value.ShouldBeGreaterThan(saved.ActionId!.Value);
    }
}
