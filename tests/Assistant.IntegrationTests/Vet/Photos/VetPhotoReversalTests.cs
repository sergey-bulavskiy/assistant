using System.Data.Common;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Infrastructure.Vet;
using Microsoft.Extensions.Logging.Abstractions;
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

public sealed class VetPhotoReversalTests : VetTestBase
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
            SentAt = Clock.UtcNow, ChatId = scope.ChatId, TopicId = scope.TopicId, ChatType = privateChat ? "private" : "supergroup" };
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

    private IVetPhotoReversalStore Reversals(VetTestSession s) => s.Diary;
    private async Task<(Evidence[] Items, VetMutationResult Saved)> SaveMany(VetTestSession s, int count)
    {
        var items = new List<Evidence>();
        for (var i = 1; i <= count; i++) items.Add(await Prepare(s, i, collection: i == 1));
        var batch = await s.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(Ct);
        (await Photos(s).CloseCollectionAsync(Scope, batch.Id, batch.ReviewRevision, 111, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var selections = new List<VetPhotoDiarySelection>();
        foreach (var item in items) selections.Add(await Select(s, item, duplicate: "separate"));
        var review = await Review(s, Scope, selections.ToArray());
        var saved = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct); saved.Status.ShouldBe(VetMutationStatus.Applied);
        return (items.ToArray(), saved);
    }
    private async Task<VetPhotoReview> ReverseReview(VetTestSession s, long actionId, Guid[] ids, bool deliver = true)
    {
        var view = (await Reversals(s).ReadReversalAsync(Scope, actionId, ids, 111, Ct)).ShouldNotBeNull();
        var preview = VetPhotoReversalApplication.Render(view); preview.Success.ShouldBeTrue();
        var entries = view.Items.Select(i => i.Entry).ToArray();
        foreach (var entry in entries) JsonSerializer.Serialize(entry, Json).Length.ShouldBeLessThanOrEqualTo(4096);
        var staged = await Photos(s).StageReviewAsync(new(Scope, Guid.NewGuid(), 111, VetPhotoReviewKind.Reverse,
            null, null, view.ProfileRevision, JsonSerializer.Serialize(entries, Json), preview), Ct);
        staged.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); var review = staged.Review.ShouldNotBeNull();
        if (deliver)
        {
            var handle = new VetPhotoReviewHandle(Scope, review.Id, review.Revision, review.OperationKey, 111);
            for (var i = 0; i < preview.Pages.Count; i++)
                (await Photos(s).RecordPageDeliveryAsync(handle, i, i == preview.Pages.Count - 1 ? 701 : 500 + i,
                    Hash(preview.Pages[i]), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
            (await Photos(s).CompleteDeliveryAsync(handle, 701, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        }
        return await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == review.Id, Ct);
    }
    private async Task<VetMutationResult> Reverse(VetTestSession s, VetPhotoReview r, long actor = 222) =>
        await Reversals(s).ApplyReversalAsync(Accept(Scope, r, actor), Ct);
    private static string Pages(VetPhotoReview r) => string.Join("\n", JsonSerializer.Deserialize<string[]>(r.PreviewPagesJson, Json)!);

    [Fact]
    public async Task Arbitrary_older_subset_preserves_unselected_newer_actions_and_later_subset_remains_available()
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 3);
        Clock.UtcNow = Now.AddDays(3); var newest = await SavePhoto(s, await Prepare(s, 100));
        var oldReview = await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.ActionId == saved.ActionId, Ct);
        var oldProof = JsonSerializer.Serialize(oldReview, Json); var retained = await Retention(s);
        var first = await ReverseReview(s, saved.ActionId!.Value, [items[0].Candidate.Id]);
        Pages(first).ShouldContain("5.6 mmol/L"); Pages(first).ShouldContain("2031-05-11T10:20:00.0000000+00:00");
        Pages(first).ShouldContain("исходный участник 222"); Pages(first).ShouldContain("автор 111");
        var result = await Reverse(s, first, 111); result.Status.ShouldBe(VetMutationStatus.Applied);
        var owned = await s.Context.VetEvents.AsNoTracking().SingleAsync(e => e.PhotoSourceId == items[0].Source.Id, Ct);
        result.EventIds.ShouldBe(new[] { owned.Id }); owned.Revision.ShouldBe(2); owned.DeletedAt.ShouldBe(Clock.UtcNow);
        owned.DeletedByUserId.ShouldBe(111); owned.DeleteReason.ShouldBe("photo_reverse");
        (await s.Context.VetEvents.SingleAsync(e => e.Id == newest.EventIds.Single(), Ct)).DeletedAt.ShouldBeNull();
        (await s.Context.VetEvents.CountAsync(e => e.DeletedAt == null, Ct)).ShouldBe(3);
        (await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.Id == saved.ActionId, Ct)).ReversedByActionId.ShouldBeNull();
        var second = await ReverseReview(s, saved.ActionId!.Value, [items[1].Candidate.Id]);
        (await Reverse(s, second)).Status.ShouldBe(VetMutationStatus.Applied);
        (await s.Context.VetEvents.CountAsync(e => e.DeletedAt == null, Ct)).ShouldBe(2);
        (await s.Context.VetEvents.SingleAsync(e => e.PhotoSourceId == items[2].Source.Id, Ct)).DeletedAt.ShouldBeNull();
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == oldReview.Id, Ct), Json).ShouldBe(oldProof);
        (await Retention(s)).ShouldBe(retained);
        var inverse = await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.Id == result.ActionId, Ct);
        inverse.ReversesActionId.ShouldBe(saved.ActionId); inverse.ActorUserId.ShouldBe(111);
    }
    [Fact]
    public async Task Full_fifty_selection_reverses_all_and_adjacent_fifty_one_refuses_without_writes()
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 50);
        var ids = items.Select(i => i.Candidate.Id).ToArray(); var before = await Mutable(s);
        (await Reversals(s).ReadReversalAsync(Scope, saved.ActionId!.Value, ids.Concat(new[] { Guid.NewGuid() }).ToArray(), 111, Ct)).ShouldBeNull();
        (await Mutable(s)).ShouldBe(before); var review = await ReverseReview(s, saved.ActionId!.Value, ids);
        var entries = JsonSerializer.Deserialize<VetPhotoReversalEntry[]>(review.SelectionJson, Json)!; entries.Length.ShouldBe(50);
        var shownPages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!;
        shownPages.Length.ShouldBeLessThanOrEqualTo(VetPhotoReviewFormatter.MaxReviewPages);
        foreach (var page in shownPages) page.Length.ShouldBeLessThanOrEqualTo(VetPhotoReviewFormatter.MaxPageChars);
        foreach (var id in ids) Pages(review).ShouldContain(id.ToString("D"));
        var retained = await Retention(s); var result = await Reverse(s, review);
        result.Status.ShouldBe(VetMutationStatus.Applied); result.EventIds.Order().ShouldBe(saved.EventIds.Order());
        result.Revisions.Count.ShouldBe(50); result.ProtectedIds.ShouldBeEmpty(); result.ProtectedCandidateIds.ShouldBeEmpty();
        (await s.Context.VetEvents.CountAsync(e => e.DeletedAt == null, Ct)).ShouldBe(0);
        (await Outcome(s, result.ActionId!.Value)).PhotoChanges.Count.ShouldBe(50);
        (await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.Id == saved.ActionId, Ct)).ReversedByActionId.ShouldBe(result.ActionId);
        (await Retention(s)).ShouldBe(retained);
    }
    [Theory]
    [InlineData("candidate_revision")]
    [InlineData("candidate_state")]
    [InlineData("event_revision")]
    public async Task Already_protected_rows_are_fully_shown_and_skipped_while_safe_subset_commits(string protection)
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 2);
        var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == items[0].Candidate.Id, Ct);
        if (protection == "candidate_revision") await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == candidate.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.Revision, c => c.Revision + 1), Ct);
        if (protection == "candidate_state") await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == candidate.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.ManuallyCorrected, true), Ct);
        if (protection == "event_revision") await s.Context.VetEvents.Where(e => e.Id == candidate.EventId).ExecuteUpdateAsync(u => u.SetProperty(e => e.Revision, e => e.Revision + 1).SetProperty(e => e.Value, 8.125m), Ct);
        var protectedFact = JsonSerializer.Serialize(await s.Context.VetEvents.AsNoTracking().SingleAsync(e => e.Id == candidate.EventId, Ct), Json);
        var protectedCandidate = JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == candidate.Id, Ct), Json);
        var review = await ReverseReview(s, saved.ActionId!.Value, items.Select(i => i.Candidate.Id).ToArray());
        Pages(review).ShouldContain("ЗАЩИЩЕНО"); var result = await Reverse(s, review);
        result.Status.ShouldBe(VetMutationStatus.Applied); result.ProtectedIds.ShouldBe(new[] { candidate.EventId!.Value });
        result.ProtectedCandidateIds.ShouldBe(new[] { candidate.Id }); result.EventIds.Count.ShouldBe(1);
        (await Outcome(s, result.ActionId!.Value)).PhotoChanges.Single().CandidateId.ShouldBe(items[1].Candidate.Id);
        JsonSerializer.Serialize(await s.Context.VetEvents.AsNoTracking().SingleAsync(e => e.Id == candidate.EventId, Ct), Json).ShouldBe(protectedFact);
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == candidate.Id, Ct), Json).ShouldBe(protectedCandidate);
        (await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.Id == saved.ActionId, Ct)).ReversedByActionId.ShouldBeNull();
    }
    [Theory]
    [InlineData("source_ordinal")]
    [InlineData("source_state")]
    [InlineData("source_author")]
    [InlineData("candidate_revision")]
    [InlineData("candidate_state")]
    [InlineData("candidate_effective")]
    [InlineData("candidate_reason")]
    [InlineData("event_revision")]
    [InlineData("event_value")]
    [InlineData("event_time")]
    [InlineData("action_fingerprint")]
    [InlineData("profile_revision")]
    public async Task Any_post_preview_current_snapshot_change_stales_whole_subset_before_writes(string change)
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 2);
        var review = await ReverseReview(s, saved.ActionId!.Value, items.Select(i => i.Candidate.Id).ToArray());
        var source = items[0].Source.Id; var candidate = items[0].Candidate.Id;
        var eventId = await s.Context.VetEvents.Where(e => e.PhotoSourceId == source).Select(e => e.Id).SingleAsync(Ct);
        if (change == "source_ordinal") await s.Context.Set<VetPhotoSource>().Where(x => x.Id == source).ExecuteUpdateAsync(u => u.SetProperty(x => x.CurrentOrdinal, x => x.CurrentOrdinal + 1), Ct);
        if (change == "source_state") await s.Context.Set<VetPhotoSource>().Where(x => x.Id == source).ExecuteUpdateAsync(u => u.SetProperty(x => x.State, "cancelled"), Ct);
        if (change == "source_author") await s.Context.Set<VetPhotoSource>().Where(x => x.Id == source).ExecuteUpdateAsync(u => u.SetProperty(x => x.SourceAuthorUserId, 222L), Ct);
        if (change == "candidate_revision") await s.Context.Set<VetPhotoCandidate>().Where(x => x.Id == candidate).ExecuteUpdateAsync(u => u.SetProperty(x => x.Revision, x => x.Revision + 1), Ct);
        if (change == "candidate_state") await s.Context.Set<VetPhotoCandidate>().Where(x => x.Id == candidate).ExecuteUpdateAsync(u => u.SetProperty(x => x.ManuallyCorrected, true), Ct);
        if (change == "candidate_effective") await s.Context.Set<VetPhotoCandidate>().Where(x => x.Id == candidate).ExecuteUpdateAsync(u => u.SetProperty(x => x.EffectiveJson, "{}"), Ct);
        if (change == "candidate_reason") await s.Context.Set<VetPhotoCandidate>().Where(x => x.Id == candidate).ExecuteUpdateAsync(u => u.SetProperty(x => x.ReasonsJson, "[\"synthetic changed reason\"]"), Ct);
        if (change == "event_revision") await s.Context.VetEvents.Where(x => x.Id == eventId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Revision, x => x.Revision + 1), Ct);
        if (change == "event_value") await s.Context.VetEvents.Where(x => x.Id == eventId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Value, 8.125m), Ct);
        if (change == "event_time") await s.Context.VetEvents.Where(x => x.Id == eventId).ExecuteUpdateAsync(u => u.SetProperty(x => x.OccurredAt, Measured.AddSeconds(1)), Ct);
        if (change == "action_fingerprint") await s.Context.VetDiaryActions.Where(x => x.Id == saved.ActionId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Fingerprint, "synthetic changed fingerprint"), Ct);
        if (change == "profile_revision") await s.Context.Set<VetProfile>().ExecuteUpdateAsync(u => u.SetProperty(x => x.Revision, x => x.Revision + 1), Ct);
        var before = await Mutable(s); var retained = await Retention(s);
        (await Reverse(s, review)).Status.ShouldBe(VetMutationStatus.Stale);
        (await Mutable(s)).ShouldBe(before); (await Retention(s)).ShouldBe(retained);
    }
    [Theory]
    [InlineData("missing_complete")]
    [InlineData("absent_prompt")]
    [InlineData("missing_pages")]
    [InlineData("changed_page")]
    [InlineData("wrong_count")]
    [InlineData("failed_state")]
    [InlineData("wrong_kind")]
    [InlineData("wrong_revision")]
    [InlineData("wrong_key")]
    [InlineData("wrong_prompt")]
    [InlineData("fingerprint")]
    [InlineData("profile_id")]
    public async Task Exact_review_and_full_delivered_preview_fences_refuse_every_incomplete_or_changed_proof(string fence)
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 1);
        var review = await ReverseReview(s, saved.ActionId!.Value, [items[0].Candidate.Id]); var accept = Accept(Scope, review);
        var rows = s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id);
        if (fence == "missing_complete") await rows.ExecuteUpdateAsync(u => u.SetProperty(r => r.CompletePreviewDelivered, false), Ct);
        if (fence == "absent_prompt") await rows.ExecuteUpdateAsync(u => u.SetProperty(r => r.AcceptancePromptMessageId, (int?)null), Ct);
        if (fence == "missing_pages") await rows.ExecuteUpdateAsync(u => u.SetProperty(r => r.DeliveredPagesJson, "[]"), Ct);
        if (fence == "changed_page") await rows.ExecuteUpdateAsync(u => u.SetProperty(r => r.PreviewPagesJson, "[\"synthetic unseen replacement page\"]"), Ct);
        if (fence == "wrong_count") await rows.ExecuteUpdateAsync(u => u.SetProperty(r => r.PageCount, r => r.PageCount + 1), Ct);
        if (fence == "failed_state") await rows.ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "preview_failed").SetProperty(r => r.CompletePreviewDelivered, false), Ct);
        if (fence == "wrong_kind") await rows.ExecuteUpdateAsync(u => u.SetProperty(r => r.Kind, "save"), Ct);
        if (fence == "wrong_revision") accept = accept with { ReviewRevision = review.Revision + 1 };
        if (fence == "wrong_key") accept = accept with { OperationKey = Guid.NewGuid() };
        if (fence == "wrong_prompt") accept = accept with { CallbackPromptMessageId = 702 };
        if (fence == "fingerprint") await rows.ExecuteUpdateAsync(u => u.SetProperty(r => r.Fingerprint, "synthetic wrong fingerprint"), Ct);
        if (fence == "profile_id") await rows.ExecuteUpdateAsync(u => u.SetProperty(r => r.ProfileId, (long?)null), Ct);
        var before = await Mutable(s); var retained = await Retention(s);
        var result = await Reversals(s).ApplyReversalAsync(accept, Ct);
        result.Status.ShouldBe(VetMutationStatus.Stale);
        (await Mutable(s)).ShouldBe(before); (await Retention(s)).ShouldBe(retained);
    }
    [Theory]
    [InlineData("protected_candidate")]
    [InlineData("protected_event")]
    [InlineData("source_hash")]
    [InlineData("candidate_hash")]
    [InlineData("event_hash")]
    [InlineData("history_hash")]
    [InlineData("action_id")]
    [InlineData("foreign_candidate")]
    [InlineData("wrong_event")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unknown_field")]
    public async Task Rehashed_tampered_selection_cannot_add_authority_or_change_the_shown_protected_set(string fence)
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 1);
        var review = await ReverseReview(s, saved.ActionId!.Value, [items[0].Candidate.Id]);
        var selected = JsonSerializer.Deserialize<VetPhotoReversalEntry[]>(review.SelectionJson, Json)!;
        var entry = selected.Single();
        entry = fence switch {
            "protected_candidate" => entry with { CandidateProtected = !entry.CandidateProtected },
            "protected_event" => entry with { EventProtected = !entry.EventProtected },
            "source_hash" => entry with { SourceHash = new string('0', 64) },
            "candidate_hash" => entry with { CandidateHash = new string('0', 64) },
            "event_hash" => entry with { EventHash = new string('0', 64) },
            "history_hash" => entry with { HistoryHash = new string('0', 64) },
            "action_id" => entry with { ActionId = saved.ActionId!.Value + 1000 },
            "foreign_candidate" => entry with { CandidateId = Guid.NewGuid() },
            "wrong_event" => entry with { EventId = entry.EventId + 1000 }, _ => entry };
        var json = fence == "empty" ? "[]" : JsonSerializer.Serialize(fence == "duplicate" ? new[] { entry, entry } : new[] { entry }, Json);
        if (fence == "unknown_field") json = json.Replace("[{", "[{\"unshown_authority\":true,", StringComparison.Ordinal);
        await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.SelectionJson, json).SetProperty(r => r.Fingerprint, Hash(json)), Ct);
        var before = await Mutable(s); var retained = await Retention(s);
        (await Reverse(s, review)).Status.ShouldBe(fence is "empty" or "duplicate" ? VetMutationStatus.Refused : VetMutationStatus.Stale);
        (await Mutable(s)).ShouldBe(before); (await Retention(s)).ShouldBe(retained);
    }
    [Theory]
    [InlineData("member")]
    [InlineData("place")]
    [InlineData("inactive_bot")]
    [InlineData("topic")]
    [InlineData("null_topic")]
    [InlineData("chat")]
    public async Task Fresh_scope_actor_place_and_active_bot_are_required_before_read_or_mutation(string fence)
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 1);
        var review = await ReverseReview(s, saved.ActionId!.Value, [items[0].Candidate.Id]); var scope = Scope;
        if (fence == "member") await s.Context.FamilyMembers.Where(m => m.TelegramUserId == 222).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        if (fence == "place") await s.Context.Places.Where(p => p.TopicId == 7).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlaceStatus.Denied), Ct);
        if (fence == "inactive_bot") await s.Context.Bots.ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BotStatus.Disabled), Ct);
        if (fence == "topic") scope = scope with { TopicId = 8 };
        if (fence == "null_topic") scope = scope with { TopicId = null };
        if (fence == "chat") scope = scope with { ChatId = -200 };
        var before = await Mutable(s);
        (await Reversals(s).ReadReversalAsync(scope, saved.ActionId!.Value, [items[0].Candidate.Id], 222, Ct)).ShouldBeNull();
        var result = await Reversals(s).ApplyReversalAsync(Accept(scope, review), Ct);
        result.Status.ShouldBe(fence == "topic" ? VetMutationStatus.NotFound : VetMutationStatus.Refused);
        (await Mutable(s)).ShouldBe(before);
    }
    [Theory]
    [InlineData("unset")]
    [InlineData("foreign_family")]
    [InlineData("telegram_bot")]
    [InlineData("internal_bot")]
    public async Task Fail_closed_family_and_bot_guard_never_leaks_or_mutates_foreign_history(string fence)
    {
        await SeedAsync(); await using var setup = Open(); var (items, saved) = await SaveMany(setup, 1);
        var review = await ReverseReview(setup, saved.ActionId!.Value, [items[0].Candidate.Id]); var before = await Mutable(setup);
        await using var wrong = fence == "unset" ? Unscoped() : fence == "foreign_family" ? Open(FamilyId + 1000) : Open();
        var scope = fence == "telegram_bot" ? Scope with { TelegramBotId = 2002 } : fence == "internal_bot" ? Scope with { BotDbId = Bot.BotDbId + 1000 } : Scope;
        await Should.ThrowAsync<InvalidOperationException>(() => Reversals(wrong).ReadReversalAsync(scope, saved.ActionId!.Value, [items[0].Candidate.Id], 222, Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => Reversals(wrong).ApplyReversalAsync(Accept(scope, review), Ct));
        (await Mutable(setup)).ShouldBe(before);
    }
    [Theory]
    [InlineData("all")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("wrong_action")]
    public async Task Selection_requires_exact_nonempty_unique_membership_in_the_immutable_action(string fence)
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 1); var id = items[0].Candidate.Id;
        var ids = fence == "all" ? Array.Empty<Guid>() : fence == "duplicate" ? new[] { id, id } : fence == "unknown" ? new[] { Guid.NewGuid() } : new[] { id };
        var before = await Mutable(s);
        var read = await Reversals(s).ReadReversalAsync(Scope, fence == "wrong_action" ? saved.ActionId!.Value + 1000 : saved.ActionId!.Value, ids, 111, Ct);
        if (fence == "all") { read.ShouldNotBeNull().Items.Count.ShouldBe(1); read!.Items.Single().Entry.CandidateId.ShouldBe(id); }
        else read.ShouldBeNull();
        (await Mutable(s)).ShouldBe(before);
    }
    [Theory]
    [InlineData("both")]
    [InlineData("canonical_only")]
    [InlineData("link_only")]
    [InlineData("protected_canonical")]
    public async Task Canonical_and_linked_subset_preserve_single_fact_ownership_and_visible_protection(string subset)
    {
        await SeedAsync(); await using var s = Open(); var canonical = await Prepare(s, 1, collection: true); var linked = await Prepare(s, 2, sameBytes: true);
        var batch = await s.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(Ct);
        await Photos(s).CloseCollectionAsync(Scope, batch.Id, batch.ReviewRevision, 111, Ct);
        var first = await Select(s, canonical, duplicate: "canonical");
        var second = (await Select(s, linked, "link", duplicate: "same")) with { LinkCandidateId = canonical.Candidate.Id };
        var savedReview = await Review(s, Scope, [first, second]); var saved = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, savedReview), Ct);
        saved.Status.ShouldBe(VetMutationStatus.Applied); saved.EventIds.Count.ShouldBe(1);
        if (subset == "protected_canonical") await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == canonical.Candidate.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Revision, c => c.Revision + 1), Ct);
        var canonicalBefore = JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == canonical.Candidate.Id, Ct), Json);
        var linkedBefore = JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == linked.Candidate.Id, Ct), Json);
        var factBefore = JsonSerializer.Serialize(await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct), Json); var retained = await Retention(s);
        var ids = subset == "canonical_only" ? new[] { canonical.Candidate.Id } : subset == "link_only" ? new[] { linked.Candidate.Id } : new[] { canonical.Candidate.Id, linked.Candidate.Id };
        var review = await ReverseReview(s, saved.ActionId!.Value, ids); var result = await Reverse(s, review);
        var c = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(x => x.Id == canonical.Candidate.Id, Ct);
        var l = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(x => x.Id == linked.Candidate.Id, Ct);
        l.EventId.ShouldBeNull(); l.DuplicateEventId.ShouldBe(saved.EventIds.Single()); l.DuplicateSourceId.ShouldBe(c.SourceId);
        if (subset == "protected_canonical")
        {
            result.Status.ShouldBe(VetMutationStatus.NoChange); result.EventIds.ShouldBeEmpty();
            result.ProtectedIds.ShouldBe(saved.EventIds); result.ProtectedCandidateIds.Order().ShouldBe(ids.Order());
            (await Outcome(s, result.ActionId!.Value)).PhotoChanges.ShouldBeEmpty(); Pages(review).ShouldContain("ЗАЩИЩЕНО");
            JsonSerializer.Serialize(c, Json).ShouldBe(canonicalBefore); JsonSerializer.Serialize(l, Json).ShouldBe(linkedBefore);
            JsonSerializer.Serialize(await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct), Json).ShouldBe(factBefore);
        }
        else
        {
            result.Status.ShouldBe(VetMutationStatus.Applied);
            if (subset == "link_only")
            { result.EventIds.ShouldBeEmpty(); JsonSerializer.Serialize(c, Json).ShouldBe(canonicalBefore);
              JsonSerializer.Serialize(await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct), Json).ShouldBe(factBefore); }
            else { result.EventIds.ShouldBe(saved.EventIds); c.State.ShouldBe("deleted"); c.EventRevision.ShouldBe(2); c.RequiresExplicitRestoration.ShouldBeTrue(); }
            if (subset == "canonical_only") JsonSerializer.Serialize(l, Json).ShouldBe(linkedBefore);
            else { l.State.ShouldBe("deleted"); l.RequiresExplicitRestoration.ShouldBeTrue(); l.DuplicateEventRevision.ShouldBe(subset == "both" ? 2 : 1); }
            (await Outcome(s, result.ActionId!.Value)).PhotoChanges.Count.ShouldBe(ids.Length);
        }
        (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(1); (await Retention(s)).ShouldBe(retained);
        var target = await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.Id == saved.ActionId, Ct);
        target.ReversedByActionId.ShouldBe(subset == "both" ? result.ActionId : null);
    }
    [Theory]
    [InlineData("event")]
    [InlineData("candidate")]
    [InlineData("source")]
    public async Task A_post_preview_linked_canonical_change_stales_link_only_reversal_without_touching_the_fact(string change)
    {
        await SeedAsync(); await using var s = Open(); var canonical = await Prepare(s, 1); var saved = await SavePhoto(s, canonical);
        var linked = await Prepare(s, 2, sameBytes: true);
        var selection = (await Select(s, linked, "link", duplicate: "same")) with { LinkEventId = saved.EventIds.Single(), LinkEventRevision = 1 };
        var linkReview = await Review(s, Scope, [selection]); var linkedAction = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, linkReview), Ct);
        linkedAction.Status.ShouldBe(VetMutationStatus.Applied); var review = await ReverseReview(s, linkedAction.ActionId!.Value, [linked.Candidate.Id]);
        if (change == "event") await s.Context.VetEvents.Where(e => e.Id == saved.EventIds.Single()).ExecuteUpdateAsync(u => u.SetProperty(e => e.Value, 9.125m), Ct);
        if (change == "candidate") await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == canonical.Candidate.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.ManuallyCorrected, true), Ct);
        if (change == "source") await s.Context.Set<VetPhotoSource>().Where(c => c.Id == canonical.Source.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.CurrentOrdinal, c => c.CurrentOrdinal + 1), Ct);
        var before = await Mutable(s); (await Reverse(s, review)).Status.ShouldBe(VetMutationStatus.Stale); (await Mutable(s)).ShouldBe(before);
    }
    [Theory]
    [InlineData("exclude")]
    [InlineData("cancel")]
    public async Task Candidate_only_old_action_can_reverse_without_extraction_event_or_original_bytes(string disposition)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, image: false);
        var review = await Review(s, Scope, [await Select(s, item, disposition)]);
        var changed = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, review), Ct); changed.Status.ShouldBe(VetMutationStatus.Applied);
        var retained = await Retention(s); var inverse = await ReverseReview(s, changed.ActionId!.Value, [item.Candidate.Id]);
        var result = await Reverse(s, inverse); result.Status.ShouldBe(VetMutationStatus.Applied); result.EventIds.ShouldBeEmpty();
        (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0); (await s.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(0);
        var outcome = await Outcome(s, result.ActionId!.Value); outcome.PhotoChanges.Single().After.State.ShouldBe(item.Candidate.State);
        outcome.PhotoChanges.Single().AfterRevision.ShouldBe(outcome.PhotoChanges.Single().BeforeRevision + 1);
        (await Retention(s)).ShouldBe(retained);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Correction_or_explicit_restoration_reversal_recovers_exact_prior_fact_with_increasing_revisions(bool restoration)
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
        var selected = (await Select(s, item, "correct", Fact(item) with { Value = 7.25m,
            OccurredAtSource = "human_correction", ValueUnitSource = "human_correction" }, "separate", restoration))
            with { Context = new("7.25", "mmol/L", 2031, 5, 11, "10:20", "+00:00", CorrectionApproved: true) };
        var correctedReview = await Review(s, Scope, [selected], VetPhotoReviewKind.Correction);
        var corrected = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, correctedReview), Ct); corrected.Status.ShouldBe(VetMutationStatus.Applied);
        var retained = await Retention(s); Clock.UtcNow = Now.AddDays(4);
        var preview = await ReverseReview(s, corrected.ActionId!.Value, [item.Candidate.Id]); Pages(preview).ShouldContain("7.25 mmol/L"); Pages(preview).ShouldContain("5.6 mmol/L");
        var inverse = await Reverse(s, preview, 111); inverse.EventIds.ShouldBe(saved.EventIds);
        var fact = await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct);
        VetDiaryStore.State(fact).ShouldBe(VetDiaryStore.State(beforeFact)); fact.Revision.ShouldBe(beforeFact.Revision + 2);
        var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct);
        CandidateState(candidate).ShouldBe(CandidateState(beforeCandidate) with { EventRevision = fact.Revision, LastReviewId = null });
        candidate.Revision.ShouldBe(beforeCandidate.Revision + 2); (await Retention(s)).ShouldBe(retained);
    }
    [Fact]
    public async Task Same_bound_caption_insulin_interpretation_bytes_and_original_review_survive_selected_photo_reversal()
    {
        await SeedAsync(); await using var s = Open(); var photo = await Prepare(s, caption: true);
        var source = (await s.Diary.FindSourceAsync(Scope, photo.Source.TelegramMessageId, Ct)).ShouldNotBeNull();
        var parsedResult = (await s.Diary.GetResultAsync(Scope, source.Revision.Id, Ct)).ShouldNotBeNull();
        var parsed = VetInterpretationParser.Parse(parsedResult.Json).ShouldNotBeNull();
        var validated = VetEventValidation.Validate(parsed.Events.Single(), photo.Profile, source, parsedResult.Id);
        var insulin = await s.Diary.ApplyAsync(Save(Scope, source, photo.Profile, validated.State.ShouldNotBeNull()), Ct);
        insulin.Status.ShouldBe(VetMutationStatus.Applied); await s.Diary.SetProcessingAsync(Scope, source.Revision.Id, "ready", "written", null, Ct);
        var saved = await SavePhoto(s, photo); var captionFact = JsonSerializer.Serialize(await s.Context.VetEvents.AsNoTracking().SingleAsync(e => e.Id == insulin.EventIds.Single(), Ct), Json);
        var originalReview = await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.ActionId == saved.ActionId, Ct);
        var originalProof = JsonSerializer.Serialize(originalReview, Json); var retained = await Retention(s);
        Clock.UtcNow = Now.AddDays(5); var preview = await ReverseReview(s, saved.ActionId!.Value, [photo.Candidate.Id]);
        (await Reverse(s, preview)).EventIds.ShouldBe(saved.EventIds);
        JsonSerializer.Serialize(await s.Context.VetEvents.AsNoTracking().SingleAsync(e => e.Id == insulin.EventIds.Single(), Ct), Json).ShouldBe(captionFact);
        (await s.Context.VetEvents.SingleAsync(e => e.Id == insulin.EventIds.Single(), Ct)).Value.ShouldBe(0.125m);
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == originalReview.Id, Ct), Json).ShouldBe(originalProof);
        (await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.Id == insulin.ActionId, Ct)).ReversedByActionId.ShouldBeNull();
        (await Retention(s)).ShouldBe(retained);
    }
    [Theory]
    [InlineData(111L)]
    [InlineData(222L)]
    public async Task First_valid_member_wins_real_transaction_race_and_restart_replay_keeps_first_actor(long firstActor)
    {
        await SeedAsync(); await using var setup = Open(); var (items, saved) = await SaveMany(setup, 1);
        var review = await ReverseReview(setup, saved.ActionId!.Value, [items[0].Candidate.Id]);
        var held = new HoldWrite("UPDATE vet_events"); var observed = new ObserveBotLock();
        await using var first = Open(interceptor: held); await using var second = Open(interceptor: observed);
        var winner = Reverse(first, review, firstActor); Task<VetMutationResult>? loser = null;
        try
        { await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); loser = Reverse(second, review, firstActor == 111 ? 222 : 111);
          await observed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { held.Release(); if (loser != null) await Task.WhenAll(winner, loser).WaitAsync(TimeSpan.FromSeconds(20)); else await winner.WaitAsync(TimeSpan.FromSeconds(20)); }
        var a = await winner.WaitAsync(TimeSpan.FromSeconds(20)); var b = await loser!.WaitAsync(TimeSpan.FromSeconds(20));
        a.Status.ShouldBe(VetMutationStatus.Applied); b.Status.ShouldBe(VetMutationStatus.AlreadyApplied); b.ActionId.ShouldBe(a.ActionId); b.Revisions.ShouldBe(a.Revisions);
        await using var restart = Open(); var accepted = await restart.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == review.Id, Ct);
        accepted.DecisionActorUserId.ShouldBe(firstActor); accepted.ActionId.ShouldBe(a.ActionId);
        (await restart.Context.VetDiaryActions.CountAsync(x => x.Kind == "photo_reverse", Ct)).ShouldBe(1);
        (await restart.Context.VetDiaryActionChanges.CountAsync(x => x.ActionId == a.ActionId, Ct)).ShouldBe(1);
        var state = await Mutable(restart); var again = await Reverse(restart, review, 111);
        again.Status.ShouldBe(VetMutationStatus.AlreadyApplied); again.ActionId.ShouldBe(a.ActionId); (await Mutable(restart)).ShouldBe(state);
        (await restart.Context.VetEvents.SingleAsync(Ct)).DeletedByUserId.ShouldBe(firstActor);
    }
    [Theory]
    [InlineData("vet_photo_candidates")]
    [InlineData("vet_photo_reviews")]
    public async Task Failure_rolls_back_event_candidate_action_review_and_same_context_retry_recovers_exactly_once(string table)
    {
        await SeedAsync(); await using var setup = Open(); var (items, saved) = await SaveMany(setup, 1);
        var review = await ReverseReview(setup, saved.ActionId!.Value, [items[0].Candidate.Id]); var before = await Mutable(setup); var retained = await Retention(setup);
        var fail = new FailWrite(table); await using var s = Open(interceptor: fail);
        var error = await Should.ThrowAsync<DbUpdateException>(() => Reverse(s, review));
        error.InnerException.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("synthetic reversal write failure"); fail.Hits.ShouldBe(1);
        await using (var verify = Open()) { (await Mutable(verify)).ShouldBe(before); (await Retention(verify)).ShouldBe(retained); }
        var result = await Reverse(s, review); result.Status.ShouldBe(VetMutationStatus.Applied); result.Revisions.Single().Revision.ShouldBe(2);
        await using var restart = Open(); var after = await Mutable(restart);
        (await Reverse(restart, review, 111)).Status.ShouldBe(VetMutationStatus.AlreadyApplied); (await Mutable(restart)).ShouldBe(after);
        (await Retention(restart)).ShouldBe(retained); (await restart.Context.VetDiaryActions.CountAsync(a => a.Kind == "photo_reverse", Ct)).ShouldBe(1);
    }
    [Fact]
    public async Task Accepted_lost_ack_replay_is_durable_after_profile_change_and_another_approved_member()
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 1);
        var review = await ReverseReview(s, saved.ActionId!.Value, [items[0].Candidate.Id]); var result = await Reverse(s, review);
        result.Status.ShouldBe(VetMutationStatus.Applied); await s.Context.Set<VetProfile>().ExecuteUpdateAsync(u => u.SetProperty(p => p.Revision, p => p.Revision + 1), Ct);
        await using var restart = Open(); var before = await Mutable(restart);
        var replay = await Reverse(restart, review, 111); replay.Status.ShouldBe(VetMutationStatus.AlreadyApplied);
        replay.ActionId.ShouldBe(result.ActionId); replay.Revisions.ShouldBe(result.Revisions); replay.ProtectedCandidateIds.ShouldBe(result.ProtectedCandidateIds);
        (await Mutable(restart)).ShouldBe(before); (await restart.Context.Set<VetPhotoReview>().SingleAsync(r => r.Id == review.Id, Ct)).DecisionActorUserId.ShouldBe(222);
    }
    [Fact]
    public async Task Ordinary_latest_own_undo_of_new_reversal_restores_fact_without_erasing_older_evidence()
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 1); Clock.UtcNow = Now.AddDays(3);
        var review = await ReverseReview(s, saved.ActionId!.Value, [items[0].Candidate.Id]); var result = await Reverse(s, review, 111);
        result.Status.ShouldBe(VetMutationStatus.Applied); var accepted = await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == review.Id, Ct);
        var immutableAccepted = JsonSerializer.Serialize(accepted, Json); var retained = await Retention(s);
        var undone = await s.Diary.UndoAsync(Scope, 111, Guid.NewGuid(), Ct); undone.Status.ShouldBe(VetMutationStatus.Applied); undone.EventIds.ShouldBe(saved.EventIds);
        var fact = await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct); fact.DeletedAt.ShouldBeNull(); fact.Revision.ShouldBe(3); fact.Value.ShouldBe(5.6m);
        var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct); candidate.State.ShouldBe("saved"); candidate.EventRevision.ShouldBe(3);
        (await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.Id == saved.ActionId, Ct)).ReversedByActionId.ShouldBe(result.ActionId);
        (await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.Id == result.ActionId, Ct)).ReversedByActionId.ShouldBe(undone.ActionId);
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Id == accepted.Id, Ct), Json).ShouldBe(immutableAccepted);
        (await Retention(s)).ShouldBe(retained);
    }
    [Fact]
    public async Task Application_shows_full_exact_preview_once_and_routes_only_reverse_confirmation()
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 1); var key = Guid.NewGuid();
        var op = Operation(saved.ActionId!.Value, [items[0].Candidate.Id]); var beforeFacts = await s.Context.VetEvents.AsNoTracking().ToArrayAsync(Ct);
        await App(s).StageAsync(Bot, s.Telegram, Text("synthetic explicit reversal"), op, key, Ct);
        var review = await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Kind == "reverse", Ct);
        review.CompletePreviewDelivered.ShouldBeTrue(); review.RequesterUserId.ShouldBe(111);
        JsonSerializer.Serialize(await s.Context.VetEvents.AsNoTracking().ToArrayAsync(Ct), Json).ShouldBe(JsonSerializer.Serialize(beforeFacts, Json));
        var sent = s.Telegram.SentMessages.Count; await App(s).StageAsync(Bot, s.Telegram, Text("synthetic explicit reversal"), op, key, Ct);
        (await s.Context.Set<VetPhotoReview>().CountAsync(r => r.Kind == "reverse", Ct)).ShouldBe(1); s.Telegram.SentMessages.Count.ShouldBe(sent);
        (await App(s).ConfirmAsync(Scope, new VetPhotoReview { Kind = "save" }, 222, review.AcceptancePromptMessageId, s.Telegram, 900, Ct)).ShouldBeFalse();
        (await App(s).ConfirmAsync(Scope, review, 222, review.AcceptancePromptMessageId, s.Telegram, 900, Ct)).ShouldBeTrue();
        (await s.Context.VetEvents.SingleAsync(Ct)).DeletedByUserId.ShouldBe(222); (await s.Context.VetDiaryActions.CountAsync(a => a.Kind == "photo_reverse", Ct)).ShouldBe(1);
        s.Telegram.SentMessages.Last().Text.ShouldContain("изменено фактов 1");
    }
    [Theory]
    [InlineData("ambiguous")]
    [InlineData("full_action")]
    [InlineData("foreign_member")]
    [InlineData("oversized_preview")]
    [InlineData("failed_delivery")]
    public async Task Application_preserves_facts_for_ambiguous_unapproved_oversized_or_failed_preview(string boundary)
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 1); var op = Operation(saved.ActionId!.Value, [items[0].Candidate.Id]);
        if (boundary == "ambiguous") op = op with { ActionId = null, BatchId = items[0].Source.BatchId };
        if (boundary == "full_action") op = op with { CandidateIds = [] };
        if (boundary == "oversized_preview") await s.Context.Set<VetPhotoCandidate>().ExecuteUpdateAsync(u => u.SetProperty(c => c.CorrectionProvenanceJson, JsonSerializer.Serialize(new { note = new string('x', 12000) }, Json)), Ct);
        if (boundary == "failed_delivery") s.Telegram.ThrowOnSendToChatId = Scope.ChatId;
        var before = await Mutable(s); var text = Text("synthetic selected reversal", actor: boundary == "foreign_member" ? 333 : 111);
        await App(s).StageAsync(Bot, s.Telegram, text, op, Guid.NewGuid(), Ct);
        var reverse = await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleOrDefaultAsync(r => r.Kind == "reverse", Ct);
        if (boundary == "failed_delivery")
        { var failed = reverse.ShouldNotBeNull(); failed.State.ShouldBe("preview_failed"); failed.CompletePreviewDelivered.ShouldBeFalse();
          (await Reversals(s).ApplyReversalAsync(new(Scope, failed.Id, failed.Revision, failed.OperationKey, 222), Ct)).Status.ShouldBe(VetMutationStatus.Stale); }
        else if (boundary == "full_action")
        { var full = reverse.ShouldNotBeNull(); full.CompletePreviewDelivered.ShouldBeTrue();
          JsonSerializer.Deserialize<VetPhotoReversalEntry[]>(full.SelectionJson, Json)!.Single().CandidateId.ShouldBe(items[0].Candidate.Id); }
        else { reverse.ShouldBeNull(); (await Mutable(s)).ShouldBe(before); }
        (await s.Context.VetDiaryActions.CountAsync(a => a.Kind == "photo_reverse", Ct)).ShouldBe(0);
        (await s.Context.VetEvents.SingleAsync(Ct)).DeletedAt.ShouldBeNull(); (await s.Context.VetEvents.SingleAsync(Ct)).Value.ShouldBe(5.6m);
    }
    [Fact]
    public async Task Historical_fifty_one_membership_refuses_action_wide_selection_but_allows_exact_bounded_subset()
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 50);
        Clock.UtcNow = Now.AddMinutes(1); var extra = await Prepare(s, 100); var extraSaved = await SavePhoto(s, extra);
        var original = await Outcome(s, saved.ActionId!.Value); var additional = await Outcome(s, extraSaved.ActionId!.Value);
        var expanded = original with { PhotoChanges = original.PhotoChanges.Concat(additional.PhotoChanges).ToArray(),
            EventIds = original.EventIds.Concat(additional.EventIds).ToArray(), Revisions = original.Revisions.Concat(additional.Revisions).ToArray() };
        await s.Context.VetDiaryActions.Where(a => a.Id == saved.ActionId).ExecuteUpdateAsync(u => u.SetProperty(a => a.OutcomeJson, JsonSerializer.Serialize(expanded)), Ct);
        var change = await s.Context.VetDiaryActionChanges.AsNoTracking().SingleAsync(c => c.ActionId == extraSaved.ActionId, Ct);
        s.Context.Add(new VetDiaryActionChange { FamilyId = FamilyId, BotDbId = Bot.BotDbId, ActionId = saved.ActionId!.Value,
            EventId = change.EventId, BeforeJson = change.BeforeJson, AfterJson = change.AfterJson, BeforeRevision = change.BeforeRevision, AfterRevision = change.AfterRevision });
        await s.Context.SaveChangesAsync(Ct); var before = await Mutable(s);
        (await Reversals(s).ReadReversalAsync(Scope, saved.ActionId!.Value, [], 111, Ct)).ShouldBeNull(); (await Mutable(s)).ShouldBe(before);
        var review = await ReverseReview(s, saved.ActionId!.Value, [extra.Candidate.Id]);
        JsonSerializer.Deserialize<VetPhotoReversalEntry[]>(review.SelectionJson, Json)!.Length.ShouldBe(1);
        var result = await Reverse(s, review); result.Status.ShouldBe(VetMutationStatus.Applied); result.EventIds.ShouldBe(extraSaved.EventIds);
        (await s.Context.VetEvents.CountAsync(e => e.DeletedAt == null, Ct)).ShouldBe(50);
        (await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.Id == saved.ActionId, Ct)).ReversedByActionId.ShouldBeNull();
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Natural_confirmation_without_callback_still_requires_every_delivered_page(bool complete)
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 1);
        var review = await ReverseReview(s, saved.ActionId!.Value, [items[0].Candidate.Id], deliver: complete); var before = await Mutable(s);
        var result = await Reversals(s).ApplyReversalAsync(new(Scope, review.Id, review.Revision, review.OperationKey, 222), Ct);
        result.Status.ShouldBe(complete ? VetMutationStatus.Applied : VetMutationStatus.Stale);
        if (complete) { result.EventIds.ShouldBe(saved.EventIds); (await s.Context.VetEvents.SingleAsync(Ct)).DeletedByUserId.ShouldBe(222); }
        else (await Mutable(s)).ShouldBe(before);
    }
    [Fact]
    public async Task Real_later_admitted_input_pointer_change_stales_preview_and_preserves_immutable_old_evidence()
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 1); var item = items[0];
        var review = await ReverseReview(s, saved.ActionId!.Value, [item.Candidate.Id]); var bytes = Png(1);
        var edit = Text("synthetic changed photo caption", 1) with { Kind = MessageKind.Photo, IsEdit = true, EditedAt = Now.AddMinutes(1) };
        var admitted = await Photos(s).AdmitAsync(Scope, edit, 9000, new("synthetic-file-1", "synthetic-unique-1", "synthetic.png", "image/png", bytes.Length, 32, 24), null, Ct);
        admitted.Input.ShouldNotBeNull().Id.ShouldNotBe(item.Input.Id);
        var source = await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(Ct); source.CurrentInputRevisionId.ShouldBe(admitted.Input!.Id); source.CurrentOrdinal.ShouldBe(2);
        var before = await Mutable(s); var retained = await Retention(s);
        (await Reverse(s, review)).Status.ShouldBe(VetMutationStatus.Stale); (await Mutable(s)).ShouldBe(before); (await Retention(s)).ShouldBe(retained);
        (await s.Context.Set<VetPhotoExtraction>().SingleAsync(Ct)).Id.ShouldBe(item.Result!.Id);
    }
    [Fact]
    public async Task An_original_action_actor_losing_approval_does_not_block_another_fresh_approved_confirmer()
    {
        await SeedAsync(); await using var s = Open(); var (items, saved) = await SaveMany(s, 1);
        await s.Context.FamilyMembers.Where(m => m.TelegramUserId == 222).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        var review = await ReverseReview(s, saved.ActionId!.Value, [items[0].Candidate.Id]);
        Pages(review).ShouldContain("исходный участник 222"); var result = await Reverse(s, review, 111);
        result.Status.ShouldBe(VetMutationStatus.Applied); (await s.Context.VetDiaryActions.AsNoTracking().SingleAsync(a => a.Id == result.ActionId, Ct)).ActorUserId.ShouldBe(111);
        (await s.Context.VetEvents.SingleAsync(Ct)).SourceAuthorUserId.ShouldBe(111);
    }
    [Fact]
    public async Task Caller_cancellation_while_owned_transaction_write_is_held_rolls_back_and_allows_same_context_retry()
    {
        await SeedAsync(); await using var setup = Open(); var (items, saved) = await SaveMany(setup, 1);
        var review = await ReverseReview(setup, saved.ActionId!.Value, [items[0].Candidate.Id]); var before = await Mutable(setup);
        var held = new HoldWrite("UPDATE vet_events"); await using var s = Open(interceptor: held); using var cancel = new CancellationTokenSource();
        var work = Reversals(s).ApplyReversalAsync(Accept(Scope, review), cancel.Token);
        try { await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work); }
        finally
        { cancel.Cancel(); held.Release(); if (!work.IsCompleted)
          { try { await work.WaitAsync(TimeSpan.FromSeconds(20)); } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { } } }
        cancel.IsCancellationRequested.ShouldBeTrue(); await using (var verify = Open()) (await Mutable(verify)).ShouldBe(before);
        var result = await Reverse(s, review); result.Status.ShouldBe(VetMutationStatus.Applied);
        (await s.Context.VetDiaryActions.CountAsync(a => a.Kind == "photo_reverse", Ct)).ShouldBe(1); result.Revisions.Single().Revision.ShouldBe(2);
    }
    private static VetPhotoOperation Operation(long id, Guid[] candidates) =>
        new("reverse", null, null, null, [], candidates, null, [], id, null, null, null, null, null, null, null, []);
    private VetPhotoReversalApplication App(VetTestSession s)
    {
        var p = Photos(s); var composer = new VetPhotoReviewComposer(p, p, s.Profiles, s.Diary, NullLogger<VetPhotoReviewComposer>.Instance);
        return new(Reversals(s), p, composer, new ReadApprovals(s));
    }
    private sealed class FailWrite(string table) : DbCommandInterceptor
    {
        public int Hits { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { if (Hits == 0 && command.CommandText.Replace("\"", "", StringComparison.Ordinal).Contains("UPDATE " + table, StringComparison.OrdinalIgnoreCase))
          { Hits++; throw new InvalidOperationException("synthetic reversal write failure"); } return ValueTask.FromResult(result); }
    }
    private sealed class HoldWrite(string needle) : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => release.TrySetResult(true);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { if (command.CommandText.Replace("\"", "", StringComparison.Ordinal).Contains(needle, StringComparison.OrdinalIgnoreCase))
          { Entered.TrySetResult(true); await release.Task.WaitAsync(cancellationToken); } return result; }
    }
    private sealed class ObserveBotLock : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal) && command.CommandText.Contains("vet:", StringComparison.Ordinal)) Entered.TrySetResult(true); return ValueTask.FromResult(result); }
    }
    private sealed class ReadApprovals(VetTestSession s) : IApprovalService
    {
        public Task<FamilyMemberStatus?> FindFamilyMemberStatusAsync(long family, long actor, CancellationToken ct) => s.Context.FamilyMembers.Where(m => m.FamilyId == family && m.TelegramUserId == actor).Select(m => (FamilyMemberStatus?)m.Status).SingleOrDefaultAsync(ct);
        public Task<PlaceStatus?> FindPlaceStatusAsync(long bot, long chat, int? topic, CancellationToken ct) => s.Context.Places.Where(p => p.BotId == bot && p.ChatId == chat && p.TopicId == topic).Select(p => (PlaceStatus?)p.Status).SingleOrDefaultAsync(ct);
        public Task<long> GetOrCreatePendingPlaceAsync(long bot, long chat, int? topic, string title, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApprovalResolution> ResolvePlaceApprovalAsync(long id, bool approve, CancellationToken ct) => throw new NotSupportedException();
        public Task<PlaceStatus> GetPlaceStatusAsync(long id, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> GetPlaceReplyToAllAsync(long id, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> GetOrCreatePendingFamilyMemberAsync(long family, long actor, string display, string? username, string bot, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApprovalResolution> ResolveUserApprovalAsync(long id, bool approve, CancellationToken ct) => throw new NotSupportedException();
        public Task<FamilyMemberStatus> GetFamilyMemberStatusAsync(long id, CancellationToken ct) => throw new NotSupportedException();
    }
}
