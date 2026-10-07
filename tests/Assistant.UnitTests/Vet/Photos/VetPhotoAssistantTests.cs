using System.Text.Json;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Assistant.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.UnitTests.Vet.Photos;

public sealed class VetPhotoAssistantTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2031-05-12T12:00:00Z");
    private static readonly ReceivingBot Bot = new(1, 1001, "synthetic_vet", 42, "vet");
    private static readonly VetDiaryScope Scope = new(42, 1, 1001, -100, 7);
    private static IncomingMessage Message() => new(-100, "supergroup", "synthetic topic", 7, 10, 111,
        "synthetic_user", "synthetic caption", MessageKind.Photo, false, Now, null, null, "{}", null, null,
        Photo: new([new("synthetic-file", null, 32, 24, 100)]));
    private static (VetPhotoAssistant Assistant, Ports Ports, Approvals Approvals, FakeTelegramClient Telegram) Create()
    {
        var ports = new Ports(); var approvals = new Approvals(); var telegram = new FakeTelegramClient();
        // Unit transport paths do not invoke the diary composer; complete composer recovery uses real SQL above.
        var composer = new VetPhotoReviewComposer(ports, ports, null!, null!, NullLogger<VetPhotoReviewComposer>.Instance);
        return (new(ports, ports, ports, ports, composer, ports, approvals, NullLogger<VetPhotoAssistant>.Instance), ports, approvals, telegram);
    }

    [Theory]
    [InlineData("area")]
    [InlineData("size_tie")]
    [InlineData("ordinal_tie")]
    [InlineData("invalid_variant")]
    [InlineData("reversed_tie")]
    public async Task Admission_selects_one_deterministic_largest_variant_and_preserves_bound_TEXT_revision_without_image_work(string selection)
    {
        var (assistant, ports, _, telegram) = Create(); var message = Message();
        PhotoSizeAttachment[] variants = selection switch
        {
            "area" => [new("synthetic-small", null, 10, 10, 999), new("synthetic-large", "synthetic-unique", 20, 20, 100)],
            "size_tie" => [new("synthetic-low", null, 20, 20, null), new("synthetic-high", "synthetic-unique", 20, 20, 100)],
            "ordinal_tie" => [new("synthetic-z", null, 20, 20, 100), new("synthetic-a", "synthetic-unique", 20, 20, 100)],
            "reversed_tie" => [new("synthetic-a", "synthetic-unique", 20, 20, 100), new("synthetic-z", null, 20, 20, 100)],
            _ => [new("", null, 99, 99, 999), new("synthetic-invalid", null, 0, 99, 999), new("synthetic-valid", "synthetic-unique", 20, 20, 100)]
        };
        message = message with { Photo = new(variants) };
        var text = new VetAdmittedSource(new() { Id = Guid.NewGuid() }, new() { Id = Guid.NewGuid() });
        var admitted = await assistant.AdmitAsync(Bot, message, 77, text, Ct);
        admitted!.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted); var call = ports.Admissions.Single();
        call.Scope.ShouldBe(Scope); call.Update.ShouldBe(77); call.Text.ShouldBe(text.Revision.Id); call.Message.ShouldBe(message);
        call.Attachment.FileId.ShouldBe(selection switch { "area" => "synthetic-large", "size_tie" => "synthetic-high", "invalid_variant" => "synthetic-valid", _ => "synthetic-a" });
        call.Attachment.FileUniqueId.ShouldBe("synthetic-unique"); call.Attachment.ReportedWidth.ShouldBe(20);
        call.Attachment.ReportedHeight.ShouldBe(20); call.Attachment.ReportedSize.ShouldBe(100); call.Attachment.FileName.ShouldBeNull();
        ports.ForbiddenWork.ShouldBe(0); telegram.DownloadedFiles.ShouldBeEmpty(); ports.Bindings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    public async Task Supported_original_document_preserves_metadata_and_remains_dispatch_free(string mime)
    {
        var (assistant, ports, _, telegram) = Create(); var message = Message() with { Kind = MessageKind.Document, Photo = null,
            Document = new("synthetic-original", "synthetic-unique", "synthetic.png", mime, 100) };
        (await assistant.AdmitAsync(Bot, message, 77, null, Ct))!.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        var attachment = ports.Admissions.Single().Attachment; attachment.FileId.ShouldBe("synthetic-original"); attachment.ReportedMimeType.ShouldBe(mime);
        attachment.FileName.ShouldBe("synthetic.png"); attachment.ReportedSize.ShouldBe(100); attachment.ReportedWidth.ShouldBeNull();
        ports.ForbiddenWork.ShouldBe(0); telegram.DownloadedFiles.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("no_attachment")]
    [InlineData("empty_photo")]
    [InlineData("invalid_dimensions")]
    [InlineData("pdf")]
    [InlineData("mime_spoof")]
    [InlineData("empty_document_id")]
    public async Task Unsupported_or_absent_attachment_has_visible_refusal_and_no_admission_or_work(string invalid)
    {
        var (assistant, ports, _, telegram) = Create(); var message = Message();
        message = invalid switch
        {
            "no_attachment" => message with { Photo = null }, "empty_photo" => message with { Photo = new([]) },
            "invalid_dimensions" => message with { Photo = new([new("synthetic-invalid", null, -1, 0, 100)]) },
            "pdf" => message with { Kind = MessageKind.Document, Photo = null, Document = new("synthetic-file", null, "synthetic.png", "application/pdf", 100) },
            "mime_spoof" => message with { Kind = MessageKind.Document, Photo = null, Document = new("synthetic-file", null, "synthetic.png", "IMAGE/PNG", 100) },
            _ => message with { Kind = MessageKind.Document, Photo = null, Document = new("", null, "synthetic.png", "image/png", 100) }
        };
        var admitted = (await assistant.AdmitAsync(Bot, message, 77, null, Ct)).ShouldNotBeNull();
        admitted.Status.ShouldBe(VetPhotoAdmissionStatus.InvalidMetadata); admitted.Source.ShouldBeNull();
        await assistant.HandleAdmissionAsync(Bot, telegram, message, new(StoreOutcome.Stored, 1), admitted, Ct);
        telegram.Sent.Single().Text.ShouldContain("Нужен оригинал JPEG или PNG"); telegram.Sent.Single().ReplyToMessageId.ShouldBe(message.MessageId);
        ports.Admissions.ShouldBeEmpty(); ports.Bindings.ShouldBeEmpty(); ports.ForbiddenWork.ShouldBe(0); telegram.DownloadedFiles.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("member")]
    [InlineData("place")]
    [InlineData("missing_actor")]
    [InlineData("private_other_person")]
    [InlineData("private_topic")]
    [InlineData("other_role")]
    [InlineData("no_family")]
    [InlineData("text")]
    public async Task Admission_fails_closed_before_any_metadata_or_work_when_fresh_authorization_or_shape_fails(string denied)
    {
        var (assistant, ports, approvals, telegram) = Create(); var bot = Bot; var message = Message();
        if (denied == "member") approvals.Member = FamilyMemberStatus.Denied;
        if (denied == "place") approvals.Place = PlaceStatus.Disabled;
        if (denied == "missing_actor") message = message with { UserId = null };
        if (denied == "private_other_person") message = message with { ChatType = "private", ChatId = 222, TopicId = null };
        if (denied == "private_topic") message = message with { ChatType = "private", ChatId = 111, TopicId = 7 };
        if (denied == "other_role") bot = bot with { Role = "health" };
        if (denied == "no_family") bot = bot with { FamilyId = null };
        if (denied == "text") message = message with { Kind = MessageKind.Text };
        (await assistant.AdmitAsync(bot, message, 77, null, Ct)).ShouldBeNull(); ports.Admissions.ShouldBeEmpty();
        ports.ForbiddenWork.ShouldBe(0); telegram.Sent.ShouldBeEmpty(); telegram.DownloadedFiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Private_self_chat_requires_current_member_without_a_place_approval()
    {
        var (assistant, ports, approvals, _) = Create(); approvals.Place = null;
        var message = Message() with { ChatType = "private", ChatId = 111, TopicId = null };
        (await assistant.AdmitAsync(Bot, message, 77, null, Ct))!.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        ports.Admissions.Single().Scope.ShouldBe(Scope with { ChatId = 111, TopicId = null }); approvals.PlaceReads.ShouldBe(0);
        approvals.MemberReads.ShouldBe(1);
    }

    [Theory]
    [InlineData(StoreOutcome.Stored)]
    [InlineData(StoreOutcome.Updated)]
    [InlineData(StoreOutcome.AlreadyProcessed)]
    [InlineData(StoreOutcome.Duplicate)]
    [InlineData(StoreOutcome.OffsetOnly)]
    public async Task Binding_uses_full_identity_recovery_for_stored_or_lost_results_and_never_binds_offset_only(StoreOutcome outcome)
    {
        var (assistant, ports, _, _) = Create(); var message = Message(); var admission = await assistant.AdmitAsync(Bot, message, 77, null, Ct);
        await assistant.BindAsync(Bot, message, new(outcome, outcome is StoreOutcome.Stored or StoreOutcome.Updated ? 17 : null), admission, Ct);
        ports.Bindings.Count.ShouldBe(outcome == StoreOutcome.OffsetOnly ? 0 : 1);
        if (outcome != StoreOutcome.OffsetOnly) ports.Bindings.Single().ShouldBe((Scope, admission!.Source!.Id, 111L));
        ports.DirectBindings.ShouldBe(0); ports.ForbiddenWork.ShouldBe(0);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("place")]
    public async Task Authorization_is_rechecked_after_admission_before_binding_or_visible_status(string gate)
    {
        var (assistant, ports, approvals, telegram) = Create(); var message = Message(); var admitted = await assistant.AdmitAsync(Bot, message, 77, null, Ct);
        if (gate == "member") approvals.Member = FamilyMemberStatus.Denied; else approvals.Place = PlaceStatus.Disabled;
        await assistant.BindAsync(Bot, message, new(StoreOutcome.Stored, 17), admitted, Ct);
        await assistant.HandleAdmissionAsync(Bot, telegram, message, new(StoreOutcome.Stored, 17), admitted, Ct);
        ports.Bindings.ShouldBeEmpty(); telegram.Sent.ShouldBeEmpty(); ports.ForbiddenWork.ShouldBe(0);
    }

    [Theory]
    [InlineData(VetPhotoAdmissionStatus.Full, "В партии уже 50 фото")]
    [InlineData(VetPhotoAdmissionStatus.Late, "поздний источник")]
    [InlineData(VetPhotoAdmissionStatus.Refused, "Фото не принято")]
    public async Task Refused_or_late_admission_remains_visible_without_progress_or_image_work(VetPhotoAdmissionStatus status, string expected)
    {
        var (assistant, ports, _, telegram) = Create(); var admission = ports.Admission with { Status = status };
        await assistant.HandleAdmissionAsync(Bot, telegram, Message(), new(StoreOutcome.Stored, 17), admission, Ct);
        telegram.Sent.Single().Text.ShouldContain(expected); telegram.Sent.Single().Text.ShouldContain(admission.Source!.Id.ToString("D"));
        ports.BatchReads.ShouldBe(0); ports.ForbiddenWork.ShouldBe(0);
    }

    [Fact]
    public async Task Unbound_admission_is_visibly_pending_and_never_enters_progress_or_work()
    {
        var (assistant, ports, _, telegram) = Create(); ports.Admission.Source!.SourceMessageDbId = null;
        await assistant.HandleAdmissionAsync(Bot, telegram, Message(), new(StoreOutcome.OffsetOnly, null), ports.Admission, Ct);
        telegram.Sent.Single().Text.ShouldContain("Обработка не начата"); telegram.Sent.Single().Text.ShouldContain("сохранённых фактов нет");
        ports.BatchReads.ShouldBe(0); ports.ForbiddenWork.ShouldBe(0);
    }

    [Fact]
    public async Task Bound_collecting_status_requires_explicit_close_without_auto_review_or_work()
    {
        var (assistant, ports, _, telegram) = Create(); ports.Admission.Source!.SourceMessageDbId = 17; ports.UseBatch = true;
        await assistant.HandleAdmissionAsync(Bot, telegram, Message(), new(StoreOutcome.Stored, 17), ports.Admission, Ct);
        telegram.Sent.Single().Text.ShouldContain("/photos_close"); telegram.Sent.Single().Text.ShouldContain("Принято 1");
        ports.ProgressMessages.Single().ShouldBe(1); ports.ForbiddenWork.ShouldBe(0); telegram.ButtonMessages.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Caption_filter_removes_only_attached_glucose_and_preserves_immutable_interpretation_actual_insulin_reply_and_photo_context(bool attached)
    {
        var (assistant, ports, _, _) = Create(); ports.FindAttached = attached;
        var insulin = new VetCandidate("insulin", "record", "0.125", "U", null, "2031-05-11", "10:00", "+00:00", "explicit", 0);
        var glucose = new VetCandidate("glucose", "record", "5.01", "mmol/L", null, "2031-05-11", "10:00", "+00:00", "explicit", 0);
        var question = glucose with { Intent = "question_only", Ordinal = 1 };
        var original = new VetInterpretation(true, [glucose, insulin, question], ["synthetic clarification"], null, null)
            { PhotoCaption = new("record", new(null, "mmol/L", 2031, 5, 11, "10:00", "+00:00")) };
        var frozen = JsonSerializer.Serialize(original); var filtered = await assistant.FilterCaptionAsync(Scope, 10, original, Ct);
        filtered.Events.ShouldBe(attached ? new[] { insulin } : original.Events); filtered.NeedsReply.ShouldBeTrue();
        filtered.PhotoCaption.ShouldBe(original.PhotoCaption); filtered.Unclear.ShouldBe(original.Unclear);
        JsonSerializer.Serialize(original).ShouldBe(frozen); original.Events.Count.ShouldBe(3);
        ports.FindRequests.Single().ShouldBe((Scope, 10)); ports.ForbiddenWork.ShouldBe(0);
    }

    [Fact]
    public async Task Resume_requests_bounded_unbound_metadata_and_uses_each_original_author_without_image_or_transport_rewrites()
    {
        var (assistant, ports, _, telegram) = Create(); var second = ports.Admission with { Source = new() { Id = Guid.NewGuid(),
            SourceAuthorUserId = 222, ChatId = -200, TopicId = 8 } };
        ports.Unbound = [ports.Admission, second];
        await assistant.ResumeAsync(Bot, telegram, Ct);
        ports.UnboundRequests.Single().ShouldBe((42L, 1L, 5));
        ports.Bindings.ShouldBe(new[] { (Scope, ports.Admission.Source!.Id, 111L), (Scope with { ChatId = -200, TopicId = 8 }, second.Source!.Id, 222L) });
        ports.RunResumes.ShouldBe(1); ports.RecoverableRequests.Single().ShouldBe((42L, 1L, 5));
        telegram.Sent.ShouldBeEmpty(); telegram.DownloadedFiles.ShouldBeEmpty(); ports.ForbiddenWork.ShouldBe(0);
    }

    private sealed class Approvals : IApprovalService
    {
        public FamilyMemberStatus? Member = FamilyMemberStatus.Approved; public PlaceStatus? Place = PlaceStatus.Approved;
        public int MemberReads, PlaceReads;
        public Task<FamilyMemberStatus?> FindFamilyMemberStatusAsync(long family, long actor, CancellationToken ct) { MemberReads++; return Task.FromResult(Member); }
        public Task<PlaceStatus?> FindPlaceStatusAsync(long bot, long chat, int? topic, CancellationToken ct) { PlaceReads++; return Task.FromResult(Place); }
        public Task<long> GetOrCreatePendingPlaceAsync(long bot, long chat, int? topic, string title, CancellationToken ct) => throw new InvalidOperationException("Unexpected approval mutation.");
        public Task<ApprovalResolution> ResolvePlaceApprovalAsync(long place, bool approve, CancellationToken ct) => throw new InvalidOperationException("Unexpected approval mutation.");
        public Task<PlaceStatus> GetPlaceStatusAsync(long place, CancellationToken ct) => throw new InvalidOperationException("Unexpected approval lookup.");
        public Task<bool> GetPlaceReplyToAllAsync(long place, CancellationToken ct) => throw new InvalidOperationException("Unexpected approval lookup.");
        public Task<long> GetOrCreatePendingFamilyMemberAsync(long family, long actor, string name, string? username, string bot, CancellationToken ct) => throw new InvalidOperationException("Unexpected approval mutation.");
        public Task<ApprovalResolution> ResolveUserApprovalAsync(long member, bool approve, CancellationToken ct) => throw new InvalidOperationException("Unexpected approval mutation.");
        public Task<FamilyMemberStatus> GetFamilyMemberStatusAsync(long member, CancellationToken ct) => throw new InvalidOperationException("Unexpected approval lookup.");
    }

    private sealed class Ports : IVetPhotoArchiveStore, IVetPhotoBindingStore, IVetPhotoWorkflowStore, IVetPhotoPresentationStore, IVetPhotoApplicationOperations
    {
        public VetPhotoAdmission Admission = new(VetPhotoAdmissionStatus.Admitted,
            new() { Id = Guid.NewGuid(), SourceAuthorUserId = 111, ChatId = -100, TopicId = 7, BatchId = Guid.NewGuid() }, new() { Id = Guid.NewGuid() });
        public List<(VetDiaryScope Scope, IncomingMessage Message, long Update, VetPhotoAttachment Attachment, Guid? Text)> Admissions = [];
        public List<(VetDiaryScope Scope, Guid Source, long Actor)> Bindings = [];
        public List<(VetDiaryScope Scope, int Message)> FindRequests = [];
        public List<(long Family, long Bot, int Limit)> UnboundRequests = [], RecoverableRequests = [];
        public IReadOnlyList<VetPhotoAdmission> Unbound = [];
        public List<int> ProgressMessages = [];
        public int DirectBindings, ForbiddenWork, BatchReads, RunResumes;
        public bool UseBatch, FindAttached;
        private Exception Unexpected() { ForbiddenWork++; return new InvalidOperationException("Unexpected photo work or mutation."); }
        public Task<VetPhotoAdmission> AdmitAsync(VetDiaryScope scope, IncomingMessage message, long update, VetPhotoAttachment attachment, Guid? text, CancellationToken ct)
        { Admissions.Add((scope, message, update, attachment, text)); return Task.FromResult(Admission); }
        public Task<bool> BindMessageAsync(VetDiaryScope scope, Guid source, long message, CancellationToken ct) { DirectBindings++; throw Unexpected(); }
        public Task<bool> BindStoredMessageAsync(VetDiaryScope scope, Guid source, long actor, CancellationToken ct) { Bindings.Add((scope, source, actor)); return Task.FromResult(true); }
        public Task<VetPhotoAdmission?> GetSourceAsync(VetDiaryScope scope, Guid source, CancellationToken ct) => Task.FromResult<VetPhotoAdmission?>(Admission);
        public Task<VetPhotoAdmission?> FindSourceAsync(VetDiaryScope scope, int message, CancellationToken ct) { FindRequests.Add((scope, message)); return Task.FromResult<VetPhotoAdmission?>(FindAttached ? Admission : null); }
        public Task<IReadOnlyList<VetPhotoAdmission>> GetUnboundAsync(long family, long bot, int limit, CancellationToken ct) { UnboundRequests.Add((family, bot, limit)); return Task.FromResult(Unbound); }
        public Task<VetPhotoReservation> ReserveDownloadAsync(VetDiaryScope scope, Guid source, Guid input, long actor, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoArchiveResult> CommitOriginalAsync(VetPhotoArchiveCommit commit, CancellationToken ct) => throw Unexpected();
        public Task<bool> RecordDownloadFailureAsync(VetDiaryScope scope, Guid attempt, Guid token, string category, bool transient, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoOriginalRead?> ReadOriginalAsync(VetDiaryScope scope, Guid input, long actor, Guid attempt, Guid token, DateTimeOffset lease, CancellationToken ct) => throw Unexpected();
        public Task ReleaseReaderAsync(VetDiaryScope scope, Guid lease, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoCapacityTotals> GetCapacityAsync(VetDiaryScope scope, long actor, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoOriginalDeletionResult> DeleteOriginalsAsync(VetPhotoOriginalDeletion deletion, CancellationToken ct) => throw Unexpected();
        public Task<int> ReclaimAsync(long family, int limit, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoBatchSnapshot?> GetBatchAsync(VetDiaryScope scope, Guid batch, long actor, CancellationToken ct)
        { BatchReads++; return Task.FromResult<VetPhotoBatchSnapshot?>(UseBatch ? new(new() { Id = batch, State = "collecting" }, new(1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0), []) : null); }
        public Task<VetPhotoWorkflowStatus> SetProgressMessageAsync(VetDiaryScope scope, Guid batch, long actor, int message, CancellationToken ct) { ProgressMessages.Add(message); return Task.FromResult(VetPhotoWorkflowStatus.Applied); }
        public Task<IReadOnlyList<VetPhotoRecoverableBatch>> GetRecoverableBatchesAsync(long family, long bot, int limit, CancellationToken ct) { RecoverableRequests.Add((family, bot, limit)); return Task.FromResult<IReadOnlyList<VetPhotoRecoverableBatch>>([]); }
        public Task ResumeRunsAsync(ReceivingBot bot, ITelegramClient client, CancellationToken ct) { RunResumes++; return Task.CompletedTask; }
        public Task WorkFinishedAsync(ReceivingBot bot, ITelegramClient client, VetPhotoWork work, VetPhotoProcessResult result, CancellationToken ct) => throw Unexpected();
        public Task<bool> CommandAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message, string command, string? args, Guid key, CancellationToken ct) => throw Unexpected();
        public Task<bool> OperationAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message, VetPhotoOperation op, Guid key, CancellationToken ct) => throw Unexpected();
        public Task<bool> HandleCallbackAsync(ReceivingBot bot, ITelegramClient client, CallbackQueryInfo callback, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoPresentationEvidence?> ReadEvidenceAsync(VetDiaryScope scope, Guid source, Guid input, Guid? extraction, long actor, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoBatchChange> RefreshProfileSnapshotAsync(VetDiaryScope scope, Guid batch, int revision, int profile, long actor, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoWorkflowStatus> SetValidationAsync(VetPhotoValidationUpdate update, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoWorkflowStatus> ProposeHumanCorrectionAsync(VetPhotoHumanProposal proposal, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoReview?> ReadPreviewAsync(VetDiaryScope scope, Guid review, long actor, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoWorkflowStatus> DeclineReviewAsync(VetPhotoReviewHandle handle, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoBatchChange> StartCollectionAsync(VetDiaryScope scope, long actor, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoBatchChange> CloseCollectionAsync(VetDiaryScope scope, Guid batch, int revision, long actor, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoBatchHistory> ListBatchesAsync(VetDiaryScope scope, long actor, int offset, int limit, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoBatchChange> CancelRemainderAsync(VetDiaryScope scope, Guid batch, int revision, long actor, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoBatchChange> ChangeAssumptionAsync(VetPhotoAssumptionChange change, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoWorkflowStatus> ChangeCandidateAsync(VetPhotoCandidateChange change, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoBatchChange> AddLateSourceAsync(VetDiaryScope scope, Guid batch, int revision, Guid source, Guid input, long actor, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoReviewChange> StageReviewAsync(VetPhotoStageReview stage, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoReviewChange> BeginPageDeliveryAsync(VetPhotoReviewHandle handle, int page, string hash, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoReviewChange> RecordPageDeliveryAsync(VetPhotoReviewHandle handle, int page, int message, string hash, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoReviewChange> CompleteDeliveryAsync(VetPhotoReviewHandle handle, int prompt, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoReviewChange> RecordPreviewFailureAsync(VetPhotoReviewHandle handle, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoReviewChange> RetryPreviewAsync(VetPhotoReviewHandle handle, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoReviewLookup> GetReviewAsync(VetPhotoReviewHandle handle, CancellationToken ct) => throw Unexpected();
        public Task<VetPhotoReviewLookup> FindNaturalReviewAsync(VetDiaryScope scope, long actor, Guid? key, int? revision, CancellationToken ct) => throw Unexpected();
    }
}
