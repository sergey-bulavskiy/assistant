using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Bots;
using Assistant.Domain.Messages;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore : IVetPhotoExtractionStore
{
    public Task<VetPhotoImageResult> ClaimCurrentImageAsync(VetDiaryScope scope, Guid sourceId,
        Guid inputRevisionId, long actorUserId, CancellationToken ct) =>
        ImageTransactionAsync(scope, async () =>
        {
            if (!await ImageActorAsync(scope, actorUserId, ct)) return new(VetPhotoImageStatus.Refused);
            var source = await ImageSourceAsync(scope, sourceId, ct);
            if (source is null) return new(VetPhotoImageStatus.NotFound);
            if (source.CurrentInputRevisionId != inputRevisionId || !await CurrentOriginalAsync(scope, source, inputRevisionId, ct))
                return new(VetPhotoImageStatus.Stale);
            var attempt = await Scoped<VetPhotoAttempt>(scope).SingleOrDefaultAsync(a =>
                a.SourceId == sourceId && a.InputRevisionId == inputRevisionId && a.Kind == "image" && a.RunWindowId == null, ct);
            if (attempt is null)
            {
                var reused = await Scoped<VetPhotoExtraction>(scope).AsNoTracking().Where(e =>
                    e.InputRevisionId == inputRevisionId && e.SourceId == sourceId && e.State == "returned")
                    .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).FirstOrDefaultAsync(ct);
                if (reused is not null) return await ExistingImageAsync(scope, reused, actorUserId, ct);
                attempt = InScope(new VetPhotoAttempt
                {
                    Id = Guid.NewGuid(), SourceId = sourceId, InputRevisionId = inputRevisionId,
                    ActorUserId = actorUserId, Kind = "image", State = "queued",
                    ExpectedSourceOrdinal = source.CurrentOrdinal, ExpectedCurrentInputId = inputRevisionId,
                    CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow
                }, scope);
                db.Add(attempt);
            }
            return await ClaimImageLockedAsync(scope, source, attempt, actorUserId, ct);
        }, ct);

    public Task<VetPhotoImageResult> ClaimScheduledImageAsync(VetDiaryScope scope, Guid attemptKey,
        long actorUserId, CancellationToken ct) => ImageTransactionAsync(scope, async () =>
    {
        if (!await ImageActorAsync(scope, actorUserId, ct)) return new(VetPhotoImageStatus.Refused);
        var attempt = await Scoped<VetPhotoAttempt>(scope).SingleOrDefaultAsync(a =>
            a.Id == attemptKey && a.Kind == "image" && a.RunWindowId != null && a.ActorUserId == actorUserId, ct);
        if (attempt is null) return new(VetPhotoImageStatus.NotFound);
        var source = await ImageSourceAsync(scope, attempt.SourceId, ct);
        if (source is null || !await ScheduledSelectionAsync(scope, source, attempt, actorUserId, ct))
            return new(VetPhotoImageStatus.Stale);
        return await ClaimImageLockedAsync(scope, source, attempt, actorUserId, ct);
    }, ct);

    private async Task<VetPhotoImageResult> ClaimImageLockedAsync(VetDiaryScope scope,
        VetPhotoSource source, VetPhotoAttempt attempt, long actor, CancellationToken ct)
    {
        var stored = await Scoped<VetPhotoExtraction>(scope).AsNoTracking().SingleOrDefaultAsync(e => e.AttemptId == attempt.Id, ct);
        if (stored is not null) return await ExistingImageAsync(scope, stored, actor, ct);
        if (attempt.State == "dispatched" && attempt.LeaseUntil <= clock.UtcNow)
        {
            attempt.State = "unknown";
            attempt.FailureCategory = "outcome_unknown";
            attempt.UpdatedAt = clock.UtcNow;
        }
        if (attempt.State is "dispatched" or "unknown") return new(VetPhotoImageStatus.Unknown);
        if (attempt.State == "claimed" && attempt.LeaseUntil > clock.UtcNow) return new(VetPhotoImageStatus.Busy);
        if (attempt.State is not ("queued" or "claimed")) return new(VetPhotoImageStatus.Refused);
        if (source.CurrentOrdinal != attempt.ExpectedSourceOrdinal || source.CurrentInputRevisionId != attempt.ExpectedCurrentInputId)
            return new(VetPhotoImageStatus.Stale);
        var otherAttemptStatus = await OtherImageAttemptGuardAsync(scope, source, attempt, actor, ct);
        if (otherAttemptStatus is { } blockedStatus) return new(blockedStatus);
        var totals = await TotalsLockedAsync(ct);
        if (!attempt.ReservedResultSlot && !totals.CanReserve(capacity, 0, false, true))
        {
            if (db.Entry(attempt).State == EntityState.Added) db.Entry(attempt).State = EntityState.Detached;
            return new(VetPhotoImageStatus.CapacityFull);
        }
        attempt.ActorUserId = actor;
        attempt.ClaimToken = Guid.NewGuid();
        attempt.LeaseUntil = clock.UtcNow.AddMinutes(5);
        attempt.ReservedResultSlot = true;
        attempt.State = "claimed";
        attempt.UpdatedAt = clock.UtcNow;
        return new(VetPhotoImageStatus.Claimed, ImageClaim(scope, attempt));
    }

    public async Task<bool> MarkImageDispatchedAsync(VetDiaryScope scope, Guid attemptKey,
        Guid claimToken, long actorUserId, CancellationToken ct)
    {
        var outcome = await ImageTransactionAsync(scope, async () =>
        {
            if (!await ImageActorAsync(scope, actorUserId, ct)) return new(VetPhotoImageStatus.Refused);
            var attempt = await OwnedImageAttemptAsync(scope, attemptKey, claimToken, actorUserId, ct);
            if (attempt is null || attempt.State != "claimed" || attempt.LeaseUntil is not { } lease || lease <= clock.UtcNow || !attempt.ReservedResultSlot)
                return new(VetPhotoImageStatus.Stale);
            var source = await ImageSourceAsync(scope, attempt.SourceId, ct);
            if (source is null || source.CurrentOrdinal != attempt.ExpectedSourceOrdinal
                || source.CurrentInputRevisionId != attempt.ExpectedCurrentInputId
                || !await RetainedInputAsync(scope, source.Id, attempt.InputRevisionId, ct)
                || attempt.RunWindowId == null && !await CurrentOriginalAsync(scope, source, attempt.InputRevisionId, ct)
                || attempt.RunWindowId != null && !await ScheduledSelectionAsync(scope, source, attempt, actorUserId, ct))
                return new(VetPhotoImageStatus.Stale);
            var otherAttemptStatus = await OtherImageAttemptGuardAsync(scope, source, attempt, actorUserId, ct);
            if (otherAttemptStatus is { } blockedStatus) return new(blockedStatus);
            attempt.State = "dispatched";
            attempt.UpdatedAt = clock.UtcNow;
            return new(VetPhotoImageStatus.Existing);
        }, ct);
        return outcome.Status == VetPhotoImageStatus.Existing;
    }

    public Task<VetPhotoImageResult> CompleteImageAsync(VetPhotoImageCompletion completion, CancellationToken ct) =>
        ImageTransactionAsync(completion.Scope, async () =>
        {
            var attempt = await OwnedImageAttemptAsync(completion.Scope, completion.AttemptKey,
                completion.ClaimToken, completion.ActorUserId, ct);
            if (attempt is null || attempt.SourceId != completion.SourceId || attempt.InputRevisionId != completion.InputRevisionId)
                return new(VetPhotoImageStatus.Stale);
            var prior = await Scoped<VetPhotoExtraction>(completion.Scope).AsNoTracking().SingleOrDefaultAsync(e => e.AttemptId == attempt.Id, ct);
            if (prior is not null) return new(VetPhotoImageStatus.Existing, Extraction: prior);
            if (attempt.State is not ("dispatched" or "unknown") || !attempt.ReservedResultSlot)
                return new(VetPhotoImageStatus.Stale);
            var source = await ImageSourceAsync(completion.Scope, completion.SourceId, ct);
            if (source is null) return new(VetPhotoImageStatus.NotFound);
            var validName = completion.ModelName is { Length: > 0 and <= 200 } && ScalarText(completion.ModelName);
            var validPrompt = completion.PromptVersion is { Length: > 0 and <= 30 } && ScalarText(completion.PromptVersion);
            var parsed = validName && validPrompt && completion.StructuredJson is not null
                ? VetPhotoInterpretationParser.Parse(completion.StructuredJson, source.Id, attempt.InputRevisionId) : null;
            var live = attempt.RunWindowId == null && attempt.LeaseUntil > clock.UtcNow && attempt.State == "dispatched"
                && source.CurrentInputRevisionId == attempt.InputRevisionId
                && source.CurrentOrdinal == attempt.ExpectedSourceOrdinal && source.CurrentInputRevisionId == attempt.ExpectedCurrentInputId
                && await ImageActorAsync(completion.Scope, completion.ActorUserId, ct)
                && await BatchOpenAsync(completion.Scope, source, ct)
                && await RetainedInputAsync(completion.Scope, source.Id, attempt.InputRevisionId, ct);
            var extraction = InScope(new VetPhotoExtraction
            {
                Id = Guid.NewGuid(), SourceId = source.Id, InputRevisionId = attempt.InputRevisionId,
                AttemptId = attempt.Id, ModelName = validName ? completion.ModelName : "unknown",
                PromptVersion = validPrompt ? completion.PromptVersion : "photo-v1",
                StructuredJson = parsed is null ? "{}" : completion.StructuredJson!,
                State = parsed is null ? "invalid" : attempt.RunWindowId != null ? "comparison" : live ? "returned" : "superseded",
                FailureCategory = parsed is null ? !validName ? "invalid_model_name" : !validPrompt ? "invalid_prompt_version" : "invalid_result" : null,
                DiagnosticAttemptId = completion.DiagnosticAttemptId, CreatedAt = clock.UtcNow
            }, completion.Scope);
            db.Add(extraction);
            attempt.ExtractionResultId = extraction.Id;
            attempt.State = parsed is null ? "failed" : "returned";
            attempt.FailureCategory = extraction.FailureCategory;
            attempt.ReservedResultSlot = false;
            attempt.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct); // The candidate FK references durable evidence in this same transaction.
            await InvalidateBatchAsync(completion.Scope, source.BatchId, ct);
            if (parsed is null) return new(VetPhotoImageStatus.InvalidResult, Extraction: extraction);
            if (attempt.RunWindowId != null && await ImageActorAsync(completion.Scope, completion.ActorUserId, ct))
            {
                var candidate = await Scoped<VetPhotoCandidate>(completion.Scope).AsNoTracking()
                    .SingleAsync(c => c.SourceId == source.Id && c.CandidateOrdinal == 0, ct);
                return new(VetPhotoImageStatus.ProposedDelta, Extraction: extraction,
                    Delta: new(candidate.Id, candidate.Revision, source.Id, extraction.InputRevisionId, extraction.Id,
                        candidate.RequiresExplicitRestoration || candidate.State is "excluded" or "cancelled" or "deleted"));
            }
            return live ? await InstallImageLockedAsync(completion.Scope, source, extraction, ct)
                : new(VetPhotoImageStatus.EvidenceOnly, Extraction: extraction);
        }, ct);

    public async Task<bool> RecordImageFailureAsync(VetDiaryScope scope, Guid attemptKey, Guid claimToken,
        long actorUserId, string category, VetPhotoImageFailureDisposition disposition, CancellationToken ct)
    {
        if (category is not ("provider_unavailable" or "provider_refused" or "provider_cancelled" or "outcome_unknown")
            || !Enum.IsDefined(disposition)) throw new InvalidOperationException("Photo image failure is invalid.");
        var outcome = await ImageTransactionAsync(scope, async () =>
        {
            var attempt = await OwnedImageAttemptAsync(scope, attemptKey, claimToken, actorUserId, ct);
            if (attempt is null || attempt.State is not ("claimed" or "dispatched" or "unknown"))
                return new(VetPhotoImageStatus.Stale);
            // Unknown accounting survives every restart. Only positively unlaunched work releases its slot.
            if (attempt.State == "unknown" && disposition == VetPhotoImageFailureDisposition.KnownNotDispatched)
                return new(VetPhotoImageStatus.Stale);
            attempt.State = disposition == VetPhotoImageFailureDisposition.OutcomeUnknown ? "unknown" : "failed";
            attempt.FailureCategory = category;
            attempt.ReservedResultSlot = disposition == VetPhotoImageFailureDisposition.OutcomeUnknown;
            attempt.UpdatedAt = clock.UtcNow;
            return new(VetPhotoImageStatus.Existing);
        }, ct);
        return outcome.Status == VetPhotoImageStatus.Existing;
    }

    public Task<VetPhotoImageResult> InstallCurrentExtractionAsync(VetDiaryScope scope,
        Guid extractionResultId, long actorUserId, CancellationToken ct) => ImageTransactionAsync(scope, async () =>
    {
        if (!await ImageActorAsync(scope, actorUserId, ct)) return new(VetPhotoImageStatus.Refused);
        var extraction = await Scoped<VetPhotoExtraction>(scope).AsNoTracking().SingleOrDefaultAsync(e => e.Id == extractionResultId, ct);
        if (extraction is null) return new(VetPhotoImageStatus.NotFound);
        var source = await ImageSourceAsync(scope, extraction.SourceId, ct);
        if (source is null || extraction.State != "returned" || !await CurrentOriginalAsync(scope, source, extraction.InputRevisionId, ct)
            || VetPhotoInterpretationParser.Parse(extraction.StructuredJson, source.Id, extraction.InputRevisionId) is null)
            return new(VetPhotoImageStatus.Stale);
        var result = await InstallImageLockedAsync(scope, source, extraction, ct);
        if (result.Status == VetPhotoImageStatus.Installed) await InvalidateBatchAsync(scope, source.BatchId, ct);
        return result;
    }, ct);

    public Task<VetPhotoImageResult> ReuseDisplayAsync(VetDiaryScope scope, Guid sourceId,
        Guid inputRevisionId, long actorUserId, string modelName, CancellationToken ct, string promptVersion = "photo-v1") => ImageTransactionAsync(scope, async () =>
    {
        if (!await ImageActorAsync(scope, actorUserId, ct)) return new(VetPhotoImageStatus.Refused);
        if (modelName is not { Length: > 0 and <= 200 } || !ScalarText(modelName)
            || promptVersion is not { Length: > 0 and <= 30 } || !ScalarText(promptVersion)) return new(VetPhotoImageStatus.Refused);
        var source = await ImageSourceAsync(scope, sourceId, ct);
        if (source is null || !await CurrentOriginalAsync(scope, source, inputRevisionId, ct)) return new(VetPhotoImageStatus.Stale);
        var input = await Scoped<VetPhotoInputRevision>(scope).AsNoTracking().SingleAsync(r => r.Id == inputRevisionId && r.SourceId == sourceId, ct);
        var prior = await Scoped<VetPhotoExtraction>(scope).AsNoTracking().Where(e => e.InputRevisionId == inputRevisionId)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).FirstOrDefaultAsync(ct);
        if (prior is not null) return await ExistingImageAsync(scope, prior, actorUserId, ct);
        if (await Scoped<VetPhotoAttempt>(scope).AnyAsync(a => a.InputRevisionId == inputRevisionId && a.Kind == "image", ct))
            return new(VetPhotoImageStatus.Busy);
        if (input.ReusesImageInputId is not { } reusedInputId) return new(VetPhotoImageStatus.NotFound);
        var old = await Scoped<VetPhotoExtraction>(scope).AsNoTracking().Where(e => e.SourceId == sourceId
            && e.InputRevisionId == reusedInputId && e.State == "returned" && e.ModelName == modelName
            && e.PromptVersion == promptVersion && e.SchemaVersion == 1)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).FirstOrDefaultAsync(ct);
        var original = await Scoped<VetPhotoOriginalReference>(scope).AsNoTracking().SingleAsync(r => r.InputRevisionId == inputRevisionId && r.State == "retained", ct);
        if (old is null || !await Scoped<VetPhotoOriginalReference>(scope).AnyAsync(r => r.InputRevisionId == reusedInputId
                && r.State == "retained" && r.BlobId == original.BlobId, ct)
            || VetPhotoInterpretationParser.Parse(old.StructuredJson, sourceId, reusedInputId) is null)
            return new(VetPhotoImageStatus.NotFound);
        if (!(await TotalsLockedAsync(ct)).CanReserve(capacity, 0, false, true)) return new(VetPhotoImageStatus.CapacityFull);
        var node = JsonNode.Parse(old.StructuredJson)!.AsObject();
        node["input_revision_id"] = inputRevisionId.ToString("D");
        var json = node.ToJsonString(Json);
        if (VetPhotoInterpretationParser.Parse(json, sourceId, inputRevisionId) is null) return new(VetPhotoImageStatus.InvalidResult);
        var attempt = InScope(new VetPhotoAttempt
        {
            Id = Guid.NewGuid(), SourceId = sourceId, InputRevisionId = inputRevisionId,
            ActorUserId = actorUserId, Kind = "reuse", State = "returned", ExpectedSourceOrdinal = source.CurrentOrdinal,
            ExpectedCurrentInputId = inputRevisionId, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow
        }, scope);
        var extraction = InScope(new VetPhotoExtraction
        {
            Id = Guid.NewGuid(), SourceId = sourceId, InputRevisionId = inputRevisionId, AttemptId = attempt.Id,
            ReusesExtractionId = old.Id, ModelName = old.ModelName, PromptVersion = old.PromptVersion, State = "returned", StructuredJson = json, CreatedAt = clock.UtcNow
        }, scope);
        attempt.ExtractionResultId = extraction.Id;
        db.Add(attempt); db.Add(extraction);
        await db.SaveChangesAsync(ct);
        var result = await InstallImageLockedAsync(scope, source, extraction, ct);
        await InvalidateBatchAsync(scope, source.BatchId, ct);
        return result with { Status = result.Status == VetPhotoImageStatus.ProposedDelta ? result.Status : VetPhotoImageStatus.Reused };
    }, ct);

    private async Task<VetPhotoImageResult> ExistingImageAsync(VetDiaryScope scope, VetPhotoExtraction extraction,
        long actor, CancellationToken ct)
    {
        var source = await ImageSourceAsync(scope, extraction.SourceId, ct);
        if (VetPhotoInterpretationParser.Parse(extraction.StructuredJson, extraction.SourceId, extraction.InputRevisionId) is null)
            return new(VetPhotoImageStatus.InvalidResult, Extraction: extraction);
        if (extraction.State == "returned" && source is not null && await ImageActorAsync(scope, actor, ct)
            && await CurrentOriginalAsync(scope, source, extraction.InputRevisionId, ct))
        {
            var result = await InstallImageLockedAsync(scope, source, extraction, ct);
            if (result.Status == VetPhotoImageStatus.Installed) await InvalidateBatchAsync(scope, source.BatchId, ct);
            return result with { Status = result.Status == VetPhotoImageStatus.ProposedDelta ? result.Status : VetPhotoImageStatus.Existing };
        }
        return new(VetPhotoImageStatus.Existing, Extraction: extraction);
    }

    private async Task<VetPhotoImageResult> InstallImageLockedAsync(VetDiaryScope scope, VetPhotoSource source,
        VetPhotoExtraction extraction, CancellationToken ct)
    {
        var candidate = await Scoped<VetPhotoCandidate>(scope).SingleAsync(c => c.SourceId == source.Id && c.CandidateOrdinal == 0, ct);
        if (candidate.EventId != null || candidate.ManuallyCorrected || candidate.RequiresExplicitRestoration
            || candidate.State is "saved" or "linked" or "excluded" or "cancelled" or "deleted")
            return new(VetPhotoImageStatus.ProposedDelta, Extraction: extraction,
                Delta: new(candidate.Id, candidate.Revision, source.Id, extraction.InputRevisionId,
                    extraction.Id, candidate.RequiresExplicitRestoration || candidate.State is "excluded" or "cancelled" or "deleted"));
        if (candidate.InputRevisionId == extraction.InputRevisionId && candidate.ExtractionResultId == extraction.Id)
            return new(VetPhotoImageStatus.Existing, Extraction: extraction);
        candidate.InputRevisionId = extraction.InputRevisionId;
        candidate.ExtractionResultId = extraction.Id;
        candidate.State = "pending";
        candidate.Revision++;
        candidate.EffectiveJson = "{}";
        candidate.ReasonsJson = "[\"awaiting_context\"]";
        candidate.UpdatedAt = clock.UtcNow;
        return new(VetPhotoImageStatus.Installed, Extraction: extraction);
    }

    private async Task<bool> ScheduledSelectionAsync(VetDiaryScope scope, VetPhotoSource source,
        VetPhotoAttempt attempt, long actor, CancellationToken ct)
    {
        var window = await Scoped<VetPhotoRunWindow>(scope).AsNoTracking().SingleOrDefaultAsync(w =>
            w.Id == attempt.RunWindowId && (w.State == "queued" || w.State == "running"), ct);
        if (window is null) return false;
        var run = await Scoped<VetPhotoRun>(scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == window.RunId
            && r.ActorUserId == actor && (r.State == "approved" || r.State == "running") && r.CancelledAt == null, ct);
        if (run is null) return false;
        var review = await Scoped<VetPhotoReview>(scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == run.SelectionReviewId
            && r.DecisionActorUserId == actor && r.Kind == "reextract_selection" && r.State == "accepted", ct);
        if (review is null || review.PageCount > 64 || !ReviewDelivered(review)) return false;
        try
        {
            var selected = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(window.SelectionJson, Json);
            var all = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(run.SelectionJson, Json);
            var proof = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(review.SelectionJson, Json);
            if (selected is null || all is null || proof is null || selected.Length is < 1 or > 50
                || selected.Any(s => s is null) || all.Any(s => s is null) || proof.Any(s => s is null)
                || selected.Select(s => s.AttemptKey).Distinct().Count() != selected.Length
                || all.Length != run.SelectedCount || all.Select(s => s.AttemptKey).Distinct().Count() != all.Length
                || !all.SequenceEqual(proof)) return false;
            var snapshot = selected.SingleOrDefault(s => s.AttemptKey == attempt.Id);
            if (snapshot is null || !all.Contains(snapshot) || snapshot.SourceId != source.Id
                || snapshot.InputRevisionId != attempt.InputRevisionId || snapshot.ExpectedCurrentInputId != attempt.ExpectedCurrentInputId
                || snapshot.ExpectedSourceOrdinal != attempt.ExpectedSourceOrdinal
                || source.CurrentInputRevisionId != snapshot.ExpectedCurrentInputId || source.CurrentOrdinal != snapshot.ExpectedSourceOrdinal
                || attempt.HistoricalSelection != (attempt.InputRevisionId != source.CurrentInputRevisionId)) return false;
            if (snapshot.ExpectedCandidateRevision is { } revision && !await Scoped<VetPhotoCandidate>(scope).AnyAsync(c =>
                    c.SourceId == source.Id && c.CandidateOrdinal == 0 && c.Revision == revision, ct)) return false;
            return await RetainedInputAsync(scope, source.Id, attempt.InputRevisionId, ct)
                && await Scoped<VetPhotoOriginalReference>(scope).AnyAsync(r => r.Id == snapshot.OriginalReferenceId
                    && r.InputRevisionId == attempt.InputRevisionId && r.Revision == snapshot.ExpectedReferenceRevision && r.State == "retained", ct);
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private sealed record PhotoOtherAttemptFence(int LiveCalls, int UnknownCount, int ChargedUnknownCount, string? Fingerprint);
    private async Task<PhotoOtherAttemptFence> OtherImageAttemptsAsync(VetDiaryScope scope, Guid sourceId,
        Guid inputId, Guid? excludedAttemptKey, CancellationToken ct)
    {
        var attempts = await Scoped<VetPhotoAttempt>(scope).Where(a => a.Kind == "image"
            && a.SourceId == sourceId && a.InputRevisionId == inputId && a.Id != excludedAttemptKey
            && (a.State == "claimed" || a.State == "dispatched" || a.State == "unknown")).ToListAsync(ct);
        foreach (var a in attempts.Where(a => a.State == "dispatched" && (a.LeaseUntil == null || a.LeaseUntil <= clock.UtcNow)))
        {
            a.State = "unknown"; a.FailureCategory = "outcome_unknown"; a.UpdatedAt = clock.UtcNow;
            // Preserve token, original result reservation and all prior accounting. A new review must acknowledge this state.
        }
        var unknown = attempts.Where(a => a.State == "unknown").OrderBy(a => a.Id).ToArray();
        var live = attempts.Count(a => a.State == "dispatched" || a.State == "claimed" && a.LeaseUntil > clock.UtcNow);
        if (unknown.Length == 0) return new(live, 0, 0, null);
        var proof = unknown.Select(a => new { a.Id, a.SourceId, a.InputRevisionId, a.ExpectedCurrentInputId,
            a.ExpectedSourceOrdinal, a.HistoricalSelection, a.State, a.ReservedResultSlot, a.ExtractionResultId }).ToArray();
        return new(live, unknown.Length, unknown.Count(a => a.ReservedResultSlot),
            Hash(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(proof, Json))));
    }

    private async Task<(bool Valid, string? Fingerprint)> ImageAcknowledgementAsync(VetDiaryScope scope,
        VetPhotoSource source, VetPhotoAttempt attempt, long actor, CancellationToken ct)
    {
        if (attempt.RunWindowId == null) return (true, null); // Ordinary automatic work never receives acknowledgement.
        if (!await ScheduledSelectionAsync(scope, source, attempt, actor, ct)) return (false, null);
        var window = await Scoped<VetPhotoRunWindow>(scope).AsNoTracking().SingleAsync(w => w.Id == attempt.RunWindowId, ct);
        var run = await Scoped<VetPhotoRun>(scope).AsNoTracking().SingleAsync(r => r.Id == window.RunId, ct);
        var review = await Scoped<VetPhotoReview>(scope).AsNoTracking().SingleAsync(r => r.Id == run.SelectionReviewId, ct);
        var profile = await WorkflowProfileAsync(scope, ct);
        if (profile == null || review.ProfileId != profile.Id || review.ProfileRevision != profile.Revision
            || !WorkflowTextBound(window.SelectionJson, 65536)) return (false, null);
        try
        {
            var selected = JsonSerializer.Deserialize<VetPhotoRunInputSnapshot[]>(window.SelectionJson, Json);
            var snapshot = selected?.SingleOrDefault(s => s.AttemptKey == attempt.Id);
            if (snapshot == null) return (false, null);
            var fingerprint = snapshot.AcknowledgedUnknownFingerprint;
            if (fingerprint != null && (fingerprint.Length != 64 || fingerprint.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
                return (false, null);
            return (true, fingerprint);
        }
        catch (JsonException) { return (false, null); }
        catch (InvalidOperationException) { return (false, null); }
    }

    private async Task<VetPhotoImageStatus?> OtherImageAttemptGuardAsync(VetDiaryScope scope,
        VetPhotoSource source, VetPhotoAttempt attempt, long actor, CancellationToken ct)
    {
        var fence = await OtherImageAttemptsAsync(scope, source.Id, attempt.InputRevisionId, attempt.Id, ct);
        if (fence.LiveCalls > 0) return VetPhotoImageStatus.Busy;
        var acknowledged = await ImageAcknowledgementAsync(scope, source, attempt, actor, ct);
        if (!acknowledged.Valid) return VetPhotoImageStatus.Stale;
        if (acknowledged.Fingerprint == fence.Fingerprint) return null;
        return attempt.RunWindowId == null && fence.Fingerprint != null ? VetPhotoImageStatus.Unknown : VetPhotoImageStatus.Stale;
    }

    private Task<VetPhotoAttempt?> OwnedImageAttemptAsync(VetDiaryScope scope, Guid key, Guid token, long actor, CancellationToken ct) =>
        Scoped<VetPhotoAttempt>(scope).SingleOrDefaultAsync(a => a.Id == key && a.Kind == "image"
            && a.ClaimToken == token && a.ActorUserId == actor, ct);
    private async Task<bool> ImageActorAsync(VetDiaryScope scope, long actor, CancellationToken ct) =>
        await ActorAsync(scope, actor, ct) && await db.Bots.AnyAsync(b => b.Id == scope.BotDbId && b.FamilyId == scope.FamilyId
            && b.TelegramBotId == scope.TelegramBotId && b.Status == BotStatus.Active, ct);
    private async Task<VetPhotoSource?> ImageSourceAsync(VetDiaryScope scope, Guid sourceId, CancellationToken ct)
    {
        var source = await Scoped<VetPhotoSource>(scope).AsNoTracking().SingleOrDefaultAsync(s => s.Id == sourceId && s.SourceSlot == 1, ct);
        if (source?.SourceMessageDbId is not { } messageId) return null;
        return await db.Messages.AnyAsync(m => m.Id == messageId && m.FamilyId == scope.FamilyId && m.BotId == scope.TelegramBotId
            && m.ChatId == scope.ChatId && m.TopicId == scope.TopicId && m.TelegramMessageId == source.TelegramMessageId
            && m.UserId == source.SourceAuthorUserId && m.ChatType == source.ChatType && m.SentAt == source.SentAt
            && m.Direction == MessageDirection.In && (m.Kind == MessageKind.Photo || m.Kind == MessageKind.Document), ct) ? source : null;
    }
    private Task<bool> BatchOpenAsync(VetDiaryScope scope, VetPhotoSource source, CancellationToken ct) =>
        source.State is "late" or "full" ? Task.FromResult(false)
            : source.BatchId is { } batchId ? Scoped<VetPhotoBatch>(scope).AnyAsync(b => b.Id == batchId && b.State != "cancelled", ct)
            : Task.FromResult(false);
    private async Task<bool> RetainedInputAsync(VetDiaryScope scope, Guid sourceId, Guid inputId, CancellationToken ct) =>
        await Scoped<VetPhotoInputRevision>(scope).AnyAsync(i => i.Id == inputId && i.SourceId == sourceId, ct)
        && await (from reference in Scoped<VetPhotoOriginalReference>(scope)
            join blob in db.Set<VetPhotoBlob>() on reference.BlobId equals blob.Id
            where reference.InputRevisionId == inputId && reference.State == "retained"
                && blob.FamilyId == scope.FamilyId && blob.Content != null
            select reference.Id).AnyAsync(ct);
    private async Task<bool> CurrentOriginalAsync(VetDiaryScope scope, VetPhotoSource source, Guid inputId, CancellationToken ct) =>
        source.CurrentInputRevisionId == inputId && await BatchOpenAsync(scope, source, ct)
        && await Scoped<VetPhotoCandidate>(scope).AnyAsync(c => c.SourceId == source.Id && c.CandidateOrdinal == 0
            && c.State != "cancelled" && c.State != "excluded" && c.State != "deleted" && !c.RequiresExplicitRestoration, ct)
        && await RetainedInputAsync(scope, source.Id, inputId, ct);
    private static VetPhotoImageClaim ImageClaim(VetDiaryScope scope, VetPhotoAttempt attempt) =>
        new(scope, attempt.Id, attempt.ClaimToken!.Value, attempt.LeaseUntil!.Value, attempt.ActorUserId,
            attempt.SourceId, attempt.InputRevisionId, attempt.ExpectedCurrentInputId, attempt.ExpectedSourceOrdinal,
            attempt.HistoricalSelection, attempt.RunWindowId);

    private async Task<VetPhotoImageResult> ImageTransactionAsync(VetDiaryScope scope,
        Func<Task<VetPhotoImageResult>> operation, CancellationToken ct)
    {
        _guard.Family(scope.FamilyId);
        ForgetPhotoSnapshots();
        var before = TrackedBefore();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await CapacityLockAsync(ct);
            await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
            await _guard.BotAsync(scope.FamilyId, scope.BotDbId, scope.TelegramBotId, ct);
            var result = await operation();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            DetachOwned(before);
            throw;
        }
    }
}
