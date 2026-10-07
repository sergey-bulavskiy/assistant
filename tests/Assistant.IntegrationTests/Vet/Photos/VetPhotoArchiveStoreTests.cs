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
using SkiaSharp;

namespace Assistant.IntegrationTests.Vet.Photos;

public sealed class VetPhotoArchiveStoreTests : VetTestBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Original = ImageBytes(SKColors.White);
    private static readonly byte[] EditedOriginal = ImageBytes(SKColors.Blue);
    private static readonly VetPhotoImageDecoder Decoder = new();
    private VetPhotoStore Archive(VetTestSession s, VetPhotoCapacity? limit = null) =>
        new(s.Context, s.Current, Clock, limit ?? new(), Decoder);
    private static VetPhotoAttachment Attachment(int id, long? size = null) =>
        new($"synthetic-file-{id}", $"synthetic-unique-{id}", "synthetic.png", "image/png", size, 32, 24);
    private static IncomingMessage ImageMessage(int id, long actor = 111, string caption = "synthetic caption") =>
        Text(caption, id, actor) with { Kind = MessageKind.Photo };
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private async Task<VetPhotoAdmission> BoundAsync(VetTestSession s, int id,
        VetDiaryScope? selectedScope = null, long actor = 111, long? size = null)
    {
        var scope = selectedScope ?? Scope;
        var message = ImageMessage(id, actor) with { ChatId = scope.ChatId, TopicId = scope.TopicId };
        var admitted = await Archive(s).AdmitAsync(scope, message, id, Attachment(id, size), null, CancellationToken.None);
        admitted.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        var stored = await s.Messages.StoreAsync(scope.TelegramBotId, id, message, CancellationToken.None);
        stored.MessageDbId.ShouldNotBeNull();
        (await Archive(s).BindMessageAsync(scope, admitted.Source!.Id, stored.MessageDbId!.Value, CancellationToken.None)).ShouldBeTrue();
        return (await Archive(s).GetSourceAsync(scope, admitted.Source.Id, CancellationToken.None))!;
    }

    private async Task<VetPhotoOriginalReference> RetainAsync(VetTestSession s, VetPhotoAdmission admitted,
        VetDiaryScope? selectedScope = null, long actor = 111, byte[]? bytes = null, VetPhotoCapacity? capacity = null)
    {
        var scope = selectedScope ?? Scope;
        var archive = Archive(s, capacity);
        var reservation = await archive.ReserveDownloadAsync(scope, admitted.Source!.Id, admitted.Input!.Id, actor, CancellationToken.None);
        reservation.Status.ShouldBe(VetPhotoArchiveStatus.Reserved);
        var original = bytes ?? Original;
        var result = await archive.CommitOriginalAsync(new(scope, actor, reservation.Claim!.Attempt.Id,
            reservation.Claim.ClaimToken, admitted.Input.Id, original, Decoder.Decode(original, CancellationToken.None).Image!),
            CancellationToken.None);
        result.Status.ShouldBe(VetPhotoArchiveStatus.Retained);
        return result.Reference!;
    }

    private async Task<VetDiaryScope> OtherFamilyAsync()
    {
        var family = new Family { Name = "synthetic second family", CreatedAt = Now };
        Db.Add(family); await Db.SaveChangesAsync();
        var bot = new Assistant.Domain.Bots.Bot { FamilyId = family.Id, TelegramBotId = 2001,
            Username = "synthetic_other_vet_bot", Role = "vet", Status = BotStatus.Active, CreatedAt = Now };
        Db.Add(bot); Db.Add(new FamilyMember { FamilyId = family.Id, TelegramUserId = 333,
            DisplayName = "synthetic second owner", IsOwner = true, Status = FamilyMemberStatus.Approved,
            CreatedAt = Now, UpdatedAt = Now });
        await Db.SaveChangesAsync();
        Db.Add(new Place { BotId = bot.Id, ChatId = -200, TopicId = 7, Title = "synthetic second topic",
            Status = PlaceStatus.Approved, CreatedAt = Now });
        await Db.SaveChangesAsync();
        return new(family.Id, bot.Id, bot.TelegramBotId, -200, 7);
    }

    private async Task<VetPhotoBatch> CollectingAsync(VetTestSession s)
    {
        var profile = await s.Context.VetProfiles.SingleAsync();
        var batch = new VetPhotoBatch { Id = Guid.NewGuid(), FamilyId = FamilyId,
            BotDbId = Bot.BotDbId, TelegramBotId = Bot.TelegramBotId, ChatId = Scope.ChatId, TopicId = Scope.TopicId,
            ProfileId = profile.Id, ProfileRevision = profile.Revision, StarterUserId = 111,
            State = "collecting", IntakeKind = "collection", CreatedAt = Now,
            IntakeOpenedAt = Now.AddSeconds(-1), UpdatedAt = Now };
        s.Context.Add(batch); await s.Context.SaveChangesAsync();
        return batch;
    }

    [Fact]
    public async Task Admission_precedes_offset_and_unbound_source_cannot_reserve_original()
    {
        await SeedAsync(); await using var s = Open();
        var message = ImageMessage(1);
        var source = await Archive(s).AdmitAsync(Scope, message, 1, Attachment(1), null, CancellationToken.None);
        source.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        source.Source!.SourceMessageDbId.ShouldBeNull();
        (await s.Context.Messages.CountAsync()).ShouldBe(0);
        (await s.Context.Bots.SingleAsync()).LastUpdateId.ShouldBe(0);
        (await Archive(s).ReserveDownloadAsync(Scope, source.Source.Id, source.Input!.Id, 111, CancellationToken.None))
            .Status.ShouldBe(VetPhotoArchiveStatus.Refused);
        var stored = await s.Messages.StoreAsync(Bot.TelegramBotId, 1, message, CancellationToken.None);
        (await Archive(s).BindMessageAsync(Scope, source.Source.Id, stored.MessageDbId!.Value, CancellationToken.None)).ShouldBeTrue();
        (await Archive(s).ReserveDownloadAsync(Scope, source.Source.Id, source.Input.Id, 222, CancellationToken.None))
            .Status.ShouldBe(VetPhotoArchiveStatus.Reserved);
        var exact = await s.Context.Set<VetPhotoSource>().SingleAsync();
        exact.SourceAuthorUserId.ShouldBe(111); exact.SourceMessageDbId.ShouldBe(stored.MessageDbId);
    }

    [Fact]
    public async Task Repeated_transport_reuses_identity_and_caption_edit_appends_immutable_input()
    {
        await SeedAsync(); await using var s = Open();
        var first = await BoundAsync(s, 1);
        var replay = await Archive(s).AdmitAsync(Scope, ImageMessage(1), 1, Attachment(1), null, CancellationToken.None);
        replay.Status.ShouldBe(VetPhotoAdmissionStatus.Existing);
        replay.Source!.Id.ShouldBe(first.Source!.Id); replay.Input!.Id.ShouldBe(first.Input!.Id);
        var edited = ImageMessage(1, caption: "synthetic revised caption") with { IsEdit = true, EditedAt = Now.AddSeconds(1) };
        var changed = await Archive(s).AdmitAsync(Scope, edited, 2, Attachment(1), null, CancellationToken.None);
        changed.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        changed.Source!.Id.ShouldBe(first.Source.Id);
        changed.Input!.Id.ShouldNotBe(first.Input.Id);
        changed.Input.ReusesImageInputId.ShouldBe(first.Input.Id);
        (await s.Context.Set<VetPhotoInputRevision>().OrderBy(i => i.Ordinal).Select(i => i.Caption).ToListAsync())
            .ShouldBe(new[] { "synthetic caption", "synthetic revised caption" });
        (await s.Context.Set<VetPhotoSource>().SingleAsync()).CurrentInputRevisionId.ShouldBe(changed.Input.Id);
        (await s.Context.Set<VetPhotoCandidate>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Unassociated_single_images_in_the_same_second_are_independent_reviews()
    {
        await SeedAsync(); await using var s = Open();
        var first = await BoundAsync(s, 1); var second = await BoundAsync(s, 2);
        first.Source!.Association.ShouldBe("single"); second.Source!.Association.ShouldBe("single");
        second.Source.BatchId.ShouldNotBe(first.Source.BatchId);
        (await s.Context.Set<VetPhotoBatch>().Select(b => b.IntakeKind).ToListAsync())
            .ShouldBe(new[] { "single", "single" });
    }

    [Fact]
    public async Task Item_fifty_is_admitted_and_fifty_one_is_visible_full_metadata_without_download()
    {
        await SeedAsync(); await using var s = Open();
        var batch = await CollectingAsync(s);
        for (var id = 1; id <= 50; id++)
        {
            var admitted = await Archive(s).AdmitAsync(Scope, ImageMessage(id), id, Attachment(id), null, CancellationToken.None);
            admitted.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
            admitted.Source!.BatchId.ShouldBe(batch.Id); admitted.Source.ItemNumber.ShouldBe(id);
        }
        var rejected = await Archive(s).AdmitAsync(Scope, ImageMessage(51), 51, Attachment(51), null, CancellationToken.None);
        rejected.Status.ShouldBe(VetPhotoAdmissionStatus.Full);
        rejected.Source!.BatchId.ShouldBeNull(); rejected.Source.ProposedBatchId.ShouldBe(batch.Id);
        rejected.Source.State.ShouldBe("full");
        (await s.Context.Set<VetPhotoSource>().CountAsync(x => x.BatchId == batch.Id)).ShouldBe(50);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync()).ShouldBe(50);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Collecting_window_and_album_association_mark_late_inputs_without_implicit_addition()
    {
        await SeedAsync(); await using var s = Open();
        var batch = await CollectingAsync(s);
        var first = await Archive(s).AdmitAsync(Scope, ImageMessage(1) with { MediaGroupId = "synthetic-album" },
            1, Attachment(1), null, CancellationToken.None);
        await s.Context.Set<VetPhotoBatch>().Where(b => b.Id == batch.Id).ExecuteUpdateAsync(u =>
            u.SetProperty(b => b.State, "closed").SetProperty(b => b.ClosedAt, Now)
                .SetProperty(b => b.IntakeClosedAt, Now), CancellationToken.None);
        var late = await Archive(s).AdmitAsync(Scope, ImageMessage(2) with { MediaGroupId = "synthetic-album" },
            2, Attachment(2), null, CancellationToken.None);
        late.Status.ShouldBe(VetPhotoAdmissionStatus.Late);
        late.Source!.BatchId.ShouldBeNull(); late.Source.ProposedBatchId.ShouldBe(batch.Id);
        (await s.Context.Set<VetPhotoSource>().CountAsync(x => x.BatchId == batch.Id)).ShouldBe(1);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync()).ShouldBe(1);
        first.Source!.Association.ShouldBe("collecting");
    }

    [Fact]
    public async Task Null_topic_is_exact_and_unapproved_actor_or_missing_topic_cannot_admit()
    {
        await SeedAsync(); await using var s = Open();
        var scope = Scope with { TopicId = null };
        var message = ImageMessage(1) with { TopicId = null };
        (await Archive(s).AdmitAsync(scope, message, 1, Attachment(1), null, CancellationToken.None))
            .Status.ShouldBe(VetPhotoAdmissionStatus.Refused);
        (await Archive(s).AdmitAsync(Scope, ImageMessage(2, 333), 2, Attachment(2), null, CancellationToken.None))
            .Status.ShouldBe(VetPhotoAdmissionStatus.Refused);
        (await s.Context.Set<VetPhotoSource>().CountAsync()).ShouldBe(0);
        var own = await BoundAsync(s, 3);
        (await Archive(s).GetSourceAsync(scope, own.Source!.Id, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Safe_scalar_metadata_preserves_whole_runes_and_invalid_caption_is_not_persisted()
    {
        await SeedAsync(); await using var s = Open();
        var attachment = Attachment(1) with { FileName = new string('n', 255) + "😀", ReportedMimeType = "image/png\uD800" };
        var admitted = await Archive(s).AdmitAsync(Scope, ImageMessage(1), 1, attachment, null, CancellationToken.None);
        admitted.Input!.FileName.ShouldBe(new string('n', 255));
        admitted.Input.ReportedMimeType.ShouldBe("image/png�");
        var roundtrip = await s.Context.Set<VetPhotoInputRevision>().SingleAsync();
        roundtrip.FileName.ShouldBe(admitted.Input.FileName);
        roundtrip.ReportedMimeType.ShouldBe(admitted.Input.ReportedMimeType);
        var rejected = await Archive(s).AdmitAsync(Scope, ImageMessage(2, caption: "synthetic\uD800"),
            2, Attachment(2), null, CancellationToken.None);
        rejected.Status.ShouldBe(VetPhotoAdmissionStatus.InvalidMetadata);
        (await s.Context.Set<VetPhotoInputRevision>().CountAsync()).ShouldBe(1);
        (await s.Context.Set<VetPhotoSource>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Caption_only_revision_shares_exact_retained_blob_without_another_byte_reservation()
    {
        await SeedAsync(); await using var s = Open();
        var first = await BoundAsync(s, 1); var original = await RetainAsync(s, first);
        var edited = ImageMessage(1, caption: "synthetic new caption") with { IsEdit = true, EditedAt = Now.AddSeconds(1) };
        var changed = await Archive(s).AdmitAsync(Scope, edited, 2, Attachment(1), null, CancellationToken.None);
        var reused = await Archive(s).ReserveDownloadAsync(Scope, changed.Source!.Id, changed.Input!.Id, 222, CancellationToken.None);
        reused.Status.ShouldBe(VetPhotoArchiveStatus.Retained); reused.Claim.ShouldBeNull();
        reused.Reference!.BlobId.ShouldBe(original.BlobId);
        var totals = await Archive(s).GetCapacityAsync(Scope, 111, CancellationToken.None);
        totals.ShouldBe(new(Original.LongLength, 0, 2, 0, 0, 0));
        (await s.Context.Set<VetPhotoBlob>().SingleAsync()).Content.ShouldBe(Original);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Across_family_concurrent_reservations_cannot_overfill_global_byte_capacity()
    {
        await SeedAsync(); var other = await OtherFamilyAsync();
        await using var first = Open(); await using var second = Open(other.FamilyId);
        var ownInput = await BoundAsync(first, 1, size: Original.LongLength);
        var otherInput = await BoundAsync(second, 2, other, 333, Original.LongLength);
        var capacity = new VetPhotoCapacity(Original.LongLength, 100, 100);
        var results = await Task.WhenAll(
            Archive(first, capacity).ReserveDownloadAsync(Scope, ownInput.Source!.Id, ownInput.Input!.Id, 111, CancellationToken.None),
            Archive(second, capacity).ReserveDownloadAsync(other, otherInput.Source!.Id, otherInput.Input!.Id, 333, CancellationToken.None));
        results.Select(r => r.Status).OrderBy(s => s).ShouldBe(
            new[] { VetPhotoArchiveStatus.Reserved, VetPhotoArchiveStatus.CapacityFull }.OrderBy(s => s));
        await using var verify = Open();
        (await Archive(verify, capacity).GetCapacityAsync(Scope, 111, CancellationToken.None))
            .ShouldBe(new(0, Original.LongLength, 0, 1, 0, 0));
        (await verify.Context.Set<VetPhotoBlob>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Retained_and_reserved_input_slots_both_apply_before_new_fetch()
    {
        await SeedAsync(); await using var s = Open();
        var first = await BoundAsync(s, 1, size: Original.LongLength);
        await RetainAsync(s, first);
        var second = await BoundAsync(s, 2, size: Original.LongLength);
        var third = await BoundAsync(s, 3, size: Original.LongLength);
        var capacity = new VetPhotoCapacity(MaxInputRevisions: 2);
        (await Archive(s, capacity).ReserveDownloadAsync(Scope, second.Source!.Id, second.Input!.Id, 111, CancellationToken.None))
            .Status.ShouldBe(VetPhotoArchiveStatus.Reserved);
        (await Archive(s, capacity).ReserveDownloadAsync(Scope, third.Source!.Id, third.Input!.Id, 111, CancellationToken.None))
            .Status.ShouldBe(VetPhotoArchiveStatus.CapacityFull);
        (await Archive(s, capacity).GetCapacityAsync(Scope, 111, CancellationToken.None))
            .ShouldBe(new(Original.LongLength, Original.LongLength, 1, 1, 0, 0));
    }

    [Fact]
    public async Task Actual_size_larger_than_hint_is_rechecked_before_any_blob_or_reference_write()
    {
        await SeedAsync(); await using var s = Open();
        var input = await BoundAsync(s, 1, size: 1);
        var capacity = new VetPhotoCapacity(Original.LongLength - 1, 100, 100);
        var claim = (await Archive(s, capacity).ReserveDownloadAsync(Scope, input.Source!.Id, input.Input!.Id, 111, CancellationToken.None)).Claim!;
        var refused = await Archive(s, capacity).CommitOriginalAsync(new(Scope, 111, claim.Attempt.Id, claim.ClaimToken,
            input.Input.Id, Original, Decoder.Decode(Original, CancellationToken.None).Image!), CancellationToken.None);
        refused.Status.ShouldBe(VetPhotoArchiveStatus.CapacityFull);
        (await s.Context.Set<VetPhotoBlob>().CountAsync()).ShouldBe(0);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync()).ShouldBe(0);
        (await Archive(s, capacity).GetCapacityAsync(Scope, 111, CancellationToken.None)).ReservedBytes.ShouldBe(1);
    }

    [Fact]
    public async Task Exact_content_deduplicates_only_within_family_and_private_handles_do_not_cross_scope()
    {
        await SeedAsync(); var other = await OtherFamilyAsync();
        await using var own = Open(); await using var foreign = Open(other.FamilyId);
        var first = await BoundAsync(own, 1); var second = await BoundAsync(own, 2);
        var firstRef = await RetainAsync(own, first); var secondRef = await RetainAsync(own, second);
        firstRef.BlobId.ShouldBe(secondRef.BlobId);
        var otherInput = await BoundAsync(foreign, 3, other, 333);
        var otherRef = await RetainAsync(foreign, otherInput, other, 333);
        otherRef.BlobId.ShouldNotBe(firstRef.BlobId); otherRef.ContentHash.ShouldBe(firstRef.ContentHash);
        (await own.Context.Set<VetPhotoBlob>().CountAsync()).ShouldBe(1);
        (await foreign.Context.Set<VetPhotoBlob>().CountAsync()).ShouldBe(1);
        (await Archive(own).GetSourceAsync(Scope, otherInput.Source!.Id, CancellationToken.None)).ShouldBeNull();
        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Archive(own).GetSourceAsync(other, otherInput.Source.Id, CancellationToken.None));
        error.Message.ShouldBe("Vet scope is not active.");
        (await Archive(own).GetCapacityAsync(Scope, 111, CancellationToken.None)).RetainedBytes.ShouldBe(Original.LongLength * 2);
    }

    [Fact]
    public async Task Superseded_download_can_retain_its_original_without_replacing_current_source()
    {
        await SeedAsync(); await using var s = Open();
        var first = await BoundAsync(s, 1, size: Original.LongLength);
        var oldClaim = (await Archive(s).ReserveDownloadAsync(Scope, first.Source!.Id, first.Input!.Id, 111, CancellationToken.None)).Claim!;
        var changed = await Archive(s).AdmitAsync(Scope,
            ImageMessage(1, caption: "synthetic edited image") with { IsEdit = true, EditedAt = Now.AddSeconds(1) },
            2, Attachment(2, EditedOriginal.LongLength), null, CancellationToken.None);
        var oldResult = await Archive(s).CommitOriginalAsync(new(Scope, 111, oldClaim.Attempt.Id, oldClaim.ClaimToken,
            first.Input.Id, Original, Decoder.Decode(Original, CancellationToken.None).Image!), CancellationToken.None);
        oldResult.Status.ShouldBe(VetPhotoArchiveStatus.Retained);
        var current = await s.Context.Set<VetPhotoSource>().SingleAsync();
        current.CurrentInputRevisionId.ShouldBe(changed.Input!.Id); current.State.ShouldBe("admitted");
        var revised = await RetainAsync(s, changed, bytes: EditedOriginal);
        revised.BlobId.ShouldNotBe(oldResult.Reference!.BlobId);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Expired_claim_is_stale_and_takeover_fences_old_token_before_retaining_once()
    {
        await SeedAsync(); await using var s = Open();
        var input = await BoundAsync(s, 1, size: Original.LongLength);
        var first = (await Archive(s).ReserveDownloadAsync(Scope, input.Source!.Id, input.Input!.Id, 111, CancellationToken.None)).Claim!;
        Clock.UtcNow = Now.AddMinutes(3);
        var stale = new VetPhotoArchiveCommit(Scope, 111, first.Attempt.Id, first.ClaimToken,
            input.Input.Id, Original, Decoder.Decode(Original, CancellationToken.None).Image!);
        (await Archive(s).CommitOriginalAsync(stale, CancellationToken.None)).Status.ShouldBe(VetPhotoArchiveStatus.Stale);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync()).ShouldBe(0);
        var second = (await Archive(s).ReserveDownloadAsync(Scope, input.Source.Id, input.Input.Id, 222, CancellationToken.None)).Claim!;
        second.ClaimToken.ShouldNotBe(first.ClaimToken);
        (await Archive(s).CommitOriginalAsync(stale, CancellationToken.None)).Status.ShouldBe(VetPhotoArchiveStatus.Stale);
        var saved = await Archive(s).CommitOriginalAsync(stale with { ActorUserId = 222, ClaimToken = second.ClaimToken }, CancellationToken.None);
        saved.Status.ShouldBe(VetPhotoArchiveStatus.Retained);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync()).ShouldBe(1);
        (await s.Context.Set<VetPhotoSource>().SingleAsync()).SourceAuthorUserId.ShouldBe(111);
    }

    [Theory]
    [InlineData("download_unavailable", true, "failed")]
    [InlineData("download_timeout", false, "failed")]
    [InlineData("download_cancelled", true, "paused")]
    [InlineData("invalid_image", true, "failed")]
    public async Task Nonretryable_failure_releases_capacity_and_never_claims_automatically(string category, bool transient, string state)
    {
        await SeedAsync(); await using var s = Open();
        var input = await BoundAsync(s, 1, size: Original.LongLength);
        var claim = (await Archive(s).ReserveDownloadAsync(Scope, input.Source!.Id, input.Input!.Id, 111, CancellationToken.None)).Claim!;
        (await Archive(s).RecordDownloadFailureAsync(Scope, claim.Attempt.Id, claim.ClaimToken,
            category, transient, CancellationToken.None)).ShouldBeTrue();
        (await s.Context.Set<VetPhotoAttempt>().SingleAsync()).State.ShouldBe(state);
        (await Archive(s).GetCapacityAsync(Scope, 111, CancellationToken.None)).ShouldBe(new(0, 0, 0, 0, 0, 0));
        Clock.UtcNow = Now.AddMinutes(10);
        (await Archive(s).ReserveDownloadAsync(Scope, input.Source.Id, input.Input.Id, 111, CancellationToken.None))
            .Status.ShouldBe(VetPhotoArchiveStatus.Refused);
    }

    [Fact]
    public async Task Only_known_timeout_can_retry_after_five_seconds_and_never_attempt_a_third_download()
    {
        await SeedAsync(); await using var s = Open();
        var input = await BoundAsync(s, 1, size: Original.LongLength);
        var first = (await Archive(s).ReserveDownloadAsync(Scope, input.Source!.Id, input.Input!.Id, 111, CancellationToken.None)).Claim!;
        (await Archive(s).RecordDownloadFailureAsync(Scope, first.Attempt.Id, first.ClaimToken,
            "download_timeout", true, CancellationToken.None)).ShouldBeTrue();
        (await Archive(s).ReserveDownloadAsync(Scope, input.Source.Id, input.Input.Id, 111, CancellationToken.None))
            .Status.ShouldBe(VetPhotoArchiveStatus.Refused);
        Clock.UtcNow = Now.AddSeconds(5);
        var second = (await Archive(s).ReserveDownloadAsync(Scope, input.Source.Id, input.Input.Id, 111, CancellationToken.None)).Claim!;
        second.Attempt.DownloadAttemptCount.ShouldBe(2);
        second.ClaimToken.ShouldNotBe(first.ClaimToken);
        (await Archive(s).RecordDownloadFailureAsync(Scope, second.Attempt.Id, second.ClaimToken,
            "download_timeout", true, CancellationToken.None)).ShouldBeTrue();
        Clock.UtcNow = Now.AddMinutes(10);
        (await Archive(s).ReserveDownloadAsync(Scope, input.Source.Id, input.Input.Id, 111, CancellationToken.None))
            .Status.ShouldBe(VetPhotoArchiveStatus.Refused);
        (await s.Context.Set<VetPhotoAttempt>().SingleAsync()).DownloadAttemptCount.ShouldBe(2);
        (await Archive(s).GetCapacityAsync(Scope, 111, CancellationToken.None)).ReservedBytes.ShouldBe(0);
    }

    [Fact]
    public async Task Revoked_member_or_exact_place_prevents_claim_before_any_original_write()
    {
        await SeedAsync(); await using var s = Open();
        var input = await BoundAsync(s, 1);
        await s.Context.FamilyMembers.Where(m => m.TelegramUserId == 111)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), CancellationToken.None);
        (await Archive(s).ReserveDownloadAsync(Scope, input.Source!.Id, input.Input!.Id, 111, CancellationToken.None))
            .Status.ShouldBe(VetPhotoArchiveStatus.Refused);
        await s.Context.Places.Where(p => p.TopicId == 7)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlaceStatus.Disabled), CancellationToken.None);
        (await Archive(s).ReserveDownloadAsync(Scope, input.Source.Id, input.Input.Id, 222, CancellationToken.None))
            .Status.ShouldBe(VetPhotoArchiveStatus.Refused);
        (await s.Context.Set<VetPhotoBlob>().CountAsync()).ShouldBe(0);
        (await s.Context.Set<VetPhotoAttempt>().SingleAsync()).State.ShouldBe("queued");
    }

    private async Task<VetPhotoReview> DeletionReviewAsync(VetTestSession s,
        params VetPhotoOriginalReference[] references)
    {
        var selection = new List<VetPhotoOriginalSelection>();
        foreach (var reference in references)
        {
            var input = await s.Context.Set<VetPhotoInputRevision>().AsNoTracking().SingleAsync(x => x.Id == reference.InputRevisionId);
            var source = await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(x => x.Id == input.SourceId);
            var count = await s.Context.Set<VetPhotoOriginalReference>().LongCountAsync(x => x.BlobId == reference.BlobId && x.State == "retained");
            selection.Add(new(reference.Id, reference.Revision, input.Id, reference.BlobId, source.Id,
                source.CurrentInputRevisionId, source.CurrentOrdinal, count, null, null));
        }
        var pages = new[] { "synthetic selected original preview", "synthetic deletion acceptance prompt" };
        var review = new VetPhotoReview { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = Scope.ChatId, TopicId = Scope.TopicId,
            OperationKey = Guid.NewGuid(), Kind = "delete_originals", RequesterUserId = 111,
            SelectionJson = JsonSerializer.Serialize(selection, Json), PreviewPagesJson = JsonSerializer.Serialize(pages, Json),
            DeliveredPagesJson = JsonSerializer.Serialize(new[] { new VetPhotoPageDelivery(0, 901, Hash(pages[0])),
                new VetPhotoPageDelivery(1, 902, Hash(pages[1])) }, Json), PageCount = 2,
            CompletePreviewDelivered = true, AcceptancePromptMessageId = 902, CreatedAt = Now };
        review.Fingerprint = Hash(review.SelectionJson);
        s.Context.Add(review); await s.Context.SaveChangesAsync();
        return review;
    }

    private VetPhotoOriginalDeletion Delete(VetPhotoReview review, long actor = 111, int? prompt = null) =>
        new(Scope, review.Id, review.Revision, review.OperationKey, actor, prompt);

    private async Task<VetPhotoAttempt> ImageClaimAsync(VetTestSession s, VetPhotoAdmission input)
    {
        var attempt = new VetPhotoAttempt { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = Scope.ChatId, TopicId = Scope.TopicId,
            SourceId = input.Source!.Id, InputRevisionId = input.Input!.Id, ActorUserId = 111,
            Kind = "image", State = "claimed", ClaimToken = Guid.NewGuid(), LeaseUntil = Clock.UtcNow.AddMinutes(2),
            ExpectedSourceOrdinal = input.Source.CurrentOrdinal, ExpectedCurrentInputId = input.Source.CurrentInputRevisionId,
            ReservedResultSlot = true, CreatedAt = Now, UpdatedAt = Now };
        s.Context.Add(attempt); await s.Context.SaveChangesAsync(); return attempt;
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("missing_page")]
    [InlineData("wrong_hash")]
    [InlineData("duplicate_index")]
    [InlineData("oversized_page")]
    [InlineData("wrong_last_prompt")]
    [InlineData("wrong_selection_hash")]
    [InlineData("failed")]
    [InlineData("callback_wrong_prompt")]
    public async Task Incomplete_or_changed_review_proof_never_deletes_any_reference(string defect)
    {
        await SeedAsync(); await using var s = Open();
        var input = await BoundAsync(s, 1); var reference = await RetainAsync(s, input);
        var review = await DeletionReviewAsync(s, reference);
        if (defect == "incomplete") review.CompletePreviewDelivered = false;
        if (defect == "missing_page") review.DeliveredPagesJson = JsonSerializer.Serialize(new[] {
            new VetPhotoPageDelivery(1, 902, Hash("synthetic deletion acceptance prompt")) }, Json);
        if (defect == "wrong_hash") review.DeliveredPagesJson = JsonSerializer.Serialize(new[] {
            new VetPhotoPageDelivery(0, 901, Hash("changed synthetic page")),
            new VetPhotoPageDelivery(1, 902, Hash("synthetic deletion acceptance prompt")) }, Json);
        if (defect == "duplicate_index") review.DeliveredPagesJson = JsonSerializer.Serialize(new[] {
            new VetPhotoPageDelivery(1, 901, Hash("synthetic selected original preview")),
            new VetPhotoPageDelivery(1, 902, Hash("synthetic deletion acceptance prompt")) }, Json);
        if (defect == "oversized_page")
        {
            var pages = new[] { new string('x', 3501), "synthetic deletion acceptance prompt" };
            review.PreviewPagesJson = JsonSerializer.Serialize(pages, Json);
            review.DeliveredPagesJson = JsonSerializer.Serialize(new[] { new VetPhotoPageDelivery(0, 901, Hash(pages[0])),
                new VetPhotoPageDelivery(1, 902, Hash(pages[1])) }, Json);
        }
        if (defect == "wrong_last_prompt") review.AcceptancePromptMessageId = 903;
        if (defect == "wrong_selection_hash") review.Fingerprint = new string('0', 64);
        if (defect == "failed") review.State = "preview_failed";
        await s.Context.SaveChangesAsync();
        var result = await Archive(s).DeleteOriginalsAsync(Delete(review, prompt: defect == "callback_wrong_prompt" ? 903 : null), CancellationToken.None);
        result.ShouldBe(new(VetMutationStatus.Stale, 0, 0));
        var stored = await s.Context.Set<VetPhotoOriginalReference>().SingleAsync();
        stored.State.ShouldBe("retained"); stored.Revision.ShouldBe(1); stored.DeletedAt.ShouldBeNull();
        (await s.Context.Set<VetPhotoBlob>().SingleAsync()).Content.ShouldBe(Original);
    }

    [Fact]
    public async Task Complete_natural_delete_is_audited_and_same_operation_replays_without_second_mutation()
    {
        await SeedAsync(); await using var s = Open();
        var input = await BoundAsync(s, 1); var reference = await RetainAsync(s, input);
        var review = await DeletionReviewAsync(s, reference);
        var result = await Archive(s).DeleteOriginalsAsync(Delete(review), CancellationToken.None);
        result.ShouldBe(new(VetMutationStatus.Applied, 1, Original.LongLength));
        var replay = await Archive(s).DeleteOriginalsAsync(Delete(review), CancellationToken.None);
        replay.ShouldBe(new(VetMutationStatus.AlreadyApplied, 1, Original.LongLength));
        var stored = await s.Context.Set<VetPhotoOriginalReference>().SingleAsync();
        stored.State.ShouldBe("deleted"); stored.Revision.ShouldBe(2); stored.DeletedAt.ShouldBe(Now);
        stored.DeletedByUserId.ShouldBe(111); stored.DeletionReviewId.ShouldBe(review.Id);
        var resolved = await s.Context.Set<VetPhotoReview>().SingleAsync();
        resolved.State.ShouldBe("accepted"); resolved.DecisionActorUserId.ShouldBe(111);
        (await Archive(s).GetCapacityAsync(Scope, 111, CancellationToken.None)).ShouldBe(new(Original.LongLength, 0, 0, 0, 0, 0));
    }

    [Theory]
    [InlineData("member")]
    [InlineData("revoked_owner")]
    [InlineData("revoked_place")]
    public async Task Deletion_requires_current_owner_and_exact_approved_place(string gate)
    {
        await SeedAsync(); await using var s = Open();
        var input = await BoundAsync(s, 1); var reference = await RetainAsync(s, input);
        var review = await DeletionReviewAsync(s, reference);
        if (gate == "revoked_owner") await s.Context.FamilyMembers.Where(m => m.TelegramUserId == 111)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied));
        if (gate == "revoked_place") await s.Context.Places.Where(p => p.TopicId == 7)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlaceStatus.Disabled));
        (await Archive(s).DeleteOriginalsAsync(Delete(review, gate == "member" ? 222 : 111), CancellationToken.None))
            .ShouldBe(new(VetMutationStatus.Refused, 0, 0));
        (await s.Context.Set<VetPhotoOriginalReference>().SingleAsync()).State.ShouldBe("retained");
        (await s.Context.Set<VetPhotoReview>().SingleAsync()).State.ShouldBe("preview");
    }

    [Theory]
    [InlineData("reference_revision")]
    [InlineData("source_id")]
    [InlineData("current_input")]
    [InlineData("source_ordinal")]
    [InlineData("event_revision")]
    public async Task Stale_selected_reference_source_input_or_event_rolls_back_whole_subset(string stale)
    {
        await SeedAsync(); await using var s = Open();
        var first = await BoundAsync(s, 1); var second = await BoundAsync(s, 2);
        var firstRef = await RetainAsync(s, first); var secondRef = await RetainAsync(s, second, bytes: EditedOriginal);
        var evidence = await EvidenceAsync(s, id: 1000, update: 1000);
        var saved = await s.Diary.ApplyAsync(Save(Scope, evidence.Source, evidence.Profile, evidence.State), CancellationToken.None);
        saved.Status.ShouldBe(VetMutationStatus.Applied);
        var eventRow = await s.Context.Set<VetEvent>().SingleAsync();
        var review = await DeletionReviewAsync(s, firstRef, secondRef);
        var selection = JsonSerializer.Deserialize<VetPhotoOriginalSelection[]>(review.SelectionJson, Json)!;
        selection[1] = stale switch {
            "reference_revision" => selection[1] with { Revision = 2 },
            "source_id" => selection[1] with { SourceId = first.Source!.Id },
            "current_input" => selection[1] with { ExpectedCurrentInputId = first.Input!.Id },
            "source_ordinal" => selection[1] with { ExpectedSourceOrdinal = 2 },
            _ => selection[1] with { EventId = eventRow.Id, EventRevision = eventRow.Revision + 1 }
        };
        review.SelectionJson = JsonSerializer.Serialize(selection, Json); review.Fingerprint = Hash(review.SelectionJson);
        await s.Context.SaveChangesAsync();
        (await Archive(s).DeleteOriginalsAsync(Delete(review), CancellationToken.None)).ShouldBe(new(VetMutationStatus.Stale, 0, 0));
        (await s.Context.Set<VetPhotoOriginalReference>().Select(r => r.State).ToListAsync()).ShouldAllBe(x => x == "retained");
        (await s.Context.Set<VetPhotoOriginalReference>().Select(r => r.Revision).ToListAsync()).ShouldBe(new[] { 1, 1 });
        (await s.Context.Set<VetPhotoBlob>().CountAsync(b => b.Content != null && b.State == "retained")).ShouldBe(2);
    }

    [Fact]
    public async Task Deleting_originals_preserves_facts_actions_and_immutable_provenance()
    {
        await SeedAsync(); await using var s = Open();
        var input = await BoundAsync(s, 1); var reference = await RetainAsync(s, input);
        var evidence = await EvidenceAsync(s, update: 1000); await s.Diary.ApplyAsync(Save(Scope, evidence.Source, evidence.Profile, evidence.State), CancellationToken.None);
        var review = await DeletionReviewAsync(s, reference);
        var facts = JsonSerializer.Serialize(await s.Context.Set<VetEvent>().AsNoTracking().ToListAsync(), Json);
        var actions = JsonSerializer.Serialize(await s.Context.Set<VetDiaryAction>().AsNoTracking().ToListAsync(), Json);
        var inputs = JsonSerializer.Serialize(await s.Context.Set<VetPhotoInputRevision>().AsNoTracking().ToListAsync(), Json);
        var sources = JsonSerializer.Serialize(await s.Context.Set<VetPhotoSource>().AsNoTracking().ToListAsync(), Json);
        (await Archive(s).DeleteOriginalsAsync(Delete(review), CancellationToken.None)).Status.ShouldBe(VetMutationStatus.Applied);
        JsonSerializer.Serialize(await s.Context.Set<VetEvent>().AsNoTracking().ToListAsync(), Json).ShouldBe(facts);
        JsonSerializer.Serialize(await s.Context.Set<VetDiaryAction>().AsNoTracking().ToListAsync(), Json).ShouldBe(actions);
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoInputRevision>().AsNoTracking().ToListAsync(), Json).ShouldBe(inputs);
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoSource>().AsNoTracking().ToListAsync(), Json).ShouldBe(sources);
    }

    [Fact]
    public async Task Shared_retained_reference_protects_blob_and_later_delete_reclaims_once_after_restart()
    {
        await SeedAsync();
        await using (var s = Open())
        {
            var first = await RetainAsync(s, await BoundAsync(s, 1)); var second = await RetainAsync(s, await BoundAsync(s, 2));
            (await Archive(s).DeleteOriginalsAsync(Delete(await DeletionReviewAsync(s, first)), CancellationToken.None))
                .ShouldBe(new(VetMutationStatus.Applied, 1, 0));
            (await Archive(s).ReclaimAsync(FamilyId, 50, CancellationToken.None)).ShouldBe(0);
            (await s.Context.Set<VetPhotoBlob>().SingleAsync()).Content.ShouldBe(Original);
            (await Archive(s).DeleteOriginalsAsync(Delete(await DeletionReviewAsync(s, second)), CancellationToken.None))
                .ShouldBe(new(VetMutationStatus.Applied, 1, Original.LongLength));
        }
        await using var restarted = Open();
        (await Archive(restarted).ReclaimAsync(FamilyId, 50, CancellationToken.None)).ShouldBe(1);
        (await Archive(restarted).ReclaimAsync(FamilyId, 50, CancellationToken.None)).ShouldBe(0);
        var blob = await restarted.Context.Set<VetPhotoBlob>().SingleAsync();
        blob.Content.ShouldBeNull(); blob.State.ShouldBe("reclaimed"); blob.ReclaimedAt.ShouldBe(Now);
        (await Archive(restarted).GetCapacityAsync(Scope, 111, CancellationToken.None)).ShouldBe(new(0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task Active_reader_lease_protects_bytes_but_deleted_reference_cannot_be_read_or_downloaded_again()
    {
        await SeedAsync(); await using var s = Open();
        var input = await BoundAsync(s, 1); var reference = await RetainAsync(s, input);
        var claim = await ImageClaimAsync(s, input);
        var read = await Archive(s).ReadOriginalAsync(Scope, input.Input!.Id, 111, claim.Id, claim.ClaimToken!.Value, claim.LeaseUntil!.Value, CancellationToken.None);
        read!.Original.ShouldBe(Original); read.OriginalReferenceRevision.ShouldBe(1);
        var again = await Archive(s).ReadOriginalAsync(Scope, input.Input.Id, 111, claim.Id, claim.ClaimToken.Value, claim.LeaseUntil.Value, CancellationToken.None);
        again!.ReaderLeaseId.ShouldBe(read.ReaderLeaseId);
        (await s.Context.Set<VetPhotoReaderLease>().CountAsync()).ShouldBe(1);
        (await Archive(s).DeleteOriginalsAsync(Delete(await DeletionReviewAsync(s, reference)), CancellationToken.None))
            .ShouldBe(new(VetMutationStatus.Applied, 1, 0));
        (await Archive(s).ReadOriginalAsync(Scope, input.Input.Id, 111, claim.Id, claim.ClaimToken.Value, claim.LeaseUntil.Value, CancellationToken.None)).ShouldBeNull();
        (await Archive(s).ReserveDownloadAsync(Scope, input.Source!.Id, input.Input.Id, 111, CancellationToken.None)).Status.ShouldBe(VetPhotoArchiveStatus.OriginalDeleted);
        (await Archive(s).CommitOriginalAsync(new(Scope, 111, claim.Id, claim.ClaimToken.Value, input.Input.Id, Original, Decoder.Decode(Original, CancellationToken.None).Image!), CancellationToken.None)).Status.ShouldBe(VetPhotoArchiveStatus.OriginalDeleted);
        (await Archive(s).ReclaimAsync(FamilyId, 50, CancellationToken.None)).ShouldBe(0);
        (await s.Context.Set<VetPhotoBlob>().SingleAsync()).Content.ShouldBe(Original);
        var edited = await Archive(s).AdmitAsync(Scope, ImageMessage(1, caption: "synthetic after deletion") with { IsEdit = true, EditedAt = Now.AddSeconds(1) },
            2, Attachment(1), null, CancellationToken.None);
        (await Archive(s).ReserveDownloadAsync(Scope, edited.Source!.Id, edited.Input!.Id, 111, CancellationToken.None)).Status.ShouldBe(VetPhotoArchiveStatus.OriginalDeleted);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync()).ShouldBe(1);
        await Archive(s).ReleaseReaderAsync(Scope, read.ReaderLeaseId, CancellationToken.None);
        await Archive(s).ReleaseReaderAsync(Scope, read.ReaderLeaseId, CancellationToken.None);
        (await Archive(s).ReclaimAsync(FamilyId, 50, CancellationToken.None)).ShouldBe(1);
        (await s.Context.Set<VetPhotoReaderLease>().SingleAsync()).ReleasedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("actor")]
    [InlineData("expiry")]
    [InlineData("source_pointer")]
    [InlineData("released")]
    public async Task Read_requires_live_exact_image_claim_and_source_pointer(string stale)
    {
        await SeedAsync(); await using var s = Open();
        var input = await BoundAsync(s, 1); await RetainAsync(s, input);
        var claim = await ImageClaimAsync(s, input);
        if (stale == "source_pointer") await Archive(s).AdmitAsync(Scope,
            ImageMessage(1, caption: "synthetic revision") with { IsEdit = true, EditedAt = Now.AddSeconds(1) }, 2, Attachment(1), null, CancellationToken.None);
        if (stale == "expiry") Clock.UtcNow = Now.AddMinutes(3);
        if (stale == "released")
        {
            var read = await Archive(s).ReadOriginalAsync(Scope, input.Input!.Id, 111, claim.Id, claim.ClaimToken!.Value, claim.LeaseUntil!.Value, CancellationToken.None);
            await Archive(s).ReleaseReaderAsync(Scope, read!.ReaderLeaseId, CancellationToken.None);
        }
        (await Archive(s).ReadOriginalAsync(Scope, input.Input!.Id, stale == "actor" ? 222 : 111, claim.Id,
            stale == "token" ? Guid.NewGuid() : claim.ClaimToken!.Value, claim.LeaseUntil!.Value, CancellationToken.None)).ShouldBeNull();
        (await s.Context.Set<VetPhotoReaderLease>().CountAsync()).ShouldBe(stale == "released" ? 1 : 0);
    }

    [Fact]
    public async Task Expired_reader_allows_restart_reclamation_and_new_reference_protects_pending_blob()
    {
        await SeedAsync(); await using var s = Open();
        var first = await BoundAsync(s, 1); var reference = await RetainAsync(s, first);
        var claim = await ImageClaimAsync(s, first);
        var read = await Archive(s).ReadOriginalAsync(Scope, first.Input!.Id, 111, claim.Id, claim.ClaimToken!.Value, claim.LeaseUntil!.Value, CancellationToken.None);
        read!.Original.ShouldBe(Original);
        (await Archive(s).DeleteOriginalsAsync(Delete(await DeletionReviewAsync(s, reference)), CancellationToken.None)).Status.ShouldBe(VetMutationStatus.Applied);
        var secondRef = await RetainAsync(s, await BoundAsync(s, 2));
        secondRef.BlobId.ShouldBe(reference.BlobId);
        (await Archive(s).ReclaimAsync(FamilyId, 50, CancellationToken.None)).ShouldBe(0);
        (await s.Context.Set<VetPhotoBlob>().SingleAsync()).State.ShouldBe("retained");
        (await Archive(s).DeleteOriginalsAsync(Delete(await DeletionReviewAsync(s, secondRef)), CancellationToken.None)).Status.ShouldBe(VetMutationStatus.Applied);
        Clock.UtcNow = Now.AddMinutes(3);
        await using var restarted = Open();
        (await Archive(restarted).ReclaimAsync(FamilyId, 50, CancellationToken.None)).ShouldBe(1);
        (await restarted.Context.Set<VetPhotoReaderLease>().SingleAsync()).ReleasedAt.ShouldBeNull();
        (await restarted.Context.Set<VetPhotoBlob>().SingleAsync()).Content.ShouldBeNull();
    }

    [Fact]
    public async Task New_shared_reference_after_preview_makes_deletion_stale_without_writes()
    {
        await SeedAsync(); await using var s = Open();
        var first = await RetainAsync(s, await BoundAsync(s, 1));
        var review = await DeletionReviewAsync(s, first);
        await RetainAsync(s, await BoundAsync(s, 2));
        (await Archive(s).DeleteOriginalsAsync(Delete(review), CancellationToken.None)).ShouldBe(new(VetMutationStatus.Stale, 0, 0));
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained")).ShouldBe(2);
        (await s.Context.Set<VetPhotoBlob>().SingleAsync()).Content.ShouldBe(Original);
    }

    [Fact]
    public async Task Final_review_database_failure_rolls_back_reference_blob_and_review_then_retry_succeeds()
    {
        await SeedAsync(); VetPhotoReview review;
        await using (var seed = Open())
        {
            var reference = await RetainAsync(seed, await BoundAsync(seed, 1));
            review = await DeletionReviewAsync(seed, reference);
        }
        var failure = new AcceptedReviewFailure();
        await using var failed = Open(interceptor: failure);
        await Should.ThrowAsync<DbUpdateException>(() => Archive(failed).DeleteOriginalsAsync(Delete(review), CancellationToken.None));
        failure.Hits.ShouldBe(1);
        await using var verify = Open();
        var referenceAfter = await verify.Context.Set<VetPhotoOriginalReference>().SingleAsync();
        referenceAfter.State.ShouldBe("retained"); referenceAfter.Revision.ShouldBe(1); referenceAfter.DeletedAt.ShouldBeNull();
        var blob = await verify.Context.Set<VetPhotoBlob>().SingleAsync();
        blob.State.ShouldBe("retained"); blob.ReclaimRequestedAt.ShouldBeNull(); blob.Content.ShouldBe(Original);
        (await verify.Context.Set<VetPhotoReview>().SingleAsync()).State.ShouldBe("preview");
        (await Archive(failed).DeleteOriginalsAsync(Delete(review), CancellationToken.None)).ShouldBe(new(VetMutationStatus.Applied, 1, Original.LongLength));
    }

    [Fact]
    public async Task Global_result_totals_count_foreign_extractions_and_reserved_image_slots_without_exposing_rows()
    {
        await SeedAsync(); var other = await OtherFamilyAsync();
        await using var own = Open(); await using var foreign = Open(other.FamilyId);
        var input = await BoundAsync(foreign, 2, other, 333);
        var attempt = new VetPhotoAttempt { Id = Guid.NewGuid(), FamilyId = other.FamilyId, BotDbId = other.BotDbId,
            TelegramBotId = other.TelegramBotId, ChatId = other.ChatId, TopicId = other.TopicId,
            SourceId = input.Source!.Id, InputRevisionId = input.Input!.Id, ActorUserId = 333,
            Kind = "image", State = "returned", CreatedAt = Now, UpdatedAt = Now };
        foreign.Context.Add(attempt); await foreign.Context.SaveChangesAsync();
        foreign.Context.Add(new VetPhotoExtraction { Id = Guid.NewGuid(), FamilyId = other.FamilyId,
            BotDbId = other.BotDbId, TelegramBotId = other.TelegramBotId, ChatId = other.ChatId, TopicId = other.TopicId,
            SourceId = input.Source.Id, InputRevisionId = input.Input.Id, AttemptId = attempt.Id,
            ModelName = "synthetic-model", StructuredJson = "{}", CreatedAt = Now });
        foreign.Context.Add(new VetPhotoAttempt { Id = Guid.NewGuid(), FamilyId = other.FamilyId, BotDbId = other.BotDbId,
            TelegramBotId = other.TelegramBotId, ChatId = other.ChatId, TopicId = other.TopicId,
            SourceId = input.Source.Id, InputRevisionId = input.Input.Id, ActorUserId = 333,
            Kind = "image", State = "claimed", ReservedResultSlot = true, CreatedAt = Now, UpdatedAt = Now });
        await foreign.Context.SaveChangesAsync();
        (await Archive(own).GetCapacityAsync(Scope, 111, CancellationToken.None)).ShouldBe(new(0, 0, 0, 0, 1, 1));
        (await own.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0);
        (await own.Context.Set<VetPhotoAttempt>().CountAsync()).ShouldBe(0);
    }

    private sealed class AcceptedReviewFailure : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Hits { get; private set; }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Hits == 0 && command.CommandText.Contains("UPDATE vet_photo_reviews", StringComparison.Ordinal)
                && command.CommandText.Contains("outcome_json", StringComparison.Ordinal)
                && command.Parameters.Cast<System.Data.Common.DbParameter>().Any(p => Equals(p.Value, "accepted")))
            { Hits++; throw new InvalidOperationException("synthetic final review persistence failure"); }
            return ValueTask.FromResult(result);
        }
    }

    private static byte[] ImageBytes(SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(32, 24, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
