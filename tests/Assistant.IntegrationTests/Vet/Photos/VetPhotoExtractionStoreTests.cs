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
using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SkiaSharp;

namespace Assistant.IntegrationTests.Vet.Photos;

public sealed class VetPhotoExtractionStoreTests : VetTestBase
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly VetPhotoImageDecoder Decoder = new();
    private static readonly byte[] Original = ImageBytes();
    private VetPhotoStore Store(VetTestSession s, VetPhotoCapacity? capacity = null) =>
        new(s.Context, s.Current, Clock, capacity ?? new(), Decoder);
    private static VetPhotoAttachment Attachment(int id) =>
        new($"synthetic-file-{id}", $"synthetic-unique-{id}", "synthetic.png", "image/png", Original.Length, 32, 24);
    private static IncomingMessage ImageMessage(int id, VetDiaryScope scope, long actor = 111, string caption = "synthetic caption") =>
        Text(caption, id, actor) with { Kind = MessageKind.Photo, ChatId = scope.ChatId, TopicId = scope.TopicId };
    private static string ResultJson(Guid sourceId, Guid inputId) => JsonSerializer.Serialize(new
    {
        schema_version = 1, photo_source_id = sourceId.ToString("D"), input_revision_id = inputId.ToString("D"), kind = "meter",
        displays = new[] { new { value_text = "5.6", decimal_value = 5.6m, unit = "mmol/L", year = 2031,
            year_displayed = true, month = 5, day = 11, time = "10:20", offset = "+00:00" } },
        reasons = Array.Empty<string>(), notes = (string?)null
    }, Json);
    private static VetPhotoImageCompletion Completion(VetPhotoImageClaim claim, string? json = null, string model = "synthetic-model") =>
        new(claim.Scope, claim.AttemptKey, claim.ClaimToken, claim.ActorUserId, claim.SourceId,
            claim.InputRevisionId, model, json ?? ResultJson(claim.SourceId, claim.InputRevisionId));

    private async Task<VetPhotoAdmission> PreparedAsync(VetTestSession s, int id = 1,
        VetDiaryScope? selected = null, long actor = 111)
    {
        var scope = selected ?? Scope;
        var message = ImageMessage(id, scope, actor);
        var store = Store(s);
        var admitted = await store.AdmitAsync(scope, message, id, Attachment(id), null, Ct);
        admitted.Status.ShouldBe(VetPhotoAdmissionStatus.Admitted);
        var source = admitted.Source!.ShouldNotBeNull();
        var input = admitted.Input!.ShouldNotBeNull();
        var stored = await s.Messages.StoreAsync(scope.TelegramBotId, id, message, Ct);
        (await store.BindMessageAsync(scope, source.Id, stored.MessageDbId.ShouldNotBeNull(), Ct)).ShouldBeTrue();
        var reservation = await store.ReserveDownloadAsync(scope, source.Id, input.Id, actor, Ct);
        reservation.Status.ShouldBe(VetPhotoArchiveStatus.Reserved);
        var download = reservation.Claim.ShouldNotBeNull();
        (await store.CommitOriginalAsync(new(scope, actor, download.Attempt.Id, download.ClaimToken,
            input.Id, Original, Decoder.Decode(Original, Ct).Image.ShouldNotBeNull()), Ct)).Status.ShouldBe(VetPhotoArchiveStatus.Retained);
        return (await store.GetSourceAsync(scope, source.Id, Ct)).ShouldNotBeNull();
    }
    private async Task<VetPhotoImageClaim> ClaimAsync(VetTestSession s, VetPhotoAdmission input)
    {
        var result = await Store(s).ClaimCurrentImageAsync(Scope, input.Source!.Id, input.Input!.Id, 111, Ct);
        result.Status.ShouldBe(VetPhotoImageStatus.Claimed);
        var claim = result.Claim.ShouldNotBeNull();
        claim.SourceId.ShouldBe(input.Source!.Id); claim.InputRevisionId.ShouldBe(input.Input!.Id);
        claim.ExpectedCurrentInputId.ShouldBe(input.Input!.Id); claim.ExpectedSourceOrdinal.ShouldBe(input.Input!.Ordinal);
        claim.ActorUserId.ShouldBe(111); claim.LeaseUntil.ShouldBe(Clock.UtcNow.AddMinutes(5));
        claim.HistoricalSelection.ShouldBeFalse(); claim.RunWindowId.ShouldBeNull();
        return claim;
    }
    private async Task<VetPhotoImageResult> CompleteAsync(VetTestSession s, VetPhotoImageClaim claim)
    {
        (await Store(s).MarkImageDispatchedAsync(claim.Scope, claim.AttemptKey, claim.ClaimToken, claim.ActorUserId, Ct)).ShouldBeTrue();
        return await Store(s).CompleteImageAsync(Completion(claim), Ct);
    }
    private async Task<VetPhotoAdmission> CaptionEditAsync(VetTestSession s, VetPhotoAdmission prior, int update = 100)
    {
        var source = prior.Source!;
        var changed = await Store(s).AdmitAsync(Scope,
            ImageMessage(source.TelegramMessageId, Scope, caption: "synthetic changed caption") with
            { IsEdit = true, EditedAt = Now.AddSeconds(1) }, update, Attachment(source.TelegramMessageId), null, Ct);
        changed.Input!.ReusesImageInputId.ShouldBe(prior.Input!.Id);
        (await Store(s).ReserveDownloadAsync(Scope, source.Id, changed.Input!.Id, 111, Ct)).Status.ShouldBe(VetPhotoArchiveStatus.Retained);
        return changed;
    }
    private async Task<VetDiaryScope> OtherFamilyAsync()
    {
        var family = new Family { Name = "synthetic second family", CreatedAt = Now };
        Db.Add(family); await Db.SaveChangesAsync();
        var bot = new Assistant.Domain.Bots.Bot { FamilyId = family.Id, TelegramBotId = 2001,
            Username = "synthetic_other_vet_bot", Role = "vet", Status = BotStatus.Active, CreatedAt = Now };
        Db.Add(bot); Db.Add(new FamilyMember { FamilyId = family.Id, TelegramUserId = 333,
            DisplayName = "synthetic second owner", IsOwner = true, Status = FamilyMemberStatus.Approved, CreatedAt = Now, UpdatedAt = Now });
        await Db.SaveChangesAsync();
        Db.Add(new Place { BotId = bot.Id, ChatId = -200, TopicId = 7, Title = "synthetic other topic",
            Status = PlaceStatus.Approved, CreatedAt = Now }); await Db.SaveChangesAsync();
        return new(family.Id, bot.Id, bot.TelegramBotId, -200, 7);
    }

    [Fact]
    public async Task Global_result_capacity_race_reserves_one_slot_across_families_without_exposing_rows()
    {
        await SeedAsync(); var other = await OtherFamilyAsync();
        VetPhotoAdmission first, second;
        await using (var seed = Open()) first = await PreparedAsync(seed);
        await using (var seed = Open(other.FamilyId)) second = await PreparedAsync(seed, 2, other, 333);
        var hold = new HoldImageInsert(); var arriving = new ObserveCapacity();
        await using var one = Open(interceptor: hold); await using var two = Open(other.FamilyId, arriving);
        var capacity = new VetPhotoCapacity(MaxResults: 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstClaim = Store(one, capacity).ClaimCurrentImageAsync(Scope, first.Source!.Id, first.Input!.Id, 111, timeout.Token);
        Task<VetPhotoImageResult>? secondClaim = null;
        try
        {
            await hold.Entered.Task.WaitAsync(timeout.Token); // First transaction already counted zero and reserved its slot.
            secondClaim = Store(two, capacity).ClaimCurrentImageAsync(other, second.Source!.Id, second.Input!.Id, 333, timeout.Token);
            await arriving.Entered.Task.WaitAsync(timeout.Token);
        }
        finally { hold.Release(); await Task.WhenAll(firstClaim, secondClaim ?? Task.FromResult(new VetPhotoImageResult(VetPhotoImageStatus.NotFound))); }
        (await firstClaim).Status.ShouldBe(VetPhotoImageStatus.Claimed);
        (await secondClaim!).Status.ShouldBe(VetPhotoImageStatus.CapacityFull);
        (await secondClaim!).Claim.ShouldBeNull(); (await secondClaim!).Extraction!.ShouldBeNull();
        await using var verify = Open(); await using var foreign = Open(other.FamilyId);
        (await Store(verify).GetCapacityAsync(Scope, 111, Ct)).ShouldBe(new(Original.Length * 2L, 0, 2, 0, 0, 1));
        (await verify.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image")).ShouldBe(1);
        (await foreign.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image")).ShouldBe(0);
        (await foreign.Context.Set<VetPhotoSource>().Select(s => s.Id).ToArrayAsync()).ShouldBe([second.Source!.Id]);
    }

    [Fact]
    public async Task Concurrent_current_claims_share_one_stable_attempt_and_only_one_live_owner()
    {
        await SeedAsync(); VetPhotoAdmission input;
        await using (var seed = Open()) input = await PreparedAsync(seed);
        await using var one = Open(); await using var two = Open();
        var results = await Task.WhenAll(Store(one).ClaimCurrentImageAsync(Scope, input.Source!.Id, input.Input!.Id, 111, Ct),
            Store(two).ClaimCurrentImageAsync(Scope, input.Source!.Id, input.Input!.Id, 222, Ct));
        results.Select(r => r.Status).OrderBy(x => x).ShouldBe(new[] { VetPhotoImageStatus.Claimed, VetPhotoImageStatus.Busy }.OrderBy(x => x));
        var winner = results.Single(r => r.Status == VetPhotoImageStatus.Claimed).Claim.ShouldNotBeNull();
        await using var verify = Open();
        var attempt = await verify.Context.Set<VetPhotoAttempt>().SingleAsync(a => a.Kind == "image");
        attempt.Id.ShouldBe(winner.AttemptKey); attempt.ActorUserId.ShouldBe(winner.ActorUserId);
        attempt.ClaimToken.ShouldBe(winner.ClaimToken); attempt.ReservedResultSlot.ShouldBeTrue();
        (await verify.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, true)]
    [InlineData(301, true)]
    public async Task Unstarted_claim_restart_reuses_attempt_and_fences_old_token_at_exact_expiry(int seconds, bool expired)
    {
        await SeedAsync(); VetPhotoAdmission input; VetPhotoImageClaim first;
        await using (var seed = Open()) { input = await PreparedAsync(seed); first = await ClaimAsync(seed, input); }
        Clock.UtcNow = Now.AddSeconds(seconds); await using var restarted = Open();
        var resumed = await Store(restarted).ClaimCurrentImageAsync(Scope, input.Source!.Id, input.Input!.Id, 111, Ct);
        resumed.Status.ShouldBe(expired ? VetPhotoImageStatus.Claimed : VetPhotoImageStatus.Busy);
        if (expired)
        {
            var current = resumed.Claim.ShouldNotBeNull(); current.AttemptKey.ShouldBe(first.AttemptKey);
            current.ClaimToken.ShouldNotBe(first.ClaimToken); current.LeaseUntil.ShouldBe(Clock.UtcNow.AddMinutes(5));
            (await Store(restarted).MarkImageDispatchedAsync(Scope, first.AttemptKey, first.ClaimToken, 111, Ct)).ShouldBeFalse();
            (await Store(restarted).MarkImageDispatchedAsync(Scope, current.AttemptKey, current.ClaimToken, 111, Ct)).ShouldBeTrue();
        }
        (await restarted.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image")).ShouldBe(1);
        (await Store(restarted).GetCapacityAsync(Scope, 111, Ct)).ReservedResults.ShouldBe(1);
    }

    [Fact]
    public async Task Expired_dispatched_restart_is_unknown_and_keeps_charged_key_and_result_slot()
    {
        await SeedAsync(); VetPhotoAdmission input; VetPhotoImageClaim first;
        await using (var seed = Open())
        {
            input = await PreparedAsync(seed); first = await ClaimAsync(seed, input);
            (await Store(seed).MarkImageDispatchedAsync(Scope, first.AttemptKey, first.ClaimToken, 111, Ct)).ShouldBeTrue();
        }
        Clock.UtcNow = Now.AddMinutes(5); await using var restarted = Open();
        (await Store(restarted).ClaimCurrentImageAsync(Scope, input.Source!.Id, input.Input!.Id, 111, Ct)).Status.ShouldBe(VetPhotoImageStatus.Unknown);
        (await Store(restarted).ClaimCurrentImageAsync(Scope, input.Source!.Id, input.Input!.Id, 222, Ct)).Status.ShouldBe(VetPhotoImageStatus.Unknown);
        var attempt = await restarted.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Kind == "image");
        attempt.Id.ShouldBe(first.AttemptKey); attempt.ClaimToken.ShouldBe(first.ClaimToken);
        attempt.State.ShouldBe("unknown"); attempt.FailureCategory.ShouldBe("outcome_unknown"); attempt.ReservedResultSlot.ShouldBeTrue();
        (await restarted.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0);
        (await Store(restarted).MarkImageDispatchedAsync(Scope, first.AttemptKey, first.ClaimToken, 111, Ct)).ShouldBeFalse();
        (await Store(restarted).RecordImageFailureAsync(Scope, first.AttemptKey, first.ClaimToken, 111,
            "provider_refused", VetPhotoImageFailureDisposition.KnownNotDispatched, Ct)).ShouldBeFalse();
        (await Store(restarted).GetCapacityAsync(Scope, 111, Ct)).ReservedResults.ShouldBe(1);
    }

    [Fact]
    public async Task Persisted_success_reinstalls_without_a_second_attempt_and_repeated_completion_keeps_exact_evidence()
    {
        await SeedAsync(); VetPhotoAdmission input; VetPhotoImageClaim claim; Guid resultId;
        await using (var seed = Open())
        {
            input = await PreparedAsync(seed); claim = await ClaimAsync(seed, input);
            var returned = await CompleteAsync(seed, claim); returned.Status.ShouldBe(VetPhotoImageStatus.Installed);
            resultId = returned.Extraction!.ShouldNotBeNull().Id;
        }
        await using var restarted = Open();
        // A downstream installation acknowledgement may be lost; reuse durable success, never launch again.
        await restarted.Context.Set<VetPhotoCandidate>().ExecuteUpdateAsync(u => u.SetProperty(c => c.InputRevisionId, (Guid?)null)
            .SetProperty(c => c.ExtractionResultId, (Guid?)null).SetProperty(c => c.State, "waiting"));
        var resumed = await Store(restarted).ClaimCurrentImageAsync(Scope, input.Source!.Id, input.Input!.Id, 222, Ct);
        resumed.Status.ShouldBe(VetPhotoImageStatus.Existing); resumed.Claim.ShouldBeNull(); resumed.Extraction!.Id.ShouldBe(resultId);
        var repeated = await Store(restarted).CompleteImageAsync(Completion(claim, "malformed second response", "different-synthetic-model"), Ct);
        repeated.Status.ShouldBe(VetPhotoImageStatus.Existing); repeated.Extraction!.Id.ShouldBe(resultId);
        repeated.Extraction!.StructuredJson.ShouldBe(ResultJson(claim.SourceId, claim.InputRevisionId));
        repeated.Extraction!.ModelName.ShouldBe("synthetic-model");
        (await restarted.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image")).ShouldBe(1);
        (await restarted.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(1);
        var candidate = await restarted.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync();
        candidate.CandidateOrdinal.ShouldBe(0); candidate.InputRevisionId.ShouldBe(input.Input!.Id);
        candidate.ExtractionResultId.ShouldBe(resultId); candidate.State.ShouldBe("pending");
        candidate.EffectiveJson.ShouldBe("{}"); candidate.ReasonsJson.ShouldBe("[\"awaiting_context\"]");
        (await restarted.Context.VetEvents.CountAsync()).ShouldBe(0);
        (await restarted.Context.VetDiaryActions.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("input")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("oversized_utf8")]
    [InlineData("surrogate")]
    [InlineData("model_length")]
    [InlineData("model_scalar")]
    [InlineData("model_empty")]
    public async Task Invalid_response_is_fixed_bounded_evidence_and_never_a_current_candidate(string invalid)
    {
        await SeedAsync(); await using var s = Open(); var input = await PreparedAsync(s); var claim = await ClaimAsync(s, input);
        (await Store(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
        var json = invalid switch
        {
            "source" => ResultJson(Guid.NewGuid(), claim.InputRevisionId),
            "input" => ResultJson(claim.SourceId, Guid.NewGuid()),
            "malformed" => "not-json synthetic secret sentinel",
            "oversized" => new string('x', 16_385), "surrogate" => "\ud800", _ => ResultJson(claim.SourceId, claim.InputRevisionId)
        };
        if (invalid == "oversized_utf8")
        {
            json = ResultJson(claim.SourceId, claim.InputRevisionId).Replace("\"notes\":null",
                "\"notes\":\"" + new string('\u20ac', 500) + "\"", StringComparison.Ordinal);
            json += new string(' ', 16_385 - Encoding.UTF8.GetByteCount(json));
            json.Length.ShouldBeLessThan(16_384); Encoding.UTF8.GetByteCount(json).ShouldBe(16_385);
        }
        var model = invalid switch { "model_length" => new string('a', 201), "model_scalar" => "\ud800", "model_empty" => "", _ => "synthetic-model" };
        var result = await Store(s).CompleteImageAsync(Completion(claim, json, model), Ct);
        result.Status.ShouldBe(VetPhotoImageStatus.InvalidResult);
        var evidence = result.Extraction!.ShouldNotBeNull(); evidence.StructuredJson.ShouldBe("{}"); evidence.State.ShouldBe("invalid");
        evidence.FailureCategory.ShouldBe(invalid.StartsWith("model", StringComparison.Ordinal) ? "invalid_model_name" : "invalid_result");
        evidence.ModelName.ShouldBe(invalid.StartsWith("model", StringComparison.Ordinal) ? "unknown" : "synthetic-model");
        await using var verify = Open();
        (await verify.Context.Set<VetPhotoExtraction>().SingleAsync()).StructuredJson.ShouldBe("{}");
        var candidate = await verify.Context.Set<VetPhotoCandidate>().SingleAsync();
        candidate.ExtractionResultId.ShouldBeNull(); candidate.State.ShouldBe("waiting");
        (await verify.Context.Set<VetPhotoAttempt>().SingleAsync(a => a.Kind == "image")).ReservedResultSlot.ShouldBeFalse();
        (await Store(verify).ClaimCurrentImageAsync(Scope, input.Source!.Id, input.Input!.Id, 111, Ct)).Claim.ShouldBeNull();
        (await verify.Context.VetEvents.CountAsync()).ShouldBe(0);
        (await verify.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image")).ShouldBe(1);
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("edit")]
    [InlineData("member")]
    [InlineData("place")]
    [InlineData("bot")]
    [InlineData("cancel")]
    [InlineData("original")]
    public async Task Late_owned_result_retains_evidence_but_cannot_install_after_lease_input_or_grant_changes(string stale)
    {
        await SeedAsync(); VetPhotoAdmission input; VetPhotoImageClaim claim;
        await using (var seed = Open())
        {
            input = await PreparedAsync(seed); claim = await ClaimAsync(seed, input);
            (await Store(seed).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
        }
        await using (var changed = Open())
        {
            if (stale == "lease") Clock.UtcNow = Now.AddMinutes(5);
            if (stale == "edit") await CaptionEditAsync(changed, input);
            if (stale == "member") await changed.Context.FamilyMembers.Where(m => m.TelegramUserId == 111)
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied));
            if (stale == "place") await changed.Context.Places.Where(p => p.ChatId == Scope.ChatId && p.TopicId == Scope.TopicId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PlaceStatus.Disabled));
            if (stale == "bot") await changed.Context.Bots.Where(b => b.Id == Scope.BotDbId)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BotStatus.Disabled));
            if (stale == "cancel") await changed.Context.Set<VetPhotoBatch>().ExecuteUpdateAsync(u => u.SetProperty(b => b.State, "cancelled"));
            if (stale == "original") await changed.Context.Set<VetPhotoOriginalReference>().ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "deleted"));
        }
        await using var completion = Open(); var returned = await Store(completion).CompleteImageAsync(Completion(claim), Ct);
        returned.Status.ShouldBe(VetPhotoImageStatus.EvidenceOnly); returned.Extraction!.State.ShouldBe("superseded");
        returned.Extraction!.StructuredJson.ShouldBe(ResultJson(claim.SourceId, claim.InputRevisionId));
        await using var verify = Open();
        (await verify.Context.Set<VetPhotoCandidate>().SingleAsync()).ExtractionResultId.ShouldBeNull();
        (await verify.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(1);
        (await verify.Context.Set<VetPhotoAttempt>().SingleAsync(a => a.Kind == "image")).ReservedResultSlot.ShouldBeFalse();
        (await verify.Context.VetEvents.CountAsync()).ShouldBe(0); (await verify.Context.VetDiaryActions.CountAsync()).ShouldBe(0);
        if (stale is "lease" or "edit" or "cancel" or "original")
            (await Store(verify).InstallCurrentExtractionAsync(Scope, returned.Extraction!.Id, 111, Ct)).Status.ShouldBe(VetPhotoImageStatus.Stale);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("actor")]
    [InlineData("source")]
    [InlineData("input")]
    [InlineData("topic")]
    public async Task Wrong_completion_identity_neither_returns_private_evidence_nor_changes_attempt(string wrong)
    {
        await SeedAsync(); await using var s = Open(); var input = await PreparedAsync(s); var claim = await ClaimAsync(s, input);
        (await Store(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
        var request = Completion(claim);
        request = wrong switch
        {
            "token" => request with { ClaimToken = Guid.NewGuid() }, "actor" => request with { ActorUserId = 222 },
            "source" => request with { SourceId = Guid.NewGuid() }, "input" => request with { InputRevisionId = Guid.NewGuid() },
            _ => request with { Scope = Scope with { TopicId = 8 } }
        };
        var result = await Store(s).CompleteImageAsync(request, Ct);
        result.Status.ShouldBe(VetPhotoImageStatus.Stale); result.Extraction!.ShouldBeNull(); result.Delta!.ShouldBeNull();
        (await s.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0);
        var attempt = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Kind == "image");
        attempt.State.ShouldBe("dispatched"); attempt.ReservedResultSlot.ShouldBeTrue(); attempt.ClaimToken.ShouldBe(claim.ClaimToken);
    }

    [Theory]
    [InlineData("saved")]
    [InlineData("event")]
    [InlineData("linked")]
    [InlineData("excluded")]
    [InlineData("cancelled")]
    [InlineData("deleted")]
    [InlineData("manual")]
    [InlineData("restoration")]
    public async Task Protected_candidate_is_unchanged_and_new_evidence_returns_only_a_proposed_delta(string protection)
    {
        await SeedAsync(); await using var s = Open(); var input = await PreparedAsync(s); var claim = await ClaimAsync(s, input);
        (await Store(s).MarkImageDispatchedAsync(claim.Scope, claim.AttemptKey, claim.ClaimToken, claim.ActorUserId, Ct)).ShouldBeTrue();
        var candidate = await s.Context.Set<VetPhotoCandidate>().SingleAsync();
        if (protection == "event")
        {
            var eventEvidence = await EvidenceAsync(s, id: 44, update: 444);
            (await s.Diary.ApplyAsync(Save(Scope, eventEvidence.Source, eventEvidence.Profile, eventEvidence.State), Ct)).Status.ShouldBe(VetMutationStatus.Applied);
            // Prior store operations may detach snapshots; use the current candidate tracked by this context.
            candidate = await s.Context.Set<VetPhotoCandidate>().SingleAsync();
            candidate.EventId = (await s.Context.VetEvents.SingleAsync()).Id;
            candidate.EventRevision = 1;
        }
        candidate.State = protection is "manual" or "restoration" or "event" ? "pending" : protection;
        candidate.ManuallyCorrected = protection == "manual"; candidate.RequiresExplicitRestoration = protection == "restoration";
        candidate.EffectiveJson = "{\"synthetic_previous\":true}"; candidate.ReasonsJson = "[\"synthetic_reason\"]";
        candidate.CorrectionProvenanceJson = "{\"synthetic_human\":true}"; candidate.DuplicateDecision = "separate";
        await s.Context.SaveChangesAsync();
        var before = JsonSerializer.Serialize(candidate, Json);
        var result = await Store(s).CompleteImageAsync(Completion(claim), Ct); result.Status.ShouldBe(VetPhotoImageStatus.ProposedDelta);
        var delta = result.Delta!.ShouldNotBeNull(); delta.CandidateId.ShouldBe(candidate.Id); delta.ExpectedCandidateRevision.ShouldBe(candidate.Revision);
        delta.ExtractionResultId.ShouldBe(result.Extraction!.Id);
        delta.RequiresExplicitRestoration.ShouldBe(protection is "excluded" or "cancelled" or "deleted" or "restoration");
        await using var verify = Open();
        JsonSerializer.Serialize(await verify.Context.Set<VetPhotoCandidate>().SingleAsync(), Json).ShouldBe(before);
        (await verify.Context.VetEvents.CountAsync()).ShouldBe(protection == "event" ? 1 : 0);
        (await verify.Context.VetDiaryActions.CountAsync()).ShouldBe(protection == "event" ? 1 : 0);
    }

    [Fact]
    public async Task Caption_only_reuse_links_new_input_and_identical_display_without_an_image_attempt_or_insulin_write()
    {
        await SeedAsync(); await using var s = Open(); var original = await PreparedAsync(s);
        var old = (await CompleteAsync(s, await ClaimAsync(s, original))).Extraction!.ShouldNotBeNull();
        var changed = await CaptionEditAsync(s, original);
        var reuse = await Store(s).ReuseDisplayAsync(Scope, original.Source!.Id, changed.Input!.Id, 111, "synthetic-model", Ct);
        reuse.Status.ShouldBe(VetPhotoImageStatus.Reused);
        var linked = reuse.Extraction!.ShouldNotBeNull(); linked.ReusesExtractionId.ShouldBe(old.Id); linked.InputRevisionId.ShouldBe(changed.Input!.Id);
        linked.Id.ShouldNotBe(old.Id); linked.ModelName.ShouldBe(old.ModelName);
        var parsed = VetPhotoInterpretationParser.Parse(linked.StructuredJson, original.Source!.Id, changed.Input!.Id).ShouldNotBeNull();
        parsed.Displays.Single().NumericValue.ShouldBe(5.6m); parsed.Displays.Single().Year.ShouldBe(2031);
        var replay = await Store(s).ReuseDisplayAsync(Scope, original.Source!.Id, changed.Input!.Id, 222, "synthetic-model", Ct);
        replay.Status.ShouldBe(VetPhotoImageStatus.Existing); replay.Extraction!.Id.ShouldBe(linked.Id);
        await using var verify = Open();
        (await verify.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image")).ShouldBe(1);
        (await verify.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "reuse")).ShouldBe(1);
        (await verify.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(2);
        (await verify.Context.Set<VetPhotoBlob>().CountAsync()).ShouldBe(1);
        (await verify.Context.Set<VetPhotoOriginalReference>().CountAsync(r => r.State == "retained")).ShouldBe(2);
        (await verify.Context.Set<VetPhotoSource>().SingleAsync()).CurrentInputRevisionId.ShouldBe(changed.Input!.Id);
        (await verify.Context.VetEvents.CountAsync()).ShouldBe(0); (await verify.Context.VetDiaryActions.CountAsync()).ShouldBe(0);
        (await Store(verify).GetCapacityAsync(Scope, 111, Ct)).ShouldBe(new(Original.Length, 0, 2, 0, 2, 0));
    }

    [Theory]
    [InlineData("capacity")]
    [InlineData("model")]
    [InlineData("deleted_reference")]
    public async Task Reuse_requires_unchanged_model_retained_reference_and_an_available_global_result_slot(string unavailable)
    {
        await SeedAsync(); await using var s = Open(); var original = await PreparedAsync(s);
        await CompleteAsync(s, await ClaimAsync(s, original)); var changed = await CaptionEditAsync(s, original);
        if (unavailable == "deleted_reference") await s.Context.Set<VetPhotoOriginalReference>().Where(r => r.InputRevisionId == original.Input!.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "deleted"));
        var capacity = unavailable == "capacity" ? new VetPhotoCapacity(MaxResults: 1) : new VetPhotoCapacity();
        var result = await Store(s, capacity).ReuseDisplayAsync(Scope, original.Source!.Id, changed.Input!.Id, 111,
            unavailable == "model" ? "different-synthetic-model" : "synthetic-model", Ct);
        result.Status.ShouldBe(unavailable == "capacity" ? VetPhotoImageStatus.CapacityFull : VetPhotoImageStatus.NotFound);
        result.Extraction!.ShouldBeNull(); result.Claim.ShouldBeNull();
        (await s.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(1);
        (await s.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "reuse")).ShouldBe(0);
        (await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync()).ExtractionResultId.ShouldNotBeNull();
    }

    [Fact]
    public async Task Final_candidate_write_failure_rolls_back_result_attempt_review_and_same_context_retry_succeeds()
    {
        await SeedAsync(); VetPhotoAdmission input; VetPhotoImageClaim claim; int reviewRevision;
        await using (var seed = Open())
        {
            input = await PreparedAsync(seed); claim = await ClaimAsync(seed, input);
            (await Store(seed).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
            reviewRevision = (await seed.Context.Set<VetPhotoBatch>().SingleAsync()).ReviewRevision;
        }
        var failure = new CandidateWriteFailure(); await using var failing = Open(interceptor: failure);
        await Should.ThrowAsync<DbUpdateException>(() => Store(failing).CompleteImageAsync(Completion(claim), Ct));
        failure.Hits.ShouldBe(1);
        await using (var verify = Open())
        {
            (await verify.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0);
            var attempt = await verify.Context.Set<VetPhotoAttempt>().SingleAsync(a => a.Kind == "image");
            attempt.State.ShouldBe("dispatched"); attempt.ReservedResultSlot.ShouldBeTrue(); attempt.ExtractionResultId.ShouldBeNull();
            var candidate = await verify.Context.Set<VetPhotoCandidate>().SingleAsync(); candidate.State.ShouldBe("waiting"); candidate.ExtractionResultId.ShouldBeNull();
            (await verify.Context.Set<VetPhotoBatch>().SingleAsync()).ReviewRevision.ShouldBe(reviewRevision);
        }
        var retry = await Store(failing).CompleteImageAsync(Completion(claim), Ct); retry.Status.ShouldBe(VetPhotoImageStatus.Installed);
        await using var after = Open();
        (await after.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(1);
        (await after.Context.Set<VetPhotoCandidate>().SingleAsync()).ExtractionResultId.ShouldBe(retry.Extraction!.Id);
        (await after.Context.Set<VetPhotoBatch>().SingleAsync()).ReviewRevision.ShouldBe(reviewRevision + 1);
    }

    [Fact]
    public async Task Exact_16KiB_response_and_200_character_model_name_are_retained_without_truncation()
    {
        await SeedAsync(); await using var s = Open(); var input = await PreparedAsync(s); var claim = await ClaimAsync(s, input);
        (await Store(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
        var json = ResultJson(claim.SourceId, claim.InputRevisionId);
        json += new string(' ', 16_384 - Encoding.UTF8.GetByteCount(json));
        var model = new string('a', 200);
        var result = await Store(s).CompleteImageAsync(Completion(claim, json, model), Ct);
        result.Status.ShouldBe(VetPhotoImageStatus.Installed);
        await using var verify = Open(); var persisted = await verify.Context.Set<VetPhotoExtraction>().SingleAsync();
        persisted.StructuredJson.ShouldBe(json); Encoding.UTF8.GetByteCount(persisted.StructuredJson).ShouldBe(16_384);
        persisted.ModelName.ShouldBe(model); persisted.FailureCategory.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_failure_disposition_preserves_unknown_reservation_or_releases_known_unlaunched_work(bool unknown)
    {
        await SeedAsync(); await using var s = Open(); var input = await PreparedAsync(s); var claim = await ClaimAsync(s, input);
        if (unknown) (await Store(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
        (await Store(s).RecordImageFailureAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111,
            unknown ? "outcome_unknown" : "provider_refused", unknown ? VetPhotoImageFailureDisposition.OutcomeUnknown
                : VetPhotoImageFailureDisposition.KnownNotDispatched, Ct)).ShouldBeTrue();
        await using var verify = Open(); var attempt = await verify.Context.Set<VetPhotoAttempt>().SingleAsync(a => a.Kind == "image");
        attempt.State.ShouldBe(unknown ? "unknown" : "failed"); attempt.ReservedResultSlot.ShouldBe(unknown);
        attempt.Id.ShouldBe(claim.AttemptKey); attempt.ExtractionResultId.ShouldBeNull();
        (await Store(verify).ClaimCurrentImageAsync(Scope, input.Source!.Id, input.Input!.Id, 111, Ct)).Status
            .ShouldBe(unknown ? VetPhotoImageStatus.Unknown : VetPhotoImageStatus.Refused);
        (await verify.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image")).ShouldBe(1);
    }

    [Fact]
    public async Task Raw_failure_category_is_refused_without_persisting_it_or_changing_the_live_claim()
    {
        await SeedAsync(); await using var s = Open(); var input = await PreparedAsync(s); var claim = await ClaimAsync(s, input);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => Store(s).RecordImageFailureAsync(Scope,
            claim.AttemptKey, claim.ClaimToken, 111, "synthetic raw provider output", VetPhotoImageFailureDisposition.OutcomeUnknown, Ct));
        error.Message.ShouldBe("Photo image failure is invalid.");
        var attempt = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Kind == "image");
        attempt.State.ShouldBe("claimed"); attempt.FailureCategory.ShouldBeNull(); attempt.ReservedResultSlot.ShouldBeTrue();
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("member")]
    [InlineData("original")]
    public async Task Changes_before_dispatch_cannot_mark_a_provider_call_and_known_unlaunched_cleanup_releases_the_slot(string change)
    {
        await SeedAsync(); await using var s = Open(); var input = await PreparedAsync(s); var claim = await ClaimAsync(s, input);
        if (change == "edit") await CaptionEditAsync(s, input);
        if (change == "member") await s.Context.FamilyMembers.Where(m => m.TelegramUserId == 111)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, FamilyMemberStatus.Denied));
        if (change == "original") await s.Context.Set<VetPhotoOriginalReference>().ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "deleted"));
        (await Store(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeFalse();
        var attempt = await s.Context.Set<VetPhotoAttempt>().AsNoTracking().SingleAsync(a => a.Kind == "image");
        attempt.State.ShouldBe("claimed");
        (await Store(s).RecordImageFailureAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111,
            "provider_cancelled", VetPhotoImageFailureDisposition.KnownNotDispatched, Ct)).ShouldBeTrue();
        await using var verify = Open();
        (await verify.Context.Set<VetPhotoAttempt>().SingleAsync(a => a.Kind == "image")).ReservedResultSlot.ShouldBeFalse();
        (await verify.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0); (await verify.Context.VetEvents.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Unset_or_wrong_current_family_fails_closed_and_foreign_scope_cannot_find_stored_success()
    {
        await SeedAsync(); var other = await OtherFamilyAsync(); VetPhotoAdmission input;
        await using (var seed = Open()) { input = await PreparedAsync(seed); await CompleteAsync(seed, await ClaimAsync(seed, input)); }
        await using var unset = Unscoped(); await using var foreign = Open(other.FamilyId);
        var unscoped = await Should.ThrowAsync<InvalidOperationException>(() => Store(unset).ClaimCurrentImageAsync(Scope,
            input.Source!.Id, input.Input!.Id, 111, Ct)); unscoped.Message.ShouldBe("Vet scope is not active.");
        var wrong = await Should.ThrowAsync<InvalidOperationException>(() => Store(foreign).ClaimCurrentImageAsync(Scope,
            input.Source!.Id, input.Input!.Id, 111, Ct)); wrong.Message.ShouldBe("Vet scope is not active.");
        var missing = await Store(foreign).ClaimCurrentImageAsync(other, input.Source!.Id, input.Input!.Id, 333, Ct);
        missing.Status.ShouldBe(VetPhotoImageStatus.NotFound); missing.Extraction!.ShouldBeNull(); missing.Claim.ShouldBeNull();
        (await foreign.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0);
        (await foreign.Context.Set<VetPhotoCandidate>().CountAsync()).ShouldBe(0);
        (await unset.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 9)]
    [InlineData(true, 1)]
    [InlineData(true, 9)]
    public async Task Image_claim_with_submicrosecond_clock_reads_retained_original_after_database_roundtrip(bool scheduled, int ticks)
    {
        await SeedAsync();
        VetPhotoImageClaim claim;
        await using (var seed = Open())
        {
            var input = await PreparedAsync(seed);
            var queued = scheduled ? await ScheduleAsync(seed, input) : null;
            Clock.UtcNow = Now.AddTicks(ticks);
            var result = scheduled
                ? await Store(seed).ClaimScheduledImageAsync(Scope, queued!.Attempt.Id, 111, Ct)
                : await Store(seed).ClaimCurrentImageAsync(Scope, input.Source!.Id, input.Input!.Id, 111, Ct);
            result.Status.ShouldBe(VetPhotoImageStatus.Claimed);
            claim = result.Claim.ShouldNotBeNull();
        }
        await using var reader = Open();
        var read = await Store(reader).ReadOriginalAsync(Scope, claim.InputRevisionId, 111,
            claim.AttemptKey, claim.ClaimToken, claim.LeaseUntil, Ct);
        read.ShouldNotBeNull().Original.ShouldBe(Original);
        var persisted = await reader.Context.Set<VetPhotoAttempt>().AsNoTracking()
            .SingleAsync(a => a.Id == claim.AttemptKey);
        persisted.LeaseUntil.ShouldBe(claim.LeaseUntil);
        var readerLease = await reader.Context.Set<VetPhotoReaderLease>().AsNoTracking().SingleAsync();
        readerLease.Id.ShouldBe(read!.ReaderLeaseId);
        readerLease.AttemptId.ShouldBe(claim.AttemptKey);
        readerLease.ClaimToken.ShouldBe(claim.ClaimToken);
        readerLease.ExpiresAt.ShouldBe(claim.LeaseUntil);
    }

    [Fact]
    public async Task Image_original_read_preserves_exact_claim_ownership_and_expiry_guards()
    {
        await SeedAsync();
        VetPhotoImageClaim claim;
        await using (var seed = Open())
        {
            var input = await PreparedAsync(seed);
            Clock.UtcNow = Now.AddTicks(1);
            claim = (await Store(seed).ClaimCurrentImageAsync(Scope, input.Source!.Id,
                input.Input!.Id, 111, Ct)).Claim.ShouldNotBeNull();
        }
        await using var reader = Open();
        var store = Store(reader);
        (await store.ReadOriginalAsync(Scope, claim.InputRevisionId, 111, claim.AttemptKey,
            claim.ClaimToken, claim.LeaseUntil.AddTicks(1), Ct)).ShouldBeNull();
        (await store.ReadOriginalAsync(Scope, claim.InputRevisionId, 111, claim.AttemptKey,
            Guid.NewGuid(), claim.LeaseUntil, Ct)).ShouldBeNull();
        (await store.ReadOriginalAsync(Scope, claim.InputRevisionId, 222, claim.AttemptKey,
            claim.ClaimToken, claim.LeaseUntil, Ct)).ShouldBeNull();
        (await store.ReadOriginalAsync(Scope, Guid.NewGuid(), 111, claim.AttemptKey,
            claim.ClaimToken, claim.LeaseUntil, Ct)).ShouldBeNull();
        (await reader.Context.Set<VetPhotoReaderLease>().CountAsync()).ShouldBe(0);
        Clock.UtcNow = claim.LeaseUntil.AddTicks(-1);
        (await store.ReadOriginalAsync(Scope, claim.InputRevisionId, 111, claim.AttemptKey,
            claim.ClaimToken, claim.LeaseUntil, Ct)).ShouldNotBeNull().Original.ShouldBe(Original);
        Clock.UtcNow = claim.LeaseUntil;
        (await store.ReadOriginalAsync(Scope, claim.InputRevisionId, 111, claim.AttemptKey,
            claim.ClaimToken, claim.LeaseUntil, Ct)).ShouldBeNull();
        (await reader.Context.Set<VetPhotoReaderLease>().CountAsync()).ShouldBe(1);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private T ScopedFixture<T>(VetTestSession s, T value) where T : class
    {
        var entry = s.Context.Entry(value);
        entry.Property("FamilyId").CurrentValue = Scope.FamilyId; entry.Property("BotDbId").CurrentValue = Scope.BotDbId;
        entry.Property("TelegramBotId").CurrentValue = Scope.TelegramBotId; entry.Property("ChatId").CurrentValue = Scope.ChatId;
        entry.Property("TopicId").CurrentValue = Scope.TopicId; return value;
    }
    private sealed record Schedule(VetPhotoAttempt Attempt, VetPhotoRun Run, VetPhotoRunWindow Window, VetPhotoReview Review);
    private async Task<Schedule> ScheduleAsync(VetTestSession s, VetPhotoAdmission selected, bool historical = false)
    {
        if (historical) await CaptionEditAsync(s, selected);
        var source = (await Store(s).GetSourceAsync(Scope, selected.Source!.Id, Ct)).ShouldNotBeNull().Source!.ShouldNotBeNull();
        var reference = await s.Context.Set<VetPhotoOriginalReference>().SingleAsync(r => r.InputRevisionId == selected.Input!.Id);
        var candidate = await s.Context.Set<VetPhotoCandidate>().SingleAsync();
        var key = Guid.NewGuid();
        var selection = JsonSerializer.Serialize(new[] { new VetPhotoRunInputSnapshot(source.Id, selected.Input!.Id, reference.Id,
            reference.Revision, source.CurrentInputRevisionId, source.CurrentOrdinal, candidate.Revision, key) }, Json);
        const string page = "synthetic re-extraction selection preview";
        var profile = await s.Context.Set<Assistant.Domain.Vet.VetProfile>().AsNoTracking()
            .SingleAsync(p => p.FamilyId == Scope.FamilyId && p.BotDbId == Scope.BotDbId);
        var review = ScopedFixture(s, new VetPhotoReview
        {
            ProfileId = profile.Id, ProfileRevision = profile.Revision,
            Id = Guid.NewGuid(), BatchId = source.BatchId, OperationKey = Guid.NewGuid(), Kind = "reextract_selection",
            State = "accepted", RequesterUserId = 111, DecisionActorUserId = 111, SelectionJson = selection,
            Fingerprint = Hash(selection), PreviewPagesJson = JsonSerializer.Serialize(new[] { page }, Json),
            DeliveredPagesJson = JsonSerializer.Serialize(new[] { new VetPhotoPageDelivery(0, 700, Hash(page)) }, Json),
            PageCount = 1, CompletePreviewDelivered = true, AcceptancePromptMessageId = 700, CreatedAt = Now, DecidedAt = Now
        });
        s.Context.Add(review); await s.Context.SaveChangesAsync();
        var run = ScopedFixture(s, new VetPhotoRun
        {
            Id = Guid.NewGuid(), ActorUserId = 111, OperationKey = Guid.NewGuid(), SelectionReviewId = review.Id,
            SelectionMode = historical ? "all" : "current", SelectionJson = selection, SelectedCount = 1,
            ModelName = "synthetic-model", State = "approved", CreatedAt = Now
        });
        s.Context.Add(run); await s.Context.SaveChangesAsync();
        var window = ScopedFixture(s, new VetPhotoRunWindow
        { Id = Guid.NewGuid(), RunId = run.Id, State = "queued", SelectionJson = selection, CreatedAt = Now });
        s.Context.Add(window); await s.Context.SaveChangesAsync();
        var attempt = ScopedFixture(s, new VetPhotoAttempt
        {
            Id = key, SourceId = source.Id, InputRevisionId = selected.Input!.Id, RunWindowId = window.Id, ActorUserId = 111,
            Kind = "image", State = "queued", ExpectedCurrentInputId = source.CurrentInputRevisionId,
            ExpectedSourceOrdinal = source.CurrentOrdinal, HistoricalSelection = historical, CreatedAt = Now, UpdatedAt = Now
        });
        s.Context.Add(attempt); await s.Context.SaveChangesAsync(); return new(attempt, run, window, review);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approved_scheduled_current_or_historical_result_is_comparison_only_and_never_changes_candidate_or_pointer(bool historical)
    {
        await SeedAsync(); await using var s = Open(); var input = await PreparedAsync(s); var scheduled = await ScheduleAsync(s, input, historical);
        var sourceBefore = (await s.Context.Set<VetPhotoSource>().AsNoTracking().SingleAsync()).CurrentInputRevisionId;
        var candidateBefore = JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(), Json);
        var claimed = await Store(s).ClaimScheduledImageAsync(Scope, scheduled.Attempt.Id, 111, Ct);
        claimed.Status.ShouldBe(VetPhotoImageStatus.Claimed); var claim = claimed.Claim.ShouldNotBeNull();
        claim.AttemptKey.ShouldBe(scheduled.Attempt.Id); claim.InputRevisionId.ShouldBe(input.Input!.Id);
        claim.HistoricalSelection.ShouldBe(historical); claim.ExpectedCurrentInputId.ShouldBe(sourceBefore);
        (await Store(s).ReadOriginalAsync(Scope, claim.InputRevisionId, 111, claim.AttemptKey, claim.ClaimToken, claim.LeaseUntil, Ct)).ShouldNotBeNull().Original.ShouldBe(Original);
        (await Store(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
        var completed = await Store(s).CompleteImageAsync(Completion(claim), Ct);
        completed.Status.ShouldBe(VetPhotoImageStatus.ProposedDelta); completed.Extraction!.State.ShouldBe("comparison");
        completed.Delta!.InputRevisionId.ShouldBe(input.Input!.Id);
        (await Store(s).ClaimScheduledImageAsync(Scope, scheduled.Attempt.Id, 111, Ct)).Status.ShouldBe(VetPhotoImageStatus.Existing);
        (await Store(s).InstallCurrentExtractionAsync(Scope, completed.Extraction!.Id, 111, Ct)).Status.ShouldBe(VetPhotoImageStatus.Stale);
        await using var verify = Open();
        (await verify.Context.Set<VetPhotoSource>().SingleAsync()).CurrentInputRevisionId.ShouldBe(sourceBefore);
        JsonSerializer.Serialize(await verify.Context.Set<VetPhotoCandidate>().SingleAsync(), Json).ShouldBe(candidateBefore);
        (await verify.Context.Set<VetPhotoAttempt>().CountAsync(a => a.Kind == "image")).ShouldBe(1);
        (await verify.Context.VetEvents.CountAsync()).ShouldBe(0); (await verify.Context.VetDiaryActions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Approved_scheduled_cancelled_batch_comparison_preserves_restoration_identity_and_writes_no_facts()
    {
        await SeedAsync(); await using var s = Open(); var input = await PreparedAsync(s);
        var candidate = await s.Context.Set<VetPhotoCandidate>().SingleAsync();
        candidate.State = "cancelled"; candidate.RequiresExplicitRestoration = true; candidate.Revision++;
        candidate.CorrectionProvenanceJson = "{\"synthetic_closed_identity\":true}";
        await s.Context.SaveChangesAsync();
        await s.Context.Set<VetPhotoBatch>().ExecuteUpdateAsync(u => u.SetProperty(b => b.State, "cancelled"));
        var scheduled = await ScheduleAsync(s, input);
        var before = JsonSerializer.Serialize(await s.Context.Set<VetPhotoCandidate>().AsNoTracking().SingleAsync(), Json);
        var claimed = await Store(s).ClaimScheduledImageAsync(Scope, scheduled.Attempt.Id, 111, Ct);
        claimed.Status.ShouldBe(VetPhotoImageStatus.Claimed); var claim = claimed.Claim.ShouldNotBeNull();
        (await Store(s).MarkImageDispatchedAsync(Scope, claim.AttemptKey, claim.ClaimToken, 111, Ct)).ShouldBeTrue();
        var result = await Store(s).CompleteImageAsync(Completion(claim), Ct);
        result.Status.ShouldBe(VetPhotoImageStatus.ProposedDelta); result.Extraction!.State.ShouldBe("comparison");
        result.Delta.ShouldNotBeNull().RequiresExplicitRestoration.ShouldBeTrue();
        await using var verify = Open();
        JsonSerializer.Serialize(await verify.Context.Set<VetPhotoCandidate>().SingleAsync(), Json).ShouldBe(before);
        (await verify.Context.Set<VetPhotoBatch>().SingleAsync()).State.ShouldBe("cancelled");
        (await verify.Context.Set<VetPhotoSource>().SingleAsync()).CurrentInputRevisionId.ShouldBe(input.Input!.Id);
        (await verify.Context.VetEvents.CountAsync()).ShouldBe(0); (await verify.Context.VetDiaryActions.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("unseen")]
    [InlineData("partial")]
    [InlineData("prompt")]
    [InlineData("decision_actor")]
    [InlineData("kind")]
    [InlineData("cancelled_run")]
    [InlineData("run_selection")]
    [InlineData("window_selection")]
    [InlineData("reference_revision")]
    [InlineData("candidate_revision")]
    [InlineData("pointer")]
    [InlineData("window_state")]
    [InlineData("attempt_actor")]
    public async Task Scheduled_claim_requires_delivered_accepted_exact_snapshot_and_current_grants(string invalid)
    {
        await SeedAsync(); await using var s = Open(); var input = await PreparedAsync(s); var scheduled = await ScheduleAsync(s, input);
        if (invalid == "unseen") scheduled.Review.CompletePreviewDelivered = false;
        if (invalid == "partial") scheduled.Review.DeliveredPagesJson = "[]";
        if (invalid == "prompt") scheduled.Review.AcceptancePromptMessageId = 701;
        if (invalid == "decision_actor") scheduled.Review.DecisionActorUserId = 222;
        if (invalid == "kind") scheduled.Review.Kind = "original_delete";
        if (invalid == "cancelled_run") scheduled.Run.CancelledAt = Now;
        if (invalid == "run_selection") scheduled.Run.SelectionJson = "[]";
        if (invalid == "window_selection") scheduled.Window.SelectionJson = "[]";
        if (invalid == "reference_revision") (await s.Context.Set<VetPhotoOriginalReference>().SingleAsync()).Revision++;
        if (invalid == "candidate_revision") (await s.Context.Set<VetPhotoCandidate>().SingleAsync()).Revision++;
        if (invalid == "window_state") scheduled.Window.State = "paused";
        if (invalid == "attempt_actor") scheduled.Attempt.ActorUserId = 222;
        await s.Context.SaveChangesAsync();
        if (invalid == "pointer") await CaptionEditAsync(s, input);
        var result = await Store(s).ClaimScheduledImageAsync(Scope, scheduled.Attempt.Id, 111, Ct);
        result.Status.ShouldBe(invalid == "attempt_actor" ? VetPhotoImageStatus.NotFound : VetPhotoImageStatus.Stale);
        result.Claim.ShouldBeNull(); result.Extraction!.ShouldBeNull();
        await using var verify = Open();
        var attempt = await verify.Context.Set<VetPhotoAttempt>().SingleAsync(a => a.Kind == "image");
        attempt.State.ShouldBe("queued"); attempt.ReservedResultSlot.ShouldBeFalse(); attempt.ClaimToken.ShouldBeNull();
        (await verify.Context.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0);
    }

    private sealed class HoldImageInsert : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult(true);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Replace("\"", "", StringComparison.Ordinal).Contains("INSERT INTO vet_photo_attempts", StringComparison.OrdinalIgnoreCase)
                && command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, "image")))
            { Entered.TrySetResult(true); await _release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }
    private sealed class ObserveCapacity : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("vet-photo-capacity-v1", StringComparison.Ordinal)) Entered.TrySetResult(true);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Replace("\"", "", StringComparison.Ordinal).Contains("INSERT INTO vet_photo_attempts", StringComparison.OrdinalIgnoreCase)
                && command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, "image"))) Entered.TrySetResult(true);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CandidateWriteFailure : DbCommandInterceptor
    {
        public int Hits { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Hits == 0 && command.CommandText.Replace("\"", "", StringComparison.Ordinal).Contains("UPDATE vet_photo_candidates", StringComparison.OrdinalIgnoreCase))
            { Hits++; throw new InvalidOperationException("synthetic candidate persistence failure"); }
            return ValueTask.FromResult(result);
        }
    }
    private static byte[] ImageBytes()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(32, 24, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.White); using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
    }
}
