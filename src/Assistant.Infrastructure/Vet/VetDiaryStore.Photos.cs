using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Families;
using Assistant.Domain.Bots;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet;

public sealed partial class VetDiaryStore : IVetPhotoDiaryStore
{
    private static readonly JsonSerializerOptions PhotoJson = new(JsonSerializerDefaults.Web);
    private IQueryable<T> PhotoRows<T>(VetDiaryScope s) where T : class => db.Set<T>().Where(x =>
        EF.Property<long>(x, "FamilyId") == s.FamilyId && EF.Property<long>(x, "BotDbId") == s.BotDbId
        && EF.Property<long>(x, "TelegramBotId") == s.TelegramBotId
        && EF.Property<long>(x, "ChatId") == s.ChatId && EF.Property<int?>(x, "TopicId") == s.TopicId);

    private async Task<bool> PhotoActorAsync(VetDiaryScope s, long actor, CancellationToken ct) =>
        actor > 0 && await db.Bots.AnyAsync(b => b.Id == s.BotDbId && b.FamilyId == s.FamilyId
            && b.TelegramBotId == s.TelegramBotId && b.Status == BotStatus.Active, ct)
        && await db.FamilyMembers.AnyAsync(m => m.FamilyId == s.FamilyId
            && m.TelegramUserId == actor && m.Status == FamilyMemberStatus.Approved, ct)
        && (s.ChatId == actor && s.TopicId == null
            && await PhotoRows<VetPhotoSource>(s).AnyAsync(p => p.ChatType == "private" && p.SourceAuthorUserId == actor, ct)
            || await db.Places.AnyAsync(p => p.BotId == s.BotDbId && p.ChatId == s.ChatId
                && p.TopicId == s.TopicId && p.Status == PlaceStatus.Approved, ct));

    private void ForgetDiaryPhotoSnapshots()
    {
        foreach (var entry in db.ChangeTracker.Entries().ToArray())
        {
            if (entry.Entity.GetType().Namespace != typeof(VetPhotoSource).Namespace) continue;
            if (entry.State != EntityState.Unchanged) throw new InvalidOperationException("Photo context has unfinished changes.");
            entry.State = EntityState.Detached;
        }
    }

    public async Task<VetPhotoCollisionProof?> GetPhotoCollisionProofAsync(VetDiaryScope scope,
        long profileId, Guid candidateId, VetEventState? state, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        if (!await PhotoRows<VetPhotoCandidate>(scope).AnyAsync(c => c.Id == candidateId, ct)
            || !await db.Set<VetProfile>().AnyAsync(p => p.Id == profileId && p.FamilyId == scope.FamilyId
                && p.BotDbId == scope.BotDbId, ct)) return null;
        return await PhotoCollisionProofLockedAsync(scope, profileId, candidateId, state, ct);
    }

    private async Task<VetPhotoCollisionProof> PhotoCollisionProofLockedAsync(VetDiaryScope scope,
        long profileId, Guid candidateId, VetEventState? state, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        var own = await PhotoRows<VetPhotoCandidate>(scope).AsNoTracking().SingleAsync(c => c.Id == candidateId, ct);
        var ownHashes = await PhotoRows<VetPhotoOriginalReference>(scope).AsNoTracking()
            .Where(r => db.Set<VetPhotoInputRevision>().Any(i => i.Id == r.InputRevisionId && i.SourceId == own.SourceId
                && i.FamilyId == scope.FamilyId && i.BotDbId == scope.BotDbId))
            .Select(r => r.ContentHash).Distinct().ToListAsync(ct);
        var collision = false;
        if (own.DuplicateEventId is { } linkedEventId)
        {
            var linked = await Events(scope).AsNoTracking().SingleOrDefaultAsync(e => e.Id == linkedEventId, ct);
            Add(linked == null ? "linked_missing" : JsonSerializer.Serialize(new { linked.Id, linked.Revision,
                linked.Value, linked.Unit, linked.OccurredAt, linked.DeletedAt }, PhotoJson));
        }
        // Scope-only candidates stream one row at a time. Unrelated revision changes deliberately
        // stale this conservative proof; no other place or family is materialized.
        await foreach (var row in PhotoRows<VetPhotoCandidate>(scope).AsNoTracking().OrderBy(c => c.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            Add(JsonSerializer.Serialize(new { row.Id, row.Revision, row.SourceId, row.State, row.EventId,
                row.InputRevisionId, row.ExtractionResultId, row.EffectiveJson, row.DuplicateDecision,
                row.DuplicateSourceId, row.DuplicateEventId, row.DuplicateEventRevision }, PhotoJson));
            if (row.Id == candidateId || row.State is "excluded" or "cancelled" or "deleted") continue;
            VetPhotoEffectiveReading? effective;
            try { effective = JsonSerializer.Deserialize<VetPhotoEffectiveReading>(row.EffectiveJson, PhotoJson); }
            catch (JsonException) { effective = null; }
            if (state != null && effective != null && effective.Value == state.Value && effective.Unit == state.Unit
                && effective.OccurredAt == state.OccurredAt) collision = true;
        }
        if (state != null)
        {
            await foreach (var row in Events(scope).AsNoTracking().Where(e => e.ProfileId == profileId && e.DeletedAt == null
                    && e.EventType == "glucose" && e.Value == state.Value && e.Unit == state.Unit && e.OccurredAt == state.OccurredAt)
                .OrderBy(e => e.Id).AsAsyncEnumerable().WithCancellation(ct))
            {
                Add($"e:{row.Id.ToString(CultureInfo.InvariantCulture)}:{row.Revision.ToString(CultureInfo.InvariantCulture)}");
                if (row.Id != own.EventId) collision = true;
            }
        }
        if (ownHashes.Count > 0)
        {
            var ownInputs = PhotoRows<VetPhotoInputRevision>(scope).Where(i => i.SourceId == own.SourceId).Select(i => i.Id);
            await foreach (var row in PhotoRows<VetPhotoOriginalReference>(scope).AsNoTracking()
                .Where(r => r.State == "retained" && ownHashes.Contains(r.ContentHash))
                .OrderBy(r => r.Id).Select(r => new { Reference = r, OtherSource = !ownInputs.Contains(r.InputRevisionId) })
                .AsAsyncEnumerable().WithCancellation(ct))
            {
                var reference = row.Reference;
                Add($"r:{reference.Id:D}:{reference.Revision.ToString(CultureInfo.InvariantCulture)}:{reference.ContentHash}");
                if (row.OtherSource) collision = true;
            }
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()), collision);
    }

    private sealed record PhotoPrepared(VetPhotoDiarySelection Selection, VetPhotoSource Source,
        VetPhotoCandidate Candidate, VetPhotoCandidateState CandidateBefore, VetEvent? Event,
        VetEventState? EventBefore, VetEvent? LinkTarget);

    public async Task<VetMutationResult> ApplyPhotoReviewAsync(VetPhotoDiaryAcceptance acceptance, CancellationToken ct)
    {
        var scope = acceptance.Scope;
        await CheckAsync(scope, ct);
        ForgetDiaryPhotoSnapshots();
        using var tracking = new PhotoDiaryTracking(db);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        if (!await PhotoActorAsync(scope, acceptance.ActorUserId, ct)) return VetMutationResult.Of(VetMutationStatus.Refused);
        var review = await PhotoRows<VetPhotoReview>(scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == acceptance.ReviewId, ct);
        if (review == null) return VetMutationResult.Of(VetMutationStatus.NotFound);
        if (review.Revision != acceptance.ReviewRevision || review.OperationKey != acceptance.OperationKey
            || acceptance.OperationKey == Guid.Empty || review.Kind is not ("save" or "correction" or "reextract_comparison")
            || acceptance.CallbackPromptMessageId is { } prompt && prompt != review.AcceptancePromptMessageId)
            return VetMutationResult.Of(VetMutationStatus.Stale);
        if (!VetPhotoStore.HasCompletePreview(review)) return VetMutationResult.Of(VetMutationStatus.Stale);
        if (!string.Equals(Hash(review.SelectionJson), review.Fingerprint, StringComparison.OrdinalIgnoreCase))
            return VetMutationResult.Of(VetMutationStatus.Refused);
        if (review.State == "accepted" && review.OutcomeJson != null)
            return JsonSerializer.Deserialize<VetMutationResult>(review.OutcomeJson)! with { Status = VetMutationStatus.AlreadyApplied };
        if (review.State != "preview") return VetMutationResult.Of(VetMutationStatus.Stale);
        var profile = await db.Set<VetProfile>().AsNoTracking().SingleOrDefaultAsync(p => p.FamilyId == scope.FamilyId
            && p.BotDbId == scope.BotDbId && p.Id == review.ProfileId && p.Revision == review.ProfileRevision, ct);
        if (profile == null) return VetMutationResult.Of(VetMutationStatus.Stale);
        VetPhotoDiarySelection[]? selected;
        try { selected = JsonSerializer.Deserialize<VetPhotoDiarySelection[]>(review.SelectionJson, PhotoJson); }
        catch (JsonException) { return VetMutationResult.Of(VetMutationStatus.Refused); }
        if (selected == null || selected.Length is < 1 or > 50 || selected.Any(x => x == null)
            || selected.Select(x => x.CandidateId).Distinct().Count() != selected.Length)
            return VetMutationResult.Of(VetMutationStatus.Refused);
        var prepared = new List<PhotoPrepared>();
        var eventIds = new HashSet<long>();
        var comparisonAuthorities = new Dictionary<Guid, PhotoComparisonAuthority>();
        foreach (var item in selected)
        {
            if (item.ProfileId != profile.Id || item.ProfileRevision != profile.Revision || item.BatchId == Guid.Empty
                || item.Disposition is not ("save" or "correct" or "link" or "exclude" or "cancel" or "delete" or "keep")
                || item.Disposition == "keep" && review.Kind != "reextract_comparison"
                || item.DuplicateDecision is not ("unresolved" or "canonical" or "same" or "separate" or "exclude")
                || item.Context == null || item.CollisionProof == null)
                return VetMutationResult.Of(VetMutationStatus.Refused);
            var batch = await PhotoRows<VetPhotoBatch>(scope).AsNoTracking().SingleOrDefaultAsync(b => b.Id == item.BatchId, ct);
            if (batch == null || batch.ProfileId != profile.Id || batch.ProfileRevision != profile.Revision
                || batch.ReviewRevision != item.BatchReviewRevision
                || review.BatchId is { } reviewedBatch && reviewedBatch != batch.Id
                || review.BatchId == batch.Id && review.BatchReviewRevision != batch.ReviewRevision
                || review.Kind == "save" && (batch.State is "cancelled" or "completed"))
                return VetMutationResult.Of(VetMutationStatus.Stale);
            var source = await PhotoRows<VetPhotoSource>(scope).AsNoTracking().SingleOrDefaultAsync(s => s.Id == item.SourceId, ct);
            var candidate = await PhotoRows<VetPhotoCandidate>(scope).AsNoTracking().SingleOrDefaultAsync(c => c.Id == item.CandidateId, ct);
            if (source == null || candidate == null || source.BatchId != batch.Id || candidate.BatchId != batch.Id
                || candidate.SourceId != source.Id || candidate.CandidateOrdinal != 0 || candidate.Revision != item.CandidateRevision
                || candidate.ExtractionResultId != item.ExpectedCandidateExtractionId
                || candidate.EventId != item.EventId || candidate.EventRevision != item.EventRevision
                || source.CurrentInputRevisionId != item.ExpectedCurrentInputId || source.CurrentOrdinal != item.ExpectedSourceOrdinal
                || source.SourceSlot != 1 || source.SourceMessageDbId is not { } messageId)
                return VetMutationResult.Of(VetMutationStatus.Stale);
            if (!await db.Messages.AnyAsync(m => m.Id == messageId && m.FamilyId == scope.FamilyId && m.BotId == scope.TelegramBotId
                && m.ChatId == scope.ChatId && m.TopicId == scope.TopicId && m.TelegramMessageId == source.TelegramMessageId
                && m.UserId == source.SourceAuthorUserId && m.Direction == MessageDirection.In
                && (m.Kind == MessageKind.Photo || m.Kind == MessageKind.Document), ct))
                return VetMutationResult.Of(VetMutationStatus.Refused);
            var input = await PhotoRows<VetPhotoInputRevision>(scope).AsNoTracking().SingleOrDefaultAsync(i =>
                i.Id == item.InputRevisionId && i.SourceId == source.Id, ct);
            var extraction = item.ExtractionResultId == null ? null : await PhotoRows<VetPhotoExtraction>(scope).AsNoTracking().SingleOrDefaultAsync(e =>
                e.Id == item.ExtractionResultId && e.SourceId == source.Id && e.InputRevisionId == item.InputRevisionId, ct);
            if (input == null || extraction == null && item.Disposition is not ("exclude" or "cancel")
                    && !(item.Disposition == "keep" && review.Kind == "reextract_comparison")
                || extraction == null && item.ExtractionResultId != null) return VetMutationResult.Of(VetMutationStatus.Refused);
            if (item.OriginalReferenceId is { } referenceId)
            {
                if (!await PhotoRows<VetPhotoOriginalReference>(scope).AnyAsync(r => r.Id == referenceId
                    && r.InputRevisionId == input.Id && r.Revision == item.OriginalReferenceRevision
                    && r.State == item.OriginalReferenceState, ct)) return VetMutationResult.Of(VetMutationStatus.Stale);
            }
            else if (item.OriginalReferenceRevision != null || item.OriginalReferenceState != null
                || await PhotoRows<VetPhotoOriginalReference>(scope).AnyAsync(r => r.InputRevisionId == input.Id, ct))
                return VetMutationResult.Of(VetMutationStatus.Stale);
            if (item.Disposition != "delete" && (review.Kind == "reextract_comparison"
                || input.Id != source.CurrentInputRevisionId || extraction?.State == "comparison"))
            {
                if (!await PhotoComparisonProofAsync(scope, review, item, source, input, extraction, comparisonAuthorities, ct))
                    return VetMutationResult.Of(VetMutationStatus.Stale);
            }
            else if (item.Disposition != "delete" && extraction != null && extraction.State is not ("returned" or "invalid" or "human"))
                return VetMutationResult.Of(VetMutationStatus.Stale);
            var beforeCandidate = PhotoCandidateState(candidate);
            var requiresRestore = candidate.RequiresExplicitRestoration || candidate.State is "excluded" or "cancelled" or "deleted";
            if (requiresRestore && (item.Disposition is "save" or "correct" or "link") && !item.ExplicitRestoration)
                return VetMutationResult.Of(VetMutationStatus.Refused);
            VetEvent? row = null;
            VetEventState? beforeEvent = null;
            if (item.EventId is { } eventId)
            {
                row = await Events(scope).AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId && e.ProfileId == profile.Id, ct);
                if (row == null || row.Revision != item.EventRevision) return VetMutationResult.Of(VetMutationStatus.Stale);
                beforeEvent = State(row);
            }
            else if (item.EventRevision != null) return VetMutationResult.Of(VetMutationStatus.Refused);
            if (item.Disposition is "save" or "correct" or "delete" or "link")
            {
                if (item.State == null || item.State.EventType != "glucose" || item.State.Product != null || item.State.Unit != "mmol/L"
                    || item.State.SourceKind != "photo" || item.State.SourceId != source.Id || item.State.PhotoSourceId != source.Id
                    || item.State.PhotoBatchId != batch.Id || item.State.TextSourceId != null || item.State.CandidateOrdinal != 0
                    || extraction == null || item.State.InputRevisionId != input.Id || item.State.ExtractionResultId != extraction.Id
                    || item.State.SourceAuthorUserId != source.SourceAuthorUserId || item.State.SourceMessageDbId != messageId
                    || item.State.TelegramMessageId != source.TelegramMessageId || item.State.Value <= 0)
                    return VetMutationResult.Of(VetMutationStatus.Refused);
                if (item.Disposition == "save" && row != null || (item.Disposition is "correct" or "delete") && row == null
                    || item.Disposition != "link" && row != null && (row.SourceKind != "photo" || row.SourceId != source.Id || row.CandidateOrdinal != 0))
                    return VetMutationResult.Of(VetMutationStatus.Refused);
                if (row != null && !eventIds.Add(row.Id)) return VetMutationResult.Of(VetMutationStatus.Refused);
                if (item.Disposition == "delete")
                {
                    if (beforeEvent == null || item.State != (beforeEvent with { DeletedAt = item.State.DeletedAt,
                        DeleteReason = item.State.DeleteReason, DeletedByUserId = item.State.DeletedByUserId })
                        || item.State.DeletedAt == null || item.State.DeleteReason != "photo_delete"
                        || item.State.DeletedByUserId != null) return VetMutationResult.Of(VetMutationStatus.Refused);
                }
                else
                {
                    var image = VetPhotoInterpretationParser.Parse(extraction.StructuredJson, source.Id, input.Id);
                    if (image == null || item.State.DeletedAt != null || item.State.DeleteReason != null || item.State.DeletedByUserId != null)
                        return VetMutationResult.Of(VetMutationStatus.Refused);
                    VetPhotoBatchAssumptions? assumptions;
                    try { assumptions = JsonSerializer.Deserialize<VetPhotoBatchAssumptions>(batch.AssumptionsJson, PhotoJson); }
                    catch (JsonException) { return VetMutationResult.Of(VetMutationStatus.Refused); }
                    if (assumptions == null) return VetMutationResult.Of(VetMutationStatus.Refused);
                    // The flag belongs to the shown human proposal, never to image model output.
                    if (!item.Context.CorrectionApproved)
                    {
                        var captionEvidence = await VetPhotoCaptionReader.ReadAsync(db, scope, source, input, ct);
                        var (expectedContext, captionFailure) = VetPhotoCaptionContext.Read(captionEvidence);
                        if (item.Context != expectedContext || captionFailure is "ambiguous_caption_readings"
                            or "uncertain_caption_reading" or "caption_date_requires_clarification" or "caption_processing")
                            return VetMutationResult.Of(VetMutationStatus.Refused);
                    }
                    if (item.Context.PreservedTime is { } preserved && (beforeEvent == null
                        || preserved.OccurredAt != beforeEvent.OccurredAt || preserved.LocalTime != beforeEvent.LocalTime
                        || preserved.TimeZoneSnapshot != beforeEvent.TimeZoneSnapshot || preserved.TimeEvidence != beforeEvent.OccurredAtSource))
                        return VetMutationResult.Of(VetMutationStatus.Refused);
                    var effective = VetPhotoValidationRules.Validate(image, item.Context, assumptions, input.ReceivedAt).Effective;
                    if (effective == null || effective.Value != item.State.Value || effective.Unit != item.State.Unit
                        || effective.OccurredAt != item.State.OccurredAt || effective.LocalTime != item.State.LocalTime
                        || effective.TimeZoneSnapshot != item.State.TimeZoneSnapshot || effective.TimeEvidence != item.State.OccurredAtSource
                        || VetPhotoValidationRules.ValueUnitEvidence(effective) != item.State.ValueUnitSource)
                        return VetMutationResult.Of(VetMutationStatus.Refused);
                    if (row?.DeletedAt != null && !item.ExplicitRestoration) return VetMutationResult.Of(VetMutationStatus.Refused);
                }
                if (item.Disposition == "save" && row == null && await Events(scope).AnyAsync(e => e.SourceKind == "photo" && e.SourceId == source.Id
                    && e.EventType == "glucose" && e.CandidateOrdinal == 0, ct)) return VetMutationResult.Of(VetMutationStatus.Stale);
            }
            else if (item.Disposition is "exclude" or "cancel")
            {
                if (item.State != null || row?.DeletedAt == null && row != null) return VetMutationResult.Of(VetMutationStatus.Refused);
            }
            else if (item.Disposition == "keep" && item.State != beforeEvent)
                return VetMutationResult.Of(VetMutationStatus.Refused);
            VetEvent? target = null;
            if (item.Disposition == "link")
            {
                if (row != null || item.DuplicateDecision != "same" || item.State == null
                    || (item.LinkEventId == null) == (item.LinkCandidateId == null)) return VetMutationResult.Of(VetMutationStatus.Refused);
                if (item.LinkEventId is { } targetId)
                {
                    target = await Events(scope).AsNoTracking().SingleOrDefaultAsync(e => e.Id == targetId && e.ProfileId == profile.Id && e.DeletedAt == null, ct);
                    if (target == null || target.Revision != item.LinkEventRevision || !SamePhotoMeasurement(State(target), item.State))
                        return VetMutationResult.Of(VetMutationStatus.Stale);
                }
                else
                {
                    var canonical = selected.SingleOrDefault(s => s.CandidateId == item.LinkCandidateId);
                    if (canonical == null || canonical.CandidateId == item.CandidateId || canonical.Disposition is not ("save" or "correct")
                        || canonical.DuplicateDecision != "canonical" || canonical.State == null || !SamePhotoMeasurement(canonical.State, item.State))
                        return VetMutationResult.Of(VetMutationStatus.Refused);
                }
            }
            else if (item.LinkEventId != null || item.LinkEventRevision != null || item.LinkCandidateId != null)
                return VetMutationResult.Of(VetMutationStatus.Refused);
            var collisions = await PhotoCollisionProofLockedAsync(scope, profile.Id, candidate.Id, item.State, ct);
            if (collisions != item.CollisionProof) return VetMutationResult.Of(VetMutationStatus.Stale);
            if ((item.Disposition is "save" or "correct") && (collisions.HasCollisions || selected.Any(other =>
                other.CandidateId != item.CandidateId && other.State != null && item.State != null && SamePhotoMeasurement(other.State, item.State)))
                && item.DuplicateDecision is not ("separate" or "canonical")) return VetMutationResult.Of(VetMutationStatus.Refused);
            if (item.DuplicateDecision == "canonical" && selected.Any(other => other.CandidateId != item.CandidateId
                && other.State != null && item.State != null && SamePhotoMeasurement(other.State, item.State)
                && (other.Disposition is "save" or "correct") && other.DuplicateDecision != "separate")) return VetMutationResult.Of(VetMutationStatus.Refused);
            if (item.DuplicateDecision == "canonical" && collisions.HasCollisions
                && !await ClosedCanonicalGroupAsync(scope, profile.Id, item, selected, ct))
                return VetMutationResult.Of(VetMutationStatus.Refused);
            prepared.Add(new(item, source, candidate, beforeCandidate, row, beforeEvent, target));
        }
        var fingerprint = Hash("photo:" + review.Id.ToString("D") + ":" + review.Revision.ToString(CultureInfo.InvariantCulture)
            + ":" + review.Kind + ":" + review.SelectionJson);
        if (await db.Set<VetDiaryAction>().AnyAsync(a => a.FamilyId == scope.FamilyId && a.BotDbId == scope.BotDbId
            && a.OperationKey == acceptance.OperationKey, ct)) return VetMutationResult.Of(VetMutationStatus.Stale);
        var action = NewAction(scope, acceptance.OperationKey, acceptance.ActorUserId,
            prepared.All(p => p.Selection.Disposition == "keep") ? "photo_comparison_keep" : "photo_" + review.Kind, fingerprint);
        action.PhotoBatchId = review.BatchId;
        db.Add(action);
        var affected = new List<(VetEvent Row, VetEventState? Before, int? BeforeRevision)>();
        var created = new Dictionary<Guid, VetEvent>();
        foreach (var item in prepared.Where(p => p.Selection.Disposition is "save" or "correct" or "delete"))
        {
            var row = item.Event ?? new VetEvent { FamilyId = scope.FamilyId, BotDbId = scope.BotDbId,
                TelegramBotId = scope.TelegramBotId, ChatId = scope.ChatId, TopicId = scope.TopicId,
                ProfileId = profile.Id, CreatedAt = clock.UtcNow };
            if (row.Id == 0) db.Add(row); else db.Attach(row);
            var beforeRevision = item.Event?.Revision;
            var state = item.Selection.Disposition == "delete" ? item.Selection.State! with
                { DeletedAt = clock.UtcNow, DeletedByUserId = acceptance.ActorUserId } : item.Selection.State!;
            ApplyState(row, state);
            row.Revision = beforeRevision is { } revision ? checked(revision + 1) : 1;
            row.LastMutationKind = action.Kind; row.UpdatedAt = clock.UtcNow;
            created.Add(item.Candidate.Id, row);
            affected.Add((row, item.EventBefore, beforeRevision));
        }
        await db.SaveChangesAsync(ct);
        var candidateChanges = new List<VetPhotoActionCandidateChange>();
        foreach (var item in prepared.Where(p => p.Selection.Disposition != "keep"))
        {
            var candidate = item.Candidate;
            db.Attach(candidate);
            candidate.Revision = checked(candidate.Revision + 1);
            candidate.UpdatedAt = clock.UtcNow; candidate.LastReviewId = review.Id;
            candidate.DuplicateDecision = item.Selection.DuplicateDecision;
            candidate.State = item.Selection.Disposition switch { "save" or "correct" => "saved", "link" => "linked",
                "exclude" => "excluded", "cancel" => "cancelled", _ => "deleted" };
            candidate.RequiresExplicitRestoration = item.Selection.Disposition is "exclude" or "cancel" or "delete";
            if (item.Selection.Disposition is "save" or "correct" or "delete")
            {
                var row = created[candidate.Id]; candidate.EventId = row.Id; candidate.EventRevision = row.Revision;
                candidate.DuplicateSourceId = null; candidate.DuplicateEventId = null; candidate.DuplicateEventRevision = null;
                candidate.InputRevisionId = item.Selection.InputRevisionId; candidate.ExtractionResultId = item.Selection.ExtractionResultId;
                candidate.ManuallyCorrected = item.Selection.Context.CorrectionApproved || candidate.ManuallyCorrected;
                candidate.CorrectionProvenanceJson = JsonSerializer.Serialize(item.Selection.Context, PhotoJson);
                candidate.EffectiveJson = JsonSerializer.Serialize(item.Selection.State, PhotoJson); candidate.ReasonsJson = "[]";
            }
            else if (item.Selection.Disposition == "link")
            {
                var target = item.LinkTarget ?? created[item.Selection.LinkCandidateId!.Value];
                // EventId uniquely owns a candidate's fact. Links retain only duplicate handles.
                candidate.EventId = null; candidate.EventRevision = null;
                candidate.DuplicateEventId = target.Id; candidate.DuplicateEventRevision = target.Revision;
                candidate.DuplicateSourceId = item.Selection.LinkCandidateId is { } linked ? prepared.Single(p => p.Candidate.Id == linked).Source.Id : target.PhotoSourceId;
                candidate.InputRevisionId = item.Selection.InputRevisionId; candidate.ExtractionResultId = item.Selection.ExtractionResultId;
                candidate.ManuallyCorrected = item.Selection.Context.CorrectionApproved || candidate.ManuallyCorrected;
                candidate.CorrectionProvenanceJson = JsonSerializer.Serialize(item.Selection.Context, PhotoJson);
                candidate.EffectiveJson = JsonSerializer.Serialize(item.Selection.State, PhotoJson); candidate.ReasonsJson = "[]";
            }
            candidateChanges.Add(new(candidate.Id, candidate.SourceId, item.Candidate.Revision - 1,
                candidate.Revision, item.CandidateBefore, PhotoCandidateState(candidate)));
        }
        foreach (var item in affected) db.Add(new VetDiaryActionChange { FamilyId = scope.FamilyId, BotDbId = scope.BotDbId,
            ActionId = action.Id, EventId = item.Row.Id, BeforeJson = item.Before == null ? null : JsonSerializer.Serialize(item.Before),
            AfterJson = JsonSerializer.Serialize(State(item.Row)), BeforeRevision = item.BeforeRevision, AfterRevision = item.Row.Revision });
        var result = new VetMutationResult(candidateChanges.Count == 0 ? VetMutationStatus.NoChange : VetMutationStatus.Applied,
            action.Id, affected.Select(a => a.Row.Id).ToArray(), [])
            { Revisions = affected.Select(a => new VetEventRevision(a.Row.Id, a.Row.Revision)).ToArray() };
        action.OutcomeJson = JsonSerializer.Serialize(new VetPhotoActionOutcome(result.Status, result.ActionId,
            result.EventIds, result.ProtectedIds, result.Revisions, candidateChanges));
        db.Attach(review); review.State = "accepted"; review.ActionId = action.Id;
        review.DecisionActorUserId = acceptance.ActorUserId; review.DecidedAt = clock.UtcNow;
        review.OutcomeJson = JsonSerializer.Serialize(result);
        await db.SaveChangesAsync(ct);
        foreach (var batchId in prepared.Select(p => p.Selection.BatchId).Distinct())
        {
            await PhotoRows<VetPhotoBatch>(scope).Where(b => b.Id == batchId).ExecuteUpdateAsync(u => u
                .SetProperty(b => b.ReviewRevision, b => b.ReviewRevision + 1).SetProperty(b => b.UpdatedAt, clock.UtcNow), ct);
            await PhotoRows<VetPhotoReview>(scope).Where(r => r.Id != review.Id && r.BatchId == batchId && r.State == "preview")
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "stale").SetProperty(r => r.CompletePreviewDelivered, false)
                    .SetProperty(r => r.AcceptancePromptMessageId, (int?)null), ct);
            var terminalState = prepared.Any(p => p.Selection.BatchId == batchId && p.Selection.Disposition == "cancel")
                ? "cancelled" : "completed";
            await PhotoRows<VetPhotoBatch>(scope).Where(b => b.Id == batchId && b.State == "closed"
                && !PhotoRows<VetPhotoCandidate>(scope).Any(c => c.BatchId == batchId
                    && c.State != "saved" && c.State != "linked" && c.State != "excluded"
                    && c.State != "cancelled" && c.State != "deleted"))
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.State, terminalState), ct);
        }
        await tx.CommitAsync(ct);
        return result;
    }

    private static bool SamePhotoMeasurement(VetEventState a, VetEventState b) => a.EventType == b.EventType
        && a.Value == b.Value && a.Unit == b.Unit && a.OccurredAt == b.OccurredAt;
    private Task<bool> ClosedCanonicalGroupAsync(VetDiaryScope scope, long profileId,
        VetPhotoDiarySelection canonical, VetPhotoDiarySelection[] selected, CancellationToken ct)
    {
        if (canonical.State == null || canonical.Disposition is not ("save" or "correct")) return Task.FromResult(false);
        var allowedSources = selected.Where(s => s.CandidateId == canonical.CandidateId
            || s.Disposition == "link" && s.LinkCandidateId == canonical.CandidateId).Select(s => s.SourceId).ToArray();
        return ClosedCanonicalGroupCoreAsync(scope, profileId, canonical.SourceId, canonical.EventId,
            allowedSources, canonical.State, ct);
    }

    private async Task<bool> ClosedCanonicalGroupCoreAsync(VetDiaryScope scope, long profileId,
        Guid canonicalSourceId, long? canonicalEventId, Guid[] allowedSources, VetEventState state, CancellationToken ct)
    {
        if (await Events(scope).AnyAsync(e => e.ProfileId == profileId && e.DeletedAt == null && e.Id != canonicalEventId && e.EventType == state.EventType
            && e.Value == state.Value && e.Unit == state.Unit && e.OccurredAt == state.OccurredAt, ct)) return false;
        var canonicalInputs = PhotoRows<VetPhotoInputRevision>(scope).Where(i => i.SourceId == canonicalSourceId).Select(i => i.Id);
        var hashes = await PhotoRows<VetPhotoOriginalReference>(scope).Where(r => canonicalInputs.Contains(r.InputRevisionId))
            .Select(r => r.ContentHash).Distinct().ToListAsync(ct);
        var forbiddenInputs = PhotoRows<VetPhotoInputRevision>(scope).Where(i => !allowedSources.Contains(i.SourceId)).Select(i => i.Id);
        if (await PhotoRows<VetPhotoOriginalReference>(scope).AnyAsync(r => r.State == "retained"
            && hashes.Contains(r.ContentHash) && forbiddenInputs.Contains(r.InputRevisionId), ct)) return false;
        await foreach (var row in PhotoRows<VetPhotoCandidate>(scope).AsNoTracking().Where(c => !allowedSources.Contains(c.SourceId)
            && c.State != "excluded" && c.State != "cancelled" && c.State != "deleted").AsAsyncEnumerable().WithCancellation(ct))
        {
            VetPhotoEffectiveReading? effective;
            try { effective = JsonSerializer.Deserialize<VetPhotoEffectiveReading>(row.EffectiveJson, PhotoJson); }
            catch (JsonException) { effective = null; }
            if (effective != null && effective.Value == state.Value && effective.Unit == state.Unit && effective.OccurredAt == state.OccurredAt)
                return false;
        }
        return true;
    }

    public async Task<bool> CanAutoLinkPhotoGroupAsync(VetDiaryScope scope, long profileId,
        Guid canonicalCandidateId, IReadOnlyList<Guid> groupCandidateIds, VetEventState state, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        if (groupCandidateIds.Count is < 2 or > 50 || groupCandidateIds.Any(id => id == Guid.Empty)
            || groupCandidateIds.Distinct().Count() != groupCandidateIds.Count || !groupCandidateIds.Contains(canonicalCandidateId)
            || !await db.Set<VetProfile>().AnyAsync(p => p.Id == profileId && p.FamilyId == scope.FamilyId && p.BotDbId == scope.BotDbId, ct)) return false;
        if (state.EventType != "glucose" || state.Unit != "mmol/L" || state.Value <= 0) return false;
        var profile = await db.Set<VetProfile>().AsNoTracking().SingleAsync(p => p.Id == profileId
            && p.FamilyId == scope.FamilyId && p.BotDbId == scope.BotDbId, ct);
        var candidates = await PhotoRows<VetPhotoCandidate>(scope).AsNoTracking()
            .Where(c => groupCandidateIds.Contains(c.Id)).ToListAsync(ct);
        if (candidates.Count != groupCandidateIds.Count || candidates.Any(c => c.EventId != null || c.ManuallyCorrected
            || c.RequiresExplicitRestoration || c.DuplicateDecision != "unresolved"
            || c.State is "linked" or "excluded" or "cancelled" or "deleted")) return false;
        Guid? sharedBlob = null;
        foreach (var candidate in candidates)
        {
            var source = await PhotoRows<VetPhotoSource>(scope).AsNoTracking().SingleOrDefaultAsync(s => s.Id == candidate.SourceId, ct);
            if (source == null || source.SourceMessageDbId == null || candidate.InputRevisionId != source.CurrentInputRevisionId
                || !await PhotoActorAsync(scope, source.SourceAuthorUserId, ct)) return false;
            var input = await PhotoRows<VetPhotoInputRevision>(scope).AsNoTracking().SingleOrDefaultAsync(i =>
                i.Id == source.CurrentInputRevisionId && i.SourceId == source.Id && i.Ordinal == source.CurrentOrdinal, ct);
            var batch = source.BatchId == null ? null : await PhotoRows<VetPhotoBatch>(scope).AsNoTracking()
                .SingleOrDefaultAsync(b => b.Id == source.BatchId && b.ProfileId == profileId && b.ProfileRevision == profile.Revision, ct);
            var original = input == null ? null : await PhotoRows<VetPhotoOriginalReference>(scope).AsNoTracking()
                .SingleOrDefaultAsync(r => r.InputRevisionId == input.Id && r.State == "retained", ct);
            VetPhotoEffectiveReading? effective;
            try { effective = JsonSerializer.Deserialize<VetPhotoEffectiveReading>(candidate.EffectiveJson, PhotoJson); }
            catch (JsonException) { effective = null; }
            if (batch == null || original == null || effective == null || effective.Value != state.Value
                || effective.Unit != state.Unit || effective.OccurredAt != state.OccurredAt) return false;
            if (sharedBlob != null && original.BlobId != sharedBlob) return false;
            sharedBlob = original.BlobId;
        }
        var canonical = candidates.Single(c => c.Id == canonicalCandidateId);
        if (state.SourceKind != "photo" || state.EventType != "glucose" || state.SourceId != canonical.SourceId || state.CandidateOrdinal != 0 || state.PhotoSourceId != canonical.SourceId
            || state.InputRevisionId != canonical.InputRevisionId || state.ExtractionResultId != canonical.ExtractionResultId) return false;
        return await ClosedCanonicalGroupCoreAsync(scope, profileId, canonical.SourceId, null,
            candidates.Select(c => c.SourceId).ToArray(), state, ct);
    }
    private static VetPhotoCandidateState PhotoCandidateState(VetPhotoCandidate c) => new(c.State,
        c.RequiresExplicitRestoration, c.ManuallyCorrected, c.CorrectionProvenanceJson, c.EffectiveJson,
        c.ReasonsJson, c.DuplicateDecision, c.DuplicateSourceId, c.DuplicateEventId, c.DuplicateEventRevision,
        c.EventId, c.EventRevision, c.InputRevisionId, c.ExtractionResultId, c.LastReviewId);
    private sealed class PhotoDiaryTracking(Assistant.Infrastructure.Persistence.AssistantDbContext context) : IDisposable
    {
        public void Dispose()
        {
            foreach (var entry in context.ChangeTracker.Entries().Where(e => e.Entity is VetEvent or VetDiaryAction or VetDiaryActionChange
                or VetPhotoCandidate or VetPhotoReview).ToArray()) entry.State = EntityState.Detached;
        }
    }
}
