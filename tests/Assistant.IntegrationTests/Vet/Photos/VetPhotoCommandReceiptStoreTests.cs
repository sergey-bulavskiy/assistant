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

public sealed class VetPhotoCommandReceiptStoreTests : VetTestBase
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
    private static string ImageJson(Guid source, Guid input, bool visibleUnit = true) => JsonSerializer.Serialize(new
    {
        schema_version = 1, photo_source_id = source.ToString("D"), input_revision_id = input.ToString("D"), kind = "meter",
        displays = new[] { new { value_text = "5.6", decimal_value = 5.6m, unit = visibleUnit ? "mmol/L" : (string?)null, year = 2031,
            year_displayed = true, month = 5, day = 11, time = "10:20", offset = "+00:00" } },
        reasons = Array.Empty<string>(), notes = (string?)null
    }, Json);
    private async Task<Evidence> Prepare(VetTestSession s, int id = 1, bool image = true, int topic = 7,
        bool sameBytes = false, bool collection = false, bool privateChat = false, bool caption = false, bool visibleUnit = true)
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
                claim.SourceId, claim.InputRevisionId, "synthetic-model", ImageJson(claim.SourceId, claim.InputRevisionId, visibleUnit)), Ct);
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
    private async Task<VetAdmittedSource> Command(VetTestSession s, int id = 900, bool bind = true, long actor = 222)
    {
        var message = Text("synthetic typed photo operation", id, actor);
        var admitted = await s.Diary.AdmitAsync(Scope, message, id, Ct);
        if (bind)
        {
            var stored = await s.Messages.StoreAsync(Bot.TelegramBotId, id, message, Ct);
            await s.Diary.LinkMessageAsync(Scope, admitted.Source.Id, stored.MessageDbId!.Value, Ct);
        }
        return admitted;
    }
    private static VetPhotoCommandPlanRequest Request(VetDiaryScope scope, VetAdmittedSource command,
        params VetPhotoCommandStep[] steps) => new(scope, command.Revision.OperationKey, 222, "{\"operation\":\"synthetic\"}", steps);
    private async Task<VetPhotoCommandPlan> Plan(VetTestSession s, VetAdmittedSource command, params VetPhotoCommandStep[] steps)
    {
        var result = await Photos(s).PreparePlanAsync(Request(Scope, command, steps), Ct);
        result.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); return result.Plan.ShouldNotBeNull();
    }
    private Task<VetPhotoCommandStepOutcome> Execute(VetTestSession s, VetPhotoCommandPlan plan, int index = 0) =>
        Photos(s).ExecuteStepAsync(Scope, plan.SourceOperationKey, index, 222, Ct);
    private async Task<VetPhotoHumanProposal> Human(VetTestSession s, Evidence photo, int? batchRevision = null)
    {
        var b = await s.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(x => x.Id == photo.Source.BatchId, Ct);
        var c = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(x => x.Id == photo.Candidate.Id, Ct);
        return new(Scope, b.Id, batchRevision ?? b.ReviewRevision, c.Id, c.Revision,
            photo.Input.Id, photo.Source.CurrentOrdinal, 222, new("7.125", "mmol/L", 2031, 5, 11, "10:20", "+00:00", CorrectionApproved: true));
    }
    private static Task<VetPhotoReview> Receipt(VetTestSession s, Guid plan) =>
        s.Context.Set<VetPhotoReview>().AsNoTracking().SingleAsync(r => r.Kind == "command_receipt" && r.SelectionJson.Contains(plan.ToString("D")));
    private static async Task<string> Snapshot(VetTestSession s) => JsonSerializer.Serialize(new {
        Batches = await s.Context.Set<VetPhotoBatch>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Sources = await s.Context.Set<VetPhotoSource>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Inputs = await s.Context.Set<VetPhotoInputRevision>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        TextSources = await s.Context.VetTextSources.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        TextRevisions = await s.Context.VetTextSourceRevisions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        TextResults = await s.Context.VetExtractionResults.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Messages = await s.Context.Messages.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Candidates = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Results = await s.Context.Set<VetPhotoExtraction>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Attempts = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Reviews = await s.Context.Set<VetPhotoReview>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Runs = await s.Context.Set<VetPhotoRun>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Windows = await s.Context.Set<VetPhotoRunWindow>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Facts = await s.Context.VetEvents.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Actions = await s.Context.VetDiaryActions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        References = await s.Context.Set<VetPhotoOriginalReference>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct),
        Blobs = await s.Context.Set<VetPhotoBlob>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(Ct)
    }, Json);
    [Fact]
    public async Task Lost_start_ack_restart_after_close_returns_exact_original_outcome_without_new_batch()
    {
        await SeedAsync(); await using var s = Open(); var command = await Command(s);
        var plan = await Plan(s, command, new VetPhotoCommandStep("start")); var first = await Execute(s, plan);
        first.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); first.BatchId.ShouldNotBeNull();
        var batch = await Photos(s).GetBatchAsync(Scope, first.BatchId.Value, 222, Ct);
        (await Photos(s).CloseCollectionAsync(Scope, first.BatchId.Value, batch!.Batch.ReviewRevision, 222, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        await using var restart = Open(); var before = await Snapshot(restart);
        JsonSerializer.Serialize((await Photos(restart).ReadPlanAsync(Scope, plan.SourceOperationKey, 222, Ct)).Plan, Json).ShouldBe(JsonSerializer.Serialize(plan, Json));
        (await Execute(restart, plan)).ShouldBe(first); (await Snapshot(restart)).ShouldBe(before);
        (await restart.Context.Set<VetPhotoBatch>().CountAsync(Ct)).ShouldBe(1);
        var receipt = await Receipt(restart, plan.Id);
        receipt.Kind.ShouldBe("command_receipt"); receipt.State.ShouldBe("command_done"); receipt.ActionId.ShouldBeNull();
        receipt.PageCount.ShouldBe(0); receipt.CompletePreviewDelivered.ShouldBeFalse(); receipt.AcceptancePromptMessageId.ShouldBeNull();
        receipt.DecisionActorUserId.ShouldBe(222); JsonSerializer.Deserialize<VetPhotoCommandStepOutcome>(receipt.OutcomeJson!, Json).ShouldBe(first);
    }
    [Fact]
    public async Task Concurrent_first_preparation_freezes_first_plan_and_duplicate_execution_mutates_once()
    {
        await SeedAsync(); await using var s = Open(); var command = await Command(s); var request = Request(Scope, command, new VetPhotoCommandStep("start"));
        await using var a = Open(); await using var b = Open();
        var prepared = await Task.WhenAll(Photos(a).PreparePlanAsync(request, Ct), Photos(b).PreparePlanAsync(request, Ct));
        prepared.Count(x => x.Status == VetPhotoWorkflowStatus.Applied).ShouldBe(1);
        prepared.Count(x => x.Status == VetPhotoWorkflowStatus.Existing).ShouldBe(1);
        prepared[0].Plan!.Id.ShouldBe(prepared[1].Plan!.Id);
        var outcomes = await Task.WhenAll(Execute(a, prepared[0].Plan!), Execute(b, prepared[1].Plan!));
        outcomes[0].ShouldBe(outcomes[1]); outcomes[0].Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        await using var v = Open(); (await v.Context.Set<VetPhotoBatch>().CountAsync(Ct)).ShouldBe(1);
        (await v.Context.Set<VetPhotoReview>().CountAsync(Ct)).ShouldBe(2); (await v.Context.VetDiaryActions.CountAsync(Ct)).ShouldBe(0);
    }
    [Theory]
    [InlineData(VetPhotoAssumptionKind.Year, "2032")]
    [InlineData(VetPhotoAssumptionKind.TimeZone, "UTC")]
    [InlineData(VetPhotoAssumptionKind.Unit, "mmol/L")]
    public async Task Assumption_command_commits_one_revision_and_exact_original_outcome(VetPhotoAssumptionKind kind, string value)
    {
        await SeedAsync(); await using var s = Open(); var photo = await Prepare(s); var command = await Command(s);
        var batch = await s.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(Ct);
        var step = new VetPhotoCommandStep("assumption", Assumption: new(Scope, batch.Id, batch.ReviewRevision, photo.Profile.Revision, 222, kind, value));
        var plan = await Plan(s, command, step); var first = await Execute(s, plan); first.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        await using var v = Open(); var changed = await v.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(Ct);
        changed.ReviewRevision.ShouldBe(batch.ReviewRevision + 1);
        var assumptions = JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(changed.AssumptionsJson, Json)!;
        if (kind == VetPhotoAssumptionKind.Year) { assumptions.Year.ShouldBe(2032); assumptions.YearConfirmed.ShouldBeTrue(); }
        if (kind == VetPhotoAssumptionKind.TimeZone) { assumptions.TimeZone.ShouldBe("UTC"); assumptions.TimeZoneConfirmed.ShouldBeTrue(); }
        if (kind == VetPhotoAssumptionKind.Unit) { assumptions.GlucoseUnit.ShouldBe("mmol/L"); assumptions.UnitConfirmed.ShouldBeTrue(); }
        var before = await Snapshot(v); (await Execute(v, plan)).ShouldBe(first); (await Snapshot(v)).ShouldBe(before);
        (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task Human_command_creates_exact_one_result_revision_actual_actor_and_frozen_provenance_without_facts()
    {
        await SeedAsync(); await using var s = Open(); var photo = await Prepare(s); var command = await Command(s);
        var human = await Human(s, photo); var plan = await Plan(s, command, new VetPhotoCommandStep("human", Human: human));
        var first = await Execute(s, plan); first.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); first.ExtractionResultId.ShouldNotBeNull();
        await using var v = Open(); var candidate = await v.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct);
        candidate.Revision.ShouldBe(photo.Candidate.Revision + 1); candidate.ExtractionResultId.ShouldBe(first.ExtractionResultId);
        candidate.ManuallyCorrected.ShouldBeTrue(); candidate.EventId.ShouldBeNull(); candidate.InputRevisionId.ShouldBe(photo.Input.Id);
        JsonSerializer.Deserialize<VetPhotoContext>(candidate.CorrectionProvenanceJson!, Json).ShouldBe(human.Context);
        var result = await v.Context.Set<VetPhotoExtraction>().AsNoTracking().SingleAsync(x => x.Id == first.ExtractionResultId, Ct);
        result.State.ShouldBe("human"); result.SourceId.ShouldBe(photo.Source.Id); result.InputRevisionId.ShouldBe(photo.Input.Id);
        var attempt = await v.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(x => x.Id == result.AttemptId, Ct);
        attempt.ActorUserId.ShouldBe(222); photo.Source.SourceAuthorUserId.ShouldBe(111); attempt.Kind.ShouldBe("human");
        JsonSerializer.Deserialize<VetPhotoEffectiveReading>(candidate.EffectiveJson!, Json)!.Value.ShouldBe(7.125m);
        (await v.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(2); (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
        var before = await Snapshot(v); (await Execute(v, plan)).ShouldBe(first); (await Snapshot(v)).ShouldBe(before);
    }
    [Theory]
    [InlineData("none")]
    [InlineData("candidate_revision")]
    [InlineData("source_ordinal")]
    [InlineData("current_input")]
    [InlineData("batch_revision")]
    public async Task Frozen_sequential_human_targets_resume_exact_sibling_or_record_stale_without_reselection(string fault)
    {
        var changedSibling = fault != "none";
        await SeedAsync(); await using var s = Open(); await Photos(s).StartCollectionAsync(Scope, 111, Ct);
        var a = await Prepare(s, 1); var b = await Prepare(s, 2); var command = await Command(s);
        var firstHuman = await Human(s, a); var secondHuman = await Human(s, b, firstHuman.BatchRevision + 1);
        var plan = await Plan(s, command, new VetPhotoCommandStep("human", Human: firstHuman), new VetPhotoCommandStep("human", Human: secondHuman));
        (await Execute(s, plan, 1)).Status.ShouldBe(VetPhotoWorkflowStatus.Incomplete);
        (await Execute(s, plan)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        if (fault == "candidate_revision") await s.Context.Set<VetPhotoCandidate>().Where(c => c.Id == b.Candidate.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Revision, c => c.Revision + 1), Ct);
        if (fault == "source_ordinal") await s.Context.Set<VetPhotoSource>().Where(x => x.Id == b.Source.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.CurrentOrdinal, x => x.CurrentOrdinal + 1), Ct);
        if (fault == "current_input") await s.Context.Set<VetPhotoSource>().Where(x => x.Id == b.Source.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.CurrentInputRevisionId, a.Input.Id), Ct);
        if (fault == "batch_revision") await s.Context.Set<VetPhotoBatch>().Where(x => x.Id == b.Source.BatchId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.ReviewRevision, x => x.ReviewRevision + 1), Ct);
        await using var v = Open(); var beforeCandidate = await v.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == b.Candidate.Id, Ct);
        var result = await Execute(v, plan, 1); result.Status.ShouldBe(changedSibling ? VetPhotoWorkflowStatus.Stale : VetPhotoWorkflowStatus.Applied);
        var after = await v.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(c => c.Id == b.Candidate.Id, Ct);
        if (changedSibling) JsonSerializer.Serialize(after, Json).ShouldBe(JsonSerializer.Serialize(beforeCandidate, Json));
        else { after.Revision.ShouldBe(beforeCandidate.Revision + 1); after.ExtractionResultId.ShouldBe(result.ExtractionResultId); }
        (await v.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(changedSibling ? 3 : 4);
        var before = await Snapshot(v); (await Execute(v, plan, 1)).ShouldBe(result); (await Snapshot(v)).ShouldBe(before);
        (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task Duplicate_and_late_commands_preserve_originals_and_create_only_frozen_proposals()
    {
        await SeedAsync(); await using var s = Open(); var photo = await Prepare(s); var command = await Command(s);
        var batch = await s.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(Ct);
        var duplicate = new VetPhotoCandidateChange(Scope, batch.Id, photo.Candidate.Id, batch.ReviewRevision,
            photo.Candidate.Revision, photo.Input.Id, photo.Source.CurrentOrdinal, photo.Result!.Id, 222, VetPhotoCandidateChangeKind.DuplicateSeparate);
        var plan = await Plan(s, command, new VetPhotoCommandStep("duplicate", Candidate: duplicate));
        (await Execute(s, plan)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct);
        candidate.DuplicateDecision.ShouldBe("separate"); candidate.EventId.ShouldBeNull();
        var collection = (await Photos(s).StartCollectionAsync(Scope, 222, Ct)).Batch!;
        var closed = await Photos(s).CloseCollectionAsync(Scope, collection.Id, collection.ReviewRevision, 222, Ct);
        closed.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var message = Text("synthetic explicitly late image", 3, 111) with { Kind = MessageKind.Photo };
        var late = await Photos(s).AdmitAsync(Scope, message, 901, new("synthetic-late-file", "synthetic-late-unique", "synthetic.png", "image/png", 3, 1, 1), null, Ct);
        late.Status.ShouldBe(VetPhotoAdmissionStatus.Late);
        var transport = await s.Messages.StoreAsync(Bot.TelegramBotId, 901, message, Ct);
        (await Photos(s).BindMessageAsync(Scope, late.Source!.Id, transport.MessageDbId!.Value, Ct)).ShouldBeTrue();
        var later = await Command(s, 902); var latePlan = await Plan(s, later,
            new VetPhotoCommandStep("late", Late: new(collection.Id, closed.Batch!.ReviewRevision, late.Source.Id, late.Input!.Id)));
        var first = await Execute(s, latePlan); first.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync(x => x.Id == late.Source.Id, Ct)).BatchId.ShouldBe(collection.Id);
        await using var v = Open(); var before = await Snapshot(v); (await Execute(v, latePlan)).ShouldBe(first); (await Snapshot(v)).ShouldBe(before);
        (await v.Context.Set<VetPhotoOriginalReference>().CountAsync(Ct)).ShouldBe(1); (await v.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
    }
    [Theory]
    [InlineData("actor")]
    [InlineData("topic")]
    [InlineData("chat")]
    [InlineData("membership")]
    [InlineData("place")]
    [InlineData("bot")]
    [InlineData("message_bot")]
    [InlineData("message_author")]
    [InlineData("message_kind")]
    [InlineData("message_sent")]
    public async Task Fresh_exact_author_transport_and_approval_gate_refuses_preparation_execution_and_replay(string fault)
    {
        await SeedAsync(); await using var s = Open(); var command = await Command(s); var plan = await Plan(s, command, new VetPhotoCommandStep("start"));
        var scope = Scope; long actor = 222;
        if (fault == "actor") actor = 111;
        if (fault == "topic") scope = Scope with { TopicId = 8 };
        if (fault == "chat") scope = Scope with { ChatId = -200 };
        if (fault == "membership") await s.Context.Set<FamilyMember>().Where(m => m.TelegramUserId == 222).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        if (fault == "place") await s.Context.Set<Place>().Where(p => p.TopicId == 7).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlaceStatus.Denied), Ct);
        if (fault == "bot") await s.Context.Bots.ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BotStatus.Disabled), Ct);
        if (fault.StartsWith("message_", StringComparison.Ordinal))
        {
            var message = await s.Context.Messages.SingleAsync(Ct);
            if (fault == "message_bot") message.BotId++;
            if (fault == "message_author") message.UserId = 111;
            if (fault == "message_kind") message.Kind = MessageKind.Service;
            if (fault == "message_sent") message.SentAt = message.SentAt.AddSeconds(1);
            await s.Context.SaveChangesAsync(Ct); s.Context.ChangeTracker.Clear();
        }
        var before = await Snapshot(s);
        (await Photos(s).ReadPlanAsync(scope, plan.SourceOperationKey, actor, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Photos(s).ExecuteStepAsync(scope, plan.SourceOperationKey, 0, actor, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Photos(s).PreparePlanAsync(Request(scope, command, new VetPhotoCommandStep("start")) with { ActorUserId = actor }, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Snapshot(s)).ShouldBe(before);
    }
    [Fact]
    public async Task Unbound_empty_unknown_operation_and_changed_request_refuse_without_replacing_first_plan()
    {
        await SeedAsync(); await using var s = Open(); var unbound = await Command(s, bind: false);
        (await Photos(s).PreparePlanAsync(Request(Scope, unbound, new VetPhotoCommandStep("start")), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        var command = await Command(s, 901); var plan = await Plan(s, command, new VetPhotoCommandStep("start")); var before = await Snapshot(s);
        (await Photos(s).PreparePlanAsync(Request(Scope, command, new VetPhotoCommandStep("close", Guid.NewGuid(), 1)) with { RequestJson = "{\"operation\":\"different\"}" }, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        foreach (var key in new[] { Guid.Empty, Guid.NewGuid() })
            (await Photos(s).ReadPlanAsync(Scope, key, 222, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        // Same immutable request replays first frozen target list, even if caller rebuilt a different valid selection.
        var first = await Photos(s).PreparePlanAsync(Request(Scope, command, new VetPhotoCommandStep("close", Guid.NewGuid(), 1)), Ct);
        first.Status.ShouldBe(VetPhotoWorkflowStatus.Existing); first.Plan!.Steps.Single().Kind.ShouldBe("start");
        first.Plan.Id.ShouldBe(plan.Id); (await Snapshot(s)).ShouldBe(before);
    }
    [Fact]
    public async Task Edited_command_source_stales_unexecuted_step_but_old_committed_receipt_replays_exact_outcome()
    {
        await SeedAsync(); await using var s = Open(); var command = await Command(s); var plan = await Plan(s, command, new VetPhotoCommandStep("start"), new VetPhotoCommandStep("start"));
        var first = await Execute(s, plan);
        var edit = Text("synthetic revised command", 900, 222) with { IsEdit = true, EditedAt = Now.AddMinutes(1) };
        await s.Diary.AdmitAsync(Scope, edit, 901, Ct);
        var before = await Snapshot(s); (await Execute(s, plan)).ShouldBe(first);
        (await Execute(s, plan, 1)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale); (await Snapshot(s)).ShouldBe(before);
        (await Photos(s).PreparePlanAsync(Request(Scope, command, new VetPhotoCommandStep("start")), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Existing);
    }
    [Theory]
    [InlineData("fingerprint")]
    [InlineData("outcome")]
    [InlineData("actor")]
    [InlineData("state")]
    [InlineData("prompt")]
    [InlineData("delivered")]
    public async Task Every_prior_step_requires_full_exact_receipt_proof_before_next_mutation(string tamper)
    {
        await SeedAsync(); await using var s = Open(); var command = await Command(s); var plan = await Plan(s, command, new VetPhotoCommandStep("start"), new VetPhotoCommandStep("start"));
        (await Execute(s, plan)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var id = (await Receipt(s, plan.Id)).Id; var receipt = await s.Context.Set<VetPhotoReview>().SingleAsync(r => r.Id == id, Ct);
        if (tamper == "fingerprint") receipt.Fingerprint = new string('0', 64);
        if (tamper == "outcome") receipt.OutcomeJson = "{}";
        if (tamper == "actor") receipt.DecisionActorUserId = 111;
        if (tamper == "state") receipt.State = "preview";
        if (tamper == "prompt") receipt.AcceptancePromptMessageId = 701;
        if (tamper == "delivered") receipt.CompletePreviewDelivered = true;
        await s.Context.SaveChangesAsync(Ct); s.Context.ChangeTracker.Clear(); var before = await Snapshot(s);
        (await Execute(s, plan, 1)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale); (await Snapshot(s)).ShouldBe(before);
    }
    [Theory]
    [InlineData("request_json")]
    [InlineData("request_array")]
    [InlineData("request_deep")]
    [InlineData("request_oversized")]
    [InlineData("request_surrogate")]
    [InlineData("steps_empty")]
    [InlineData("steps_65")]
    [InlineData("kind_save")]
    [InlineData("kind_delete")]
    [InlineData("kind_cancel")]
    [InlineData("kind_reverse")]
    [InlineData("kind_run")]
    [InlineData("mixed_payload")]
    [InlineData("human_surrogate")]
    [InlineData("human_nul")]
    [InlineData("human_value_101")]
    [InlineData("human_preserved_time")]
    [InlineData("late_51")]
    public async Task Invalid_bounded_plan_refuses_before_any_receipt_or_target_mutation(string fault)
    {
        await SeedAsync(); await using var s = Open(); var photo = await Prepare(s); var command = await Command(s); var human = await Human(s, photo);
        var request = Request(Scope, command, new VetPhotoCommandStep("start"));
        if (fault == "request_json") request = request with { RequestJson = "{" };
        if (fault == "request_array") request = request with { RequestJson = "[]" };
        if (fault == "request_deep") request = request with { RequestJson = "{\"x\":" + new string('[', 17) + "0" + new string(']', 17) + "}" };
        if (fault == "request_oversized") request = request with { RequestJson = "{\"x\":\"" + new string('a', 131072) + "\"}" };
        if (fault == "request_surrogate") request = request with { RequestJson = "{\"x\":\"\uD800\"}" };
        if (fault == "steps_empty") request = request with { Steps = [] };
        if (fault == "steps_65") request = request with { Steps = Enumerable.Repeat(new VetPhotoCommandStep("start"), 65).ToArray() };
        if (fault.StartsWith("kind_", StringComparison.Ordinal)) request = request with { Steps = [new(fault[5..])] };
        if (fault == "mixed_payload") request = request with { Steps = [new VetPhotoCommandStep("start", Human: human)] };
        if (fault == "human_surrogate") request = request with { Steps = [new VetPhotoCommandStep("human", Human: human with { Context = human.Context with { RawValue = "\uD800" } })] };
        if (fault == "human_nul") request = request with { Steps = [new VetPhotoCommandStep("human", Human: human with { Context = human.Context with { Unit = "mmol\0/L" } })] };
        if (fault == "human_value_101") request = request with { Steps = [new VetPhotoCommandStep("human", Human: human with { Context = human.Context with { RawValue = new string('1', 101) } })] };
        if (fault == "human_preserved_time") request = request with { Steps = [new VetPhotoCommandStep("human", Human: human with { Context = human.Context with { PreservedTime = new(Now, "2031-05-12 12:00:00", "UTC", "human_correction") } })] };
        if (fault == "late_51") request = request with { Steps = Enumerable.Range(0, 51).Select(_ => new VetPhotoCommandStep("late", Late: new(photo.Source.BatchId!.Value, 1, Guid.NewGuid(), Guid.NewGuid()))).ToArray() };
        var before = await Snapshot(s); (await Photos(s).PreparePlanAsync(request, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Snapshot(s)).ShouldBe(before);
    }
    [Fact]
    public async Task Sixty_four_ordered_steps_preserve_all_outcomes_without_truncation_and_negative_indexes_refuse()
    {
        await SeedAsync(); await using var s = Open(); var command = await Command(s); var plan = await Plan(s, command, Enumerable.Repeat(new VetPhotoCommandStep("start"), 64).ToArray());
        plan.Steps.Count.ShouldBe(64); var before = await Snapshot(s);
        foreach (var index in new[] { -1, 64 }) (await Execute(s, plan, index)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Snapshot(s)).ShouldBe(before);
        Guid? batch = null;
        for (var i = 0; i < 64; i++) { var result = await Execute(s, plan, i); result.Status.ShouldBe(i == 0 ? VetPhotoWorkflowStatus.Applied : VetPhotoWorkflowStatus.Existing); batch ??= result.BatchId; result.BatchId.ShouldBe(batch); }
        (await s.Context.Set<VetPhotoReview>().CountAsync(r => r.Kind == "command_receipt", Ct)).ShouldBe(64);
        (await s.Context.Set<VetPhotoBatch>().CountAsync(Ct)).ShouldBe(1); (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
    }
    private sealed class ReceiptFailure : DbCommandInterceptor
    {
        public bool Enabled = true;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("INSERT INTO vet_photo_reviews", StringComparison.Ordinal))
            { Enabled = false; throw new InvalidOperationException("synthetic command receipt failure"); }
            return ValueTask.FromResult(result);
        }
    }
    [Fact]
    public async Task Receipt_insert_failure_rolls_back_human_result_candidate_batch_and_receipt_then_same_context_retry_succeeds()
    {
        await SeedAsync(); await using var setup = Open(); var photo = await Prepare(setup); var command = await Command(setup);
        var plan = await Plan(setup, command, new VetPhotoCommandStep("human", Human: await Human(setup, photo))); var before = await Snapshot(setup);
        var interceptor = new ReceiptFailure(); await using var failing = Open(interceptor: interceptor);
        var error = await Should.ThrowAsync<DbUpdateException>(() => Execute(failing, plan));
        error.InnerException.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("synthetic command receipt failure");
        await using var v = Open(); (await Snapshot(v)).ShouldBe(before);
        var successful = await Execute(failing, plan); successful.Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        await using var restart = Open(); var committed = await Snapshot(restart);
        (await Execute(restart, plan)).ShouldBe(successful); (await Snapshot(restart)).ShouldBe(committed);
        (await restart.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(2);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Command_done_cannot_be_used_as_complete_natural_callback_run_or_diary_proof(bool callback)
    {
        await SeedAsync(); await using var s = Open(); var photo = await Prepare(s); var command = await Command(s);
        var plan = await Plan(s, command, new VetPhotoCommandStep("start")); (await Execute(s, plan)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var receipt = await Receipt(s, plan.Id); var handle = new VetPhotoReviewHandle(Scope, receipt.Id, receipt.Revision, receipt.OperationKey, 222, callback ? 701 : null);
        var staged = await Photos(s).StageRunAsync(new(Scope, Guid.NewGuid(), 222, photo.Profile.Revision,
            VetPhotoRunPurpose.Reprocess, VetPhotoRunSelectionMode.Current, "synthetic-model", "synthetic-provider"), Ct);
        staged.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); var before = await Snapshot(s);
        (await Photos(s).GetReviewAsync(handle, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Photos(s).FindNaturalReviewAsync(Scope, 222, receipt.OperationKey, receipt.Revision, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.NotFound);
        (await Photos(s).RecordPageDeliveryAsync(handle, 0, 701, Hash("synthetic"), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Photos(s).CompleteDeliveryAsync(handle, 701, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Photos(s).ApproveRunAsync(new(Scope, staged.Run!.Id, 222), handle, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await s.Diary.ApplyPhotoReviewAsync(new(Scope, receipt.Id, receipt.Revision, receipt.OperationKey, 222, callback ? 701 : null), Ct)).Status.ShouldBe(VetMutationStatus.Stale);
        (await s.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct)).Status.ShouldBe(VetMutationStatus.NotFound);
        (await Snapshot(s)).ShouldBe(before); (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task Revoked_successful_receipt_replay_refuses_without_disclosing_or_repeating_original_outcome()
    {
        await SeedAsync(); await using var s = Open(); var command = await Command(s); var plan = await Plan(s, command, new VetPhotoCommandStep("start"));
        (await Execute(s, plan)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        await s.Context.Set<FamilyMember>().Where(m => m.TelegramUserId == 222).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied), Ct);
        await using var v = Open(); var before = await Snapshot(v);
        (await Photos(v).ReadPlanAsync(Scope, plan.SourceOperationKey, 222, Ct)).Plan.ShouldBeNull();
        (await Execute(v, plan)).ShouldBe(new VetPhotoCommandStepOutcome(VetPhotoWorkflowStatus.Refused));
        (await Snapshot(v)).ShouldBe(before);
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
    [Fact]
    public async Task Value_only_human_proposal_without_visible_unit_uses_exact_default_provenance_then_only_complete_review_writes_fact()
    {
        await SeedAsync(); await using var s = Open(); var photo = await Prepare(s, visibleUnit: false); var command = await Command(s);
        var human = (await Human(s, photo)) with { Context = new("7.125", CorrectionApproved: true) };
        var plan = await Plan(s, command, new VetPhotoCommandStep("human", Human: human)); var outcome = await Execute(s, plan);
        outcome.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
        var candidate = await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct);
        var reading = JsonSerializer.Deserialize<VetPhotoEffectiveReading>(candidate.EffectiveJson!, Json)!;
        reading.Value.ShouldBe(7.125m); reading.Unit.ShouldBe("mmol/L"); reading.ValueEvidence.ShouldBe("human_correction"); reading.UnitEvidence.ShouldBe("batch_default");
        var extraction = await s.Context.Set<VetPhotoExtraction>().AsNoTracking().SingleAsync(x => x.Id == outcome.ExtractionResultId, Ct);
        var current = photo with { Candidate = candidate, Result = extraction };
        var state = Fact(current) with { Value = reading.Value, Unit = reading.Unit, OccurredAt = reading.OccurredAt,
            LocalTime = reading.LocalTime, TimeZoneSnapshot = reading.TimeZoneSnapshot,
            OccurredAtSource = reading.TimeEvidence, ValueUnitSource = "human_correction/batch_default" };
        state.ValueUnitSource.Length.ShouldBe(30);
        var selected = (await Select(s, current, state: state, duplicate: "separate")) with { Context = human.Context };
        var preview = await Review(s, Scope, [selected], deliver: false);
        (await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, preview), Ct)).Status.ShouldBe(VetMutationStatus.Stale);
        (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
        var pages = JsonSerializer.Deserialize<string[]>(preview.PreviewPagesJson, Json)!;
        var handle = new VetPhotoReviewHandle(Scope, preview.Id, preview.Revision, preview.OperationKey, 111);
        for (var i = 0; i < pages.Length; i++) (await Photos(s).RecordPageDeliveryAsync(handle, i,
            i == pages.Length - 1 ? 701 : 500 + i, Hash(pages[i]), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        (await Photos(s).CompleteDeliveryAsync(handle, 701, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        var accepted = await s.Diary.ApplyPhotoReviewAsync(Accept(Scope, preview), Ct); accepted.Status.ShouldBe(VetMutationStatus.Applied);
        await using var v = Open(); var fact = await v.Context.VetEvents.AsNoTracking().SingleAsync(Ct);
        fact.Value.ShouldBe(7.125m); fact.Unit.ShouldBe("mmol/L"); fact.ValueUnitSource.ShouldBe("human_correction/batch_default");
        fact.SourceAuthorUserId.ShouldBe(111); fact.ExtractionResultId.ShouldBe(extraction.Id); fact.InputRevisionId.ShouldBe(photo.Input.Id);
        // Non-fact command rows do not displace the latest actual action when ordinary undo selects it.
        var second = await Command(v, 902); var more = await Plan(v, second, new VetPhotoCommandStep("start")); await Execute(v, more);
        var undone = await v.Diary.UndoAsync(Scope, 222, Guid.NewGuid(), Ct); undone.EventIds.ShouldBe(accepted.EventIds);
        (await v.Context.VetEvents.AsNoTracking().SingleAsync(Ct)).DeletedAt.ShouldNotBeNull();
    }
    [Theory]
    [InlineData("unknown_status")]
    [InlineData("quoted_status")]
    [InlineData("empty_identity")]
    [InlineData("wrong_human_shape")]
    [InlineData("duplicate_field")]
    [InlineData("unknown_field")]
    public async Task Forged_receipt_outcome_shape_cannot_replay_or_authorize_next_step(string fault)
    {
        await SeedAsync(); await using var s = Open(); var command = await Command(s); var plan = await Plan(s, command, new VetPhotoCommandStep("start"), new VetPhotoCommandStep("start")); await Execute(s, plan);
        var receipt = await Receipt(s, plan.Id); var row = await s.Context.Set<VetPhotoReview>().SingleAsync(r => r.Id == receipt.Id, Ct);
        row.OutcomeJson = fault switch {
            "unknown_status" => "{\"status\":999,\"batchId\":null,\"candidateId\":null,\"extractionResultId\":null}",
            "quoted_status" => "{\"status\":\"0\",\"batchId\":null,\"candidateId\":null,\"extractionResultId\":null}",
            "empty_identity" => "{\"status\":0,\"batchId\":\"00000000-0000-0000-0000-000000000000\",\"candidateId\":null,\"extractionResultId\":null}",
            "wrong_human_shape" => JsonSerializer.Serialize(new VetPhotoCommandStepOutcome(VetPhotoWorkflowStatus.Applied, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Json),
            "duplicate_field" => row.OutcomeJson!.Replace("{", "{\"status\":0,", StringComparison.Ordinal),
            _ => row.OutcomeJson!.Replace("{", "{\"unknown\":true,", StringComparison.Ordinal)
        };
        await s.Context.SaveChangesAsync(Ct); s.Context.ChangeTracker.Clear(); var before = await Snapshot(s);
        (await Execute(s, plan)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Execute(s, plan, 1)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale); (await Snapshot(s)).ShouldBe(before);
    }
    [Fact]
    public async Task Human_capacity_refusal_is_durable_without_result_candidate_or_fact_mutation()
    {
        await SeedAsync(); await using var s = Open(); var photo = await Prepare(s); var command = await Command(s);
        var plan = await Plan(s, command, new VetPhotoCommandStep("human", Human: await Human(s, photo)));
        var bounded = new VetPhotoStore(s.Context, s.Current, Clock, new(MaxResults: 1), new VetPhotoImageDecoder());
        var before = JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct), Json);
        var first = await bounded.ExecuteStepAsync(Scope, plan.SourceOperationKey, 0, 222, Ct);
        first.Status.ShouldBe(VetPhotoWorkflowStatus.Full);
        JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(Ct), Json).ShouldBe(before);
        (await s.Context.Set<VetPhotoExtraction>().CountAsync(Ct)).ShouldBe(1); (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
        var snapshot = await Snapshot(s); (await Execute(s, plan)).ShouldBe(first); (await Snapshot(s)).ShouldBe(snapshot);
    }
    [Theory]
    [InlineData(50, VetPhotoWorkflowStatus.Applied)]
    [InlineData(51, VetPhotoWorkflowStatus.Refused)]
    public async Task Exact_source_mutation_limit_never_silently_truncates_selection(int count, VetPhotoWorkflowStatus status)
    {
        await SeedAsync(); await using var s = Open(); var photo = await Prepare(s); var command = await Command(s);
        var steps = Enumerable.Range(0, count).Select(_ => new VetPhotoCommandStep("late", Late: new(photo.Source.BatchId!.Value, 1, Guid.NewGuid(), Guid.NewGuid()))).ToArray();
        var before = await Snapshot(s); var result = await Photos(s).PreparePlanAsync(Request(Scope, command, steps), Ct);
        result.Status.ShouldBe(status);
        if (status == VetPhotoWorkflowStatus.Applied) { result.Plan!.Steps.Count.ShouldBe(50); result.Plan.Steps.Select(x => x.Late!.SourceId).ShouldBe(steps.Select(x => x.Late!.SourceId)); }
        else { result.Plan.ShouldBeNull(); (await Snapshot(s)).ShouldBe(before); }
        (await s.Context.VetEvents.CountAsync(Ct)).ShouldBe(0);
    }
    [Fact]
    public async Task Duplicate_mutation_target_refuses_plan_instead_of_revising_same_candidate_twice()
    {
        await SeedAsync(); await using var s = Open(); var photo = await Prepare(s); var command = await Command(s); var human = await Human(s, photo);
        var before = await Snapshot(s);
        (await Photos(s).PreparePlanAsync(Request(Scope, command, new VetPhotoCommandStep("human", Human: human), new VetPhotoCommandStep("human", Human: human with { BatchRevision = human.BatchRevision + 1, CandidateRevision = human.CandidateRevision + 1 })), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Snapshot(s)).ShouldBe(before);
    }
    [Fact]
    public async Task Closed_batch_receipt_replays_original_outcome_after_assumption_revision_changes()
    {
        await SeedAsync(); await using var s = Open(); var batch = (await Photos(s).StartCollectionAsync(Scope, 111, Ct)).Batch!;
        var command = await Command(s); var plan = await Plan(s, command, new VetPhotoCommandStep("close", batch.Id, batch.ReviewRevision));
        var first = await Execute(s, plan); first.Status.ShouldBe(VetPhotoWorkflowStatus.Applied); first.BatchId.ShouldBe(batch.Id);
        var changed = await s.Context.Set<VetPhotoBatch>().AsNoTracking().SingleAsync(Ct);
        var profile = await s.Context.Set<VetProfile>().AsNoTracking().SingleAsync(Ct);
        (await Photos(s).ChangeAssumptionAsync(new(Scope, batch.Id, changed.ReviewRevision, profile.Revision, 222, VetPhotoAssumptionKind.Year, "2032"), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Applied);
        await using var v = Open(); var before = await Snapshot(v); (await Execute(v, plan)).ShouldBe(first); (await Snapshot(v)).ShouldBe(before);
        (await v.Context.Set<VetPhotoReview>().CountAsync(r => r.Kind == "command_receipt", Ct)).ShouldBe(1);
    }
    [Fact]
    public async Task Preparing_old_operation_after_source_edit_is_stale_and_never_infers_new_revision()
    {
        await SeedAsync(); await using var s = Open(); var command = await Command(s);
        await s.Diary.AdmitAsync(Scope, Text("synthetic edited operation", 900, 222) with { IsEdit = true, EditedAt = Now.AddMinutes(1) }, 901, Ct);
        var before = await Snapshot(s);
        (await Photos(s).PreparePlanAsync(Request(Scope, command, new VetPhotoCommandStep("start")), Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Stale);
        (await Snapshot(s)).ShouldBe(before);
    }
    [Theory]
    [InlineData(131072, VetPhotoWorkflowStatus.Applied)]
    [InlineData(131073, VetPhotoWorkflowStatus.Refused)]
    public async Task Exact_request_character_boundary_is_preserved_or_refused_before_plan_write(int length, VetPhotoWorkflowStatus status)
    {
        await SeedAsync(); await using var s = Open(); var command = await Command(s);
        var json = "{\"x\":\"" + new string('a', length - 8) + "\"}";
        json.Length.ShouldBe(length); var before = await Snapshot(s);
        var result = await Photos(s).PreparePlanAsync(Request(Scope, command, new VetPhotoCommandStep("start")) with { RequestJson = json }, Ct);
        result.Status.ShouldBe(status);
        if (status == VetPhotoWorkflowStatus.Applied) result.Plan!.RequestJson.ShouldBe(json);
        else (await Snapshot(s)).ShouldBe(before);
    }
    [Fact]
    public async Task Oversized_stored_plan_refuses_before_parse_and_has_zero_target_writes()
    {
        await SeedAsync(); await using var s = Open(); var command = await Command(s); var plan = await Plan(s, command, new VetPhotoCommandStep("start"));
        var row = await s.Context.Set<VetPhotoReview>().SingleAsync(r => r.Id == plan.Id, Ct);
        row.SelectionJson = new string('a', 700001); row.Fingerprint = Hash(row.SelectionJson);
        await s.Context.SaveChangesAsync(Ct); s.Context.ChangeTracker.Clear(); var before = await Snapshot(s);
        (await Photos(s).ReadPlanAsync(Scope, plan.SourceOperationKey, 222, Ct)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused);
        (await Execute(s, plan)).Status.ShouldBe(VetPhotoWorkflowStatus.Refused); (await Snapshot(s)).ShouldBe(before);
    }
}
