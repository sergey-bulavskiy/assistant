using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Vet.Photos;

public sealed class VetPhotoWorkflowStoreTests : VetTestBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private VetPhotoStore Store(VetTestSession s, VetPhotoCapacity? capacity = null) => new(s.Context, s.Current, Clock, capacity ?? new(), new VetPhotoImageDecoder());
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static VetPhotoEffectiveReading Reading => new(6.4m, "mmol/L", Now, "2031-05-12 12:00:00", "UTC", "human_correction", "human_correction", "human_correction", false);
    private async Task<VetPhotoBatch> Start(VetTestSession s)
    {
        var started = await Store(s).StartCollectionAsync(Scope, 111, CancellationToken.None);
        started.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); return started.Batch!;
    }
    private async Task<VetPhotoBatch> Batch(VetTestSession s, Guid id) =>
        (await Store(s).GetBatchAsync(Scope, id, 111, CancellationToken.None))!.Batch;
    private async Task<VetPhotoAdmission> Admit(VetTestSession s, int id, bool bind = true, long actor = 111, int topic = 7)
    {
        var scope = Scope with { TopicId = topic };
        var message = Text("synthetic caption", id, actor, topic) with { Kind = MessageKind.Photo };
        var input = await Store(s).AdmitAsync(scope, message, id, new($"synthetic-file-{id}", $"synthetic-unique-{id}", "synthetic.png", "image/png", 3, 1, 1), null, CancellationToken.None);
        if (bind)
        {
            var stored = await s.Messages.StoreAsync(Bot.TelegramBotId, id, message, CancellationToken.None);
            stored.MessageDbId.ShouldNotBeNull();
            (await Store(s).BindMessageAsync(scope, input.Source!.Id, stored.MessageDbId!.Value, CancellationToken.None)).ShouldBeTrue();
        }
        return input;
    }
    private async Task<VetPhotoOriginalReference> Original(VetTestSession s, VetPhotoAdmission input)
    {
        var blob = new VetPhotoBlob { Id = Guid.NewGuid(), FamilyId = FamilyId, ContentHash = Hash(input.Input!.Id.ToString()),
            ActualBytes = 3, Format = "png", Width = 1, Height = 1, Content = [1, 2, 3], CreatedAt = Now };
        var reference = new VetPhotoOriginalReference { Id = Guid.NewGuid(), FamilyId = FamilyId,
            BotDbId = Bot.BotDbId, TelegramBotId = Bot.TelegramBotId, ChatId = Scope.ChatId, TopicId = Scope.TopicId,
            InputRevisionId = input.Input.Id, BlobId = blob.Id, ContentHash = blob.ContentHash,
            ActualBytes = 3, Format = "png", Width = 1, Height = 1, RetainedAt = Now };
        s.Context.AddRange(blob, reference); await s.Context.SaveChangesAsync(); return reference;
    }
    private async Task<VetPhotoStageReview> StageRequest(VetTestSession s, Guid? batchId, string? selection = null,
        VetPhotoPreviewResult? preview = null, VetPhotoReviewKind kind = VetPhotoReviewKind.Save, Guid? operation = null)
    {
        var profile = await s.Context.Set<VetProfile>().AsNoTracking().SingleAsync();
        var batch = batchId is { } id ? await Batch(s, id) : null;
        return new(Scope, operation ?? Guid.NewGuid(), 111, kind, batchId, batch?.ReviewRevision, profile.Revision,
            selection ?? JsonSerializer.Serialize(new[] { new { sourceId = Guid.NewGuid() } }, Json),
            preview ?? VetPhotoReviewFormatter.FormatBlocks("synthetic heading", ["synthetic full selected content"], "synthetic acceptance"));
    }
    private async Task<VetPhotoReview> Stage(VetTestSession s, Guid? batchId = null, VetPhotoPreviewResult? preview = null)
    {
        var result = await Store(s).StageReviewAsync(await StageRequest(s, batchId, preview: preview), CancellationToken.None);
        result.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); return result.Review!;
    }
    private VetPhotoReviewHandle Handle(VetPhotoReview review, long actor = 111, int? prompt = null) => new(Scope, review.Id, review.Revision, review.OperationKey, actor, prompt);
    private async Task Deliver(VetTestSession s, VetPhotoReview review)
    {
        var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!;
        for (var i = 0; i < pages.Length; i++)
            (await Store(s).RecordPageDeliveryAsync(Handle(review), i, 9000 + i, Hash(pages[i]), CancellationToken.None))
                .Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(s).CompleteDeliveryAsync(Handle(review), 8999 + pages.Length, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
    }
    private async Task<VetPhotoCandidateChange> CandidateChange(VetTestSession s, Guid batchId, Guid sourceId,
        VetPhotoCandidateChangeKind kind, VetPhotoEffectiveReading? reading = null)
    {
        var batch = await Batch(s, batchId);
        var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.SourceId == sourceId);
        var source = await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(x => x.Id == sourceId);
        return new(Scope, batchId, candidate.Id, batch.ReviewRevision, candidate.Revision,
            source.CurrentInputRevisionId, source.CurrentOrdinal, candidate.ExtractionResultId, 222, kind, reading);
    }

    [Fact]
    public async Task Concurrent_start_freezes_profile_and_reuses_one_collecting_batch_without_changing_starter()
    {
        await SeedAsync(); await using var first = Open(); await using var second = Open();
        var results = await Task.WhenAll(Store(first).StartCollectionAsync(Scope, 111, CancellationToken.None),
            Store(second).StartCollectionAsync(Scope, 222, CancellationToken.None));
        results.Count(r => r.Status == VetPhotoWorkflowStatus.Applied).ShouldBe(1);
        results.Count(r => r.Status == VetPhotoWorkflowStatus.Existing).ShouldBe(1);
        results[0].Batch!.Id.ShouldBe(results[1].Batch!.Id);
        await using var verify = Open();
        var batch = await verify.Context.Set<VetPhotoBatch>().SingleAsync();
        var profile = await verify.Context.Set<VetProfile>().SingleAsync();
        batch.ProfileId.ShouldBe(profile.Id); batch.ProfileRevision.ShouldBe(profile.Revision);
        var assumptions = JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(batch.AssumptionsJson, Json)!;
        assumptions.ShouldBe(new("UTC", "mmol/L", null, null, null, false, false, false));
        var starter = batch.StarterUserId;
        (await Store(verify).StartCollectionAsync(Scope, starter == 111 ? 222 : 111, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        (await verify.Context.Set<VetPhotoBatch>().SingleAsync()).StarterUserId.ShouldBe(starter);
        (await verify.Context.Set<VetPhotoAttempt>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_empty_close_is_idempotent_and_closes_UTC_intake_without_calls()
    {
        await SeedAsync(); await using var seed = Open(); var batch = await Start(seed);
        Clock.UtcNow = Now.AddMinutes(1);
        await using var first = Open(); await using var second = Open();
        var results = await Task.WhenAll(Store(first).CloseCollectionAsync(Scope, batch.Id, 1, 111, CancellationToken.None),
            Store(second).CloseCollectionAsync(Scope, batch.Id, 1, 222, CancellationToken.None));
        results.Select(r => r.Status).OrderBy(x => x).ShouldBe(new[] { VetPhotoWorkflowStatus.Applied, VetPhotoWorkflowStatus.Existing }.OrderBy(x => x));
        var closed = await Batch(seed, batch.Id);
        closed.State.ShouldBe("closed"); closed.ReviewRevision.ShouldBe(2);
        closed.IntakeOpenedAt.ShouldBe(Now); closed.IntakeClosedAt.ShouldBe(Now.AddMinutes(1));
        (await seed.Context.Set<VetPhotoAttempt>().CountAsync()).ShouldBe(0);
        (await seed.Context.Set<VetPhotoReview>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Explicit_late_addition_closes_no_window_and_fifty_boundary_preserves_rejected_metadata()
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s);
        for (var i = 1; i <= 49; i++) (await Admit(s, i)).Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        var current = await Batch(s, batch.Id);
        (await Store(s).CloseCollectionAsync(Scope, batch.Id, current.ReviewRevision, 111, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var late = await Admit(s, 50); late.Status.ShouldBe(VetPhotoAdmissionStatus.Late);
        late.Source!.BatchId.ShouldBeNull();
        current = await Batch(s, batch.Id);
        var add = await Store(s).AddLateSourceAsync(Scope, batch.Id, current.ReviewRevision, late.Source.Id, late.Input!.Id, 222, CancellationToken.None);
        add.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); add.Batch!.State.ShouldBe("closed");
        add.Batch.ReviewRevision.ShouldBe(current.ReviewRevision + 1);
        (await Store(s).AddLateSourceAsync(Scope, batch.Id, current.ReviewRevision, late.Source.Id, late.Input.Id, 222, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        var excess = await Admit(s, 51);
        (await Store(s).AddLateSourceAsync(Scope, batch.Id, add.Batch.ReviewRevision, excess.Source!.Id, excess.Input!.Id, 111, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Full);
        var snapshot = (await Store(s).GetBatchAsync(Scope, batch.Id, 111, CancellationToken.None))!;
        snapshot.Counts.Admitted.ShouldBe(50); snapshot.Counts.Delivered.ShouldBe(50); snapshot.Counts.Retained.ShouldBe(0);
        snapshot.Counts.Rejected.ShouldBe(1); snapshot.Items[^1].Source.Association.ShouldBe("explicit_late");
        snapshot.Items[^1].Source.SourceAuthorUserId.ShouldBe(111);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync()).ShouldBe(50);
    }

    [Fact]
    public async Task Late_addition_is_stale_after_cancel_and_never_reopens_terminal_batch()
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s);
        (await Store(s).CloseCollectionAsync(Scope, batch.Id, 1, 111, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var late = await Admit(s, 1);
        var cancelled = await Store(s).CancelRemainderAsync(Scope, batch.Id, 2, 111, CancellationToken.None);
        cancelled.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(s).AddLateSourceAsync(Scope, batch.Id, cancelled.Batch!.ReviewRevision, late.Source!.Id, late.Input!.Id, 111, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Batch(s, batch.Id)).State.ShouldBe("cancelled");
        (await s.Context.Set<VetPhotoAttempt>().CountAsync()).ShouldBe(0);
        (await s.Context.Set<VetPhotoSource>().SingleAsync()).BatchId.ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_late_additions_share_revision_fence_at_fifty_limit()
    {
        await SeedAsync(); await using var seed = Open(); var batch = await Start(seed);
        for (var i = 1; i <= 49; i++) await Admit(seed, i);
        var current = await Batch(seed, batch.Id);
        await Store(seed).CloseCollectionAsync(Scope, batch.Id, current.ReviewRevision, 111, CancellationToken.None);
        var firstLate = await Admit(seed, 50); var secondLate = await Admit(seed, 51);
        current = await Batch(seed, batch.Id);
        await using var first = Open(); await using var second = Open();
        var results = await Task.WhenAll(
            Store(first).AddLateSourceAsync(Scope, batch.Id, current.ReviewRevision, firstLate.Source!.Id, firstLate.Input!.Id, 111, CancellationToken.None),
            Store(second).AddLateSourceAsync(Scope, batch.Id, current.ReviewRevision, secondLate.Source!.Id, secondLate.Input!.Id, 222, CancellationToken.None));
        results.Count(r => r.Status == VetPhotoWorkflowStatus.Applied).ShouldBe(1);
        results.Count(r => r.Status == VetPhotoWorkflowStatus.Stale).ShouldBe(1);
        var snapshot = (await Store(seed).GetBatchAsync(Scope, batch.Id, 111, CancellationToken.None))!;
        snapshot.Counts.Admitted.ShouldBe(50); snapshot.Counts.Rejected.ShouldBe(1); snapshot.Batch.State.ShouldBe("closed");
        var remaining = await seed.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(x => x.ProposedBatchId == batch.Id && x.BatchId == null);
        (await Store(seed).AddLateSourceAsync(Scope, batch.Id, snapshot.Batch.ReviewRevision, remaining.Id, remaining.CurrentInputRevisionId, 111, CancellationToken.None))
            .Status.ShouldBe(VetPhotoWorkflowStatus.Full);
        (await seed.Context.Set<VetPhotoAttempt>().CountAsync()).ShouldBe(50);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("place")]
    [InlineData("wrong_topic")]
    [InlineData("unknown_actor")]
    public async Task Revoked_or_wrong_scope_reads_and_mutations_reveal_no_private_batch(string denied)
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s); await Admit(s, 1);
        var scope = Scope; long actor = 111;
        if (denied == "member") await s.Context.FamilyMembers.Where(m => m.TelegramUserId == 111).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied));
        if (denied == "place") await s.Context.Places.Where(p => p.TopicId == 7).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlaceStatus.Disabled));
        if (denied == "wrong_topic") scope = Scope with { TopicId = 8 };
        if (denied == "unknown_actor") actor = 333;
        (await Store(s).GetBatchAsync(scope, batch.Id, actor, CancellationToken.None)).ShouldBeNull();
        (await Store(s).ListBatchesAsync(scope, actor, 0, 20, CancellationToken.None)).Batches.ShouldBeEmpty();
        var result = await Store(s).CloseCollectionAsync(scope, batch.Id, 2, actor, CancellationToken.None);
        result.Status.ShouldBe(denied == "wrong_topic" ? VetPhotoWorkflowStatus.NotFound : VetPhotoWorkflowStatus.Refused);
        (await s.Context.Set<VetPhotoBatch>().SingleAsync()).State.ShouldBe("collecting");
        (await s.Context.Set<VetPhotoSource>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Historical_batch_paging_keeps_exact_scope_old_months_and_all_count_categories()
    {
        await SeedAsync(); await using var s = Open();
        var oldest = await Start(s);
        await Admit(s, 1, bind: false); await Admit(s, 2);
        var current = await Batch(s, oldest.Id); await Store(s).CloseCollectionAsync(Scope, oldest.Id, current.ReviewRevision, 111, CancellationToken.None);
        Clock.UtcNow = Now.AddMonths(1); var middle = await Start(s);
        await Store(s).CloseCollectionAsync(Scope, middle.Id, 1, 111, CancellationToken.None);
        Clock.UtcNow = Now.AddMonths(2); var newest = await Start(s);
        var first = await Store(s).ListBatchesAsync(Scope, 111, 0, 2, CancellationToken.None);
        first.Batches.Select(b => b.Batch.Id).ShouldBe(new[] { newest.Id, middle.Id }); first.NextOffset.ShouldBe(2);
        var second = await Store(s).ListBatchesAsync(Scope, 111, first.NextOffset!.Value, 2, CancellationToken.None);
        second.Batches.Single().Batch.Id.ShouldBe(oldest.Id); second.NextOffset.ShouldBeNull();
        second.Batches.Single().Counts.ShouldBe(new(2, 1, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0));
        (await Store(s).ListBatchesAsync(Scope with { TopicId = 8 }, 111, 0, 2, CancellationToken.None)).Batches.ShouldBeEmpty();
        await using var unset = Unscoped();
        (await Should.ThrowAsync<InvalidOperationException>(() => Store(unset).ListBatchesAsync(Scope, 111, 0, 2, CancellationToken.None)))
            .Message.ShouldBe("Vet scope is not active.");
    }

    [Fact]
    public async Task Status_counts_distinguish_metadata_retention_disposition_and_distinct_current_results()
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s);
        var inputs = new List<VetPhotoAdmission>();
        for (var i = 1; i <= 8; i++) inputs.Add(await Admit(s, i, bind: i != 8));
        await Original(s, inputs[0]);
        var states = new[] { "waiting", "clear", "pending", "failed", "saved", "excluded", "cancelled", "waiting" };
        for (var i = 0; i < states.Length; i++)
        {
            var sourceId = inputs[i].Source!.Id; var state = states[i];
            await s.Context.Set<VetPhotoCandidate>().Where(c => c.SourceId == sourceId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, state));
        }
        var failedId = inputs[3].Source!.Id;
        await s.Context.Set<VetPhotoAttempt>().Where(a => a.SourceId == failedId).ExecuteUpdateAsync(u => u.SetProperty(a => a.State, "failed"));
        var clear = inputs[1];
        for (var i = 0; i < 2; i++)
        {
            var attempt = new VetPhotoAttempt { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
                TelegramBotId = Bot.TelegramBotId, ChatId = Scope.ChatId, TopicId = Scope.TopicId,
                SourceId = clear.Source!.Id, InputRevisionId = clear.Input!.Id, ActorUserId = 111,
                Kind = "image", State = "returned", CreatedAt = Now, UpdatedAt = Now };
            s.Context.Add(attempt); await s.Context.SaveChangesAsync();
            s.Context.Add(new VetPhotoExtraction { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
                TelegramBotId = Bot.TelegramBotId, ChatId = Scope.ChatId, TopicId = Scope.TopicId,
                SourceId = clear.Source.Id, InputRevisionId = clear.Input.Id, AttemptId = attempt.Id,
                ModelName = "synthetic-model", StructuredJson = "{}", CreatedAt = Now });
            await s.Context.SaveChangesAsync();
        }
        var current = await Batch(s, batch.Id);
        await Store(s).CloseCollectionAsync(Scope, batch.Id, current.ReviewRevision, 111, CancellationToken.None);
        var late = await Admit(s, 9); late.Status.ShouldBe(VetPhotoAdmissionStatus.Late);
        var status = (await Store(s).GetBatchAsync(Scope, batch.Id, 111, CancellationToken.None))!;
        status.Counts.ShouldBe(new(8, 7, 1, 2, 1, 1, 1, 1, 1, 1, 1, 1));
        (await s.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(2);
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData(VetPhotoAssumptionKind.Year, "2031")]
    [InlineData(VetPhotoAssumptionKind.TimeZone, "UTC")]
    [InlineData(VetPhotoAssumptionKind.Unit, "ммоль/л")]
    public async Task Focused_assumption_changes_preserve_defaults_and_invalidate_every_preview(VetPhotoAssumptionKind kind, string value)
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s); await Admit(s, 1);
        var first = await Stage(s, batch.Id); var second = await Stage(s, batch.Id);
        var request = new VetPhotoAssumptionChange(Scope, batch.Id, (await Batch(s, batch.Id)).ReviewRevision,
            batch.ProfileRevision, 222, kind, value);
        var result = await Store(s).ChangeAssumptionAsync(request, CancellationToken.None);
        result.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(s).ChangeAssumptionAsync(request, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        var assumptions = JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(result.Batch!.AssumptionsJson, Json)!;
        assumptions.ProfileTimeZone.ShouldBe("UTC"); assumptions.ProfileGlucoseUnit.ShouldBe("mmol/L");
        if (kind == VetPhotoAssumptionKind.Year) { assumptions.Year.ShouldBe(2031); assumptions.YearConfirmed.ShouldBeTrue(); }
        if (kind == VetPhotoAssumptionKind.TimeZone) { assumptions.TimeZone.ShouldBe("UTC"); assumptions.TimeZoneConfirmed.ShouldBeTrue(); }
        if (kind == VetPhotoAssumptionKind.Unit) { assumptions.GlucoseUnit.ShouldBe("mmol/L"); assumptions.UnitConfirmed.ShouldBeTrue(); }
        (await s.Context.Set<VetPhotoReview>().Select(r => r.State).ToListAsync()).ShouldBe(new[] { "stale", "stale" });
        (await Store(s).GetReviewAsync(Handle(first), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Store(s).GetReviewAsync(Handle(second), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(VetPhotoAssumptionKind.Year, "0")]
    [InlineData(VetPhotoAssumptionKind.Year, "10000")]
    [InlineData(VetPhotoAssumptionKind.TimeZone, "synthetic-invalid-zone")]
    [InlineData(VetPhotoAssumptionKind.Unit, "mg/dL")]
    public async Task Invalid_assumptions_and_stale_profile_do_not_change_batch(VetPhotoAssumptionKind kind, string value)
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s);
        var request = new VetPhotoAssumptionChange(Scope, batch.Id, 1, batch.ProfileRevision, 111, kind, value);
        (await Store(s).ChangeAssumptionAsync(request, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Store(s).ChangeAssumptionAsync(request with { ExpectedProfileRevision = batch.ProfileRevision + 1 }, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        var unchanged = await Batch(s, batch.Id); unchanged.ReviewRevision.ShouldBe(1); unchanged.AssumptionsJson.ShouldBe(batch.AssumptionsJson);
    }

    [Fact]
    public async Task Manual_correction_exclusion_and_explicit_restoration_are_only_revised_proposals()
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s); var input = await Admit(s, 1); await Original(s, input);
        var proof = await Stage(s, batch.Id);
        var corrected = await CandidateChange(s, batch.Id, input.Source!.Id, VetPhotoCandidateChangeKind.Correct, Reading);
        (await Store(s).ChangeCandidateAsync(corrected, CancellationToken.None)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(s).ChangeCandidateAsync(corrected, CancellationToken.None)).ShouldBe(VetPhotoWorkflowStatus.Stale);
        var excluded = await CandidateChange(s, batch.Id, input.Source.Id, VetPhotoCandidateChangeKind.Exclude);
        (await Store(s).ChangeCandidateAsync(excluded, CancellationToken.None)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        var restore = await CandidateChange(s, batch.Id, input.Source.Id, VetPhotoCandidateChangeKind.Restore, Reading with { Value = 7.2m });
        (await Store(s).ChangeCandidateAsync(restore, CancellationToken.None)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        var candidate = await s.Context.Set<VetPhotoCandidate>().SingleAsync();
        candidate.State.ShouldBe("pending"); candidate.Revision.ShouldBe(4); candidate.ManuallyCorrected.ShouldBeTrue();
        candidate.RequiresExplicitRestoration.ShouldBeTrue(); candidate.EventId.ShouldBeNull();
        JsonSerializer.Deserialize<VetPhotoEffectiveReading>(candidate.EffectiveJson, Json)!.Value.ShouldBe(7.2m);
        using var provenance = JsonDocument.Parse(candidate.CorrectionProvenanceJson);
        provenance.RootElement.GetProperty("actorUserId").GetInt64().ShouldBe(222);
        input.Source!.SourceAuthorUserId.ShouldBe(111);
        (await s.Context.Set<VetPhotoReview>().SingleAsync()).State.ShouldBe("stale");
        (await s.Context.Set<VetPhotoOriginalReference>().SingleAsync()).State.ShouldBe("retained");
        (await s.Context.Set<VetPhotoBlob>().SingleAsync()).Content.ShouldBe(new byte[] { 1, 2, 3 });
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0); (await s.Context.Set<VetDiaryAction>().CountAsync()).ShouldBe(0);
        (await Store(s).GetReviewAsync(Handle(proof), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_batch_allows_explicit_historical_proposal_and_review_without_reopening_or_fact_mutation(bool cancelled)
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s); var photo = await Admit(s, 1); await Original(s, photo);
        var evidence = await EvidenceAsync(s, id: 1000, update: 1000);
        (await s.Diary.ApplyAsync(Save(Scope, evidence.Source, evidence.Profile, evidence.State), CancellationToken.None))
            .Status.ShouldBe(VetMutationStatus.Applied);
        var fact = await s.Context.Set<VetEvent>().AsNoTracking().SingleAsync();
        await s.Context.Set<VetPhotoCandidate>().Where(c => c.SourceId == photo.Source!.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, cancelled ? "cancelled" : "saved")
                .SetProperty(c => c.EventId, fact.Id).SetProperty(c => c.EventRevision, fact.Revision)
                .SetProperty(c => c.RequiresExplicitRestoration, cancelled));
        await s.Context.Set<VetPhotoBatch>().Where(b => b.Id == batch.Id).ExecuteUpdateAsync(u =>
            u.SetProperty(b => b.State, cancelled ? "cancelled" : "completed").SetProperty(b => b.ClosedAt, Now)
                .SetProperty(b => b.IntakeClosedAt, Now));
        if (cancelled) await s.Context.Set<VetEvent>().Where(e => e.Id == fact.Id).ExecuteUpdateAsync(u => u.SetProperty(e => e.DeletedAt, Now));
        var facts = JsonSerializer.Serialize(await s.Context.Set<VetEvent>().AsNoTracking().ToListAsync(), Json);
        var actions = JsonSerializer.Serialize(await s.Context.Set<VetDiaryAction>().AsNoTracking().ToListAsync(), Json);
        var request = await CandidateChange(s, batch.Id, photo.Source!.Id,
            cancelled ? VetPhotoCandidateChangeKind.Restore : VetPhotoCandidateChangeKind.Correct, Reading with { Value = 7.2m });
        (await Store(s).ChangeCandidateAsync(request, CancellationToken.None)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        var changedBatch = await Batch(s, batch.Id);
        (await Store(s).ChangeAssumptionAsync(new(Scope, batch.Id, changedBatch.ReviewRevision, changedBatch.ProfileRevision,
            222, VetPhotoAssumptionKind.Year, "2031"), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var stage = await StageRequest(s, batch.Id, kind: VetPhotoReviewKind.Correction);
        var review = (await Store(s).StageReviewAsync(stage, CancellationToken.None)).Review!;
        review.ShouldNotBeNull(); await Deliver(s, review);
        (await Store(s).GetReviewAsync(Handle(review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        (await Store(s).StageReviewAsync(stage with { Kind = VetPhotoReviewKind.Save, OperationKey = Guid.NewGuid() }, CancellationToken.None))
            .Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync();
        candidate.State.ShouldBe("pending"); candidate.EventId.ShouldBe(fact.Id); candidate.EventRevision.ShouldBe(fact.Revision);
        candidate.RequiresExplicitRestoration.ShouldBe(cancelled); candidate.ManuallyCorrected.ShouldBeTrue();
        var terminal = await Batch(s, batch.Id);
        terminal.State.ShouldBe(cancelled ? "cancelled" : "completed"); terminal.IntakeClosedAt.ShouldBe(Now);
        JsonSerializer.Serialize(await s.Context.Set<VetEvent>().AsNoTracking().ToListAsync(), Json).ShouldBe(facts);
        JsonSerializer.Serialize(await s.Context.Set<VetDiaryAction>().AsNoTracking().ToListAsync(), Json).ShouldBe(actions);
        (await s.Context.Set<VetPhotoOriginalReference>().SingleAsync()).State.ShouldBe("retained");
        (await s.Context.Set<VetPhotoBlob>().SingleAsync()).Content.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task Candidate_current_input_or_mismatched_result_fence_makes_old_proposal_stale()
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s); var input = await Admit(s, 1);
        var old = await CandidateChange(s, batch.Id, input.Source!.Id, VetPhotoCandidateChangeKind.Correct, Reading);
        await Store(s).AdmitAsync(Scope, Text("synthetic edited caption", 1) with { Kind = MessageKind.Photo, IsEdit = true, EditedAt = Now.AddSeconds(1) },
            2, new("synthetic-file-1", "synthetic-unique-1", "synthetic.png", "image/png", 3, 1, 1), null, CancellationToken.None);
        (await Store(s).ChangeCandidateAsync(old, CancellationToken.None)).ShouldBe(VetPhotoWorkflowStatus.Stale);
        var current = await CandidateChange(s, batch.Id, input.Source.Id, VetPhotoCandidateChangeKind.Correct, Reading);
        (await Store(s).ChangeCandidateAsync(current with { ExpectedExtractionId = Guid.NewGuid() }, CancellationToken.None)).ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await s.Context.Set<VetPhotoCandidate>().SingleAsync()).ManuallyCorrected.ShouldBeFalse();
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(VetPhotoCandidateChangeKind.DuplicateExisting, "keep_existing")]
    [InlineData(VetPhotoCandidateChangeKind.DuplicateSeparate, "separate")]
    public async Task Explicit_duplicate_decision_only_updates_candidate_and_invalidates_review(VetPhotoCandidateChangeKind kind, string decision)
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s); var first = await Admit(s, 1); var second = await Admit(s, 2);
        var review = await Stage(s, batch.Id);
        var change = await CandidateChange(s, batch.Id, second.Source!.Id, kind);
        if (kind == VetPhotoCandidateChangeKind.DuplicateExisting) change = change with { DuplicateSourceId = first.Source!.Id };
        (await Store(s).ChangeCandidateAsync(change, CancellationToken.None)).ShouldBe(VetPhotoWorkflowStatus.Applied);
        var candidate = await s.Context.Set<VetPhotoCandidate>().SingleAsync(c => c.SourceId == second.Source.Id);
        candidate.DuplicateDecision.ShouldBe(decision); candidate.State.ShouldBe("pending"); candidate.EventId.ShouldBeNull();
        (await Store(s).GetReviewAsync(Handle(review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_event_in_another_approved_topic_or_deleted_event_cannot_become_link_target(bool deleted)
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s); var photo = await Admit(s, 1);
        var evidence = await EvidenceAsync(s, id: 1000, update: 1000,
            message: Text("synthetic same-value actual report", 1000, topic: deleted ? 7 : 8));
        var eventScope = Scope with { TopicId = deleted ? 7 : 8 };
        (await s.Diary.ApplyAsync(Save(eventScope, evidence.Source, evidence.Profile, evidence.State), CancellationToken.None))
            .Status.ShouldBe(VetMutationStatus.Applied);
        var target = await s.Context.Set<VetEvent>().AsNoTracking().SingleAsync();
        target.Value.ShouldBe(6.4m); target.OccurredAt.ShouldBe(Now);
        if (deleted) await s.Context.Set<VetEvent>().Where(e => e.Id == target.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.DeletedAt, Now));
        var review = await Stage(s, batch.Id);
        var change = (await CandidateChange(s, batch.Id, photo.Source!.Id, VetPhotoCandidateChangeKind.DuplicateExisting))
            with { DuplicateEventId = target.Id, DuplicateEventRevision = target.Revision };
        var before = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync();
        var frozen = JsonSerializer.Serialize(before, Json);
        (await Store(s).ChangeCandidateAsync(change, CancellationToken.None)).ShouldBe(VetPhotoWorkflowStatus.Stale);
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(), Json).ShouldBe(frozen);
        (await Batch(s, batch.Id)).ReviewRevision.ShouldBe(change.ExpectedBatchRevision);
        (await s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync()).State.ShouldBe("preview");
        var afterTarget = await s.Context.Set<VetEvent>().AsNoTracking().SingleAsync();
        afterTarget.Revision.ShouldBe(target.Revision);
        afterTarget.DeletedAt.ShouldBe(deleted ? Now : null);
        (await s.Context.Set<VetDiaryAction>().CountAsync()).ShouldBe(1);
        (await Store(s).GetReviewAsync(Handle(review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete);
    }

    [Fact]
    public async Task Cancellation_preserves_saved_diary_and_independent_caption_insulin_and_originals()
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s); var first = await Admit(s, 1); var second = await Admit(s, 2);
        await Original(s, first); await Original(s, second);
        var glucose = await EvidenceAsync(s, id: 1000, update: 1000);
        await s.Diary.ApplyAsync(Save(Scope, glucose.Source, glucose.Profile, glucose.State), CancellationToken.None);
        var insulin = await EvidenceAsync(s, id: 1001, update: 1001, type: "insulin", value: "1");
        await s.Diary.ApplyAsync(Save(Scope, insulin.Source, insulin.Profile, insulin.State), CancellationToken.None);
        var savedId = await s.Context.Set<VetEvent>().Where(e => e.EventType == "glucose").Select(e => e.Id).SingleAsync();
        await s.Context.Set<VetPhotoCandidate>().Where(c => c.SourceId == first.Source!.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.State, "saved").SetProperty(c => c.EventId, savedId));
        var facts = JsonSerializer.Serialize(await s.Context.Set<VetEvent>().AsNoTracking().OrderBy(e => e.Id).ToListAsync(), Json);
        var actions = JsonSerializer.Serialize(await s.Context.Set<VetDiaryAction>().AsNoTracking().OrderBy(e => e.Id).ToListAsync(), Json);
        var imageAttempt = new VetPhotoAttempt { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = Scope.ChatId, TopicId = Scope.TopicId, SourceId = second.Source!.Id,
            InputRevisionId = second.Input!.Id, ActorUserId = 111, Kind = "image", State = "queued",
            ExpectedCurrentInputId = second.Input.Id, ExpectedSourceOrdinal = second.Source.CurrentOrdinal,
            CreatedAt = Now, UpdatedAt = Now };
        s.Context.Add(imageAttempt); await s.Context.SaveChangesAsync();
        var current = await Batch(s, batch.Id);
        (await Store(s).CancelRemainderAsync(Scope, batch.Id, current.ReviewRevision, 222, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(s).CancelRemainderAsync(Scope, batch.Id, current.ReviewRevision, 222, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        JsonSerializer.Serialize(await s.Context.Set<VetEvent>().AsNoTracking().OrderBy(e => e.Id).ToListAsync(), Json).ShouldBe(facts);
        JsonSerializer.Serialize(await s.Context.Set<VetDiaryAction>().AsNoTracking().OrderBy(e => e.Id).ToListAsync(), Json).ShouldBe(actions);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(2);
        (await s.Context.Set<VetPhotoCandidate>().SingleAsync(c => c.SourceId == first.Source!.Id)).State.ShouldBe("saved");
        (await s.Context.Set<VetPhotoCandidate>().SingleAsync(c => c.SourceId == second.Source!.Id)).State.ShouldBe("cancelled");
        (await s.Context.Set<VetPhotoAttempt>().SingleAsync(a => a.SourceId == second.Source!.Id && a.Kind == "download")).State.ShouldBe("queued");
        (await s.Context.Set<VetPhotoAttempt>().SingleAsync(a => a.Id == imageAttempt.Id)).State.ShouldBe("cancelled");
        (await s.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained")).ShouldBe(2);
        (await s.Context.Set<VetPhotoBlob>().CountAsync(b => b.Content != null)).ShouldBe(2);
    }

    [Fact]
    public async Task Full_fifty_row_preview_requires_every_delivered_page_and_exact_final_prompt()
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s);
        var rows = Enumerable.Range(1, 50).Select(n => new VetPhotoReviewRow(n, VetPhotoReviewSection.Clear, Reading,
            $"synthetic complete row {n:D3} " + new string('x', 100))).ToArray();
        var preview = VetPhotoReviewFormatter.Format("synthetic heading", ["synthetic approved defaults"], rows, "synthetic acceptance");
        var review = await Stage(s, batch.Id, preview);
        var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!;
        pages.Length.ShouldBeGreaterThan(1); string.Join("\n", pages).ShouldContain("synthetic complete row 050");
        (await Store(s).GetReviewAsync(Handle(review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        for (var i = 0; i < pages.Length - 1; i++) await Store(s).RecordPageDeliveryAsync(Handle(review), i, 9000 + i, Hash(pages[i]), CancellationToken.None);
        (await Store(s).CompleteDeliveryAsync(Handle(review), 8999 + pages.Length, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        (await s.Context.Set<VetPhotoReview>().SingleAsync()).CompletePreviewDelivered.ShouldBeFalse();
        await Store(s).RecordPageDeliveryAsync(Handle(review), pages.Length - 1, 8999 + pages.Length, Hash(pages[^1]), CancellationToken.None);
        (await Store(s).CompleteDeliveryAsync(Handle(review), 999999, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        (await Store(s).CompleteDeliveryAsync(Handle(review), 8999 + pages.Length, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(s).GetReviewAsync(Handle(review, prompt: 8999 + pages.Length), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        (await Store(s).GetReviewAsync(Handle(review, prompt: 999999), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Exact_stage_delivery_and_completion_replays_do_not_duplicate_proof()
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s);
        var request = await StageRequest(s, batch.Id); var staged = await Store(s).StageReviewAsync(request, CancellationToken.None);
        var review = staged.Review!;
        (await Store(s).StageReviewAsync(request, CancellationToken.None)).Review!.Id.ShouldBe(review.Id);
        (await Store(s).StageReviewAsync(request with { SelectionJson = "[{\"synthetic\":true}]" }, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        await Deliver(s, review);
        var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)!;
        (await Store(s).RecordPageDeliveryAsync(Handle(review), 0, 9000, Hash(pages[0]), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        (await Store(s).RecordPageDeliveryAsync(Handle(review), 0, 9001, Hash(pages[0]), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Store(s).CompleteDeliveryAsync(Handle(review), 8999 + pages.Length, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        var stored = await s.Context.Set<VetPhotoReview>().SingleAsync();
        JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(stored.DeliveredPagesJson, Json)!.Length.ShouldBe(pages.Length);
        stored.Fingerprint.ShouldBe(Hash(request.SelectionJson)); stored.ProfileId.ShouldNotBeNull(); stored.ProfileRevision.ShouldBe(batch.ProfileRevision);
    }

    [Fact]
    public async Task Changed_selection_fingerprint_or_delivered_page_hash_cannot_supply_complete_proof()
    {
        await SeedAsync(); await using var s = Open(); var review = await Stage(s); await Deliver(s, review);
        await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Fingerprint, new string('0', 64)));
        (await Store(s).GetReviewAsync(Handle(review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        await s.Context.Set<VetPhotoReview>().Where(r => r.Id == review.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Fingerprint, review.Fingerprint)
            .SetProperty(r => r.DeliveredPagesJson, "[{\"pageIndex\":0,\"messageId\":9000,\"textHash\":\"synthetic-wrong\"}]"));
        (await Store(s).CompleteDeliveryAsync(Handle(review), 9000, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        (await Store(s).GetReviewAsync(Handle(review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_same_page_delivery_reuses_one_frozen_proof_entry()
    {
        await SeedAsync(); await using var seed = Open(); var review = await Stage(seed);
        var page = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)![0];
        await using var first = Open(); await using var second = Open();
        var results = await Task.WhenAll(Store(first).RecordPageDeliveryAsync(Handle(review), 0, 9000, Hash(page), CancellationToken.None),
            Store(second).RecordPageDeliveryAsync(Handle(review, actor: 222), 0, 9000, Hash(page), CancellationToken.None));
        results.Count(r => r.Status == VetPhotoWorkflowStatus.Applied).ShouldBe(1);
        results.Count(r => r.Status == VetPhotoWorkflowStatus.Existing).ShouldBe(1);
        var stored = await seed.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync();
        JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(stored.DeliveredPagesJson, Json)!
            .ShouldBe(new[] { new VetPhotoPageDelivery(0, 9000, Hash(page)) });
        stored.CompletePreviewDelivered.ShouldBeFalse();
    }

    [Theory]
    [InlineData(VetPhotoReviewKind.DeleteOriginals)]
    [InlineData(VetPhotoReviewKind.DeleteOriginalsSelection)]
    public async Task Both_deletion_review_kinds_require_current_owner_before_staging(VetPhotoReviewKind kind)
    {
        await SeedAsync(); await using var s = Open();
        var request = (await StageRequest(s, null, kind: kind)) with { RequesterUserId = 222 };
        (await Store(s).StageReviewAsync(request, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await s.Context.Set<VetPhotoReview>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(VetPhotoReviewKind.Save, "save")]
    [InlineData(VetPhotoReviewKind.Correction, "correction")]
    [InlineData(VetPhotoReviewKind.Reverse, "reverse")]
    [InlineData(VetPhotoReviewKind.ReextractSelection, "reextract_selection")]
    [InlineData(VetPhotoReviewKind.ReextractComparison, "reextract_comparison")]
    [InlineData(VetPhotoReviewKind.DeleteOriginalsSelection, "delete_originals_selection")]
    [InlineData(VetPhotoReviewKind.DeleteOriginals, "delete_originals")]
    public async Task Closed_review_kinds_persist_exact_frozen_metadata_and_write_no_events(VetPhotoReviewKind kind, string storedKind)
    {
        await SeedAsync(); await using var s = Open();
        var request = await StageRequest(s, null, kind: kind);
        var staged = await Store(s).StageReviewAsync(request, CancellationToken.None);
        staged.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var stored = await s.Context.Set<VetPhotoReview>().SingleAsync();
        stored.Kind.ShouldBe(storedKind); stored.SelectionJson.ShouldBe(request.SelectionJson);
        stored.Fingerprint.ShouldBe(Hash(request.SelectionJson)); stored.RequesterUserId.ShouldBe(111);
        stored.ProfileId.ShouldNotBeNull(); stored.ProfileRevision.ShouldBe(request.ExpectedProfileRevision);
        stored.CompletePreviewDelivered.ShouldBeFalse();
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0); (await s.Context.Set<VetDiaryAction>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("index")]
    [InlineData("message")]
    [InlineData("operation")]
    [InlineData("revision")]
    public async Task Wrong_delivery_identity_or_page_hash_writes_no_proof(string wrong)
    {
        await SeedAsync(); await using var s = Open(); var review = await Stage(s);
        var handle = Handle(review);
        if (wrong == "operation") handle = handle with { OperationKey = Guid.NewGuid() };
        if (wrong == "revision") handle = handle with { Revision = 2 };
        var page = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json)![0];
        var result = await Store(s).RecordPageDeliveryAsync(handle, wrong == "index" ? -1 : 0, wrong == "message" ? 0 : 9000,
            wrong == "hash" ? new string('0', 64) : Hash(page), CancellationToken.None);
        result.Status.ShouldBe(wrong is "operation" or "revision" ? VetPhotoWorkflowStatus.Stale : VetPhotoWorkflowStatus.Refused);
        var stored = await s.Context.Set<VetPhotoReview>().SingleAsync();
        stored.DeliveredPagesJson.ShouldBe("[]"); stored.CompletePreviewDelivered.ShouldBeFalse(); stored.AcceptancePromptMessageId.ShouldBeNull();
    }

    [Fact]
    public async Task Preview_send_failure_is_durable_and_only_explicit_retry_creates_fresh_revision()
    {
        await SeedAsync(); await using var s = Open(); var review = await Stage(s);
        (await Store(s).RecordPreviewFailureAsync(Handle(review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Store(s).RecordPreviewFailureAsync(Handle(review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        await using var restarted = Open();
        (await restarted.Context.Set<VetPhotoReview>().SingleAsync()).State.ShouldBe("preview_failed");
        (await Store(restarted).GetReviewAsync(Handle(review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Store(restarted).CompleteDeliveryAsync(Handle(review), 9000, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        var retried = await Store(restarted).RetryPreviewAsync(Handle(review), CancellationToken.None);
        retried.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); retried.Review!.Revision.ShouldBe(2);
        retried.Review.DeliveredPagesJson.ShouldBe("[]"); retried.Review.CompletePreviewDelivered.ShouldBeFalse();
        (await Store(restarted).RetryPreviewAsync(Handle(review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        await Deliver(restarted, retried.Review);
        (await Store(restarted).GetReviewAsync(Handle(retried.Review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        (await restarted.Context.Set<VetPhotoReview>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Natural_confirmation_requires_unique_exact_current_review_and_rejects_other_topic()
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s); var first = await Stage(s, batch.Id); var second = await Stage(s, batch.Id);
        await Deliver(s, first); await Deliver(s, second);
        var ambiguous = await Store(s).FindNaturalReviewAsync(Scope, 222, null, null, CancellationToken.None);
        ambiguous.Status.ShouldBe(VetPhotoWorkflowStatus.Ambiguous); ambiguous.Review.ShouldBeNull();
        (await Store(s).FindNaturalReviewAsync(Scope, 222, first.OperationKey, first.Revision, CancellationToken.None)).Review!.Id.ShouldBe(first.Id);
        (await Store(s).FindNaturalReviewAsync(Scope, 222, first.OperationKey, 2, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.NotFound);
        (await Store(s).GetReviewAsync(Handle(first) with { Scope = Scope with { TopicId = 8 } }, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Profile_change_stales_both_batch_and_batchless_frozen_review(bool batchless)
    {
        await SeedAsync(); await using var s = Open(); var batch = await Start(s); var review = await Stage(s, batchless ? null : batch.Id); await Deliver(s, review);
        var profile = await s.Context.Set<VetProfile>().AsNoTracking().SingleAsync();
        await s.Profiles.UpdateAsync(FamilyId, Bot.BotDbId, 111, profile.Revision, [new("TimeZone", "Etc/UTC")], CancellationToken.None);
        (await Store(s).GetReviewAsync(Handle(review), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Store(s).CompleteDeliveryAsync(Handle(review), 9000, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Store(s).FindNaturalReviewAsync(Scope, 111, review.OperationKey, review.Revision, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.NotFound);
        (await s.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("selection_json")]
    [InlineData("selection_count")]
    [InlineData("selection_entry")]
    [InlineData("empty_page")]
    [InlineData("oversized_page")]
    [InlineData("surrogate_page")]
    [InlineData("refused_preview")]
    [InlineData("too_many_pages")]
    public async Task Invalid_stage_payload_is_refused_before_any_review_row(string invalid)
    {
        await SeedAsync(); await using var s = Open(); var request = await StageRequest(s, null);
        request = invalid switch {
            "kind" => request with { Kind = (VetPhotoReviewKind)99 },
            "selection_json" => request with { SelectionJson = "{}" },
            "selection_count" => request with { SelectionJson = JsonSerializer.Serialize(Enumerable.Repeat(new { synthetic = true }, 51), Json) },
            "selection_entry" => request with { SelectionJson = JsonSerializer.Serialize(new[] { new { synthetic = new string('x', 4097) } }, Json) },
            "empty_page" => request with { Preview = new(new[] { "" }, null) },
            "oversized_page" => request with { Preview = new(new[] { new string('x', 3501) }, null) },
            "surrogate_page" => request with { Preview = new(new[] { "synthetic\uD800" }, null) },
            "refused_preview" => request with { Preview = new(Array.Empty<string>(), "invalid_preview_content") },
            _ => request with { Preview = new(Enumerable.Repeat("synthetic", 65).ToArray(), null) }
        };
        (await Store(s).StageReviewAsync(request, CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await s.Context.Set<VetPhotoReview>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Full_ten_thousand_selection_keeps_complete_snapshot_but_initial_summary_writes_nothing()
    {
        await SeedAsync(); await using var s = Open();
        var references = Enumerable.Range(1, 10000).Select(n => new { referenceId = Guid.NewGuid(), revision = 1,
            inputRevisionId = Guid.NewGuid(), blobId = Guid.NewGuid(), sourceId = Guid.NewGuid(),
            expectedCurrentInputId = Guid.NewGuid(), expectedSourceOrdinal = 1, expectedBlobRetainedReferences = 1,
            eventId = (long?)null, eventRevision = (int?)null, syntheticBoundedLabel = new string('x', 100) }).ToArray();
        var selection = JsonSerializer.Serialize(references, Json);
        selection.Length.ShouldBeGreaterThan(2097152);
        var pages = new[] { "synthetic all-originals scope: 10000 retained references; 200 windows; no bytes deleted", "synthetic approve fixed selection, then review each 50-reference deletion window" };
        var request = await StageRequest(s, null, selection, new(Array.AsReadOnly(pages), null), VetPhotoReviewKind.DeleteOriginalsSelection);
        var result = await Store(s).StageReviewAsync(request, CancellationToken.None);
        result.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        await Deliver(s, result.Review!);
        await using var restarted = Open();
        var stored = await restarted.Context.Set<VetPhotoReview>().SingleAsync();
        stored.SelectionJson.ShouldBe(selection); stored.PageCount.ShouldBe(2);
        JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(stored.DeliveredPagesJson, Json)!.Length.ShouldBe(2);
        stored.CompletePreviewDelivered.ShouldBeTrue(); stored.AcceptancePromptMessageId.ShouldBe(9001);
        (await Store(restarted).GetReviewAsync(Handle(stored), CancellationToken.None)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
        (await restarted.Context.Set<VetPhotoOriginalReference>().CountAsync()).ShouldBe(0);
        (await restarted.Context.Set<VetEvent>().CountAsync()).ShouldBe(0);
        (await Store(restarted).StageReviewAsync(request with { Kind = VetPhotoReviewKind.DeleteOriginals, OperationKey = Guid.NewGuid() }, CancellationToken.None))
            .Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
    }
}
