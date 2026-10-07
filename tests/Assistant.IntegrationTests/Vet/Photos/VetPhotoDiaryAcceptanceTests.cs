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

public sealed class VetPhotoDiaryAcceptanceTests : VetTestBase
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
        bool sameBytes = false, bool collection = false, bool privateChat = false)
    {
        var scope = privateChat ? Scope with { ChatId = 111, TopicId = null } : Scope with { TopicId = topic };
        if (collection) (await Photos(s).StartCollectionAsync(scope, 111, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var bytes = Png(sameBytes ? 1 : id);
        var message = Text("synthetic image caption", id, 111, topic) with { Kind = MessageKind.Photo,
            ChatId = scope.ChatId, TopicId = scope.TopicId, ChatType = privateChat ? "private" : "supergroup" };
        var admitted = await Photos(s).AdmitAsync(scope, message, id,
            new($"synthetic-file-{id}", $"synthetic-unique-{id}", "synthetic.png", "image/png", bytes.Length, 32, 24), null, Ct);
        admitted.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        var transport = await s.Messages.StoreAsync(scope.TelegramBotId, id, message, Ct);
        (await Photos(s).BindMessageAsync(scope, admitted.Source!.Id, transport.MessageDbId.ShouldNotBeNull(), Ct)).ShouldBeTrue();
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
    private async Task AssertUnchanged(Evidence e, string candidatesBefore, string eventsBefore, int actionsBefore)
    {
        await using var v = Open();
        (await SnapshotCandidates(v)).ShouldBe(candidatesBefore); (await SnapshotEvents(v)).ShouldBe(eventsBefore);
        (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(actionsBefore);
        (await v.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(actionsBefore == 0 ? 0 : 1);
        (await v.Context.Set<VetPhotoSource>().SingleAsync(x => x.Id == e.Source.Id, Ct)).SourceAuthorUserId.ShouldBe(111);
    }
    private static async Task<string> SnapshotCandidates(VetTestSession s) => JsonSerializer.Serialize(
        await s.Context.Set<VetPhotoCandidate>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct), Json);
    private static async Task<string> SnapshotEvents(VetTestSession s) => JsonSerializer.Serialize(
        await s.Context.VetEvents.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct), Json);

    [Fact]
    public async Task Complete_preview_saves_fractional_value_exact_image_time_provenance_actual_confirmer_and_immutable_action_once()
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s);
        var selected = await Select(s, e); var review = await Review(s, e.Scope, [selected]);
        var result = await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review), Ct);
        result.Status.ShouldBe(VetMutationStatus.Applied); result.EventIds.Count.ShouldBe(1); result.ProtectedIds.ShouldBeEmpty();
        await using var v = Open(); var fact = await v.Context.VetEvents.SingleAsync(Ct);
        fact.Id.ShouldBe(result.EventIds.Single()); fact.Value.ShouldBe(5.6m); fact.Unit.ShouldBe("mmol/L"); fact.Product.ShouldBeNull();
        fact.OccurredAt.ShouldBe(Measured); fact.LocalTime.ShouldBe("2031-05-11 10:20:00"); fact.TimeZoneSnapshot.ShouldBe("+00:00");
        fact.OccurredAtSource.ShouldBe("image_or_caption"); fact.ValueUnitSource.ShouldBe("image/image");
        fact.SourceKind.ShouldBe("photo"); fact.SourceId.ShouldBe(e.Source.Id); fact.PhotoSourceId.ShouldBe(e.Source.Id);
        fact.PhotoBatchId.ShouldBe(e.Source.BatchId); fact.TextSourceId.ShouldBeNull(); fact.CandidateOrdinal.ShouldBe(0);
        fact.InputRevisionId.ShouldBe(e.Input.Id); fact.ExtractionResultId.ShouldBe(e.Result!.Id); fact.SourceAuthorUserId.ShouldBe(111);
        fact.SourceMessageDbId.ShouldBe(e.Source.SourceMessageDbId!.Value); fact.TelegramMessageId.ShouldBe(e.Source.TelegramMessageId);
        fact.Revision.ShouldBe(1); fact.DeletedAt.ShouldBeNull(); fact.LastMutationKind.ShouldBe("photo_save");
        var candidate = await v.Context.Set<VetPhotoCandidate>().SingleAsync(Ct);
        candidate.EventId.ShouldBe(fact.Id); candidate.EventRevision.ShouldBe(1); candidate.State.ShouldBe("saved");
        candidate.Revision.ShouldBe(selected.CandidateRevision + 1); candidate.LastReviewId.ShouldBe(review.Id);
        var action = await v.Context.VetDiaryActions.SingleAsync(Ct); action.Id.ShouldBe(result.ActionId!.Value);
        action.ActorUserId.ShouldBe(222); action.Kind.ShouldBe("photo_save"); action.PhotoBatchId.ShouldBe(e.Source.BatchId);
        var outcome = JsonSerializer.Deserialize<VetPhotoActionOutcome>(action.OutcomeJson, Json).ShouldNotBeNull();
        outcome.EventIds.ShouldBe(result.EventIds); outcome.PhotoChanges.Count.ShouldBe(1);
        outcome.PhotoChanges[0].Before.State.ShouldBe("clear"); outcome.PhotoChanges[0].After.EventId.ShouldBe(fact.Id);
        var change = await v.Context.VetDiaryActionChanges.SingleAsync(Ct); change.BeforeJson.ShouldBeNull(); change.BeforeRevision.ShouldBeNull();
        change.EventId.ShouldBe(fact.Id); change.AfterRevision.ShouldBe(1);
        JsonSerializer.Deserialize<VetEventState>(change.AfterJson).ShouldBe(Fact(e));
        var accepted = await v.Context.Set<VetPhotoReview>().SingleAsync(Ct); accepted.State.ShouldBe("accepted");
        accepted.DecisionActorUserId.ShouldBe(222); accepted.ActionId.ShouldBe(action.Id);
        await using var restart = Open(); var replay = await restart.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review, 111), Ct);
        replay.Status.ShouldBe(VetMutationStatus.AlreadyApplied); replay.ActionId.ShouldBe(result.ActionId);
        replay.EventIds.ShouldBe(result.EventIds); replay.Revisions.ShouldBe(result.Revisions);
        (await restart.Context.VetEvents.CountAsync(Ct)).ShouldBe(1); (await restart.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(1);
        (await restart.Context.Set<VetPhotoCandidate>().SingleAsync(Ct)).Revision.ShouldBe(candidate.Revision);
    }

    [Theory]
    [InlineData("unseen")]
    [InlineData("partial")]
    [InlineData("failed")]
    [InlineData("bad_hash")]
    [InlineData("wrong_prompt")]
    [InlineData("absent_prompt")]
    [InlineData("count")]
    [InlineData("duplicate_index")]
    public async Task Unseen_partial_failed_or_corrupted_delivery_never_saves_any_subset(string failure)
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s); var selection = await Select(s, e);
        var review = await Review(s, e.Scope, [selection], deliver: failure is not ("unseen" or "partial" or "failed"));
        var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json).ShouldNotBeNull();
        if (failure == "partial") (await Photos(s).RecordPageDeliveryAsync(new(e.Scope, review.Id, review.Revision, review.OperationKey, 111),
            0, 500, Hash(pages[0]), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        if (failure == "failed") (await Photos(s).RecordPreviewFailureAsync(new(e.Scope, review.Id, review.Revision, review.OperationKey, 111), Ct))
            .Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        if (failure is "bad_hash" or "duplicate_index")
        {
            var rows = JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(review.DeliveredPagesJson, Json).ShouldNotBeNull();
            if (failure == "bad_hash") rows[0] = rows[0] with { TextHash = Hash("synthetic different page") };
            else rows[1] = rows[1] with { PageIndex = 0 };
            await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id).ExecuteUpdateAsync(u =>
                u.SetProperty(r => r.DeliveredPagesJson, JsonSerializer.Serialize(rows, Json)), Ct);
        }
        if (failure == "wrong_prompt") await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.AcceptancePromptMessageId, (int?)702), Ct);
        if (failure == "absent_prompt") await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.AcceptancePromptMessageId, (int?)null), Ct);
        if (failure == "count") await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.PageCount, pages.Length + 1), Ct);
        var candidates = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Stale);
        await AssertUnchanged(e, candidates, facts, 0);
    }

    [Theory]
    [InlineData("topic", VetMutationStatus.NotFound)]
    [InlineData("chat", VetMutationStatus.Refused)]
    [InlineData("actor", VetMutationStatus.Refused)]
    [InlineData("member_revoked", VetMutationStatus.Refused)]
    [InlineData("place_revoked", VetMutationStatus.Refused)]
    [InlineData("bot_disabled", VetMutationStatus.Refused)]
    [InlineData("null_topic", VetMutationStatus.Refused)]
    [InlineData("review_id", VetMutationStatus.NotFound)]
    [InlineData("review_revision", VetMutationStatus.Stale)]
    [InlineData("operation", VetMutationStatus.Stale)]
    [InlineData("empty_operation", VetMutationStatus.Stale)]
    [InlineData("prompt", VetMutationStatus.Stale)]
    [InlineData("review_kind", VetMutationStatus.Stale)]
    [InlineData("profile_revision", VetMutationStatus.Stale)]
    [InlineData("review_profile", VetMutationStatus.Stale)]
    [InlineData("review_batch", VetMutationStatus.Stale)]
    public async Task Acceptance_requires_exact_place_actor_profile_review_operation_and_prompt(string fence, VetMutationStatus expected)
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s); var review = await Review(s, e.Scope, [await Select(s, e)]);
        var request = Accept(e.Scope, review);
        if (fence == "topic") request = request with { Scope = e.Scope with { TopicId = 8 } };
        if (fence == "chat") request = request with { Scope = e.Scope with { ChatId = -101 } };
        if (fence == "actor") request = request with { ActorUserId = 333 };
        if (fence == "member_revoked") await s.Context.FamilyMembers.Where(m => m.TelegramUserId == 222).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        if (fence == "place_revoked") await s.Context.Places.Where(p => p.TopicId == 7).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlaceStatus.Denied), Ct);
        if (fence == "bot_disabled") await s.Context.Bots.ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BotStatus.Disabled), Ct);
        if (fence == "null_topic") request = request with { Scope = e.Scope with { TopicId = null } };
        if (fence == "review_id") request = request with { ReviewId = Guid.NewGuid() };
        if (fence == "review_revision") request = request with { ReviewRevision = request.ReviewRevision + 1 };
        if (fence == "operation") request = request with { OperationKey = Guid.NewGuid() };
        if (fence == "empty_operation") request = request with { OperationKey = Guid.Empty };
        if (fence == "prompt") request = request with { CallbackPromptMessageId = 702 };
        if (fence == "review_kind") await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Kind, "original_delete"), Ct);
        if (fence == "profile_revision") await s.Context.Set<VetProfile>().ExecuteUpdateAsync(u => u.SetProperty(p => p.Revision, p => p.Revision + 1), Ct);
        if (fence == "review_profile") await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.ProfileId, (long?)null), Ct);
        if (fence == "review_batch") await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.BatchReviewRevision, r => r.BatchReviewRevision + 1), Ct);
        var candidates = await SnapshotCandidates(s); var events = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(request, Ct)).Status.ShouldBe(expected);
        await AssertUnchanged(e, candidates, events, 0);
    }

    [Theory]
    [InlineData(111, VetMutationStatus.Applied)]
    [InlineData(222, VetMutationStatus.Refused)]
    public async Task Private_null_topic_review_acceptance_is_limited_to_the_exact_admitted_private_actor(long actor, VetMutationStatus expected)
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s, privateChat: true);
        e.Scope.TopicId.ShouldBeNull(); e.Scope.ChatId.ShouldBe(111);
        var selected = await Select(s, e); var review = await Review(s, e.Scope, [selected]);
        var request = Accept(e.Scope, review, actor) with { CallbackPromptMessageId = null };
        (await s.Diary.ApplyPhotoReviewAsync(request, Ct)).Status.ShouldBe(expected);
        await using var v = Open(); (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(actor == 111 ? 1 : 0);
        (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(actor == 111 ? 1 : 0);
        (await v.Context.Set<VetPhotoCandidate>().SingleAsync(Ct)).Revision.ShouldBe(selected.CandidateRevision + (actor == 111 ? 1 : 0));
        if (actor == 111) { var fact = await v.Context.VetEvents.SingleAsync(Ct); fact.ChatId.ShouldBe(111); fact.TopicId.ShouldBeNull();
            (await v.Context.VetDiaryActions.SingleAsync(Ct)).ActorUserId.ShouldBe(111); }
    }

    [Theory]
    [InlineData("unset")]
    [InlineData("family")]
    [InlineData("bot_db")]
    [InlineData("telegram_bot")]
    public async Task Invalid_family_or_bot_scope_fails_closed_before_querying_photo_values(string fence)
    {
        await SeedAsync(); Evidence e; VetPhotoReview review;
        await using (var seed = Open()) { e = await Prepare(seed); review = await Review(seed, e.Scope, [await Select(seed, e)]); }
        await using var s = fence == "unset" ? Unscoped() : Open(); var scope = e.Scope;
        if (fence == "family") scope = scope with { FamilyId = FamilyId + 1000 };
        if (fence == "bot_db") scope = scope with { BotDbId = Bot.BotDbId + 1000 };
        if (fence == "telegram_bot") scope = scope with { TelegramBotId = Bot.TelegramBotId + 1000 };
        var error = await Should.ThrowAsync<InvalidOperationException>(() => s.Diary.ApplyPhotoReviewAsync(Accept(scope, review), Ct));
        error.Message.ShouldBe(fence is "unset" or "family" ? "Vet scope is not active." : "Vet bot scope is invalid.");
        var proofError = await Should.ThrowAsync<InvalidOperationException>(() => s.Diary.GetPhotoCollisionProofAsync(scope, e.Profile.Id, e.Candidate.Id, Fact(e), Ct));
        proofError.Message.ShouldBe(error.Message);
        await using var v = Open(); (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(0); (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
    }

    [Theory]
    [InlineData("profile_id", VetMutationStatus.Refused)]
    [InlineData("profile_revision", VetMutationStatus.Refused)]
    [InlineData("batch_id", VetMutationStatus.Stale)]
    [InlineData("batch_revision", VetMutationStatus.Stale)]
    [InlineData("candidate_id", VetMutationStatus.Stale)]
    [InlineData("candidate_revision", VetMutationStatus.Stale)]
    [InlineData("source_id", VetMutationStatus.Stale)]
    [InlineData("pointer", VetMutationStatus.Stale)]
    [InlineData("ordinal", VetMutationStatus.Stale)]
    [InlineData("input", VetMutationStatus.Refused)]
    [InlineData("result", VetMutationStatus.Refused)]
    [InlineData("candidate_result", VetMutationStatus.Stale)]
    [InlineData("reference", VetMutationStatus.Stale)]
    [InlineData("reference_revision", VetMutationStatus.Stale)]
    [InlineData("reference_state", VetMutationStatus.Stale)]
    [InlineData("reference_omitted", VetMutationStatus.Stale)]
    [InlineData("event", VetMutationStatus.Stale)]
    [InlineData("event_revision", VetMutationStatus.Stale)]
    [InlineData("collision", VetMutationStatus.Stale)]
    public async Task Fully_delivered_selection_cannot_substitute_any_stored_identity_or_revision(string fence, VetMutationStatus expected)
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s); var item = await Select(s, e);
        item = fence switch
        {
            "profile_id" => item with { ProfileId = item.ProfileId + 1000 }, "profile_revision" => item with { ProfileRevision = item.ProfileRevision + 1 },
            "batch_id" => item with { BatchId = Guid.NewGuid() }, "batch_revision" => item with { BatchReviewRevision = item.BatchReviewRevision + 1 },
            "candidate_id" => item with { CandidateId = Guid.NewGuid() }, "candidate_revision" => item with { CandidateRevision = item.CandidateRevision + 1 },
            "source_id" => item with { SourceId = Guid.NewGuid() }, "pointer" => item with { ExpectedCurrentInputId = Guid.NewGuid() },
            "ordinal" => item with { ExpectedSourceOrdinal = item.ExpectedSourceOrdinal + 1 }, "input" => item with { InputRevisionId = Guid.NewGuid() },
            "result" => item with { ExtractionResultId = Guid.NewGuid() }, "candidate_result" => item with { ExpectedCandidateExtractionId = Guid.NewGuid() },
            "reference" => item with { OriginalReferenceId = Guid.NewGuid() }, "reference_revision" => item with { OriginalReferenceRevision = item.OriginalReferenceRevision + 1 },
            "reference_state" => item with { OriginalReferenceState = "deleted" },
            "reference_omitted" => item with { OriginalReferenceId = null, OriginalReferenceRevision = null, OriginalReferenceState = null },
            "event" => item with { EventId = 1000 }, "event_revision" => item with { EventRevision = 1 },
            "collision" => item with { CollisionProof = item.CollisionProof with { Fingerprint = Hash("synthetic stale collision proof") } },
            _ => throw new InvalidOperationException("Synthetic test fence is unknown.")
        };
        var review = await Review(s, e.Scope, [item], batchOverride: e.Source.BatchId);
        var candidates = await SnapshotCandidates(s); var events = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review), Ct)).Status.ShouldBe(expected);
        await AssertUnchanged(e, candidates, events, 0);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("unit")]
    [InlineData("product")]
    [InlineData("time")]
    [InlineData("local_time")]
    [InlineData("zone")]
    [InlineData("time_evidence")]
    [InlineData("value_evidence")]
    [InlineData("source_kind")]
    [InlineData("author")]
    [InlineData("message")]
    [InlineData("telegram_message")]
    [InlineData("source")]
    [InlineData("photo_source")]
    [InlineData("photo_batch")]
    [InlineData("text_source")]
    [InlineData("ordinal")]
    [InlineData("input")]
    [InlineData("result")]
    [InlineData("deleted")]
    public async Task Fully_shown_but_unvalidated_or_false_fact_provenance_is_refused_without_writing(string field)
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s); var state = Fact(e);
        state = field switch
        {
            "value" => state with { Value = 5.7m }, "unit" => state with { Unit = "mg/dL" }, "product" => state with { Product = "synthetic product" },
            "time" => state with { OccurredAt = Measured.AddMinutes(1) }, "local_time" => state with { LocalTime = "2031-05-11 10:21:00" },
            "zone" => state with { TimeZoneSnapshot = "UTC" }, "time_evidence" => state with { OccurredAtSource = "human_correction" },
            "value_evidence" => state with { ValueUnitSource = "caption/image" }, "source_kind" => state with { SourceKind = "text" },
            "author" => state with { SourceAuthorUserId = 222 }, "message" => state with { SourceMessageDbId = state.SourceMessageDbId + 1000 },
            "telegram_message" => state with { TelegramMessageId = state.TelegramMessageId + 1000 }, "source" => state with { SourceId = Guid.NewGuid() },
            "photo_source" => state with { PhotoSourceId = Guid.NewGuid() }, "photo_batch" => state with { PhotoBatchId = Guid.NewGuid() },
            "text_source" => state with { TextSourceId = Guid.NewGuid() }, "ordinal" => state with { CandidateOrdinal = 1 },
            "input" => state with { InputRevisionId = Guid.NewGuid() }, "result" => state with { ExtractionResultId = Guid.NewGuid() },
            "deleted" => state with { DeletedAt = Now }, _ => throw new InvalidOperationException("Synthetic test field is unknown.")
        };
        var review = await Review(s, e.Scope, [await Select(s, e, state: state)]);
        var candidates = await SnapshotCandidates(s); var events = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Refused);
        await AssertUnchanged(e, candidates, events, 0);
    }

    [Fact]
    public async Task Explicit_reviewed_human_correction_saves_fractional_changed_value_time_and_human_provenance()
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s);
        var state = Fact(e) with { Value = 5.65m, OccurredAt = Measured.AddMinutes(1), LocalTime = "2031-05-11 10:21:00",
            OccurredAtSource = "human_correction", ValueUnitSource = "human_correction" };
        var context = new VetPhotoContext("5.65", "mmol/L", 2031, 5, 11, "10:21", "+00:00", CorrectionApproved: true);
        var selection = (await Select(s, e, state: state)) with { Context = context };
        var review = await Review(s, e.Scope, [selection]);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Applied);
        await using var v = Open(); var fact = await v.Context.VetEvents.SingleAsync(Ct);
        fact.Value.ShouldBe(5.65m); fact.OccurredAt.ShouldBe(Measured.AddMinutes(1)); fact.LocalTime.ShouldBe("2031-05-11 10:21:00");
        fact.OccurredAtSource.ShouldBe("human_correction"); fact.ValueUnitSource.ShouldBe("human_correction");
        var candidate = await v.Context.Set<VetPhotoCandidate>().SingleAsync(Ct); candidate.ManuallyCorrected.ShouldBeTrue();
        JsonSerializer.Deserialize<VetPhotoContext>(candidate.CorrectionProvenanceJson, Json).ShouldBe(context);
        fact.SourceAuthorUserId.ShouldBe(111); (await v.Context.VetDiaryActions.SingleAsync(Ct)).ActorUserId.ShouldBe(222);
    }

    [Fact]
    public async Task Two_members_racing_one_review_get_one_atomic_action_and_exact_restart_replay()
    {
        await SeedAsync(); Evidence e; VetPhotoReview review;
        await using (var seed = Open()) { e = await Prepare(seed); review = await Review(seed, e.Scope, [await Select(seed, e)]); }
        var hold = new HoldActionInsert(); var observe = new ObserveBotLock(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var first = Open(interceptor: hold); await using var second = Open(interceptor: observe);
        var firstTask = first.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review, 222), timeout.Token);
        Task<VetMutationResult>? secondTask = null;
        try
        {
            await hold.Entered.Task.WaitAsync(timeout.Token);
            secondTask = second.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review, 111), timeout.Token);
            await observe.Entered.Task.WaitAsync(timeout.Token);
        }
        finally { hold.Release(); }
        var winner = await firstTask; var loser = await secondTask.ShouldNotBeNull();
        winner.Status.ShouldBe(VetMutationStatus.Applied); loser.Status.ShouldBe(VetMutationStatus.AlreadyApplied);
        loser.ActionId.ShouldBe(winner.ActionId); loser.EventIds.ShouldBe(winner.EventIds); loser.Revisions.ShouldBe(winner.Revisions);
        await using var restart = Open(); (await restart.Context.VetEvents.CountAsync(Ct)).ShouldBe(1);
        (await restart.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(1);
        (await restart.Context.VetDiaryActions.SingleAsync(Ct)).ActorUserId.ShouldBe(222);
        (await restart.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.AlreadyApplied);
        (await restart.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Reviewed_canonical_and_links_create_one_owned_fact_and_stable_duplicate_handles()
    {
        await SeedAsync(); await using var s = Open(); var a = await Prepare(s, 1, collection: true); var b = await Prepare(s, 2);
        var canonical = await Select(s, a, duplicate: "canonical");
        var linked = (await Select(s, b, "link", duplicate: "same")) with { LinkCandidateId = a.Candidate.Id };
        var review = await Review(s, a.Scope, [linked, canonical]);
        var result = await s.Diary.ApplyPhotoReviewAsync(Accept(a.Scope, review), Ct); result.Status.ShouldBe(VetMutationStatus.Applied);
        result.EventIds.Count.ShouldBe(1); await using var v = Open(); var fact = await v.Context.VetEvents.SingleAsync(Ct);
        var candidates = await v.Context.Set<VetPhotoCandidate>().OrderBy(c => c.SourceId).ToArrayAsync(Ct);
        var owner = candidates.Single(c => c.Id == a.Candidate.Id); var link = candidates.Single(c => c.Id == b.Candidate.Id);
        owner.State.ShouldBe("saved"); owner.EventId.ShouldBe(fact.Id); owner.EventRevision.ShouldBe(1);
        link.State.ShouldBe("linked"); link.EventId.ShouldBeNull(); link.EventRevision.ShouldBeNull();
        link.DuplicateSourceId.ShouldBe(a.Source.Id); link.DuplicateEventId.ShouldBe(fact.Id); link.DuplicateEventRevision.ShouldBe(1);
        fact.SourceId.ShouldBe(a.Source.Id); fact.InputRevisionId.ShouldBe(a.Input.Id); fact.ExtractionResultId.ShouldBe(a.Result!.Id);
        (await v.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(1);
        var outcome = JsonSerializer.Deserialize<VetPhotoActionOutcome>((await v.Context.VetDiaryActions.SingleAsync(Ct)).OutcomeJson, Json).ShouldNotBeNull();
        outcome.PhotoChanges.Count.ShouldBe(2); outcome.PhotoChanges.Select(x => x.CandidateId).Order().ShouldBe(new[] { a.Candidate.Id, b.Candidate.Id }.Order());
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("event")]
    [InlineData("bytes")]
    public async Task Canonical_group_never_bypasses_matching_external_candidate_fact_or_retained_bytes(string collision)
    {
        await SeedAsync(); await using var s = Open(); var a = await Prepare(s, 1, sameBytes: collision == "bytes", collection: true);
        var b = await Prepare(s, 2); var external = await Prepare(s, 3, sameBytes: collision == "bytes");
        if (collision == "event")
        {
            var extReview = await Review(s, external.Scope, [await Select(s, external, duplicate: "separate")]);
            (await s.Diary.ApplyPhotoReviewAsync(Accept(external.Scope, extReview), Ct)).Status.ShouldBe(VetMutationStatus.Applied);
        }
        if (collision == "bytes") await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == external.Candidate.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, "excluded").SetProperty(c => c.EffectiveJson, "{}"), Ct);
        var canonical = await Select(s, a, duplicate: "canonical"); var link = (await Select(s, b, "link", duplicate: "same")) with { LinkCandidateId = a.Candidate.Id };
        canonical.CollisionProof.HasCollisions.ShouldBeTrue(); var review = await Review(s, a.Scope, [canonical, link]);
        var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(a.Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Refused);
        await AssertUnchanged(a, before, facts, collision == "event" ? 1 : 0);
    }

    [Theory]
    [InlineData("unresolved", VetMutationStatus.Refused, 0)]
    [InlineData("same", VetMutationStatus.Refused, 0)]
    [InlineData("separate", VetMutationStatus.Applied, 2)]
    public async Task Matching_measurements_need_explicit_separate_decisions_to_create_two_facts(string decision, VetMutationStatus expected, int count)
    {
        await SeedAsync(); await using var s = Open(); var a = await Prepare(s, 1, collection: true); var b = await Prepare(s, 2);
        var review = await Review(s, a.Scope, [await Select(s, a, duplicate: decision), await Select(s, b, duplicate: decision)]);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(a.Scope, review), Ct)).Status.ShouldBe(expected);
        await using var v = Open(); (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(count);
        (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(count == 0 ? 0 : 1);
        (await v.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(count);
        if (count == 2) (await v.Context.VetEvents.Select(x => x.SourceId).OrderBy(x => x).ToArrayAsync(Ct)).ShouldBe(new[] { a.Source.Id, b.Source.Id }.Order());
    }

    [Fact]
    public async Task Fact_created_after_preview_stales_collision_proof_without_partial_second_save()
    {
        await SeedAsync(); await using var s = Open(); var a = await Prepare(s, 1); var b = await Prepare(s, 2);
        var first = await Review(s, a.Scope, [await Select(s, a, duplicate: "separate")]);
        var second = await Review(s, b.Scope, [await Select(s, b, duplicate: "separate")]);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(a.Scope, first), Ct)).Status.ShouldBe(VetMutationStatus.Applied);
        var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        await using var restart = Open(); (await restart.Diary.ApplyPhotoReviewAsync(Accept(b.Scope, second), Ct)).Status.ShouldBe(VetMutationStatus.Stale);
        await AssertUnchanged(b, before, facts, 1);
        (await restart.Context.Set<VetPhotoReview>().SingleAsync(x => x.Id == second.Id, Ct)).State.ShouldBe("preview");
    }

    [Fact]
    public async Task New_duplicate_winner_commits_while_second_confirmation_waits_then_second_proof_stales_atomically()
    {
        await SeedAsync(); Evidence a; Evidence b; VetPhotoReview firstReview; VetPhotoReview secondReview;
        await using (var seed = Open())
        {
            a = await Prepare(seed, 1); b = await Prepare(seed, 2);
            firstReview = await Review(seed, Scope, [await Select(seed, a, duplicate: "separate")]);
            secondReview = await Review(seed, Scope, [await Select(seed, b, duplicate: "separate")]);
        }
        var hold = new HoldActionInsert(); var observe = new ObserveBotLock(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var first = Open(interceptor: hold); await using var second = Open(interceptor: observe);
        var winnerTask = first.Diary.ApplyPhotoReviewAsync(Accept(Scope, firstReview), timeout.Token); Task<VetMutationResult>? loserTask = null;
        try
        {
            await hold.Entered.Task.WaitAsync(timeout.Token);
            loserTask = second.Diary.ApplyPhotoReviewAsync(Accept(Scope, secondReview, 111), timeout.Token);
            await observe.Entered.Task.WaitAsync(timeout.Token);
        }
        finally { hold.Release(); }
        var winner = await winnerTask; var loser = await loserTask.ShouldNotBeNull();
        winner.Status.ShouldBe(VetMutationStatus.Applied); loser.Status.ShouldBe(VetMutationStatus.Stale);
        loser.ActionId.ShouldBeNull(); loser.EventIds.ShouldBeEmpty(); loser.Revisions.ShouldBeEmpty();
        await using var v = Open(); (await v.Context.VetEvents.SingleAsync(Ct)).SourceId.ShouldBe(a.Source.Id);
        (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(1); (await v.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(1);
        var protectedCandidate = await v.Context.Set<VetPhotoCandidate>().SingleAsync(c => c.Id == b.Candidate.Id, Ct);
        protectedCandidate.State.ShouldBe("clear"); protectedCandidate.Revision.ShouldBe(b.Candidate.Revision); protectedCandidate.EventId.ShouldBeNull();
        (await v.Context.Set<VetPhotoReview>().SingleAsync(r => r.Id == secondReview.Id, Ct)).State.ShouldBe("preview");
    }

    [Fact]
    public async Task Reviewed_existing_same_place_link_writes_only_auditable_duplicate_handles_and_selected_evidence()
    {
        await SeedAsync(); await using var s = Open(); var target = await Prepare(s, 1);
        var targetReview = await Review(s, Scope, [await Select(s, target)]);
        var targetSaved = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, targetReview), Ct); targetSaved.Status.ShouldBe(VetMutationStatus.Applied);
        var own = await Prepare(s, 2); var selected = (await Select(s, own, "link", duplicate: "same")) with
            { LinkEventId = targetSaved.EventIds.Single(), LinkEventRevision = 1 };
        var review = await Review(s, Scope, [selected]); var result = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct);
        result.Status.ShouldBe(VetMutationStatus.Applied); result.EventIds.ShouldBeEmpty(); result.Revisions.ShouldBeEmpty();
        await using var v = Open(); (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(1); (await v.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(1);
        var candidate = await v.Context.Set<VetPhotoCandidate>().SingleAsync(c => c.Id == own.Candidate.Id, Ct);
        candidate.State.ShouldBe("linked"); candidate.EventId.ShouldBeNull(); candidate.EventRevision.ShouldBeNull();
        candidate.DuplicateEventId.ShouldBe(targetSaved.EventIds.Single()); candidate.DuplicateEventRevision.ShouldBe(1);
        candidate.DuplicateSourceId.ShouldBe(target.Source.Id); candidate.InputRevisionId.ShouldBe(own.Input.Id); candidate.ExtractionResultId.ShouldBe(own.Result!.Id);
        JsonSerializer.Deserialize<VetEventState>(candidate.EffectiveJson, Json).ShouldBe(Fact(own));
        var action = await v.Context.VetDiaryActions.SingleAsync(x => x.Id == result.ActionId, Ct);
        action.ActorUserId.ShouldBe(222); JsonSerializer.Deserialize<VetPhotoActionOutcome>(action.OutcomeJson, Json).ShouldNotBeNull().PhotoChanges.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Collision_query_streams_real_retained_references_and_ignores_foreign_place_changes()
    {
        await SeedAsync(); await using var s = Open(); var own = await Prepare(s, 1); var other = await Prepare(s, 2, topic: 8);
        var proof = (await s.Diary.GetPhotoCollisionProofAsync(own.Scope, own.Profile.Id, own.Candidate.Id, Fact(own), Ct)).ShouldNotBeNull();
        proof.HasCollisions.ShouldBeFalse(); proof.Fingerprint.Length.ShouldBe(64);
        await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == other.Candidate.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.Revision, c => c.Revision + 1), Ct);
        (await s.Diary.GetPhotoCollisionProofAsync(own.Scope, own.Profile.Id, own.Candidate.Id, Fact(own), Ct)).ShouldBe(proof);
        (await s.Diary.GetPhotoCollisionProofAsync(own.Scope, own.Profile.Id, other.Candidate.Id, Fact(own), Ct)).ShouldBeNull();
        (await s.Diary.GetPhotoCollisionProofAsync(own.Scope, own.Profile.Id + 1000, own.Candidate.Id, Fact(own), Ct)).ShouldBeNull();
        var samePlace = await Prepare(s, 3);
        var changed = (await s.Diary.GetPhotoCollisionProofAsync(own.Scope, own.Profile.Id, own.Candidate.Id, Fact(own), Ct)).ShouldNotBeNull();
        changed.HasCollisions.ShouldBeTrue(); changed.Fingerprint.ShouldNotBe(proof.Fingerprint);
        (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0); (await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
        samePlace.Scope.ShouldBe(own.Scope);
    }

    [Theory]
    [InlineData("target_place")]
    [InlineData("target_revision")]
    [InlineData("target_deleted")]
    [InlineData("target_value")]
    public async Task Link_requires_active_exact_place_matching_target_and_exact_event_revision(string fence)
    {
        await SeedAsync(); await using var s = Open(); var target = await Prepare(s, 1, topic: fence == "target_place" ? 8 : 7);
        var targetReview = await Review(s, target.Scope, [await Select(s, target)]);
        var saved = await s.Diary.ApplyPhotoReviewAsync(Accept(target.Scope, targetReview), Ct); saved.Status.ShouldBe(VetMutationStatus.Applied);
        var own = await Prepare(s, 2); var item = (await Select(s, own, "link", duplicate: "same")) with { LinkEventId = saved.EventIds.Single(), LinkEventRevision = 1 };
        if (fence == "target_revision") item = item with { LinkEventRevision = 2 };
        if (fence == "target_deleted") await s.Context.VetEvents.Where(e => e.Id == saved.EventIds.Single()).ExecuteUpdateAsync(u => u.SetProperty(e => e.DeletedAt, (DateTimeOffset?)Now), Ct);
        if (fence == "target_value") await s.Context.VetEvents.Where(e => e.Id == saved.EventIds.Single()).ExecuteUpdateAsync(u => u.SetProperty(e => e.Value, 5.7m), Ct);
        var review = await Review(s, own.Scope, [item]); var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(own.Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Stale);
        await AssertUnchanged(own, before, facts, 1);
    }

    [Theory]
    [InlineData("excluded", false)]
    [InlineData("cancelled", false)]
    [InlineData("deleted", false)]
    [InlineData("excluded", true)]
    [InlineData("cancelled", true)]
    [InlineData("deleted", true)]
    public async Task Protected_candidate_requires_shown_explicit_restoration_and_preserves_stable_identity(string state, bool restore)
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s);
        await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == e.Candidate.Id).ExecuteUpdateAsync(u =>
            u.SetProperty(c => c.State, state).SetProperty(c => c.RequiresExplicitRestoration, true), Ct);
        var selection = await Select(s, e, restore: restore); var review = await Review(s, e.Scope, [selection], VetPhotoReviewKind.Correction);
        var result = await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review), Ct);
        result.Status.ShouldBe(restore ? VetMutationStatus.Applied : VetMutationStatus.Refused);
        await using var v = Open(); var c = await v.Context.Set<VetPhotoCandidate>().SingleAsync(Ct); c.Id.ShouldBe(e.Candidate.Id); c.SourceId.ShouldBe(e.Source.Id);
        c.State.ShouldBe(restore ? "saved" : state); c.RequiresExplicitRestoration.ShouldBe(!restore);
        c.Revision.ShouldBe(selection.CandidateRevision + (restore ? 1 : 0)); (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(restore ? 1 : 0);
    }

    [Theory]
    [InlineData("exclude")]
    [InlineData("cancel")]
    public async Task No_result_or_original_is_needed_for_shown_zero_fact_exclude_or_cancel_action(string disposition)
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s, image: false);
        e.Result.ShouldBeNull(); e.Original.ShouldBeNull(); var selected = await Select(s, e, disposition);
        var review = await Review(s, e.Scope, [selected]); var result = await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review), Ct);
        result.Status.ShouldBe(VetMutationStatus.Applied); result.EventIds.ShouldBeEmpty(); result.Revisions.ShouldBeEmpty();
        await using var v = Open(); (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(0); (await v.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(0);
        var action = await v.Context.VetDiaryActions.SingleAsync(Ct); action.ActorUserId.ShouldBe(222);
        var outcome = JsonSerializer.Deserialize<VetPhotoActionOutcome>(action.OutcomeJson, Json).ShouldNotBeNull();
        outcome.PhotoChanges.Count.ShouldBe(1); outcome.PhotoChanges[0].Before.State.ShouldBe(e.Candidate.State);
        outcome.PhotoChanges[0].After.State.ShouldBe(disposition == "exclude" ? "excluded" : "cancelled");
        outcome.PhotoChanges[0].After.RequiresExplicitRestoration.ShouldBeTrue();
        (await v.Context.Set<VetPhotoReview>().SingleAsync(Ct)).State.ShouldBe("accepted");
        (await v.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(0); (await v.Context.Set<VetPhotoOriginalReference>().CountAsync(Ct)).ShouldBe(0);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review, 111), Ct)).Status.ShouldBe(VetMutationStatus.AlreadyApplied);
        (await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData("exclude")]
    [InlineData("cancel")]
    public async Task Exclude_or_cancel_cannot_silently_delete_a_saved_fact(string disposition)
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s);
        var savedReview = await Review(s, e.Scope, [await Select(s, e)]);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, savedReview), Ct)).Status.ShouldBe(VetMutationStatus.Applied);
        var review = await Review(s, e.Scope, [await Select(s, e, disposition)], VetPhotoReviewKind.Correction);
        var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Refused);
        await AssertUnchanged(e, before, facts, 1);
    }

    private async Task<VetPhotoAdmission> Edit(VetTestSession s, Evidence e)
    {
        var message = Text("synthetic edited caption", e.Source.TelegramMessageId, 111, e.Scope.TopicId!.Value)
            with { Kind = MessageKind.Photo, IsEdit = true, EditedAt = Now.AddSeconds(1) };
        var result = await Photos(s).AdmitAsync(e.Scope, message, 10000 + e.Source.TelegramMessageId,
            new(e.Input.FileId, e.Input.FileUniqueId, e.Input.FileName, e.Input.ReportedMimeType,
                e.Input.ReportedSize, e.Input.ReportedWidth, e.Input.ReportedHeight), null, Ct);
        result.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted); result.Input!.Id.ShouldNotBe(e.Input.Id); return result;
    }

    [Fact]
    public async Task Deleting_saved_old_evidence_after_caption_edit_does_not_require_reprocess_window_or_change_saved_measurement()
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s);
        var savedReview = await Review(s, e.Scope, [await Select(s, e)]);
        var saved = await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, savedReview), Ct); saved.Status.ShouldBe(VetMutationStatus.Applied);
        var edit = await Edit(s, e); var delete = Fact(e) with { DeletedAt = Now, DeleteReason = "photo_delete" };
        var selected = await Select(s, e, "delete", delete); selected.ExpectedCurrentInputId.ShouldBe(edit.Input!.Id);
        var review = await Review(s, e.Scope, [selected], VetPhotoReviewKind.Correction); review.RunWindowId.ShouldBeNull();
        var result = await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review), Ct); result.Status.ShouldBe(VetMutationStatus.Applied);
        result.EventIds.ShouldBe(saved.EventIds); result.Revisions.Single().Revision.ShouldBe(2);
        await using var v = Open(); var fact = await v.Context.VetEvents.SingleAsync(Ct);
        fact.Id.ShouldBe(saved.EventIds.Single()); fact.Value.ShouldBe(5.6m); fact.OccurredAt.ShouldBe(Measured);
        fact.InputRevisionId.ShouldBe(e.Input.Id); fact.ExtractionResultId.ShouldBe(e.Result!.Id);
        fact.DeletedAt.ShouldBe(Now); fact.DeleteReason.ShouldBe("photo_delete"); fact.DeletedByUserId.ShouldBe(222);
        var candidate = await v.Context.Set<VetPhotoCandidate>().SingleAsync(Ct); candidate.Id.ShouldBe(e.Candidate.Id);
        candidate.State.ShouldBe("deleted"); candidate.RequiresExplicitRestoration.ShouldBeTrue(); candidate.EventId.ShouldBe(fact.Id); candidate.EventRevision.ShouldBe(2);
        (await v.Context.Set<VetPhotoSource>().SingleAsync(Ct)).CurrentInputRevisionId.ShouldBe(edit.Input.Id);
        (await v.Context.Set<VetPhotoRunWindow>().CountAsync(Ct)).ShouldBe(0); (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(2);
        var change = await v.Context.VetDiaryActionChanges.SingleAsync(x => x.ActionId == result.ActionId, Ct);
        change.BeforeRevision.ShouldBe(1); change.AfterRevision.ShouldBe(2);
        JsonSerializer.Deserialize<VetEventState>(change.BeforeJson!).ShouldBe(Fact(e));
        JsonSerializer.Deserialize<VetEventState>(change.AfterJson).ShouldBe(delete with { DeletedByUserId = 222 });
    }

    [Theory]
    [InlineData("event_revision")]
    [InlineData("event_value")]
    [InlineData("candidate_revision")]
    public async Task Saved_fact_or_candidate_change_after_delete_preview_protects_the_new_revision(string fence)
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s);
        var savedReview = await Review(s, e.Scope, [await Select(s, e)]);
        var saved = await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, savedReview), Ct);
        var delete = Fact(e) with { DeletedAt = Now, DeleteReason = "photo_delete" };
        var review = await Review(s, e.Scope, [await Select(s, e, "delete", delete)], VetPhotoReviewKind.Correction);
        if (fence == "event_revision") await s.Context.VetEvents.ExecuteUpdateAsync(u => u.SetProperty(x => x.Revision, 2), Ct);
        if (fence == "event_value") await s.Context.VetEvents.ExecuteUpdateAsync(u => u.SetProperty(x => x.Value, 5.7m), Ct);
        if (fence == "candidate_revision") await s.Context.Set<VetPhotoCandidate>().ExecuteUpdateAsync(u => u.SetProperty(x => x.Revision, x => x.Revision + 1), Ct);
        var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review), Ct)).Status.ShouldBe(fence == "event_value" ? VetMutationStatus.Refused : VetMutationStatus.Stale);
        await AssertUnchanged(e, before, facts, 1); saved.EventIds.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(0, VetMutationStatus.Refused)]
    [InlineData(2, VetMutationStatus.Refused)]
    [InlineData(51, VetMutationStatus.Refused)]
    public async Task Empty_duplicate_or_oversized_persisted_selection_is_refused_atomically(int count, VetMutationStatus expected)
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s); var item = await Select(s, e);
        var review = await Review(s, e.Scope, [item]);
        await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id).ExecuteUpdateAsync(u =>
            u.SetProperty(r => r.SelectionJson, JsonSerializer.Serialize(Enumerable.Repeat(item, count).ToArray(), Json))
                .SetProperty(r => r.Fingerprint, Hash(JsonSerializer.Serialize(Enumerable.Repeat(item, count).ToArray(), Json))), Ct);
        var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(e.Scope, review), Ct)).Status.ShouldBe(expected); await AssertUnchanged(e, before, facts, 0);
    }

    [Fact]
    public async Task Fifty_selected_candidates_are_all_applied_as_one_shown_atomic_action_without_truncation()
    {
        await SeedAsync(); await using var s = Open(); var evidence = new List<Evidence>();
        for (var i = 1; i <= 50; i++) evidence.Add(await Prepare(s, i, collection: i == 1));
        var selected = new List<VetPhotoDiarySelection>();
        foreach (var e in evidence) selected.Add(await Select(s, e, duplicate: "separate"));
        var review = await Review(s, Scope, selected.ToArray()); var result = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct);
        result.Status.ShouldBe(VetMutationStatus.Applied); result.EventIds.Count.ShouldBe(50); result.EventIds.Distinct().Count().ShouldBe(50);
        await using var v = Open(); (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(50); (await v.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(50);
        (await v.Context.Set<VetPhotoCandidate>().CountAsync(c => c.State == "saved", Ct)).ShouldBe(50); (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(1);
        var outcome = JsonSerializer.Deserialize<VetPhotoActionOutcome>((await v.Context.VetDiaryActions.SingleAsync(Ct)).OutcomeJson, Json).ShouldNotBeNull();
        outcome.PhotoChanges.Count.ShouldBe(50); outcome.EventIds.Order().ShouldBe(result.EventIds.Order());
        outcome.PhotoChanges.Select(x => x.CandidateId).Order().ShouldBe(evidence.Select(x => x.Candidate.Id).Order());
    }

    [Fact]
    public async Task Invalid_last_selected_item_prevents_valid_first_item_and_all_action_side_effects()
    {
        await SeedAsync(); await using var s = Open(); var a = await Prepare(s, 1, collection: true); var b = await Prepare(s, 2);
        var first = await Select(s, a, duplicate: "separate"); var last = (await Select(s, b, duplicate: "separate")) with { CandidateRevision = b.Candidate.Revision + 1 };
        var review = await Review(s, Scope, [first, last]); var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Stale); await AssertUnchanged(a, before, facts, 0);
    }

    [Fact]
    public async Task Candidate_write_failure_rolls_back_fact_action_review_and_revisions_then_same_context_retry_succeeds()
    {
        await SeedAsync(); Evidence e; VetPhotoReview review; string before; string facts;
        await using (var seed = Open()) { e = await Prepare(seed); review = await Review(seed, Scope, [await Select(seed, e)]); before = await SnapshotCandidates(seed); facts = await SnapshotEvents(seed); }
        var failure = new FailCandidateWrite(); await using var s = Open(interceptor: failure);
        var error = await Should.ThrowAsync<DbUpdateException>(() => s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct));
        error.InnerException.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("synthetic atomic candidate failure"); failure.Hits.ShouldBe(1);
        await AssertUnchanged(e, before, facts, 0);
        await using (var v = Open()) { (await v.Context.Set<VetPhotoReview>().SingleAsync(Ct)).State.ShouldBe("preview");
            (await v.Context.Set<VetPhotoBatch>().SingleAsync(Ct)).ReviewRevision.ShouldBe(review.BatchReviewRevision!.Value); }
        var retried = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct); retried.Status.ShouldBe(VetMutationStatus.Applied);
        await using var restart = Open(); var replay = await restart.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct);
        replay.Status.ShouldBe(VetMutationStatus.AlreadyApplied); replay.EventIds.ShouldBe(retried.EventIds);
        (await restart.Context.VetEvents.CountAsync(Ct)).ShouldBe(1); (await restart.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(1);
        (await restart.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(1);
    }

    private T Scoped<T>(VetTestSession s, VetDiaryScope scope, T value) where T : class
    {
        var entry = s.Context.Entry(value); entry.Property("FamilyId").CurrentValue = scope.FamilyId;
        entry.Property("BotDbId").CurrentValue = scope.BotDbId; entry.Property("TelegramBotId").CurrentValue = scope.TelegramBotId;
        entry.Property("ChatId").CurrentValue = scope.ChatId; entry.Property("TopicId").CurrentValue = scope.TopicId; return value;
    }
    private sealed record Comparison(Evidence Evidence, VetPhotoRunWindow Window, Guid InitialResultId, Evidence? OtherEvidence = null);
    private async Task<Comparison> Compare(VetTestSession s, Evidence prior, bool historical, Evidence? other = null)
    {
        if (historical) await Edit(s, prior);
        var scope = prior.Scope;
        var profile = await s.Profiles.GetOrCreateAsync(scope.FamilyId, scope.BotDbId, Ct);
        var refs = other == null ? new[] { prior.Original!.Id } : new[] { prior.Original!.Id, other.Original!.Id };
        var staged = await Photos(s).StageRunAsync(new(scope, Guid.NewGuid(), 111, profile.Revision,
            VetPhotoRunPurpose.Reprocess, VetPhotoRunSelectionMode.Selected, "gpt-6.1-sol", "codex-cli", ReferenceIds: refs), Ct);
        staged.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var selection = staged.Review.ShouldNotBeNull();
        var selectionHandle = new VetPhotoReviewHandle(scope, selection.Id, selection.Revision, selection.OperationKey, 111);
        var pages = JsonSerializer.Deserialize<string[]>(selection.PreviewPagesJson, Json)!;
        for (var i = 0; i < pages.Length; i++)
            (await Photos(s).RecordPageDeliveryAsync(selectionHandle, i, 6000 + i, Hash(pages[i]), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Photos(s).CompleteDeliveryAsync(selectionHandle, 5999 + pages.Length, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        selection = (await Photos(s).ReadPreviewAsync(scope, selection.Id, 111, Ct)).ShouldNotBeNull();
        var handle = new VetPhotoRunHandle(scope, staged.Run!.Id, 111);
        (await Photos(s).ApproveRunAsync(handle, selectionHandle with { CallbackPromptMessageId = selection.AcceptancePromptMessageId }, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var continued = await Photos(s).ContinueRunAsync(handle, Ct); continued.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var window = continued.Window.ShouldNotBeNull();
        var snapshots = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(window.SelectionJson, Json)!;
        var completed = new Dictionary<Guid, Evidence>();
        foreach (var snapshot in snapshots)
        {
            var fixture = snapshot.SourceId == prior.Source.Id ? prior : other.ShouldNotBeNull();
            var claimed = await Photos(s).ClaimScheduledImageAsync(scope, snapshot.AttemptKey, 111, Ct);
            claimed.Status.ShouldBe(VetPhotoImageStatus.Claimed); var claim = claimed.Claim.ShouldNotBeNull();
            (await Photos(s).MarkImageDispatchedAsync(scope, snapshot.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
            var result = await Photos(s).CompleteImageAsync(new(scope, snapshot.AttemptKey, claim.ClaimToken, 111,
                snapshot.SourceId, snapshot.InputRevisionId, "gpt-6.1-sol", ImageJson(snapshot.SourceId, snapshot.InputRevisionId)), Ct);
            result.Status.ShouldBe(VetPhotoImageStatus.ProposedDelta); result.Extraction!.State.ShouldBe("comparison");
            result.Extraction.Id.ShouldNotBe(fixture.Result!.Id); completed.Add(snapshot.SourceId, fixture with { Result = result.Extraction });
        }
        return new(completed[prior.Source.Id], window, prior.Result!.Id, other == null ? null : completed[other.Source.Id]);
    }
    private async Task<VetPhotoReview> ComparisonReview(VetTestSession s, Comparison comparison,
        string disposition = "save", bool restore = false)
    {
        var selection = await Select(s, comparison.Evidence, disposition, duplicate: "separate", restore: restore);
        var review = await Review(s, comparison.Evidence.Scope, [selection], VetPhotoReviewKind.ReextractComparison, comparison.Window.Id);
        (await Photos(s).AttachComparisonAsync(new(comparison.Evidence.Scope, comparison.Window.RunId, 111),
            comparison.Window.Id, review.Id, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        return review;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_delivered_current_or_historical_comparison_accepts_selected_result_without_advancing_source_pointer(bool historical)
    {
        await SeedAsync(); await using var s = Open(); var initial = await Prepare(s); var comparison = await Compare(s, initial, historical);
        var sourceBefore = await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(Ct);
        var selection = await Select(s, comparison.Evidence, duplicate: "separate");
        selection.ExtractionResultId.ShouldBe(comparison.Evidence.Result!.Id); selection.ExpectedCandidateExtractionId.ShouldNotBe(selection.ExtractionResultId);
        var review = await ComparisonReview(s, comparison);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Applied);
        await using var v = Open(); var fact = await v.Context.VetEvents.SingleAsync(Ct);
        fact.Value.ShouldBe(5.6m); fact.InputRevisionId.ShouldBe(initial.Input.Id); fact.ExtractionResultId.ShouldBe(comparison.Evidence.Result.Id);
        var sourceAfter = await v.Context.Set<VetPhotoSource>().SingleAsync(Ct);
        sourceAfter.CurrentInputRevisionId.ShouldBe(sourceBefore.CurrentInputRevisionId); sourceAfter.CurrentOrdinal.ShouldBe(sourceBefore.CurrentOrdinal);
        var candidate = await v.Context.Set<VetPhotoCandidate>().SingleAsync(Ct); candidate.Id.ShouldBe(initial.Candidate.Id);
        candidate.InputRevisionId.ShouldBe(initial.Input.Id); candidate.ExtractionResultId.ShouldBe(comparison.Evidence.Result.Id);
        (await v.Context.Set<VetPhotoExtraction>().SingleAsync(x => x.Id == comparison.InitialResultId, Ct)).State.ShouldBe("returned");
        (await v.Context.Set<VetPhotoExtraction>().SingleAsync(x => x.Id == comparison.Evidence.Result.Id, Ct)).State.ShouldBe("comparison");
        (await v.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image", Ct)).ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fully_shown_keep_of_unsaved_or_saved_comparison_preserves_all_facts_and_candidates_but_records_one_auditable_action(bool saved)
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s);
        if (saved)
        {
            var initial = await Review(s, Scope, [await Select(s, e)]);
            (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, initial), Ct)).Status.ShouldBe(VetMutationStatus.Applied);
        }
        var comparison = await Compare(s, e, false);
        var selected = await Select(s, comparison.Evidence, "keep", saved ? Fact(e) : null);
        var review = await Review(s, Scope, [selected], VetPhotoReviewKind.ReextractComparison, comparison.Window.Id);
        (await Photos(s).AttachComparisonAsync(new(comparison.Evidence.Scope, comparison.Window.RunId, 111),
            comparison.Window.Id, review.Id, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        var actionsBefore = await s.Context.VetDiaryActions.CountAsync(Ct); var changesBefore = await s.Context.VetDiaryActionChanges.CountAsync(Ct);
        var result = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct);
        result.Status.ShouldBe(VetMutationStatus.NoChange); result.ActionId.ShouldNotBeNull(); result.EventIds.ShouldBeEmpty(); result.Revisions.ShouldBeEmpty();
        await using var v = Open(); (await SnapshotCandidates(v)).ShouldBe(before); (await SnapshotEvents(v)).ShouldBe(facts);
        (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(actionsBefore + 1); (await v.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(changesBefore);
        var action = await v.Context.VetDiaryActions.SingleAsync(a => a.Id == result.ActionId, Ct);
        action.Kind.ShouldBe("photo_comparison_keep"); action.ActorUserId.ShouldBe(222);
        var outcome = JsonSerializer.Deserialize<VetPhotoActionOutcome>(action.OutcomeJson, Json).ShouldNotBeNull();
        outcome.Status.ShouldBe(VetMutationStatus.NoChange); outcome.PhotoChanges.ShouldBeEmpty(); outcome.EventIds.ShouldBeEmpty();
        var accepted = await v.Context.Set<VetPhotoReview>().SingleAsync(r => r.Id == review.Id, Ct);
        accepted.State.ShouldBe("accepted"); accepted.ActionId.ShouldBe(result.ActionId); accepted.DecisionActorUserId.ShouldBe(222);
        await using var restart = Open(); var replay = await restart.Diary.ApplyPhotoReviewAsync(Accept(Scope, review, 111), Ct);
        replay.Status.ShouldBe(VetMutationStatus.AlreadyApplied); replay.ActionId.ShouldBe(result.ActionId); replay.EventIds.ShouldBeEmpty();
        (await restart.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(actionsBefore + 1); (await SnapshotCandidates(restart)).ShouldBe(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Keep_cannot_supply_a_new_unresolved_fact_or_alter_an_existing_saved_state(bool saved)
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s);
        if (saved) (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, await Review(s, Scope, [await Select(s, e)])), Ct)).Status.ShouldBe(VetMutationStatus.Applied);
        var comparison = await Compare(s, e, false);
        var shown = saved ? Fact(e) with { Value = 5.7m } : Fact(e);
        var selected = await Select(s, comparison.Evidence, "keep", shown);
        var review = await Review(s, Scope, [selected], VetPhotoReviewKind.ReextractComparison, comparison.Window.Id);
        (await Photos(s).AttachComparisonAsync(new(comparison.Evidence.Scope, comparison.Window.RunId, 111),
            comparison.Window.Id, review.Id, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Refused);
        await AssertUnchanged(e, before, facts, saved ? 1 : 0);
    }

    [Fact]
    public async Task Mixed_keep_and_save_window_applies_only_the_changed_source_and_records_the_exact_subset()
    {
        await SeedAsync(); await using var s = Open(); var kept = await Prepare(s, 1, collection: true); var changed = await Prepare(s, 2);
        var comparison = await Compare(s, kept, false, changed); var other = comparison.OtherEvidence.ShouldNotBeNull();
        var keep = await Select(s, comparison.Evidence, "keep"); var save = await Select(s, other, duplicate: "separate");
        var review = await Review(s, Scope, [keep, save], VetPhotoReviewKind.ReextractComparison, comparison.Window.Id);
        (await Photos(s).AttachComparisonAsync(new(comparison.Evidence.Scope, comparison.Window.RunId, 111),
            comparison.Window.Id, review.Id, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var keptBefore = JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == kept.Candidate.Id, Ct), Json);
        var result = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct); result.Status.ShouldBe(VetMutationStatus.Applied); result.EventIds.Count.ShouldBe(1);
        await using var v = Open(); (await v.Context.VetEvents.SingleAsync(Ct)).SourceId.ShouldBe(changed.Source.Id);
        JsonSerializer.Serialize(await v.Context.Set<VetPhotoCandidate>().SingleAsync(c => c.Id == kept.Candidate.Id, Ct), Json).ShouldBe(keptBefore);
        var outcome = JsonSerializer.Deserialize<VetPhotoActionOutcome>((await v.Context.VetDiaryActions.SingleAsync(Ct)).OutcomeJson, Json).ShouldNotBeNull();
        outcome.PhotoChanges.Count.ShouldBe(1); outcome.PhotoChanges.Single().CandidateId.ShouldBe(changed.Candidate.Id);
        (await v.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Keep_is_refused_outside_reextraction_comparison_even_when_the_preview_is_complete()
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s); var comparison = await Compare(s, e, false);
        var item = await Select(s, comparison.Evidence, "keep"); var review = await Review(s, Scope, [item], VetPhotoReviewKind.Correction, comparison.Window.Id);
        var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Refused); await AssertUnchanged(e, before, facts, 0);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("value")]
    [InlineData("deleted")]
    public async Task Changed_linked_target_stales_null_state_collision_proof_and_unresolved_keep_review(string fence)
    {
        await SeedAsync(); await using var s = Open(); var target = await Prepare(s, 1);
        var saved = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, await Review(s, Scope, [await Select(s, target)])), Ct);
        saved.Status.ShouldBe(VetMutationStatus.Applied); var own = await Prepare(s, 2);
        var link = (await Select(s, own, "link", duplicate: "same")) with { LinkEventId = saved.EventIds.Single(), LinkEventRevision = 1 };
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, await Review(s, Scope, [link])), Ct)).Status.ShouldBe(VetMutationStatus.Applied);
        var comparison = await Compare(s, own, false); var item = await Select(s, comparison.Evidence, "keep"); item.State.ShouldBeNull(); item.EventId.ShouldBeNull();
        var proofBefore = item.CollisionProof; var review = await Review(s, Scope, [item], VetPhotoReviewKind.ReextractComparison, comparison.Window.Id);
        (await Photos(s).AttachComparisonAsync(new(comparison.Evidence.Scope, comparison.Window.RunId, 111),
            comparison.Window.Id, review.Id, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        if (fence == "revision") await s.Context.VetEvents.Where(e => e.Id == saved.EventIds.Single()).ExecuteUpdateAsync(u => u.SetProperty(e => e.Revision, 2), Ct);
        if (fence == "value") await s.Context.VetEvents.Where(e => e.Id == saved.EventIds.Single()).ExecuteUpdateAsync(u => u.SetProperty(e => e.Value, 5.7m), Ct);
        if (fence == "deleted") await s.Context.VetEvents.Where(e => e.Id == saved.EventIds.Single()).ExecuteUpdateAsync(u => u.SetProperty(e => e.DeletedAt, (DateTimeOffset?)Now), Ct);
        var currentProof = (await s.Diary.GetPhotoCollisionProofAsync(Scope, own.Profile.Id, own.Candidate.Id, null, Ct)).ShouldNotBeNull();
        currentProof.Fingerprint.ShouldNotBe(proofBefore.Fingerprint);
        var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Stale); await AssertUnchanged(own, before, facts, 2);
    }

    [Theory]
    [InlineData("review_kind")]
    [InlineData("window_missing")]
    [InlineData("window_link")]
    [InlineData("candidate_result")]
    [InlineData("result_input")]
    [InlineData("source_pointer")]
    [InlineData("cancelled_run")]
    [InlineData("run_ordinal")]
    [InlineData("run_state")]
    [InlineData("window_state")]
    [InlineData("selection_actor")]
    public async Task Historical_comparison_requires_exact_window_review_selected_result_and_current_candidate_snapshot(string fence)
    {
        await SeedAsync(); await using var s = Open(); var initial = await Prepare(s); var comparison = await Compare(s, initial, true);
        var review = await ComparisonReview(s, comparison);
        if (fence == "review_kind") await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Kind, "correction"), Ct);
        if (fence == "window_missing") await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.RunWindowId, (Guid?)null), Ct);
        if (fence == "window_link") await s.Context.Set<VetPhotoRunWindow>().Where(w => w.Id == comparison.Window.Id).ExecuteUpdateAsync(u => u.SetProperty(w => w.ComparisonReviewId, (Guid?)null), Ct);
        if (fence == "candidate_result") await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == initial.Candidate.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.ExtractionResultId, comparison.Evidence.Result!.Id), Ct);
        if (fence == "result_input")
        {
            var current = await s.Context.Set<VetPhotoSource>().Where(x => x.Id == initial.Source.Id).Select(x => x.CurrentInputRevisionId).SingleAsync(Ct);
            await s.Context.Set<VetPhotoExtraction>().Where(x => x.Id == comparison.Evidence.Result!.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.InputRevisionId, current), Ct);
        }
        if (fence == "source_pointer") await s.Context.Set<VetPhotoSource>().Where(x => x.Id == initial.Source.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.CurrentOrdinal, x => x.CurrentOrdinal + 1), Ct);
        if (fence == "cancelled_run") await s.Context.Set<VetPhotoRun>().Where(r => r.Id == comparison.Window.RunId).ExecuteUpdateAsync(u => u.SetProperty(r => r.CancelledAt, (DateTimeOffset?)Now), Ct);
        if (fence == "run_ordinal") await s.Context.Set<VetPhotoRun>().Where(r => r.Id == comparison.Window.RunId).ExecuteUpdateAsync(u => u.SetProperty(r => r.NextWindowOrdinal, 1), Ct);
        if (fence == "run_state") await s.Context.Set<VetPhotoRun>().Where(r => r.Id == comparison.Window.RunId).ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "cancelled"), Ct);
        if (fence == "window_state") await s.Context.Set<VetPhotoRunWindow>().Where(w => w.Id == comparison.Window.Id).ExecuteUpdateAsync(u => u.SetProperty(w => w.State, "queued"), Ct);
        if (fence == "selection_actor")
        {
            var selectionReviewId = await s.Context.Set<VetPhotoRun>().Where(r => r.Id == comparison.Window.RunId).Select(r => r.SelectionReviewId).SingleAsync(Ct);
            await s.Context.Set<VetPhotoReview>().Where(r => r.Id == selectionReviewId).ExecuteUpdateAsync(u => u.SetProperty(r => r.DecisionActorUserId, (long?)222), Ct);
        }
        var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct)).Status.ShouldBe(fence == "result_input" ? VetMutationStatus.Refused : VetMutationStatus.Stale);
        await AssertUnchanged(initial, before, facts, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_batch_comparison_requires_explicit_candidate_restoration_and_never_reopens_batch(bool restore)
    {
        await SeedAsync(); await using var s = Open(); var initial = await Prepare(s);
        await s.Context.Set<VetPhotoCandidate>().ExecuteUpdateAsync(u => u.SetProperty(c => c.State, "cancelled")
            .SetProperty(c => c.RequiresExplicitRestoration, true), Ct);
        await s.Context.Set<VetPhotoBatch>().ExecuteUpdateAsync(u => u.SetProperty(b => b.State, "cancelled"), Ct);
        var comparison = await Compare(s, initial, false); var review = await ComparisonReview(s, comparison, restore: restore);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct)).Status.ShouldBe(restore ? VetMutationStatus.Applied : VetMutationStatus.Refused);
        await using var v = Open(); (await v.Context.Set<VetPhotoBatch>().SingleAsync(Ct)).State.ShouldBe("cancelled");
        var candidate = await v.Context.Set<VetPhotoCandidate>().SingleAsync(Ct); candidate.Id.ShouldBe(initial.Candidate.Id);
        candidate.State.ShouldBe(restore ? "saved" : "cancelled"); candidate.RequiresExplicitRestoration.ShouldBe(!restore);
        (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(restore ? 1 : 0);
        (await v.Context.Set<VetPhotoSource>().SingleAsync(Ct)).CurrentInputRevisionId.ShouldBe(initial.Input.Id);
    }

    [Fact]
    public async Task Old_returned_historical_evidence_cannot_be_saved_after_edit_without_a_shown_comparison_window()
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s); await Edit(s, e);
        var item = await Select(s, e); item.InputRevisionId.ShouldNotBe(item.ExpectedCurrentInputId);
        var review = await Review(s, Scope, [item], VetPhotoReviewKind.Correction);
        review.RunWindowId.ShouldBeNull(); var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Stale);
        await AssertUnchanged(e, before, facts, 0);
    }

    [Fact]
    public async Task Selected_canonical_correction_restores_soft_deleted_owned_fact_and_links_second_source_without_new_identity()
    {
        await SeedAsync(); await using var s = Open(); var canonical = await Prepare(s, 1, collection: true);
        var save = await Review(s, Scope, [await Select(s, canonical)]); var saved = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, save), Ct);
        saved.Status.ShouldBe(VetMutationStatus.Applied); var eventId = saved.EventIds.Single();
        // The accepted creation-undo endpoint is a separate task. Seed its persisted protected
        // postcondition here; this tests acceptance of that state, not the implementation of undo.
        await s.Context.VetEvents.Where(e => e.Id == eventId).ExecuteUpdateAsync(u => u.SetProperty(e => e.DeletedAt, (DateTimeOffset?)Now)
            .SetProperty(e => e.DeleteReason, "undo").SetProperty(e => e.DeletedByUserId, (long?)111).SetProperty(e => e.Revision, 2), Ct);
        await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == canonical.Candidate.Id).ExecuteUpdateAsync(u =>
            u.SetProperty(c => c.State, "deleted").SetProperty(c => c.RequiresExplicitRestoration, true)
                .SetProperty(c => c.EventRevision, (int?)2).SetProperty(c => c.Revision, c => c.Revision + 1), Ct);
        var linked = await Prepare(s, 2);
        var correction = await Select(s, canonical, "correct", duplicate: "canonical", restore: true);
        var link = (await Select(s, linked, "link", duplicate: "same")) with { LinkCandidateId = canonical.Candidate.Id };
        var review = await Review(s, Scope, [link, correction], VetPhotoReviewKind.Correction);
        var result = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct); result.Status.ShouldBe(VetMutationStatus.Applied);
        result.EventIds.ShouldBe(new[] { eventId }); result.Revisions.Single().ShouldBe(new VetEventRevision(eventId, 3));
        await using var v = Open(); var fact = await v.Context.VetEvents.SingleAsync(Ct);
        fact.Id.ShouldBe(eventId); fact.DeletedAt.ShouldBeNull(); fact.DeleteReason.ShouldBeNull(); fact.DeletedByUserId.ShouldBeNull(); fact.Revision.ShouldBe(3);
        var owner = await v.Context.Set<VetPhotoCandidate>().SingleAsync(c => c.Id == canonical.Candidate.Id, Ct);
        owner.EventId.ShouldBe(eventId); owner.EventRevision.ShouldBe(3); owner.RequiresExplicitRestoration.ShouldBeFalse();
        var alias = await v.Context.Set<VetPhotoCandidate>().SingleAsync(c => c.Id == linked.Candidate.Id, Ct);
        alias.EventId.ShouldBeNull(); alias.DuplicateEventId.ShouldBe(eventId); alias.DuplicateEventRevision.ShouldBe(3);
        alias.InputRevisionId.ShouldBe(linked.Input.Id); alias.ExtractionResultId.ShouldBe(linked.Result!.Id);
        alias.State.ShouldBe("linked"); alias.DuplicateSourceId.ShouldBe(canonical.Source.Id);
        (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(2); (await v.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(2);
    }

    [Theory]
    [InlineData("missing_link")]
    [InlineData("linked_save")]
    [InlineData("two_targets")]
    [InlineData("self_link")]
    [InlineData("link_revision")]
    public async Task Link_and_canonical_selection_cannot_change_unshown_group_or_target_semantics(string fence)
    {
        await SeedAsync(); await using var s = Open(); var a = await Prepare(s, 1, collection: true); var b = await Prepare(s, 2);
        var canonical = await Select(s, a, duplicate: "canonical"); var link = (await Select(s, b, "link", duplicate: "same")) with { LinkCandidateId = a.Candidate.Id };
        if (fence == "missing_link") link = link with { LinkCandidateId = Guid.NewGuid() };
        if (fence == "linked_save") link = link with { Disposition = "save", LinkCandidateId = null, DuplicateDecision = "canonical" };
        if (fence == "two_targets") link = link with { LinkEventId = 1000, LinkEventRevision = 1 };
        if (fence == "self_link") link = link with { LinkCandidateId = b.Candidate.Id };
        if (fence == "link_revision") canonical = canonical with { Disposition = "link", DuplicateDecision = "same", LinkCandidateId = b.Candidate.Id };
        var review = await Review(s, Scope, [canonical, link]); var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct)).Status.ShouldBe(VetMutationStatus.Refused);
        await AssertUnchanged(a, before, facts, 0);
    }

    [Theory]
    [InlineData("source_author")]
    [InlineData("message_topic")]
    [InlineData("message_kind")]
    [InlineData("message_direction")]
    [InlineData("missing_binding")]
    public async Task Stored_transport_identity_must_still_prove_the_exact_photo_source(string fence)
    {
        await SeedAsync(); await using var s = Open(); var e = await Prepare(s); var review = await Review(s, Scope, [await Select(s, e)]);
        if (fence == "source_author") await s.Context.Messages.Where(m => m.Id == e.Source.SourceMessageDbId).ExecuteUpdateAsync(u => u.SetProperty(m => m.UserId, (long?)222), Ct);
        if (fence == "message_topic") await s.Context.Messages.Where(m => m.Id == e.Source.SourceMessageDbId).ExecuteUpdateAsync(u => u.SetProperty(m => m.TopicId, (int?)8), Ct);
        if (fence == "message_kind") await s.Context.Messages.Where(m => m.Id == e.Source.SourceMessageDbId).ExecuteUpdateAsync(u => u.SetProperty(m => m.Kind, MessageKind.Text), Ct);
        if (fence == "message_direction") await s.Context.Messages.Where(m => m.Id == e.Source.SourceMessageDbId).ExecuteUpdateAsync(u => u.SetProperty(m => m.Direction, MessageDirection.Out), Ct);
        if (fence == "missing_binding") await s.Context.Set<VetPhotoSource>().Where(p => p.Id == e.Source.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.SourceMessageDbId, (long?)null), Ct);
        var before = await SnapshotCandidates(s); var facts = await SnapshotEvents(s);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct)).Status.ShouldBe(fence == "missing_binding" ? VetMutationStatus.Stale : VetMutationStatus.Refused);
        await AssertUnchanged(e, before, facts, 0);
    }

    [Fact]
    public async Task Valid_foreign_family_scope_cannot_discover_or_accept_another_familys_review_or_collision_handles()
    {
        await SeedAsync(); Evidence e; VetPhotoReview review;
        await using (var seed = Open()) { e = await Prepare(seed); review = await Review(seed, Scope, [await Select(seed, e)]); }
        var family = new Family { Name = "synthetic second family", CreatedAt = Now }; Db.Add(family); await Db.SaveChangesAsync(Ct);
        var bot = new Assistant.Domain.Bots.Bot { FamilyId = family.Id, TelegramBotId = 2001, Username = "synthetic_second_vet_bot",
            Role = "vet", Status = BotStatus.Active, CreatedAt = Now }; Db.Add(bot); await Db.SaveChangesAsync(Ct);
        Db.Add(new FamilyMember { FamilyId = family.Id, TelegramUserId = 333, DisplayName = "synthetic second member", Status = FamilyMemberStatus.Approved, CreatedAt = Now, UpdatedAt = Now });
        Db.Add(new Place { BotId = bot.Id, ChatId = -200, TopicId = 9, Title = "synthetic second place", Status = PlaceStatus.Approved, CreatedAt = Now });
        await Db.SaveChangesAsync(Ct); var foreign = new VetDiaryScope(family.Id, bot.Id, bot.TelegramBotId, -200, 9);
        await using var s = Open(family.Id);
        (await s.Diary.GetPhotoCollisionProofAsync(foreign, e.Profile.Id, e.Candidate.Id, Fact(e), Ct)).ShouldBeNull();
        (await s.Diary.ApplyPhotoReviewAsync(Accept(foreign, review, 333), Ct)).Status.ShouldBe(VetMutationStatus.NotFound);
        (await s.Context.Set<VetPhotoReview>().CountAsync(Ct)).ShouldBe(0); (await s.Context.Set<VetPhotoCandidate>().CountAsync(Ct)).ShouldBe(0);
        (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0); (await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
        await using var own = Open(); (await own.Context.Set<VetPhotoReview>().SingleAsync(Ct)).State.ShouldBe("preview");
        (await own.Context.VetEvents.CountAsync(Ct)).ShouldBe(0); (await own.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
    }

    private sealed class HoldActionInsert : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult(true);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Replace("\"", "", StringComparison.Ordinal).Contains("INSERT INTO vet_diary_actions", StringComparison.OrdinalIgnoreCase))
            { Entered.TrySetResult(true); await _release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }
    private sealed class ObserveBotLock : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal)
                && command.CommandText.Contains("vet:", StringComparison.Ordinal)) Entered.TrySetResult(true);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class FailCandidateWrite : DbCommandInterceptor
    {
        public int Hits { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Hits == 0 && command.CommandText.Replace("\"", "", StringComparison.Ordinal).Contains("UPDATE vet_photo_candidates", StringComparison.OrdinalIgnoreCase))
            { Hits++; throw new InvalidOperationException("synthetic atomic candidate failure"); }
            return ValueTask.FromResult(result);
        }
    }
}
