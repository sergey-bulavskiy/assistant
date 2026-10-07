using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet;

public sealed partial class VetDiaryStore : IVetPhotoReversalStore
{
    private sealed record ReversalEvent(VetEvent Row, VetEventState Before, VetEventState After);
    private sealed record ReversalPrepared(VetDiaryAction Target, VetPhotoActionOutcome Original,
        IReadOnlyList<VetDiaryActionChange> AllEventChanges, PhotoUndoPreparation Photo,
        IReadOnlyList<ReversalEvent> Events, VetPhotoReversalPreview Preview);

    public async Task<VetPhotoReversalPreview?> ReadReversalAsync(VetDiaryScope scope, long actionId,
        IReadOnlyList<Guid> candidateIds, long actorUserId, CancellationToken ct)
    {
        await CheckAsync(scope, ct); ForgetDiaryPhotoSnapshots();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        if (!await PhotoActorAsync(scope, actorUserId, ct)) return null;
        ReversalPrepared? prepared;
        try { prepared = await PrepareReversalLockedAsync(scope, actionId, candidateIds, actorUserId, ct); }
        catch (JsonException) { return null; }
        await tx.CommitAsync(ct);
        return prepared?.Preview;
    }

    private async Task<ReversalPrepared?> PrepareReversalLockedAsync(VetDiaryScope scope, long actionId,
        IReadOnlyList<Guid> candidateIds, long actor, CancellationToken ct)
    {
        if (actionId <= 0 || candidateIds.Count > 50 || candidateIds.Any(i => i == Guid.Empty)
            || candidateIds.Distinct().Count() != candidateIds.Count) return null;
        var target = await Actions(scope).AsNoTracking().SingleOrDefaultAsync(a => a.Id == actionId, ct);
        var profile = await db.Set<VetProfile>().AsNoTracking().SingleOrDefaultAsync(p =>
            p.FamilyId == scope.FamilyId && p.BotDbId == scope.BotDbId, ct);
        if (target == null || profile == null) return null;
        VetPhotoActionOutcome? original;
        try { original = JsonSerializer.Deserialize<VetPhotoActionOutcome>(target.OutcomeJson); }
        catch (JsonException) { return null; }
        if (original?.PhotoChanges == null || original.PhotoChanges.Count < 1
            || original.ActionId != target.Id || original.PhotoChanges.Any(c => c == null)
            || original.PhotoChanges.Select(c => c.CandidateId).Distinct().Count() != original.PhotoChanges.Count
            || candidateIds.Any(id => original.PhotoChanges.All(c => c.CandidateId != id))) return null;
        if (candidateIds.Count == 0)
        { if (original.PhotoChanges.Count > 50) return null; candidateIds = original.PhotoChanges.Select(c => c.CandidateId).ToArray(); }
        var chosen = original.PhotoChanges.Where(c => candidateIds.Contains(c.CandidateId)).OrderBy(c => c.CandidateId).ToArray();
        var allChanges = await db.Set<VetDiaryActionChange>().AsNoTracking().Where(c =>
            c.FamilyId == scope.FamilyId && c.BotDbId == scope.BotDbId && c.ActionId == target.Id).OrderBy(c => c.Id).ToArrayAsync(ct);
        if (allChanges.Select(c => c.EventId).Distinct().Count() != allChanges.Length) return null;
        var ownedIds = chosen.SelectMany(c => new[] { c.Before.EventId, c.After.EventId }).Where(i => i != null).Select(i => i!.Value).ToHashSet();
        var changes = allChanges.Where(c => ownedIds.Contains(c.EventId)).ToArray();
        // Never reverse a linked canonical fact or an independent text/caption event.
        foreach (var change in changes)
        {
            var after = JsonSerializer.Deserialize<VetEventState>(change.AfterJson);
            if (after == null || after.SourceKind != "photo" || after.PhotoSourceId == null
                || chosen.All(c => c.SourceId != after.PhotoSourceId)) return null;
        }
        var selectedTarget = new VetDiaryAction { OutcomeJson = JsonSerializer.Serialize(original with { PhotoChanges = chosen }) };
        var photo = await PreparePhotoUndoAsync(scope, selectedTarget, changes, ct);
        var protectedEvents = new HashSet<long>(photo.ProtectedEventIds);
        var events = new List<ReversalEvent>();
        foreach (var change in changes)
        {
            if (protectedEvents.Contains(change.EventId)) continue;
            var row = await Events(scope).AsNoTracking().SingleOrDefaultAsync(e => e.Id == change.EventId, ct);
            if (row == null || row.Revision != change.AfterRevision) { protectedEvents.Add(change.EventId); continue; }
            var before = State(row);
            var after = change.BeforeJson == null ? before with {
                DeletedAt = clock.UtcNow, DeleteReason = "photo_reverse", DeletedByUserId = actor }
                : JsonSerializer.Deserialize<VetEventState>(change.BeforeJson)!;
            events.Add(new(row, before, after));
        }
        var additionallyProtected = photo.Inverses.Where(i =>
            i.Before.EventId is { } own && protectedEvents.Contains(own)
            || i.Before.DuplicateEventId is { } link && protectedEvents.Contains(link)).Select(i => i.Candidate.Id).ToArray();
        photo = photo with { Inverses = photo.Inverses.Where(i => !additionallyProtected.Contains(i.Candidate.Id)).ToArray(),
            ProtectedEventIds = protectedEvents,
            ProtectedCandidateIds = photo.ProtectedCandidateIds.Concat(additionallyProtected).Distinct().Order().ToArray() };
        events.RemoveAll(e => photo.ProtectedEventIds.Contains(e.Row.Id));
        var items = new List<VetPhotoReversalItem>();
        foreach (var change in chosen)
        {
            var source = await PhotoRows<VetPhotoSource>(scope).AsNoTracking().SingleOrDefaultAsync(s => s.Id == change.SourceId, ct);
            var candidate = await PhotoRows<VetPhotoCandidate>(scope).AsNoTracking().SingleOrDefaultAsync(c =>
                c.Id == change.CandidateId && c.SourceId == change.SourceId, ct);
            if (source == null || source.SourceMessageDbId == null || candidate == null) return null;
            var ownId = change.After.EventId ?? change.Before.EventId;
            var own = ownId == null ? null : await Events(scope).AsNoTracking().SingleOrDefaultAsync(e => e.Id == ownId, ct);
            var linkId = candidate.DuplicateEventId ?? change.After.DuplicateEventId;
            var linked = linkId == null ? null : await Events(scope).AsNoTracking().SingleOrDefaultAsync(e => e.Id == linkId, ct);
            var canonical = linkId == null ? null : await PhotoRows<VetPhotoCandidate>(scope).AsNoTracking()
                .SingleOrDefaultAsync(c => c.EventId == linkId, ct);
            var canonicalSource = canonical == null ? null : await PhotoRows<VetPhotoSource>(scope).AsNoTracking()
                .SingleOrDefaultAsync(s => s.Id == canonical.SourceId, ct);
            var history = allChanges.Where(c => ownedIds.Contains(c.EventId)
                && (c.EventId == change.Before.EventId || c.EventId == change.After.EventId)).ToArray();
            var inverse = photo.Inverses.SingleOrDefault(i => i.Candidate.Id == candidate.Id);
            var inverseEvent = ownId == null ? null : events.SingleOrDefault(e => e.Row.Id == ownId);
            var ownChange = history.SingleOrDefault();
            var entry = new VetPhotoReversalEntry(target.Id, target.Fingerprint, profile.Id, profile.Revision,
                candidate.Id, source.Id, candidate.Revision, source.CurrentInputRevisionId, source.CurrentOrdinal,
                ownId, own?.Revision, linkId, linked?.Revision,
                Hash(JsonSerializer.Serialize(new { target.Id, target.ActorUserId, target.Kind, target.Fingerprint,
                    target.ReversedByActionId, Change = change, Events = history })),
                Hash(JsonSerializer.Serialize(source)), Hash(JsonSerializer.Serialize(candidate)),
                Hash(JsonSerializer.Serialize(own)), Hash(JsonSerializer.Serialize(new { linked, canonical, canonicalSource })),
                photo.ProtectedCandidateIds.Contains(candidate.Id), ownId != null && protectedEvents.Contains(ownId.Value));
            items.Add(new(entry, source.SourceAuthorUserId, change.After.InputRevisionId, change.After.ExtractionResultId,
                change.Before, change.After, PhotoCandidateState(candidate), inverse?.After,
                ownChange?.BeforeJson == null ? null : JsonSerializer.Deserialize<VetEventState>(ownChange.BeforeJson),
                ownChange == null ? null : JsonSerializer.Deserialize<VetEventState>(ownChange.AfterJson),
                own == null ? null : State(own), linked == null ? null : State(linked), inverseEvent?.After));
        }
        return new(target, original, allChanges, photo, events,
            new(target.Id, target.ActorUserId, target.CreatedAt, profile.Id, profile.Revision, items));
    }

    public async Task<VetMutationResult> ApplyReversalAsync(VetPhotoDiaryAcceptance acceptance, CancellationToken ct)
    {
        var scope = acceptance.Scope; await CheckAsync(scope, ct); ForgetDiaryPhotoSnapshots();
        using var tracking = new PhotoDiaryTracking(db);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        if (!await PhotoActorAsync(scope, acceptance.ActorUserId, ct)) return VetMutationResult.Of(VetMutationStatus.Refused);
        var review = await PhotoRows<VetPhotoReview>(scope).AsNoTracking().SingleOrDefaultAsync(r => r.Id == acceptance.ReviewId, ct);
        if (review == null) return VetMutationResult.Of(VetMutationStatus.NotFound);
        if (review.Kind != "reverse" || review.RunWindowId != null || review.BatchId != null
            || review.Revision != acceptance.ReviewRevision || review.OperationKey != acceptance.OperationKey
            || acceptance.OperationKey == Guid.Empty || acceptance.CallbackPromptMessageId is { } prompt && prompt != review.AcceptancePromptMessageId)
            return VetMutationResult.Of(VetMutationStatus.Stale);
        if (!VetPhotoStore.HasCompletePreview(review)) return VetMutationResult.Of(VetMutationStatus.Stale);
        if (!string.Equals(Hash(review.SelectionJson), review.Fingerprint, StringComparison.OrdinalIgnoreCase))
            return VetMutationResult.Of(VetMutationStatus.Refused);
        if (review.State == "accepted" && review.OutcomeJson != null && review.ActionId != null)
        {
            var saved = await Actions(scope).AsNoTracking().SingleOrDefaultAsync(a => a.Id == review.ActionId
                && a.OperationKey == review.OperationKey && a.Kind == "photo_reverse", ct);
            return saved == null ? VetMutationResult.Of(VetMutationStatus.Refused)
                : JsonSerializer.Deserialize<VetMutationResult>(review.OutcomeJson)! with { Status = VetMutationStatus.AlreadyApplied };
        }
        if (review.State != "preview") return VetMutationResult.Of(VetMutationStatus.Stale);
        VetPhotoReversalEntry[]? selected;
        try { selected = JsonSerializer.Deserialize<VetPhotoReversalEntry[]>(review.SelectionJson, PhotoJson); }
        catch (JsonException) { return VetMutationResult.Of(VetMutationStatus.Refused); }
        if (selected == null || selected.Length is < 1 or > 50 || selected.Any(s => s == null)
            || selected.Select(s => s.CandidateId).Distinct().Count() != selected.Length
            || selected.Select(s => s.ActionId).Distinct().Count() != 1)
            return VetMutationResult.Of(VetMutationStatus.Refused);
        ReversalPrepared? prepared;
        try { prepared = await PrepareReversalLockedAsync(scope, selected[0].ActionId,
            selected.Select(s => s.CandidateId).ToArray(), acceptance.ActorUserId, ct); }
        catch (JsonException) { return VetMutationResult.Of(VetMutationStatus.Refused); }
        if (prepared == null || review.ProfileId != prepared.Preview.ProfileId
            || review.ProfileRevision != prepared.Preview.ProfileRevision
            || JsonSerializer.Serialize(prepared.Preview.Items.Select(i => i.Entry).ToArray(), PhotoJson) != review.SelectionJson)
            return VetMutationResult.Of(VetMutationStatus.Stale);
        var fingerprint = Hash(JsonSerializer.Serialize(new { Kind = "photo_reverse", review.Id, review.SelectionJson }));
        if (await db.Set<VetDiaryAction>().AnyAsync(a => a.FamilyId == scope.FamilyId && a.BotDbId == scope.BotDbId
            && a.OperationKey == review.OperationKey, ct)) return VetMutationResult.Of(VetMutationStatus.Refused);
        var action = NewAction(scope, review.OperationKey, acceptance.ActorUserId, "photo_reverse", fingerprint);
        action.ReversesActionId = prepared.Target.Id; action.PhotoBatchId = prepared.Target.PhotoBatchId;
        db.Add(action);
        foreach (var inverse in prepared.Events)
        {
            db.Attach(inverse.Row); ApplyState(inverse.Row, inverse.After);
            inverse.Row.Revision = checked(inverse.Row.Revision + 1);
            inverse.Row.UpdatedAt = clock.UtcNow; inverse.Row.LastMutationKind = "photo_reverse";
        }
        await db.SaveChangesAsync(ct);
        foreach (var inverse in prepared.Events) db.Add(new VetDiaryActionChange {
            FamilyId = scope.FamilyId, BotDbId = scope.BotDbId, ActionId = action.Id, EventId = inverse.Row.Id,
            BeforeJson = JsonSerializer.Serialize(inverse.Before), AfterJson = JsonSerializer.Serialize(inverse.After),
            BeforeRevision = inverse.Row.Revision - 1, AfterRevision = inverse.Row.Revision });
        var photoChanges = await ApplyPhotoUndoAsync(scope, prepared.Photo,
            prepared.Events.ToDictionary(e => e.Row.Id, e => e.Row.Revision), ct);
        var status = prepared.Events.Count == 0 && photoChanges.Count == 0 ? VetMutationStatus.NoChange : VetMutationStatus.Applied;
        var result = new VetMutationResult(status, action.Id, prepared.Events.Select(e => e.Row.Id).Order().ToArray(),
            prepared.Photo.ProtectedEventIds.Order().ToArray()) {
            Revisions = prepared.Events.Select(e => new VetEventRevision(e.Row.Id, e.Row.Revision)).ToArray(),
            ProtectedCandidateIds = prepared.Photo.ProtectedCandidateIds };
        action.OutcomeJson = JsonSerializer.Serialize(new VetPhotoActionOutcome(status, action.Id,
            result.EventIds, result.ProtectedIds, result.Revisions, photoChanges) { ProtectedCandidateIds = result.ProtectedCandidateIds });
        if (photoChanges.Count == prepared.Original.PhotoChanges.Count
            && prepared.Events.Count == prepared.AllEventChanges.Count && result.ProtectedIds.Count == 0
            && result.ProtectedCandidateIds.Count == 0)
        { db.Attach(prepared.Target); prepared.Target.ReversedByActionId = action.Id; }
        db.Attach(review); review.State = "accepted"; review.DecisionActorUserId = acceptance.ActorUserId;
        review.DecidedAt = clock.UtcNow; review.ActionId = action.Id; review.OutcomeJson = JsonSerializer.Serialize(result);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return result;
    }
}
