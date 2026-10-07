using System.Data.Common;
using System.Globalization;
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
using Assistant.IntegrationTests.Host;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Assistant.IntegrationTests.Vet.Photos;

public abstract class VetPhotoPresentationFixture : VetTestBase
{
    protected static readonly CancellationToken Ct = CancellationToken.None;
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected VetPhotoStore Photos(VetTestSession s, VetPhotoCapacity? capacity = null) =>
        new(s.Context, s.Current, Clock, capacity ?? new(), new VetPhotoImageDecoder());
    protected VetPhotoReviewComposer Composer(VetTestSession s) => new(Photos(s), Photos(s), s.Profiles,
        s.Diary, NullLogger<VetPhotoReviewComposer>.Instance);
    protected static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    protected sealed record Item(VetPhotoSource Source, VetPhotoInputRevision Input,
        VetPhotoCandidate Candidate, VetPhotoExtraction? Result, Guid? TextRevision);
    protected static byte[] Png(int color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(32, 24, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor((byte)color, 32, 64)); using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
    protected static string ImageJson(Guid source, Guid input, int number, bool complete = true, int displays = 1) =>
        JsonSerializer.Serialize(new
        {
            schema_version = 1, photo_source_id = source.ToString("D"), input_revision_id = input.ToString("D"), kind = "meter",
            displays = Enumerable.Range(0, displays).Select(index => new
            {
                value_text = (5m + (number + index) / 100m).ToString(CultureInfo.InvariantCulture),
                decimal_value = 5m + (number + index) / 100m, unit = complete ? "mmol/L" : (string?)null,
                year = complete ? 2031 : (int?)null, year_displayed = complete,
                month = complete ? 5 : (int?)null, day = complete ? 11 : (int?)null,
                time = complete ? $"10:{(number + index) % 60:00}" : null, offset = complete ? "+00:00" : null
            }).ToArray(), reasons = Array.Empty<string>(), notes = (string?)null
        }, Json);
    protected static string CaptionJson(string intent = "record", string? value = null) => JsonSerializer.Serialize(new
    {
        needs_reply = false, events = Array.Empty<object>(), unclear = Array.Empty<string>(),
        photo_caption = new { intent, value, unit = "mmol/L", year = 2031, month = 5, day = 11, time = "10:01", offset = "+00:00" }
    }, Json);
    protected async Task<Item> Prepare(VetTestSession s, int number = 1, bool image = true, bool complete = true,
        int? color = null, string? album = null, string? captionState = null, string? captionJson = null,
        int displays = 1, int topic = 7, int? readingNumber = null, VetDiaryScope? selectedScope = null)
    {
        var scope = selectedScope ?? (Scope with { TopicId = topic });
        var update = checked(await s.Context.Bots.AsNoTracking().Where(b => b.Id == scope.BotDbId)
            .Select(b => b.LastUpdateId).SingleAsync(Ct) + 1);
        var message = Text("synthetic photo caption", number, 111, topic) with { Kind = MessageKind.Photo, MediaGroupId = album,
            ChatId = scope.ChatId, TopicId = scope.TopicId };
        VetAdmittedSource? text = null;
        if (captionState != null) text = await s.Diary.AdmitAsync(scope, message, update, Ct);
        var bytes = Png(color ?? number);
        var admission = await Photos(s).AdmitAsync(scope, message, update,
            new($"synthetic-file-{number}", $"synthetic-unique-{number}", "synthetic.png", "image/png", bytes.Length, 32, 24), text?.Revision.Id, Ct);
        admission.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        var stored = await s.Messages.StoreAsync(scope.TelegramBotId, update, message, Ct);
        (await Photos(s).BindMessageAsync(scope, admission.Source!.Id, stored.MessageDbId.ShouldNotBeNull(), Ct)).ShouldBeTrue();
        if (text != null)
        {
            await s.Diary.LinkMessageAsync(scope, text.Source.Id, stored.MessageDbId!.Value, Ct);
            if (captionState is "written" or "completed")
            {
                await s.Diary.SetProcessingAsync(scope, text.Revision.Id, "admitted", "dispatching", null, Ct);
                await s.Diary.SaveResultAsync(scope, text.Revision.Id, captionJson ?? CaptionJson(), "synthetic-text-model", null, Ct);
            }
            await s.Context.Set<VetTextSourceRevision>().Where(r => r.Id == text.Revision.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, captionState), Ct);
        }
        VetPhotoExtraction? result = null;
        if (image)
        {
            var reservation = await Photos(s).ReserveDownloadAsync(scope, admission.Source.Id, admission.Input!.Id, 111, Ct);
            reservation.Status.ShouldBe(VetPhotoArchiveStatus.Reserved); var download = reservation.Claim.ShouldNotBeNull();
            (await Photos(s).CommitOriginalAsync(new(scope, 111, download.Attempt.Id, download.ClaimToken,
                admission.Input.Id, bytes, new VetPhotoImageDecoder().Decode(bytes, Ct).Image.ShouldNotBeNull()), Ct))
                .Status.ShouldBe(VetPhotoArchiveStatus.Retained);
            var claimed = await Photos(s).ClaimCurrentImageAsync(scope, admission.Source.Id, admission.Input.Id, 111, Ct);
            claimed.Status.ShouldBe(VetPhotoImageStatus.Claimed); var claim = claimed.Claim.ShouldNotBeNull();
            (await Photos(s).MarkImageDispatchedAsync(scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
            var completed = await Photos(s).CompleteImageAsync(new(scope, claim.AttemptKey, claim.ClaimToken, 111,
                claim.SourceId, claim.InputRevisionId, "synthetic-image-model", ImageJson(claim.SourceId, claim.InputRevisionId, readingNumber ?? number, complete, displays)), Ct);
            completed.Status.ShouldBe(VetPhotoImageStatus.Installed); result = completed.Extraction.ShouldNotBeNull();
        }
        s.Context.ChangeTracker.Clear();
        return new(await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(x => x.Id == admission.Source.Id, Ct),
            await s.Context.Set<VetPhotoInputRevision>().AsNoTracking().SingleAsync(x => x.Id == admission.Input!.Id, Ct),
            await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(x => x.SourceId == admission.Source.Id, Ct), result, text?.Revision.Id);
    }
    protected async Task<VetPhotoBatchSnapshot> Batch(VetTestSession s, Guid id) =>
        (await Photos(s).GetBatchAsync(Scope, id, 222, Ct)).ShouldNotBeNull();
    protected async Task Close(VetTestSession s, Guid id)
    {
        var batch = await Batch(s, id);
        (await Photos(s).CloseCollectionAsync(Scope, id, batch.Batch.ReviewRevision, 111, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
    }
    protected async Task<VetPhotoHumanProposal> Human(VetTestSession s, Item item, VetPhotoContext? context = null, bool restore = false)
    {
        var batch = await Batch(s, item.Source.BatchId!.Value);
        var current = batch.Items.Single(i => i.Source.Id == item.Source.Id);
        return new(Scope, batch.Batch.Id, batch.Batch.ReviewRevision, current.Candidate.Id,
            current.Candidate.Revision, current.Input.Id, current.Source.CurrentOrdinal, 222,
            context ?? new("7.25", "mmol/L", 2031, 5, 11, "09:32", "+00:00", CorrectionApproved: true), restore);
    }
    protected async Task<VetPhotoReview> Build(VetTestSession s, Guid batch, IReadOnlyList<Guid>? selected = null, bool restore = false)
    {
        var composed = await Composer(s).BuildBatchAsync(Scope, batch, 222, selected, restore, Ct);
        new[] { VetPhotoWorkflowStatus.Applied, VetPhotoWorkflowStatus.Existing }.ShouldContain(composed.Status);
        return composed.Review.ShouldNotBeNull();
    }
    protected async Task<VetPhotoReview> Deliver(VetTestSession s, VetPhotoReview review, ITelegramClient? client = null, bool explicitRetry = false)
    {
        var status = await Composer(s).DeliverAsync(Scope, review, 222, client ?? s.Telegram, 900, Ct, explicitRetry: explicitRetry);
        new[] { VetPhotoWorkflowStatus.Applied, VetPhotoWorkflowStatus.Existing }.ShouldContain(status);
        return (await Photos(s).ReadPreviewAsync(Scope, review.Id, 222, Ct)).ShouldNotBeNull();
    }
    protected Task<VetMutationResult> Accept(VetTestSession s, VetPhotoReview review, int? prompt = null, long actor = 222,
        VetDiaryScope? scope = null) => s.Diary.ApplyPhotoReviewAsync(new(scope ?? Scope, review.Id, review.Revision,
            review.OperationKey, actor, prompt), Ct);
    protected static string Pages(VetPhotoReview review) => string.Join("\n", JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!);
    protected async Task<string> Snapshot(VetTestSession s) => JsonSerializer.Serialize(new
    {
        Candidates = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Events = await s.Context.VetEvents.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Actions = await s.Context.VetDiaryActions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Originals = await s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Blobs = await s.Context.Set<VetPhotoBlob>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct)
    }, Json);
    protected async Task NoFacts(VetTestSession s)
    {
        s.Chat.RequestedMessages.ShouldBeEmpty();
        (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
        (await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
        (await s.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(0);
    }
    protected sealed class PreviewTelegram(FakeTelegramClient inner, int? failSend = null, bool failButtons = false) : ITelegramClient
    {
        public List<(int MessageId, string Text)> Delivered { get; } = [];
        public List<(long Chat, int Message, IReadOnlyList<InlineButton> Buttons)> EditedButtons { get; } = [];
        private int calls;
        public async Task<int> SendTextAsync(long chat, int? topic, string text, int? reply, CancellationToken ct)
        {
            if (++calls == failSend) throw new IOException("synthetic page send failure");
            var id = await inner.SendTextAsync(chat, topic, text, reply, ct); Delivered.Add((id, text)); return id;
        }
        public Task EditMessageButtonsAsync(long chat, int message, IReadOnlyList<InlineButton> buttons, CancellationToken ct)
        {
            if (failButtons) throw new IOException("synthetic button edit failure");
            EditedButtons.Add((chat, message, buttons)); return inner.EditMessageButtonsAsync(chat, message, buttons, ct);
        }
        public Task<long> DownloadFileAsync(string id, Stream destination, long max, CancellationToken ct) => inner.DownloadFileAsync(id, destination, max, ct);
        public Task<BotIdentity> GetMeAsync(CancellationToken ct) => inner.GetMeAsync(ct);
        public Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(long offset, int timeout, IReadOnlyList<UpdateKind> kinds, CancellationToken ct) => inner.GetUpdatesAsync(offset, timeout, kinds, ct);
        public Task SendChatActionAsync(long chat, int? topic, string action, CancellationToken ct) => inner.SendChatActionAsync(chat, topic, action, ct);
        public Task SetReactionAsync(long chat, int message, string? emoji, CancellationToken ct) => inner.SetReactionAsync(chat, message, emoji, ct);
        public Task<int> SendTextWithButtonsAsync(long chat, int? topic, string text, IReadOnlyList<InlineButton> buttons, int? reply, CancellationToken ct) => inner.SendTextWithButtonsAsync(chat, topic, text, buttons, reply, ct);
        public Task EditMessageTextAsync(long chat, int message, string text, CancellationToken ct) => inner.EditMessageTextAsync(chat, message, text, ct);
        public Task AnswerCallbackAsync(string id, string? text, CancellationToken ct) => inner.AnswerCallbackAsync(id, text, ct);
        public Task<string> GetManagedBotTokenAsync(long id, CancellationToken ct) => inner.GetManagedBotTokenAsync(id, ct);
    }
}

public sealed class VetPhotoPresentationStoreTests : VetPhotoPresentationFixture
{
    [Fact]
    public async Task ReviewFix_identical_new_group_with_saved_external_fact_waits_for_explicit_same_and_links_without_new_facts()
    {
        await SeedAsync(); await using var s = Open(); var external = await Prepare(s, 9, color: 99, readingNumber: 1);
        var original = await Deliver(s, await Build(s, external.Source.BatchId!.Value));
        (await Accept(s, original)).Status.ShouldBe(VetMutationStatus.Applied);
        var saved = await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct);
        var opened = await Photos(s).StartCollectionAsync(Scope, 111, Ct);
        var one = await Prepare(s, 1, color: 1); var two = await Prepare(s, 2, color: 1, readingNumber: 1);
        await Close(s, opened.Batch!.Id);
        var held = await Composer(s).BuildBatchAsync(Scope, opened.Batch.Id, 222, null, false, Ct);
        held.Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete); held.Review.ShouldBeNull();
        string.Join("\n", held.Pages).ShouldContain("Выберите:"); (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(1);
        foreach (var id in new[] { one.Candidate.Id, two.Candidate.Id })
        {
            var b = await Batch(s, opened.Batch.Id); var item = b.Items.Single(i => i.Candidate.Id == id);
            (await Photos(s).ChangeCandidateAsync(new(Scope, b.Batch.Id, id, b.Batch.ReviewRevision, item.Candidate.Revision,
                item.Input.Id, item.Source.CurrentOrdinal, item.Candidate.ExtractionResultId, 222,
                VetPhotoCandidateChangeKind.DuplicateExisting, DuplicateEventId: saved.Id, DuplicateEventRevision: saved.Revision), Ct)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        }
        var linkedReview = await Deliver(s, await Build(s, opened.Batch.Id));
        var links = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(linkedReview.SelectionJson, Json)!;
        links.Length.ShouldBe(2); links.ShouldAllBe(i => i.Disposition == "link" && i.DuplicateDecision == "same"
            && i.LinkEventId == saved.Id && i.LinkCandidateId == null);
        (await Accept(s, linkedReview)).Status.ShouldBe(VetMutationStatus.Applied);
        (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(1);
        foreach (var id in new[] { one.Candidate.Id, two.Candidate.Id })
        {
            var linked = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == id, Ct);
            linked.State.ShouldBe("linked"); linked.EventId.ShouldBeNull(); linked.DuplicateEventId.ShouldBe(saved.Id);
        }
        s.Chat.RequestedMessages.ShouldBeEmpty(); (await s.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }
    [Theory]
    [InlineData("effective")]
    [InlineData("reference")]
    public async Task ReviewFix_identical_group_with_outside_pending_or_read_only_reference_never_stages_a_false_canonical(string outside)
    {
        await SeedAsync(); await using var s = Open();
        var external = await Prepare(s, 9, complete: outside == "effective", color: outside == "reference" ? 1 : 99, readingNumber: 1);
        await Composer(s).BuildBatchAsync(Scope, external.Source.BatchId!.Value, 222, null, false, Ct);
        var opened = await Photos(s).StartCollectionAsync(Scope, 111, Ct);
        await Prepare(s, 1, color: 1); await Prepare(s, 2, color: 1, readingNumber: 1); await Close(s, opened.Batch!.Id);
        var held = await Composer(s).BuildBatchAsync(Scope, opened.Batch.Id, 222, null, false, Ct);
        held.Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete); held.Review.ShouldBeNull();
        string.Join("\n", held.Pages).ShouldContain("Выберите:");
        (await s.Context.Set<VetPhotoReview>().CountAsync(r => r.BatchId == opened.Batch.Id && r.Kind == "save", Ct)).ShouldBe(0);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained", Ct)).ShouldBe(3);
        await NoFacts(s);
    }
    [Fact]
    public async Task ReviewFix_explicit_separate_identical_sources_are_preserved_as_two_reviewed_facts()
    {
        await SeedAsync(); await using var s = Open(); var opened = await Photos(s).StartCollectionAsync(Scope, 111, Ct);
        await Prepare(s, 1, color: 1); await Prepare(s, 2, color: 1, readingNumber: 1); await Close(s, opened.Batch!.Id);
        foreach (var id in (await Batch(s, opened.Batch.Id)).Items.Select(i => i.Candidate.Id))
        {
            var b = await Batch(s, opened.Batch.Id); var i = b.Items.Single(i => i.Candidate.Id == id);
            (await Photos(s).ChangeCandidateAsync(new(Scope, b.Batch.Id, id, b.Batch.ReviewRevision, i.Candidate.Revision,
                i.Input.Id, i.Source.CurrentOrdinal, i.Candidate.ExtractionResultId, 222, VetPhotoCandidateChangeKind.DuplicateSeparate), Ct)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        }
        var review = await Deliver(s, await Build(s, opened.Batch.Id));
        var chosen = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!;
        chosen.Length.ShouldBe(2); chosen.ShouldAllBe(i => i.Disposition == "save" && i.DuplicateDecision == "separate" && i.LinkCandidateId == null);
        (await Accept(s, review)).Status.ShouldBe(VetMutationStatus.Applied); (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(2);
        (await s.Context.Set<VetPhotoCandidate>().CountAsync(c => c.State == "linked", Ct)).ShouldBe(0);
    }
    [Theory]
    [InlineData("topic")]
    [InlineData("member")]
    [InlineData("ordinal")]
    public async Task ReviewFix_auto_link_hint_fails_closed_for_wrong_place_revoked_author_or_noncurrent_input(string fence)
    {
        await SeedAsync(); await using var s = Open(); var opened = await Photos(s).StartCollectionAsync(Scope, 111, Ct);
        await Prepare(s, 1, color: 1); await Prepare(s, 2, color: 1, readingNumber: 1); await Close(s, opened.Batch!.Id);
        var review = await Build(s, opened.Batch.Id); var selected = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!;
        var canonical = selected.Single(i => i.DuplicateDecision == "canonical"); var scope = Scope;
        if (fence == "topic") scope = scope with { TopicId = 8 };
        if (fence == "member") await s.Context.FamilyMembers.Where(m => m.TelegramUserId == 111).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        if (fence == "ordinal") await s.Context.Set<VetPhotoSource>().Where(x => x.Id == canonical.SourceId).ExecuteUpdateAsync(u => u.SetProperty(x => x.CurrentOrdinal, x => x.CurrentOrdinal + 1), Ct);
        (await s.Diary.CanAutoLinkPhotoGroupAsync(scope, canonical.ProfileId, canonical.CandidateId,
            selected.Select(i => i.CandidateId).ToArray(), canonical.State!, Ct)).ShouldBeFalse(); await NoFacts(s);
    }
    [Fact]
    public async Task ReviewFix_human_correction_shows_current_fact_and_final_effective_provenance_before_acceptance()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s);
        (await Accept(s, await Deliver(s, await Build(s, item.Source.BatchId!.Value)))).Status.ShouldBe(VetMutationStatus.Applied);
        var old = await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct);
        (await Photos(s).ProposeHumanCorrectionAsync(await Human(s, item), Ct)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        var review = await Deliver(s, await Build(s, item.Source.BatchId.Value, [item.Candidate.Id]));
        var pages = Pages(review); pages.ShouldContain($"Текущий сохранённый факт #{old.Id}, версия 1: 5.01 mmol/L");
        pages.ShouldContain("локальное время 2031-05-11 10:01:00"); pages.ShouldContain("UTC 2031-05-11T10:01:00.0000000+00:00");
        pages.ShouldContain("основание времени image_or_caption"); pages.ShouldContain("основание значения/единицы image/image");
        pages.ShouldContain("7.25 mmol/L"); pages.ShouldContain("2031-05-11 09:32:00"); pages.ShouldContain("человеческое подтверждение True");
        pages.ShouldContain("значение=исправление пользователя"); pages.ShouldContain("единица=исправление пользователя");
        var selected = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!.Single();
        selected.Disposition.ShouldBe("correct"); selected.EventId.ShouldBe(old.Id); selected.State!.Value.ShouldBe(7.25m);
        (await Accept(s, review)).Status.ShouldBe(VetMutationStatus.Applied);
        var updated = await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct); updated.Id.ShouldBe(old.Id); updated.Revision.ShouldBe(2);
        updated.Value.ShouldBe(7.25m); updated.ValueUnitSource.ShouldBe("human_correction");
        s.Chat.RequestedMessages.ShouldBeEmpty(); (await s.Context.LlmCalls.CountAsync(Ct)).ShouldBe(0);
    }

    [Theory]
    [InlineData("written")]
    [InlineData("completed")]
    public async Task Bound_immutable_caption_supplies_explicit_clock_unit_and_date_without_a_text_glucose_value(string state)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, complete: false, captionState: state);
        var evidence = (await Photos(s).ReadEvidenceAsync(Scope, item.Source.Id, item.Input.Id, item.Result!.Id, 222, Ct)).ShouldNotBeNull();
        evidence.Caption!.State.ShouldBe(state); evidence.Caption.Interpretation!.Events.ShouldBeEmpty();
        var (context, failure) = VetPhotoCaptionContext.Read(evidence.Caption);
        failure.ShouldBeNull(); context.RawValue.ShouldBeNull(); context.Unit.ShouldBe("mmol/L");
        context.Year.ShouldBe(2031); context.Month.ShouldBe(5); context.Day.ShouldBe(11); context.Time.ShouldBe("10:01");
        context.Offset.ShouldBe("+00:00"); context.CorrectionApproved.ShouldBeFalse(); context.PreservedTime.ShouldBeNull();
        var review = await Build(s, item.Source.BatchId!.Value);
        var selection = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!.Single();
        selection.Context.ShouldBe(context); selection.State!.OccurredAt.ShouldBe(DateTimeOffset.Parse("2031-05-11T10:01:00Z"));
        await NoFacts(s);
    }

    [Theory]
    [InlineData("failed", "caption_not_saved")]
    [InlineData("paused", "caption_not_saved")]
    [InlineData("missing", "caption_not_saved")]
    [InlineData("dispatching", "caption_processing")]
    public async Task Failed_or_unfinished_caption_never_supplies_context_or_confirmed_text_facts(string state, string warning)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, captionState: state == "missing" ? null : state);
        var evidence = (await Photos(s).ReadEvidenceAsync(Scope, item.Source.Id, item.Input.Id, null, 222, Ct)).ShouldNotBeNull();
        evidence.Caption!.Interpretation.ShouldBeNull();
        var (context, failure) = VetPhotoCaptionContext.Read(evidence.Caption); context.ShouldBe(new VetPhotoContext()); failure.ShouldBe(warning);
        var composed = await Composer(s).BuildBatchAsync(Scope, item.Source.BatchId!.Value, 222, null, false, Ct);
        var text = composed.Review == null ? string.Join("\n", composed.Pages) : Pages(composed.Review);
        if (state == "dispatching") { composed.Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete); text.ShouldContain("caption_processing"); }
        else
        {
            composed.Review.ShouldNotBeNull(); text.ShouldContain("Подпись не сохранена");
            var review = await Deliver(s, composed.Review!); (await Accept(s, review)).Status.ShouldBe(VetMutationStatus.Applied);
            var fact = await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct);
            fact.Value.ShouldBe(5.01m); fact.OccurredAt.ShouldBe(DateTimeOffset.Parse("2031-05-11T10:01:00Z"));
            fact.SourceKind.ShouldBe("photo"); fact.TextSourceId.ShouldBeNull();
        }
        if (state == "dispatching") await NoFacts(s); s.Chat.RequestedMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Newer_TEXT_revision_cannot_replace_the_exact_caption_revision_bound_to_photo_input()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, complete: false, captionState: "written");
        var update = await s.Context.Bots.Where(b => b.Id == Bot.BotDbId).Select(b => b.LastUpdateId).SingleAsync(Ct) + 1;
        var message = Text("synthetic newer caption", 1) with { Kind = MessageKind.Photo, IsEdit = true, EditedAt = Now.AddSeconds(1) };
        var newer = await s.Diary.AdmitAsync(Scope, message, update, Ct);
        var stored = await s.Messages.StoreAsync(Bot.TelegramBotId, update, message, Ct);
        await s.Diary.LinkMessageAsync(Scope, newer.Source.Id, stored.MessageDbId!.Value, Ct);
        await s.Diary.SetProcessingAsync(Scope, newer.Revision.Id, "admitted", "dispatching", null, Ct);
        await s.Diary.SaveResultAsync(Scope, newer.Revision.Id, CaptionJson().Replace("10:01", "09:15"), "synthetic-text-model", null, Ct);
        await s.Context.Set<VetTextSourceRevision>().Where(r => r.Id == newer.Revision.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "written"), Ct);
        newer.Revision.Id.ShouldNotBe(item.TextRevision!.Value);
        var evidence = (await Photos(s).ReadEvidenceAsync(Scope, item.Source.Id, item.Input.Id, null, 222, Ct)).ShouldNotBeNull();
        VetPhotoCaptionContext.Read(evidence.Caption).Context.Time.ShouldBe("10:01");
        var review = await Build(s, item.Source.BatchId!.Value);
        JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!.Single().Context.Time.ShouldBe("10:01");
        await NoFacts(s);
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("candidate")]
    [InlineData("input")]
    [InlineData("ordinal")]
    [InlineData("actor")]
    public async Task Human_proposal_rechecks_current_revisions_and_actor_before_creating_evidence(string stale)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, image: false); var proposal = await Human(s, item);
        proposal = stale switch
        {
            "batch" => proposal with { BatchRevision = proposal.BatchRevision + 1 },
            "candidate" => proposal with { CandidateRevision = proposal.CandidateRevision + 1 },
            "input" => proposal with { InputRevisionId = Guid.NewGuid() },
            "ordinal" => proposal with { SourceOrdinal = proposal.SourceOrdinal + 1 },
            _ => proposal with { ActorUserId = 333 }
        };
        var before = await Snapshot(s);
        (await Photos(s).ProposeHumanCorrectionAsync(proposal, Ct)).ShouldBe(stale == "actor" ? VetPhotoWorkflowStatus.Refused : VetPhotoWorkflowStatus.Stale);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(0);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "human", Ct)).ShouldBe(0);
        (await Snapshot(s)).ShouldBe(before); await NoFacts(s);
    }

    [Theory]
    [InlineData("topic")]
    [InlineData("author")]
    [InlineData("message")]
    [InlineData("transport")]
    [InlineData("caption")]
    public async Task Unbound_text_revision_cannot_supply_caption_evidence(string mismatch)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, complete: false, captionState: "written");
        var textSource = await s.Context.Set<VetTextSourceRevision>().Where(r => r.Id == item.TextRevision).Select(r => r.SourceId).SingleAsync(Ct);
        if (mismatch == "topic") await s.Context.Set<VetTextSource>().Where(x => x.Id == textSource).ExecuteUpdateAsync(u => u.SetProperty(x => x.TopicId, 8), Ct);
        if (mismatch == "author") await s.Context.Set<VetTextSource>().Where(x => x.Id == textSource).ExecuteUpdateAsync(u => u.SetProperty(x => x.SourceAuthorUserId, 222), Ct);
        if (mismatch == "message") await s.Context.Set<VetTextSource>().Where(x => x.Id == textSource).ExecuteUpdateAsync(u => u.SetProperty(x => x.TelegramMessageId, 999), Ct);
        if (mismatch == "transport") await s.Context.Set<VetTextSource>().Where(x => x.Id == textSource).ExecuteUpdateAsync(u => u.SetProperty(x => x.SourceMessageDbId, (long?)null), Ct);
        if (mismatch == "caption") await s.Context.Set<VetTextSourceRevision>().Where(x => x.Id == item.TextRevision).ExecuteUpdateAsync(u => u.SetProperty(x => x.Text, "synthetic unrelated caption"), Ct);
        var evidence = (await Photos(s).ReadEvidenceAsync(Scope, item.Source.Id, item.Input.Id, null, 222, Ct)).ShouldNotBeNull();
        evidence.Caption!.FailureCategory.ShouldBe("caption_provenance_unavailable"); evidence.Caption.Interpretation.ShouldBeNull();
        var composed = await Composer(s).BuildBatchAsync(Scope, item.Source.BatchId!.Value, 222, null, false, Ct);
        composed.Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete); composed.Review.ShouldBeNull(); await NoFacts(s);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("place")]
    [InlineData("bot")]
    [InlineData("wrong_topic")]
    [InlineData("wrong_input")]
    [InlineData("wrong_result")]
    public async Task Evidence_reads_recheck_authorization_scope_and_immutable_identity(string gate)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s);
        var scope = Scope; var input = item.Input.Id; var result = item.Result!.Id;
        if (gate == "actor") await s.Context.Set<FamilyMember>().Where(x => x.TelegramUserId == 222).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, FamilyMemberStatus.Denied), Ct);
        if (gate == "place") await s.Context.Set<Place>().Where(x => x.TopicId == 7).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, PlaceStatus.Denied), Ct);
        if (gate == "bot") await s.Context.Bots.Where(x => x.Id == Bot.BotDbId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, BotStatus.Disabled), Ct);
        if (gate == "wrong_topic") scope = scope with { TopicId = 8 };
        if (gate == "wrong_input") input = Guid.NewGuid();
        if (gate == "wrong_result") result = Guid.NewGuid();
        var before = await Snapshot(s);
        var evidence = await Photos(s).ReadEvidenceAsync(scope, item.Source.Id, input, result, 222, Ct);
        if (gate == "wrong_result") { evidence.ShouldNotBeNull().Extraction.ShouldBeNull(); evidence!.Input.Id.ShouldBe(item.Input.Id); }
        else evidence.ShouldBeNull();
        (await Snapshot(s)).ShouldBe(before); await NoFacts(s);
    }

    [Fact]
    public async Task Profile_refresh_adopts_only_current_defaults_preserving_confirmed_batch_year_unit_and_zone()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s);
        foreach (var change in new[] { (VetPhotoAssumptionKind.Year, "2031"), (VetPhotoAssumptionKind.Unit, "mmol/L"), (VetPhotoAssumptionKind.TimeZone, "UTC") })
        {
            var b = await Batch(s, item.Source.BatchId!.Value);
            (await Photos(s).ChangeAssumptionAsync(new(Scope, b.Batch.Id, b.Batch.ReviewRevision, b.Batch.ProfileRevision, 222, change.Item1, change.Item2), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        }
        var oldReview = await Build(s, item.Source.BatchId!.Value); var old = await Batch(s, item.Source.BatchId.Value);
        var profile = await s.Profiles.GetOrCreateAsync(FamilyId, Bot.BotDbId, Ct);
        var updated = await s.Profiles.UpdateAsync(FamilyId, Bot.BotDbId, 111, profile.Revision,
            [new("TimeZone", "Europe/Berlin"), new("GlucoseUnit", null)], Ct);
        updated.Applied.ShouldBeTrue(); profile = await s.Profiles.GetOrCreateAsync(FamilyId, Bot.BotDbId, Ct);
        var result = await Photos(s).RefreshProfileSnapshotAsync(Scope, old.Batch.Id, old.Batch.ReviewRevision, profile.Revision, 222, Ct);
        result.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var a = JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(result.Batch!.AssumptionsJson, Json)!;
        a.ProfileTimeZone.ShouldBe("Europe/Berlin"); a.ProfileGlucoseUnit.ShouldBeNull();
        a.Year.ShouldBe(2031); a.YearConfirmed.ShouldBeTrue(); a.TimeZone.ShouldBe("UTC"); a.TimeZoneConfirmed.ShouldBeTrue();
        a.GlucoseUnit.ShouldBe("mmol/L"); a.UnitConfirmed.ShouldBeTrue();
        result.Batch.ReviewRevision.ShouldBe(old.Batch.ReviewRevision + 1); result.Batch.ProfileRevision.ShouldBe(profile.Revision);
        (await Photos(s).ReadPreviewAsync(Scope, oldReview.Id, 222, Ct)).ShouldNotBeNull().State.ShouldBe("stale");
        (await Photos(s).RefreshProfileSnapshotAsync(Scope, old.Batch.Id, result.Batch.ReviewRevision, profile.Revision, 222, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        await NoFacts(s);
    }

    [Theory]
    [InlineData("linked")]
    [InlineData("excluded")]
    [InlineData("cancelled")]
    [InlineData("deleted")]
    [InlineData("manual")]
    [InlineData("restore")]
    public async Task Computed_validation_never_overwrites_protected_proposals(string protection)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s);
        await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == item.Candidate.Id).ExecuteUpdateAsync(u =>
            u.SetProperty(c => c.State, protection is "manual" or "restore" ? "pending" : protection)
                .SetProperty(c => c.ManuallyCorrected, protection == "manual")
                .SetProperty(c => c.RequiresExplicitRestoration, protection == "restore"), Ct);
        var batch = await Batch(s, item.Source.BatchId!.Value); var candidate = batch.Items.Single().Candidate;
        var image = VetPhotoInterpretationParser.Parse(item.Result!.StructuredJson, item.Source.Id, item.Input.Id)!;
        var a = JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(batch.Batch.AssumptionsJson, Json)!;
        var validation = VetPhotoValidationRules.Validate(image, new(), a, item.Input.ReceivedAt);
        var before = await Snapshot(s);
        (await Photos(s).SetValidationAsync(new(Scope, batch.Batch.Id, batch.Batch.ReviewRevision, candidate.Id,
            candidate.Revision, item.Input.Id, item.Result.Id, batch.Batch.ProfileRevision, 222, new(), validation), Ct))
            .ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Snapshot(s)).ShouldBe(before); await NoFacts(s);
    }

    [Fact]
    public async Task Unbound_automatic_context_never_strengthens_image_evidence()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, complete: false);
        var batch = await Batch(s, item.Source.BatchId!.Value);
        var fakeContext = new VetPhotoContext(null, "mmol/L", 2031, 5, 11, "08:47", "+00:00");
        var image = VetPhotoInterpretationParser.Parse(item.Result!.StructuredJson, item.Source.Id, item.Input.Id)!;
        var assumptions = JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(batch.Batch.AssumptionsJson, Json)!;
        var validation = VetPhotoValidationRules.Validate(image, fakeContext, assumptions, item.Input.ReceivedAt);
        validation.Effective.ShouldNotBeNull(); var before = await Snapshot(s);
        (await Photos(s).SetValidationAsync(new(Scope, batch.Batch.Id, batch.Batch.ReviewRevision, item.Candidate.Id,
            item.Candidate.Revision, item.Input.Id, item.Result.Id, batch.Batch.ProfileRevision, 222, fakeContext, validation), Ct))
            .ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Snapshot(s)).ShouldBe(before); await NoFacts(s);
    }

    [Fact]
    public async Task Computed_clear_validation_is_idempotent_and_has_no_fact_action_or_provider_call()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s); var batch = await Batch(s, item.Source.BatchId!.Value);
        var image = VetPhotoInterpretationParser.Parse(item.Result!.StructuredJson, item.Source.Id, item.Input.Id)!;
        var validation = VetPhotoValidationRules.Validate(image, new(), JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(batch.Batch.AssumptionsJson, Json)!, item.Input.ReceivedAt);
        var request = new VetPhotoValidationUpdate(Scope, batch.Batch.Id, batch.Batch.ReviewRevision, item.Candidate.Id,
            item.Candidate.Revision, item.Input.Id, item.Result.Id, batch.Batch.ProfileRevision, 222, new(), validation);
        (await Photos(s).SetValidationAsync(request, Ct)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        var fresh = await Batch(s, batch.Batch.Id); var c = fresh.Items.Single().Candidate; c.State.ShouldBe("clear");
        JsonSerializer.Deserialize<VetPhotoEffectiveReading>(c.EffectiveJson, Json).ShouldBe(validation.Effective);
        (await Photos(s).SetValidationAsync(request with { BatchRevision = fresh.Batch.ReviewRevision, CandidateRevision = c.Revision }, Ct)).ShouldBe(VetPhotoWorkflowStatus.Existing);
        (await Batch(s, batch.Batch.Id)).Batch.ReviewRevision.ShouldBe(fresh.Batch.ReviewRevision);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(1); await NoFacts(s);
    }

    [Fact]
    public async Task Human_no_result_proposal_creates_durable_attributed_evidence_then_waits_for_complete_review()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, image: false);
        (await Photos(s).ProposeHumanCorrectionAsync(await Human(s, item), Ct)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        await NoFacts(s);
        var attempt = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Kind == "human", Ct);
        attempt.ActorUserId.ShouldBe(222); attempt.State.ShouldBe("returned"); attempt.ReservedResultSlot.ShouldBeFalse();
        var result = await s.Context.Set<VetPhotoExtraction>().AsNoTracking().SingleAsync(Ct);
        result.ModelName.ShouldBe("human"); result.State.ShouldBe("human"); result.AttemptId.ShouldBe(attempt.Id);
        var candidate = (await Batch(s, item.Source.BatchId!.Value)).Items.Single().Candidate;
        candidate.ManuallyCorrected.ShouldBeTrue(); candidate.State.ShouldBe("pending"); candidate.ExtractionResultId.ShouldBe(result.Id);
        item.Source.SourceAuthorUserId.ShouldBe(111);
        var review = await Build(s, item.Source.BatchId.Value);
        (await Accept(s, review)).Status.ShouldBe(VetMutationStatus.Stale); await NoFacts(s);
        review = await Deliver(s, review); (await Accept(s, review, review.AcceptancePromptMessageId)).Status.ShouldBe(VetMutationStatus.Applied);
        var fact = await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct); fact.Value.ShouldBe(7.25m);
        fact.OccurredAt.ShouldBe(DateTimeOffset.Parse("2031-05-11T09:32:00Z")); fact.SourceAuthorUserId.ShouldBe(111);
        fact.ExtractionResultId.ShouldBe(result.Id); (await s.Context.VetDiaryActions.SingleAsync(Ct)).ActorUserId.ShouldBe(222);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("unit")]
    [InlineData("year")]
    [InlineData("month")]
    [InlineData("day")]
    [InlineData("clock")]
    [InlineData("approval")]
    [InlineData("model_preserved_time")]
    public async Task Human_no_result_requires_complete_explicit_evidence_and_rejects_supplied_preserved_time(string missing)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, image: false);
        var proposal = await Human(s, item); var context = proposal.Context;
        context = missing switch
        {
            "value" => context with { RawValue = null }, "unit" => context with { Unit = null },
            "year" => context with { Year = null }, "month" => context with { Month = null }, "day" => context with { Day = null },
            "clock" => context with { Time = null }, "approval" => context with { CorrectionApproved = false },
            _ => context with { PreservedTime = new(Now.AddDays(-1), "2031-05-11 12:00:00", "UTC", "human_correction") }
        };
        var before = await Snapshot(s);
        (await Photos(s).ProposeHumanCorrectionAsync(proposal with { Context = context }, Ct)).ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(0);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "human", Ct)).ShouldBe(0); (await Snapshot(s)).ShouldBe(before); await NoFacts(s);
    }

    [Fact]
    public async Task Human_result_capacity_counts_existing_results_and_keeps_candidate_unchanged_on_full()
    {
        await SeedAsync(); await using var s = Open(); await Prepare(s); var item = await Prepare(s, 2, image: false);
        var before = await Snapshot(s); var attempts = await s.Context.Set<VetPhotoAttempt>().CountAsync(Ct);
        (await Photos(s, new(MaxResults: 1)).ProposeHumanCorrectionAsync(await Human(s, item), Ct)).ShouldBe(VetPhotoWorkflowStatus.Full);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(1);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(Ct)).ShouldBe(attempts); (await Snapshot(s)).ShouldBe(before); await NoFacts(s);
    }

    [Fact]
    public async Task Foreign_family_results_consume_global_human_capacity_without_exposing_private_rows()
    {
        await SeedAsync(); var family = new Family { Name = "synthetic second family", CreatedAt = Now };
        Db.Add(family); await Db.SaveChangesAsync(Ct);
        var bot = new Assistant.Domain.Bots.Bot { FamilyId = family.Id, TelegramBotId = 2001, Username = "synthetic_second_vet",
            Role = "vet", Status = BotStatus.Active, CreatedAt = Now };
        Db.Add(bot); Db.Add(new FamilyMember { FamilyId = family.Id, TelegramUserId = 111, DisplayName = "synthetic second owner",
            IsOwner = true, Status = FamilyMemberStatus.Approved, CreatedAt = Now, UpdatedAt = Now }); await Db.SaveChangesAsync(Ct);
        Db.Add(new Place { BotId = bot.Id, ChatId = -200, TopicId = 7, Title = "synthetic second place", Status = PlaceStatus.Approved, CreatedAt = Now });
        await Db.SaveChangesAsync(Ct); var foreign = new VetDiaryScope(family.Id, bot.Id, bot.TelegramBotId, -200, 7);
        await using (var other = Open(family.Id))
        {
            var p = await other.Profiles.GetOrCreateAsync(family.Id, bot.Id, Ct);
            (await other.Profiles.UpdateAsync(family.Id, bot.Id, 111, p.Revision, [new("TimeZone", "UTC"), new("GlucoseUnit", "mmol/L")], Ct)).Applied.ShouldBeTrue();
            await Prepare(other, selectedScope: foreign);
        }
        await using var s = Open(); var item = await Prepare(s, image: false); var before = await Snapshot(s);
        (await Photos(s, new(MaxResults: 1)).ProposeHumanCorrectionAsync(await Human(s, item), Ct)).ShouldBe(VetPhotoWorkflowStatus.Full);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(0);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "human", Ct)).ShouldBe(0);
        (await Snapshot(s)).ShouldBe(before); await NoFacts(s);
        await using var verify = Open(family.Id); (await verify.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Human_proposal_database_failure_rolls_back_result_attempt_candidate_and_batch()
    {
        await SeedAsync(); Item item; VetPhotoHumanProposal proposal; string before; int revision;
        await using (var s = Open()) { item = await Prepare(s, image: false); proposal = await Human(s, item); before = await Snapshot(s); revision = (await Batch(s, item.Source.BatchId!.Value)).Batch.ReviewRevision; }
        var fail = new FailCandidateWrite(); await using (var s = Open(interceptor: fail))
            await Should.ThrowAsync<DbUpdateException>(() => Photos(s).ProposeHumanCorrectionAsync(proposal, Ct));
        fail.Hits.ShouldBe(1); await using var verify = Open();
        (await Snapshot(verify)).ShouldBe(before); (await sResultCount(verify)).ShouldBe(0);
        (await verify.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "human", Ct)).ShouldBe(0);
        (await Batch(verify, item.Source.BatchId!.Value)).Batch.ReviewRevision.ShouldBe(revision);
    }
    private static Task<int> sResultCount(VetTestSession s) => s.Context.Set<VetPhotoExtraction>().CountAsync(Ct);
    private sealed class FailCandidateWrite : DbCommandInterceptor
    {
        public int Hits { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("UPDATE vet_photo_candidates", StringComparison.OrdinalIgnoreCase))
            { Hits++; throw new InvalidOperationException("synthetic candidate update failure"); }
            return base.ReaderExecutingAsync(command, data, result, ct);
        }
    }

    [Theory]
    [InlineData("unique")]
    [InlineData("ambiguous")]
    [InlineData("full_manual")]
    public async Task Multiple_displays_require_unique_explicit_match_or_complete_manual_measurement(string choice)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s, displays: 2);
        var context = choice == "full_manual" ? new VetPhotoContext("7.25", "mmol/L", 2031, 5, 11, "09:32", "+00:00", CorrectionApproved: true)
            : new VetPhotoContext(choice == "unique" ? "5.02" : null, CorrectionApproved: true);
        var before = await Snapshot(s);
        var status = await Photos(s).ProposeHumanCorrectionAsync(await Human(s, item, context), Ct);
        status.ShouldBe(choice == "ambiguous" ? VetPhotoWorkflowStatus.Refused : VetPhotoWorkflowStatus.Applied);
        if (choice == "ambiguous") { (await Snapshot(s)).ShouldBe(before); (await sResultCount(s)).ShouldBe(1); }
        else
        {
            var c = (await Batch(s, item.Source.BatchId!.Value)).Items.Single().Candidate;
            var evidence = JsonSerializer.Deserialize<VetPhotoContext>(c.CorrectionProvenanceJson, Json)!;
            evidence.SelectedDisplayIndex.ShouldBe(choice == "unique" ? 1 : 0);
            var reading = JsonSerializer.Deserialize<VetPhotoEffectiveReading>(c.EffectiveJson, Json)!;
            reading.Value.ShouldBe(choice == "unique" ? 5.02m : 7.25m); (await sResultCount(s)).ShouldBe(2);
        }
        await NoFacts(s);
    }

    [Fact]
    public async Task Value_only_saved_correction_preserves_server_event_time_and_stable_identity_until_explicit_acceptance()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s);
        var initial = await Deliver(s, await Build(s, item.Source.BatchId!.Value)); (await Accept(s, initial)).Status.ShouldBe(VetMutationStatus.Applied);
        var old = await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct);
        var profile = await s.Profiles.GetOrCreateAsync(FamilyId, Bot.BotDbId, Ct);
        (await s.Profiles.UpdateAsync(FamilyId, Bot.BotDbId, 111, profile.Revision,
            [new("TimeZone", "Europe/Berlin")], Ct)).Applied.ShouldBeTrue();
        (await Photos(s).ProposeHumanCorrectionAsync(await Human(s, item,
            new("8.125", CorrectionApproved: true)), Ct)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        var current = (await Batch(s, item.Source.BatchId.Value)).Items.Single().Candidate;
        var context = JsonSerializer.Deserialize<VetPhotoContext>(current.CorrectionProvenanceJson, Json)!;
        context.PreservedTime.ShouldBe(new VetPhotoPreservedTime(old.OccurredAt, old.LocalTime, old.TimeZoneSnapshot, old.OccurredAtSource));
        context.Year.ShouldBeNull(); context.Time.ShouldBeNull();
        (await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct)).Value.ShouldBe(old.Value);
        var review = await Deliver(s, await Build(s, item.Source.BatchId.Value));
        var accepted = await Accept(s, review); accepted.Status.ShouldBe(VetMutationStatus.Applied); accepted.EventIds.ShouldBe([old.Id]);
        var edited = await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct);
        edited.Value.ShouldBe(8.125m); edited.Revision.ShouldBe(old.Revision + 1); edited.OccurredAt.ShouldBe(old.OccurredAt);
        edited.LocalTime.ShouldBe(old.LocalTime); edited.TimeZoneSnapshot.ShouldBe(old.TimeZoneSnapshot);
        edited.OccurredAtSource.ShouldBe(old.OccurredAtSource);
        (await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Forged_preserved_time_in_complete_review_cannot_change_saved_event()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s);
        var initial = await Deliver(s, await Build(s, item.Source.BatchId!.Value)); (await Accept(s, initial)).Status.ShouldBe(VetMutationStatus.Applied);
        (await Photos(s).ProposeHumanCorrectionAsync(await Human(s, item, new("8.125", CorrectionApproved: true)), Ct)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        var review = await Build(s, item.Source.BatchId.Value); var selection = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!;
        var row = selection.Single(); var time = row.Context.PreservedTime!;
        selection[0] = row with { Context = row.Context with { PreservedTime = time with { OccurredAt = time.OccurredAt.AddMinutes(1), LocalTime = "2031-05-11 10:02:00" } },
            State = row.State! with { OccurredAt = time.OccurredAt.AddMinutes(1), LocalTime = "2031-05-11 10:02:00" } };
        var changed = JsonSerializer.Serialize(selection, Json);
        await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.SelectionJson, changed).SetProperty(r => r.Fingerprint, Hash(changed)), Ct);
        review = (await Photos(s).ReadPreviewAsync(Scope, review.Id, 222, Ct))!; review = await Deliver(s, review); var before = await Snapshot(s);
        (await Accept(s, review)).Status.ShouldBe(VetMutationStatus.Refused); (await Snapshot(s)).ShouldBe(before);
    }

    [Fact]
    public async Task Progress_preview_and_recovery_are_scoped_durable_and_do_not_create_facts()
    {
        await SeedAsync(); await using var s = Open(); var opened = await Photos(s).StartCollectionAsync(Scope, 111, Ct);
        var item = await Prepare(s); await Close(s, opened.Batch!.Id);
        (await Photos(s).SetProgressMessageAsync(Scope, opened.Batch.Id, 222, 456, Ct)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Photos(s).SetProgressMessageAsync(Scope with { TopicId = 8 }, opened.Batch.Id, 222, 789, Ct)).ShouldBe(VetPhotoWorkflowStatus.NotFound);
        var recoverable = await Photos(s).GetRecoverableBatchesAsync(FamilyId, Bot.BotDbId, 5, Ct);
        recoverable.Select(x => x.BatchId).ShouldContain(opened.Batch.Id); recoverable.Single(x => x.BatchId == opened.Batch.Id).Scope.ShouldBe(Scope);
        (await Batch(s, item.Source.BatchId!.Value)).Batch.ProgressMessageId.ShouldBe(456);
        await s.Context.Set<FamilyMember>().Where(m => m.TelegramUserId == 111).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        (await Photos(s).GetRecoverableBatchesAsync(FamilyId, Bot.BotDbId, 5, Ct)).ShouldBeEmpty(); await NoFacts(s);
    }
}

public sealed class VetPhotoReviewComposerIntegrationTests : VetPhotoPresentationFixture
{
    [Fact]
    public async Task Invented_caption_context_in_fully_delivered_selection_never_writes_any_fact()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s); var review = await Build(s, item.Source.BatchId!.Value);
        var rows = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!;
        rows[0] = rows[0] with { Context = new("5.01") };
        var selection = JsonSerializer.Serialize(rows, Json);
        await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id).ExecuteUpdateAsync(u =>
            u.SetProperty(r => r.SelectionJson, selection).SetProperty(r => r.Fingerprint, Hash(selection)), Ct);
        review = (await Photos(s).ReadPreviewAsync(Scope, review.Id, 222, Ct))!; review = await Deliver(s, review);
        var before = await Snapshot(s); (await Accept(s, review)).Status.ShouldBe(VetMutationStatus.Refused);
        (await Snapshot(s)).ShouldBe(before); await NoFacts(s);
    }

    [Fact]
    public async Task Corrupted_full_delivery_hash_never_authorizes_facts_despite_complete_flag_and_current_prompt()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s); var review = await Deliver(s, await Build(s, item.Source.BatchId!.Value));
        var deliveries = JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(review.DeliveredPagesJson, Json)!;
        deliveries[0] = deliveries[0] with { TextHash = new string('0', 64) };
        var encoded = JsonSerializer.Serialize(deliveries, Json);
        await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.DeliveredPagesJson, encoded), Ct);
        var before = await Snapshot(s);
        (await Accept(s, review, review.AcceptancePromptMessageId)).Status.ShouldBe(VetMutationStatus.Stale);
        (await Snapshot(s)).ShouldBe(before); await NoFacts(s);
    }

    [Fact]
    public async Task Forty_photo_collection_composes_every_individual_and_album_row_then_saves_exact_stable_provenance_once()
    {
        await SeedAsync(); await using var s = Open(); var started = await Photos(s).StartCollectionAsync(Scope, 111, Ct);
        var items = new List<Item>();
        for (var n = 1; n <= 40; n++) items.Add(await Prepare(s, n, album: n <= 30 ? null : "synthetic-album"));
        items.Select(i => i.Source.BatchId).Distinct().ShouldBe([started.Batch!.Id]);
        items.Count(i => i.Source.MediaGroupId == null).ShouldBe(30); items.Count(i => i.Source.MediaGroupId == "synthetic-album").ShouldBe(10);
        var collecting = await Composer(s).BuildBatchAsync(Scope, started.Batch.Id, 222, null, false, Ct);
        collecting.Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete); collecting.Review.ShouldBeNull(); await NoFacts(s);
        await Close(s, started.Batch.Id); var review = await Build(s, started.Batch.Id);
        var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!;
        pages.Length.ShouldBeGreaterThan(1); pages.Length.ShouldBeLessThanOrEqualTo(64); pages.All(p => p.Length <= 3500).ShouldBeTrue();
        foreach (var item in items)
        {
            var full = string.Join("\n", pages); full.Split(item.Source.Id.ToString("D"), StringSplitOptions.None).Length.ShouldBe(2);
            full.ShouldContain((5m + item.Source.TelegramMessageId / 100m).ToString(CultureInfo.InvariantCulture));
            full.ShouldContain($"2031-05-11 10:{item.Source.TelegramMessageId % 60:00}:00");
        }
        var selected = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!;
        selected.Length.ShouldBe(40); selected.Select(x => x.SourceId).ShouldBe(items.Select(i => i.Source.Id), ignoreOrder: true);
        await NoFacts(s); var telegram = new PreviewTelegram(s.Telegram); review = await Deliver(s, review, telegram);
        telegram.Delivered.Select(d => d.Text).ShouldBe(pages);
        var deliveries = JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(review.DeliveredPagesJson, Json)!;
        deliveries.Select(d => d.PageIndex).ShouldBe(Enumerable.Range(0, pages.Length));
        for (var index = 0; index < pages.Length; index++)
        { deliveries[index].TextHash.ShouldBe(Hash(pages[index])); deliveries[index].MessageId.ShouldBe(telegram.Delivered[index].MessageId); }
        review.AcceptancePromptMessageId.ShouldBe(telegram.Delivered[^1].MessageId); review.CompletePreviewDelivered.ShouldBeTrue();
        telegram.EditedButtons.Count.ShouldBe(1); telegram.EditedButtons[0].Message.ShouldBe(review.AcceptancePromptMessageId!.Value);
        telegram.EditedButtons[0].Buttons.Select(b => b.CallbackData).ShouldBe([VetPhotoReviewComposer.Callback("a", review), VetPhotoReviewComposer.Callback("d", review)]);
        var accepted = await Accept(s, review, review.AcceptancePromptMessageId); accepted.Status.ShouldBe(VetMutationStatus.Applied); accepted.EventIds.Count.ShouldBe(40);
        var facts = await s.Context.VetEvents.AsNoTracking().OrderBy(e => e.TelegramMessageId).ToArrayAsync(Ct);
        facts.Length.ShouldBe(40); facts.Select(e => e.Id).Distinct().Count().ShouldBe(40);
        foreach (var fact in facts)
        {
            var item = items.Single(i => i.Source.Id == fact.PhotoSourceId); fact.SourceId.ShouldBe(item.Source.Id);
            fact.SourceAuthorUserId.ShouldBe(111); fact.SourceMessageDbId.ShouldBe(item.Source.SourceMessageDbId!.Value);
            fact.InputRevisionId.ShouldBe(item.Input.Id); fact.ExtractionResultId.ShouldBe(item.Result!.Id);
            fact.PhotoBatchId.ShouldBe(started.Batch.Id); fact.Value.ShouldBe(5m + fact.TelegramMessageId / 100m);
            fact.Unit.ShouldBe("mmol/L"); fact.DeletedAt.ShouldBeNull(); fact.Revision.ShouldBe(1);
        }
        (await s.Context.VetDiaryActions.SingleAsync(Ct)).ActorUserId.ShouldBe(222);
        (await s.Context.VetDiaryActionChanges.CountAsync(Ct)).ShouldBe(40);
        var candidates = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().ToArrayAsync(Ct);
        candidates.Length.ShouldBe(40); candidates.All(c => c.State == "saved" && c.LastReviewId == review.Id).ShouldBeTrue();
        candidates.Select(c => c.EventId!.Value).ShouldBe(facts.Select(f => f.Id), ignoreOrder: true);
        (await Batch(s, started.Batch.Id)).Batch.State.ShouldBe("completed");
        var before = await Snapshot(s); await using var restart = Open();
        var replay = await Accept(restart, review, actor: 111); replay.Status.ShouldBe(VetMutationStatus.AlreadyApplied);
        replay.ActionId.ShouldBe(accepted.ActionId); replay.EventIds.ShouldBe(accepted.EventIds); (await Snapshot(restart)).ShouldBe(before);
    }

    [Fact]
    public async Task Fifty_first_collection_photo_is_rejected_without_silent_subset_or_queued_call()
    {
        await SeedAsync(); await using var s = Open(); var started = await Photos(s).StartCollectionAsync(Scope, 111, Ct);
        for (var n = 1; n <= 50; n++) await Prepare(s, n, image: false);
        var message = Text("synthetic fifty-first photo", 51) with { Kind = MessageKind.Photo };
        var update = await s.Context.Bots.Where(b => b.Id == Bot.BotDbId).Select(b => b.LastUpdateId).SingleAsync(Ct) + 1;
        var rejected = await Photos(s).AdmitAsync(Scope, message, update, new("synthetic-file-51", null, null, "image/png", 100, 32, 24), null, Ct);
        rejected.Status.ShouldBe(VetPhotoAdmissionStatus.Full); rejected.Source!.BatchId.ShouldBeNull(); rejected.Source.ProposedBatchId.ShouldBe(started.Batch!.Id);
        var batch = await Batch(s, started.Batch.Id); batch.Counts.Admitted.ShouldBe(50); batch.Counts.Rejected.ShouldBe(1);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.SourceId == rejected.Source.Id, Ct)).ShouldBe(0);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(0); await NoFacts(s);
    }

    [Fact]
    public async Task Partial_review_preserves_visible_waiting_row_and_keeps_batch_closed_after_shown_subset_save()
    {
        await SeedAsync(); await using var s = Open(); var started = await Photos(s).StartCollectionAsync(Scope, 111, Ct);
        var clear = await Prepare(s); var waiting = await Prepare(s, 2, image: false); await Close(s, started.Batch!.Id);
        var review = await Build(s, started.Batch.Id); Pages(review).ShouldContain(waiting.Source.Id.ToString("D"));
        Pages(review).ShouldContain("image_result_unavailable");
        var selected = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!;
        selected.Length.ShouldBe(1); selected[0].SourceId.ShouldBe(clear.Source.Id);
        var waitingBefore = JsonSerializer.Serialize((await Batch(s, started.Batch.Id)).Items.Single(i => i.Source.Id == waiting.Source.Id).Candidate, Json);
        review = await Deliver(s, review); (await Accept(s, review)).Status.ShouldBe(VetMutationStatus.Applied);
        var batch = await Batch(s, started.Batch.Id); batch.Batch.State.ShouldBe("closed");
        batch.Counts.Admitted.ShouldBe(2); batch.Counts.Saved.ShouldBe(1); batch.Counts.Waiting.ShouldBe(1);
        JsonSerializer.Serialize(batch.Items.Single(i => i.Source.Id == waiting.Source.Id).Candidate, Json).ShouldBe(waitingBefore);
        (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(1); (await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(1);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync(e => e.SourceId == waiting.Source.Id, Ct)).ShouldBe(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Failed_first_or_partial_page_delivery_never_allows_natural_or_callback_subset_acceptance(int failedPage)
    {
        await SeedAsync(); await using var s = Open(); var opened = await Photos(s).StartCollectionAsync(Scope, 111, Ct);
        for (var n = 1; n <= 12; n++) await Prepare(s, n);
        await Close(s, opened.Batch!.Id); var review = await Build(s, opened.Batch.Id);
        JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!.Length.ShouldBeGreaterThan(1);
        var telegram = new PreviewTelegram(s.Telegram, failedPage);
        (await Composer(s).DeliverAsync(Scope, review, 222, telegram, 900, Ct)).ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        var failed = (await Photos(s).ReadPreviewAsync(Scope, review.Id, 222, Ct))!;
        failed.CompletePreviewDelivered.ShouldBeFalse(); failed.State.ShouldBe("preview_failed");
        JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(failed.DeliveredPagesJson, Json)!.Length.ShouldBe(failedPage - 1);
        telegram.EditedButtons.ShouldBeEmpty(); var before = await Snapshot(s);
        (await Accept(s, failed)).Status.ShouldBe(VetMutationStatus.Stale);
        (await Accept(s, failed, telegram.Delivered.LastOrDefault().MessageId)).Status.ShouldBe(VetMutationStatus.Stale);
        (await Snapshot(s)).ShouldBe(before); await NoFacts(s);
        await using var restart = Open(); var retryClient = new PreviewTelegram(restart.Telegram);
        var complete = await Deliver(restart, failed, retryClient, explicitRetry: true);
        retryClient.Delivered.Count.ShouldBe(complete.PageCount); complete.Revision.ShouldBe(failed.Revision + 1);
        (await Accept(restart, failed)).Status.ShouldBe(VetMutationStatus.Stale);
        (await Accept(restart, complete)).Status.ShouldBe(VetMutationStatus.Applied);
        (await restart.Context.VetEvents.CountAsync(Ct)).ShouldBe(12);
    }

    [Theory]
    [InlineData("natural")]
    [InlineData("callback")]
    [InlineData("wrong_prompt")]
    [InlineData("wrong_topic")]
    [InlineData("revoked")]
    public async Task Complete_review_acceptance_requires_exact_live_scope_actor_and_callback_prompt(string proof)
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s);
        var review = await Deliver(s, await Build(s, item.Source.BatchId!.Value));
        if (proof == "revoked") await s.Context.Set<FamilyMember>().Where(m => m.TelegramUserId == 222).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        var before = await Snapshot(s); var result = await Accept(s, review,
            proof == "natural" ? null : proof == "wrong_prompt" ? review.AcceptancePromptMessageId + 1 : review.AcceptancePromptMessageId,
            scope: proof == "wrong_topic" ? Scope with { TopicId = 8 } : Scope);
        if (proof is "natural" or "callback") { result.Status.ShouldBe(VetMutationStatus.Applied); (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(1); }
        else { new[] { VetMutationStatus.Stale, VetMutationStatus.Refused, VetMutationStatus.NotFound }.ShouldContain(result.Status); (await Snapshot(s)).ShouldBe(before); await NoFacts(s); }
    }

    [Fact]
    public async Task Identical_bytes_choose_one_shown_canonical_fact_and_link_every_selected_source()
    {
        await SeedAsync(); await using var s = Open(); var opened = await Photos(s).StartCollectionAsync(Scope, 111, Ct);
        var first = await Prepare(s, 1, color: 1); var second = await Prepare(s, 2, color: 1, readingNumber: 1);
        await Close(s, opened.Batch!.Id); var review = await Build(s, opened.Batch.Id);
        var rows = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, Json)!;
        rows.Length.ShouldBe(2); rows.Single(r => r.CandidateId == first.Candidate.Id).DuplicateDecision.ShouldBe("canonical");
        var link = rows.Single(r => r.CandidateId == second.Candidate.Id); link.Disposition.ShouldBe("link"); link.LinkCandidateId.ShouldBe(first.Candidate.Id);
        Pages(review).ShouldContain("Одинаковые исходные байты"); review = await Deliver(s, review);
        (await Accept(s, review)).Status.ShouldBe(VetMutationStatus.Applied); (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(1);
        var candidates = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().ToArrayAsync(Ct);
        var canonical = candidates.Single(c => c.Id == first.Candidate.Id); var linked = candidates.Single(c => c.Id == second.Candidate.Id);
        canonical.EventId.ShouldNotBeNull(); canonical.State.ShouldBe("saved"); linked.EventId.ShouldBeNull();
        linked.State.ShouldBe("linked"); linked.DuplicateEventId.ShouldBe(canonical.EventId);
        linked.DuplicateEventRevision.ShouldBe(canonical.EventRevision); linked.DuplicateSourceId.ShouldBe(first.Source.Id);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained", Ct)).ShouldBe(2);
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("separate")]
    [InlineData("same")]
    public async Task Distinct_original_same_value_time_requires_explicit_duplicate_decision(string decision)
    {
        await SeedAsync(); await using var s = Open(); var first = await Prepare(s, 1);
        var savedReview = await Deliver(s, await Build(s, first.Source.BatchId!.Value)); (await Accept(s, savedReview)).Status.ShouldBe(VetMutationStatus.Applied);
        var saved = await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct); var second = await Prepare(s, 2, readingNumber: 1);
        if (decision != "unresolved")
        {
            var b = await Batch(s, second.Source.BatchId!.Value); var i = b.Items.Single();
            (await Photos(s).ChangeCandidateAsync(new(Scope, b.Batch.Id, i.Candidate.Id, b.Batch.ReviewRevision,
                i.Candidate.Revision, i.Input.Id, i.Source.CurrentOrdinal, i.Candidate.ExtractionResultId, 222,
                decision == "same" ? VetPhotoCandidateChangeKind.DuplicateExisting : VetPhotoCandidateChangeKind.DuplicateSeparate,
                DuplicateEventId: decision == "same" ? saved.Id : null, DuplicateEventRevision: decision == "same" ? saved.Revision : null), Ct))
                .ShouldBe(VetPhotoWorkflowStatus.Applied);
        }
        var composed = await Composer(s).BuildBatchAsync(Scope, second.Source.BatchId!.Value, 222, null, false, Ct);
        if (decision == "unresolved")
        {
            composed.Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete); composed.Review.ShouldBeNull();
            string.Join("\n", composed.Pages).ShouldContain("Выберите:"); (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(1);
        }
        else
        {
            var review = await Deliver(s, composed.Review.ShouldNotBeNull()); (await Accept(s, review)).Status.ShouldBe(VetMutationStatus.Applied);
            (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(decision == "separate" ? 2 : 1);
            if (decision == "same")
            {
                var linked = (await Batch(s, second.Source.BatchId.Value)).Items.Single().Candidate;
                linked.EventId.ShouldBeNull(); linked.State.ShouldBe("linked"); linked.DuplicateEventId.ShouldBe(saved.Id);
                linked.DuplicateEventRevision.ShouldBe(saved.Revision); linked.DuplicateSourceId.ShouldBe(first.Source.Id);
            }
        }
        (await s.Context.Set<VetPhotoBlob>().CountAsync(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Declining_complete_review_freezes_actual_decider_and_prevents_later_acceptance_without_fact_writes()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s); var review = await Deliver(s, await Build(s, item.Source.BatchId!.Value));
        var before = await Snapshot(s); var handle = new VetPhotoReviewHandle(Scope, review.Id, review.Revision, review.OperationKey, 222, review.AcceptancePromptMessageId);
        (await Photos(s).DeclineReviewAsync(handle, Ct)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        var declined = (await Photos(s).ReadPreviewAsync(Scope, review.Id, 222, Ct))!; declined.State.ShouldBe("declined");
        declined.DecisionActorUserId.ShouldBe(222); declined.DecidedAt.ShouldBe(Now);
        (await Accept(s, review)).Status.ShouldBe(VetMutationStatus.Stale); (await Snapshot(s)).ShouldBe(before); await NoFacts(s);
    }

    [Fact]
    public async Task Repeat_composition_returns_same_frozen_pages_without_extra_validation_revisions_or_facts()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s); var first = await Build(s, item.Source.BatchId!.Value);
        var batch = await Batch(s, item.Source.BatchId.Value); var before = await Snapshot(s); var second = await Build(s, item.Source.BatchId.Value);
        second.Id.ShouldBe(first.Id); second.SelectionJson.ShouldBe(first.SelectionJson); second.PreviewPagesJson.ShouldBe(first.PreviewPagesJson);
        (await Batch(s, batch.Batch.Id)).Batch.ReviewRevision.ShouldBe(batch.Batch.ReviewRevision);
        (await Snapshot(s)).ShouldBe(before); (await s.Context.Set<VetPhotoReview>().CountAsync(Ct)).ShouldBe(1); await NoFacts(s);
    }

    [Fact]
    public async Task Completed_batch_refuses_late_addition_without_reopening_or_fact_changes()
    {
        await SeedAsync(); await using var s = Open(); var item = await Prepare(s);
        var review = await Deliver(s, await Build(s, item.Source.BatchId!.Value)); (await Accept(s, review)).Status.ShouldBe(VetMutationStatus.Applied);
        var batch = await Batch(s, item.Source.BatchId.Value); batch.Batch.State.ShouldBe("completed");
        var update = await s.Context.Bots.Where(b => b.Id == Bot.BotDbId).Select(b => b.LastUpdateId).SingleAsync(Ct) + 1;
        var message = Text("synthetic late photo", 99) with { Kind = MessageKind.Photo, ReplyToMessageId = 1 };
        var late = await Photos(s).AdmitAsync(Scope, message, update, new("synthetic-late-file", null, null, "image/png", 100, 32, 24), null, Ct);
        late.Status.ShouldBe(VetPhotoAdmissionStatus.Late); late.Source!.BatchId.ShouldBeNull(); late.Source.ProposedBatchId.ShouldBe(batch.Batch.Id);
        var before = await Snapshot(s);
        (await Photos(s).AddLateSourceAsync(Scope, batch.Batch.Id, batch.Batch.ReviewRevision, late.Source.Id, late.Input!.Id, 222, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Snapshot(s)).ShouldBe(before); (await Batch(s, batch.Batch.Id)).Batch.State.ShouldBe("completed");
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.SourceId == late.Source.Id, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Saved_edited_input_remains_proposed_until_explicit_current_input_correction_review()
    {
        await SeedAsync(); await using var s = Open(); var first = await Prepare(s);
        var saved = await Deliver(s, await Build(s, first.Source.BatchId!.Value)); (await Accept(s, saved)).Status.ShouldBe(VetMutationStatus.Applied);
        var old = await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct);
        var update = await s.Context.Bots.Where(b => b.Id == Bot.BotDbId).Select(b => b.LastUpdateId).SingleAsync(Ct) + 1;
        var bytes = Png(23); var message = Text("synthetic edited photo", 1) with { Kind = MessageKind.Photo, IsEdit = true, EditedAt = Now.AddSeconds(1) };
        var edited = await Photos(s).AdmitAsync(Scope, message, update, new("synthetic-edited-file", "synthetic-edited-unique", "synthetic.png", "image/png", bytes.Length, 32, 24), null, Ct);
        edited.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        await s.Messages.StoreAsync(Bot.TelegramBotId, update, message, Ct);
        var reservation = (await Photos(s).ReserveDownloadAsync(Scope, first.Source.Id, edited.Input!.Id, 111, Ct)).Claim.ShouldNotBeNull();
        (await Photos(s).CommitOriginalAsync(new(Scope, 111, reservation.Attempt.Id, reservation.ClaimToken, edited.Input.Id,
            bytes, new VetPhotoImageDecoder().Decode(bytes, Ct).Image.ShouldNotBeNull()), Ct)).Status.ShouldBe(VetPhotoArchiveStatus.Retained);
        var claim = (await Photos(s).ClaimCurrentImageAsync(Scope, first.Source.Id, edited.Input.Id, 111, Ct)).Claim.ShouldNotBeNull();
        (await Photos(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
        var newResult = await Photos(s).CompleteImageAsync(new(Scope, claim.AttemptKey, claim.ClaimToken, 111,
            first.Source.Id, edited.Input.Id, "synthetic-image-model", ImageJson(first.Source.Id, edited.Input.Id, 23)), Ct);
        newResult.Status.ShouldBe(VetPhotoImageStatus.ProposedDelta);
        var before = await Snapshot(s); var candidate = (await Batch(s, first.Source.BatchId.Value)).Items.Single().Candidate;
        candidate.InputRevisionId.ShouldBe(first.Input.Id); candidate.ExtractionResultId.ShouldBe(first.Result!.Id);
        var automatic = await Composer(s).BuildBatchAsync(Scope, first.Source.BatchId.Value, 222, null, false, Ct);
        automatic.Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete); automatic.Review.ShouldBeNull();
        string.Join("\n", automatic.Pages).ShouldContain(old.Id.ToString(CultureInfo.InvariantCulture));
        (await Snapshot(s)).ShouldBe(before); (await Batch(s, first.Source.BatchId.Value)).Batch.State.ShouldBe("completed");
        var correction = await Deliver(s, await Build(s, first.Source.BatchId.Value, [candidate.Id]));
        correction.Kind.ShouldBe("correction"); var selected = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(correction.SelectionJson, Json)!.Single();
        selected.InputRevisionId.ShouldBe(edited.Input.Id); selected.ExtractionResultId.ShouldBe(newResult.Extraction!.Id);
        selected.ExpectedCandidateExtractionId.ShouldBe(first.Result.Id); selected.EventId.ShouldBe(old.Id);
        (await Accept(s, correction)).Status.ShouldBe(VetMutationStatus.Applied);
        var fact = await s.Context.VetEvents.AsNoTracking().SingleAsync(Ct); fact.Id.ShouldBe(old.Id); fact.Revision.ShouldBe(2);
        fact.Value.ShouldBe(5.23m); fact.InputRevisionId.ShouldBe(edited.Input.Id); fact.ExtractionResultId.ShouldBe(newResult.Extraction.Id);
        (await s.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(2);
    }
}
