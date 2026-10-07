using System.Text.Json;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore
{
    public async Task<VetPhotoOriginalRead?> ReadOriginalAsync(
        VetDiaryScope scope, Guid inputRevisionId, long actorUserId,
        Guid attemptId, Guid claimToken, DateTimeOffset leaseUntil, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        var before = TrackedBefore();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await CapacityLockAsync(ct);
            await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
            if (!await ActorAsync(scope, actorUserId, ct)) return null;
            var attempt = await Scoped<VetPhotoAttempt>(scope).AsNoTracking().SingleOrDefaultAsync(
                a => a.Id == attemptId && a.InputRevisionId == inputRevisionId && a.Kind == "image"
                    && a.State == "claimed" && a.ClaimToken == claimToken && a.ActorUserId == actorUserId, ct);
            if (attempt is null || leaseUntil <= clock.UtcNow || attempt.LeaseUntil != leaseUntil)
                return null;
            var input = await Scoped<VetPhotoInputRevision>(scope).AsNoTracking()
                .SingleOrDefaultAsync(r => r.Id == inputRevisionId, ct);
            if (input is null) return null;
            var source = await Scoped<VetPhotoSource>(scope).AsNoTracking()
                .SingleOrDefaultAsync(s => s.Id == input.SourceId && s.SourceMessageDbId != null, ct);
            if (source is null || source.CurrentInputRevisionId != attempt.ExpectedCurrentInputId
                || source.CurrentOrdinal != attempt.ExpectedSourceOrdinal)
                return null;
            var reference = await Scoped<VetPhotoOriginalReference>(scope).AsNoTracking()
                .SingleOrDefaultAsync(r => r.InputRevisionId == inputRevisionId && r.State == "retained", ct);
            if (reference is null) return null;
            var blob = await db.Set<VetPhotoBlob>().AsNoTracking().SingleOrDefaultAsync(
                b => b.FamilyId == scope.FamilyId && b.Id == reference.BlobId && b.Content != null, ct);
            if (blob?.Content is null) return null;
            var lease = await Scoped<VetPhotoReaderLease>(scope).AsNoTracking().SingleOrDefaultAsync(
                l => l.AttemptId == attemptId && l.ClaimToken == claimToken, ct);
            if (lease is not null && (lease.ReleasedAt != null || lease.OriginalReferenceId != reference.Id
                    || lease.OriginalReferenceRevision != reference.Revision))
                return null;
            if (lease is null)
            {
                lease = InScope(new VetPhotoReaderLease
                {
                    Id = Guid.NewGuid(), BlobId = blob.Id, OriginalReferenceId = reference.Id,
                    OriginalReferenceRevision = reference.Revision, AttemptId = attemptId,
                    ClaimToken = claimToken, CreatedAt = clock.UtcNow, ExpiresAt = leaseUntil
                }, scope);
                db.Add(lease);
                await db.SaveChangesAsync(ct);
            }
            var copy = blob.Content.ToArray();
            await tx.CommitAsync(ct);
            return new(lease.Id, inputRevisionId, reference.Id, reference.Revision, copy,
                new(blob.Width, blob.Height, blob.Format == "png" ? "image/png" : "image/jpeg",
                    blob.ActualBytes, checked((long)blob.Width * blob.Height * 4)));
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            DetachOwned(before);
            throw;
        }
    }

    public async Task ReleaseReaderAsync(VetDiaryScope scope, Guid readerLeaseId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await CapacityLockAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        await Scoped<VetPhotoReaderLease>(scope).Where(l => l.Id == readerLeaseId && l.ReleasedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.ReleasedAt, clock.UtcNow), ct);
        await tx.CommitAsync(ct);
    }

    public async Task<VetPhotoOriginalDeletionResult> DeleteOriginalsAsync(
        VetPhotoOriginalDeletion deletion, CancellationToken ct)
    {
        var scope = deletion.Scope;
        await CheckAsync(scope, ct);
        var before = TrackedBefore();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await CapacityLockAsync(ct);
            await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
            if (!await ActorAsync(scope, deletion.ActorUserId, ct)
                || !await OwnerAsync(scope, deletion.ActorUserId, ct))
                return new(VetMutationStatus.Refused, 0, 0);
            var review = await Scoped<VetPhotoReview>(scope).AsNoTracking()
                .SingleOrDefaultAsync(r => r.Id == deletion.ReviewId, ct);
            if (review is null || review.Kind != "delete_originals"
                || review.OperationKey != deletion.OperationKey)
                return new(VetMutationStatus.Refused, 0, 0);
            if (review.State == "accepted" && review.OutcomeJson is { } outcome)
                return JsonSerializer.Deserialize<VetPhotoOriginalDeletionResult>(outcome, Json)!
                    with { Status = VetMutationStatus.AlreadyApplied };
            if (review.RunWindowId is { } runWindowId)
            {
                var window = await Scoped<VetPhotoRunWindow>(scope).AsNoTracking()
                    .SingleOrDefaultAsync(w => w.Id == runWindowId, ct);
                var run = window == null ? null : await OwnedRunAsync(new(scope, window.RunId, deletion.ActorUserId), ct);
                if (run == null || !DeletionRun(run) || run.State != "running" || run.CancelledAt != null
                    || window!.Ordinal != run.NextWindowOrdinal || window.State != "awaiting_review"
                    || window.ComparisonReviewId != review.Id)
                    return new(VetMutationStatus.Stale, 0, 0);
            }
            if (review.State != "preview" || review.Revision != deletion.ReviewRevision
                || !ReviewDelivered(review)
                || deletion.CallbackPromptMessageId is { } prompt && prompt != review.AcceptancePromptMessageId)
                return new(VetMutationStatus.Stale, 0, 0);
            var selection = JsonSerializer.Deserialize<VetPhotoOriginalSelection[]>(review.SelectionJson, Json) ?? [];
            if (selection.Length == 0 || selection.Length > capacity.MaxInputRevisions
                || selection.Select(s => s.ReferenceId).Distinct().Count() != selection.Length)
                return new(VetMutationStatus.Refused, 0, 0);
            var references = new List<VetPhotoOriginalReference>();
            foreach (var selected in selection)
            {
                var reference = await Scoped<VetPhotoOriginalReference>(scope).AsNoTracking().SingleOrDefaultAsync(
                    r => r.Id == selected.ReferenceId && r.InputRevisionId == selected.InputRevisionId
                        && r.BlobId == selected.BlobId && r.Revision == selected.Revision && r.State == "retained", ct);
                var source = await Scoped<VetPhotoSource>(scope).AsNoTracking().SingleOrDefaultAsync(
                    s => s.Id == selected.SourceId && s.CurrentInputRevisionId == selected.ExpectedCurrentInputId
                        && s.CurrentOrdinal == selected.ExpectedSourceOrdinal, ct);
                if (reference is null || source is null
                    || !await Scoped<VetPhotoInputRevision>(scope).AnyAsync(
                        r => r.Id == reference.InputRevisionId && r.SourceId == source.Id, ct))
                    return new(VetMutationStatus.Stale, 0, 0);
                var retained = await db.Set<VetPhotoOriginalReference>().LongCountAsync(
                    r => r.FamilyId == scope.FamilyId && r.BlobId == selected.BlobId && r.State == "retained", ct);
                if (retained != selected.ExpectedBlobRetainedReferences)
                    return new(VetMutationStatus.Stale, 0, 0);
                if (selected.EventId is { } eventId && !await db.Set<VetEvent>().AnyAsync(
                        e => e.Id == eventId && e.FamilyId == scope.FamilyId && e.BotDbId == scope.BotDbId
                            && e.Revision == selected.EventRevision, ct))
                    return new(VetMutationStatus.Stale, 0, 0);
                references.Add(reference);
            }
            foreach (var reference in references)
            {
                db.Attach(reference);
                reference.Revision++;
                reference.State = "deleted";
                reference.DeletedAt = clock.UtcNow;
                reference.DeletedByUserId = deletion.ActorUserId;
                reference.DeletionReviewId = review.Id;
            }
            await db.SaveChangesAsync(ct);
            long reclaimable = 0;
            foreach (var blobId in references.Select(r => r.BlobId).Distinct())
            {
                if (await db.Set<VetPhotoOriginalReference>().AnyAsync(
                        r => r.FamilyId == scope.FamilyId && r.BlobId == blobId && r.State == "retained", ct))
                    continue;
                var blob = await db.Set<VetPhotoBlob>().SingleAsync(
                    b => b.Id == blobId && b.FamilyId == scope.FamilyId, ct);
                if (blob.Content is null) continue;
                blob.State = "reclaim_pending";
                blob.ReclaimRequestedAt = clock.UtcNow;
                if (!await db.Set<VetPhotoReaderLease>().AnyAsync(
                        l => l.FamilyId == scope.FamilyId && l.BlobId == blobId
                            && l.ReleasedAt == null && l.ExpiresAt > clock.UtcNow, ct))
                    reclaimable = checked(reclaimable + blob.ActualBytes);
            }
            var result = new VetPhotoOriginalDeletionResult(VetMutationStatus.Applied, references.Count, reclaimable);
            db.Attach(review);
            review.State = "accepted";
            review.DecisionActorUserId = deletion.ActorUserId;
            review.DecidedAt = clock.UtcNow;
            review.OutcomeJson = JsonSerializer.Serialize(result, Json);
            foreach (var batchId in await Scoped<VetPhotoSource>(scope)
                    .Where(s => selection.Select(x => x.SourceId).Contains(s.Id) && s.BatchId != null)
                    .Select(s => s.BatchId).Distinct().ToListAsync(ct))
                await InvalidateBatchAsync(scope, batchId, ct);
            // Invalidation targets preview rows; keep this resolved review accepted after its own deletion.
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

    public async Task<int> ReclaimAsync(long familyId, int limit, CancellationToken ct)
    {
        _guard.Family(familyId);
        ForgetPhotoSnapshots();
        var before = TrackedBefore();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await CapacityLockAsync(ct);
            var blobs = await db.Set<VetPhotoBlob>().Where(b => b.FamilyId == familyId
                    && b.State == "reclaim_pending" && b.Content != null)
                .OrderBy(b => b.ReclaimRequestedAt).ThenBy(b => b.Id).Take(Math.Clamp(limit, 1, 50)).ToListAsync(ct);
            var reclaimed = 0;
            foreach (var blob in blobs)
            {
                if (await db.Set<VetPhotoOriginalReference>().AnyAsync(
                        r => r.FamilyId == familyId && r.BlobId == blob.Id && r.State == "retained", ct))
                {
                    blob.State = "retained";
                    blob.ReclaimRequestedAt = null;
                    continue;
                }
                if (await db.Set<VetPhotoReaderLease>().AnyAsync(
                        l => l.FamilyId == familyId && l.BlobId == blob.Id
                            && l.ReleasedAt == null && l.ExpiresAt > clock.UtcNow, ct))
                    continue;
                blob.Content = null;
                blob.State = "reclaimed";
                blob.ReclaimedAt = clock.UtcNow;
                reclaimed++;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return reclaimed;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            DetachOwned(before);
            throw;
        }
    }
}
