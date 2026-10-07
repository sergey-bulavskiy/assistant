using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore
{
    public async Task<VetPhotoReservation> ReserveDownloadAsync(
        VetDiaryScope scope, Guid sourceId, Guid inputRevisionId, long actorUserId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        var before = TrackedBefore();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await CapacityLockAsync(ct);
            await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
            if (!await ActorAsync(scope, actorUserId, ct))
                return new(VetPhotoArchiveStatus.Refused, null, null);
            var source = await Scoped<VetPhotoSource>(scope).AsNoTracking().SingleOrDefaultAsync(s => s.Id == sourceId, ct);
            var input = await Scoped<VetPhotoInputRevision>(scope).AsNoTracking()
                .SingleOrDefaultAsync(r => r.Id == inputRevisionId && r.SourceId == sourceId, ct);
            if (source is null || input is null) return new(VetPhotoArchiveStatus.NotFound, null, null);
            if (source.SourceMessageDbId is null || source.State is "late" or "full")
                return new(VetPhotoArchiveStatus.Refused, null, null);
            var reference = await Scoped<VetPhotoOriginalReference>(scope).AsNoTracking()
                .SingleOrDefaultAsync(r => r.InputRevisionId == inputRevisionId, ct);
            if (reference is not null)
                return new(reference.State == "retained" ? VetPhotoArchiveStatus.Existing
                    : VetPhotoArchiveStatus.OriginalDeleted, null, reference);
            if (input.ReusesImageInputId is { } oldId
                && await Scoped<VetPhotoOriginalReference>(scope).AnyAsync(
                    r => r.InputRevisionId == oldId && r.State == "deleted", ct))
                return new(VetPhotoArchiveStatus.OriginalDeleted, null, null);
            var attempt = await Scoped<VetPhotoAttempt>(scope).AsNoTracking()
                .Where(a => a.InputRevisionId == inputRevisionId && a.Kind == "download")
                .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).FirstOrDefaultAsync(ct);
            if (attempt is null || attempt.State is "failed" or "cancelled" or "paused"
                || attempt.RetryNotBefore > clock.UtcNow)
                return new(VetPhotoArchiveStatus.Refused, null, null);
            if (attempt.State == "downloading" && attempt.LeaseUntil > clock.UtcNow)
                return new(VetPhotoArchiveStatus.Existing, null, null);
            if (attempt.DownloadAttemptCount >= 2)
                return new(VetPhotoArchiveStatus.Refused, null, null);
            var totals = await TotalsLockedAsync(ct);
            totals = totals with
            {
                ReservedBytes = totals.ReservedBytes - attempt.ReservedBytes,
                ReservedInputs = totals.ReservedInputs - (attempt.ReservedInputSlot ? 1 : 0)
            };
            var reserve = input.ReportedSize is > 0 and <= 10_485_760 ? input.ReportedSize.Value : 10_485_760;
            if (input.ReportedSize > 10_485_760)
                return new(VetPhotoArchiveStatus.InvalidImage, null, null);
            VetPhotoOriginalReference? reusable = null;
            if (input.ReusesImageInputId is { } reuseId)
                reusable = await Scoped<VetPhotoOriginalReference>(scope).AsNoTracking()
                    .SingleOrDefaultAsync(r => r.InputRevisionId == reuseId && r.State == "retained", ct);
            if (!totals.CanReserve(capacity, reusable is null ? reserve : 0, input: true, result: false))
                return new(VetPhotoArchiveStatus.CapacityFull, null, null);
            db.Attach(attempt);
            ReleaseReservation(attempt);
            if (reusable is not null)
            {
                var blob = await db.Set<VetPhotoBlob>().SingleAsync(
                    b => b.Id == reusable.BlobId && b.FamilyId == scope.FamilyId && b.Content != null, ct);
                blob.State = "retained";
                blob.ReclaimRequestedAt = null;
                reference = InScope(new VetPhotoOriginalReference
                {
                    Id = Guid.NewGuid(), InputRevisionId = inputRevisionId, BlobId = blob.Id,
                    ContentHash = blob.ContentHash, ActualBytes = blob.ActualBytes,
                    Format = blob.Format, Width = blob.Width, Height = blob.Height, RetainedAt = clock.UtcNow
                }, scope);
                db.Add(reference);
                attempt.State = "retained";
                attempt.ClaimToken = null;
                attempt.LeaseUntil = null;
                attempt.UpdatedAt = clock.UtcNow;
                if (source.CurrentInputRevisionId == inputRevisionId)
                    await Scoped<VetPhotoSource>(scope).Where(s => s.Id == sourceId)
                        .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, "retained"), ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new(VetPhotoArchiveStatus.Retained, null, reference);
            }
            var token = Guid.NewGuid();
            attempt.ActorUserId = actorUserId;
            attempt.State = "downloading";
            attempt.ClaimToken = token;
            attempt.LeaseUntil = clock.UtcNow.AddMinutes(2);
            attempt.ReservedBytes = reserve;
            attempt.ReservedInputSlot = true;
            attempt.DownloadAttemptCount++;
            attempt.ExpectedSourceOrdinal = source.CurrentOrdinal;
            attempt.ExpectedCurrentInputId = source.CurrentInputRevisionId;
            attempt.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(VetPhotoArchiveStatus.Reserved, new(scope, source, input, attempt, token), null);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            DetachOwned(before);
            throw;
        }
    }

    public async Task<VetPhotoArchiveResult> CommitOriginalAsync(VetPhotoArchiveCommit commit, CancellationToken ct)
    {
        await CheckAsync(commit.Scope, ct);
        if (commit.Original.Length is <= 0 or > 10_485_760)
            return new(VetPhotoArchiveStatus.InvalidImage, null);
        // The admitted copy is stable even if the transport caller later mutates its buffer.
        var original = commit.Original.ToArray();
        var decoded = decoder.Decode(original, ct);
        if (decoded.Image is not { } image || image != commit.Image)
            return new(VetPhotoArchiveStatus.InvalidImage, null);
        var format = image.MimeType == "image/png" ? "png" : "jpeg";
        var hash = Hash(original);
        var before = TrackedBefore();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await CapacityLockAsync(ct);
            await _guard.LockAsync(commit.Scope.FamilyId, commit.Scope.BotDbId, ct);
            var attempt = await Scoped<VetPhotoAttempt>(commit.Scope).AsNoTracking()
                .SingleOrDefaultAsync(a => a.Id == commit.AttemptId && a.InputRevisionId == commit.InputRevisionId
                    && a.Kind == "download", ct);
            var existing = await Scoped<VetPhotoOriginalReference>(commit.Scope).AsNoTracking()
                .SingleOrDefaultAsync(r => r.InputRevisionId == commit.InputRevisionId, ct);
            if (existing is not null)
                return new(existing.State == "retained" && existing.ContentHash == hash
                    ? VetPhotoArchiveStatus.Existing : VetPhotoArchiveStatus.OriginalDeleted, existing);
            if (attempt is null || attempt.State != "downloading" || attempt.ClaimToken != commit.ClaimToken
                || attempt.ActorUserId != commit.ActorUserId || attempt.LeaseUntil is not { } until
                || until <= clock.UtcNow)
                return new(VetPhotoArchiveStatus.Stale, null);
            if (!await ActorAsync(commit.Scope, commit.ActorUserId, ct))
                return new(VetPhotoArchiveStatus.Refused, null);
            var input = await Scoped<VetPhotoInputRevision>(commit.Scope).AsNoTracking()
                .SingleAsync(r => r.Id == commit.InputRevisionId && r.SourceId == attempt.SourceId, ct);
            var source = await Scoped<VetPhotoSource>(commit.Scope).AsNoTracking().SingleAsync(
                s => s.Id == input.SourceId && s.SourceMessageDbId != null, ct);
            var blob = await db.Set<VetPhotoBlob>().SingleOrDefaultAsync(
                b => b.FamilyId == commit.Scope.FamilyId && b.ContentHash == hash, ct);
            var totals = await TotalsLockedAsync(ct);
            totals = totals with
            {
                ReservedBytes = totals.ReservedBytes - attempt.ReservedBytes,
                ReservedInputs = totals.ReservedInputs - (attempt.ReservedInputSlot ? 1 : 0)
            };
            var newBytes = blob?.Content is not null ? 0 : original.LongLength;
            if (!totals.CanReserve(capacity, newBytes, input: true, result: false))
                return new(VetPhotoArchiveStatus.CapacityFull, null);
            if (blob is null)
            {
                blob = new VetPhotoBlob
                {
                    Id = Guid.NewGuid(), FamilyId = commit.Scope.FamilyId, ContentHash = hash,
                    ActualBytes = original.LongLength, Format = format, Width = image.Width,
                    Height = image.Height, Content = original, CreatedAt = clock.UtcNow
                };
                db.Add(blob);
            }
            else
            {
                if (blob.ActualBytes != original.LongLength || blob.Format != format
                    || blob.Width != image.Width || blob.Height != image.Height)
                    throw new InvalidOperationException("Photo content identity conflict.");
                blob.Content ??= original;
                blob.State = "retained";
                blob.ReclaimRequestedAt = null;
                blob.ReclaimedAt = null;
            }
            var reference = InScope(new VetPhotoOriginalReference
            {
                Id = Guid.NewGuid(), InputRevisionId = input.Id, BlobId = blob.Id, ContentHash = hash,
                ActualBytes = original.LongLength, Format = format, Width = image.Width,
                Height = image.Height, RetainedAt = clock.UtcNow
            }, commit.Scope);
            db.Add(reference);
            db.Attach(attempt);
            ReleaseReservation(attempt);
            attempt.State = "retained";
            attempt.ClaimToken = null;
            attempt.LeaseUntil = null;
            attempt.UpdatedAt = clock.UtcNow;
            if (source.CurrentInputRevisionId == input.Id)
                await Scoped<VetPhotoSource>(commit.Scope).Where(s => s.Id == source.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, "retained"), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(VetPhotoArchiveStatus.Retained, reference);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            DetachOwned(before);
            throw;
        }
    }

    public async Task<bool> RecordDownloadFailureAsync(
        VetDiaryScope scope, Guid attemptId, Guid claimToken, string category,
        bool knownTransient, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        if (category is not ("download_timeout" or "download_unavailable" or "download_too_large"
            or "download_cancelled" or "invalid_image" or "encoded_image_too_large"
            or "decoded_image_too_large" or "image_metadata_too_large" or "unsupported_image"
            or "decoder_unavailable"))
            throw new InvalidOperationException("Photo failure category is invalid.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await CapacityLockAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        var attempt = await Scoped<VetPhotoAttempt>(scope).SingleOrDefaultAsync(
            a => a.Id == attemptId && a.Kind == "download"
                && a.State == "downloading" && a.ClaimToken == claimToken, ct);
        if (attempt is null) return false;
        // Unavailable includes permanent Bot API failures. Only an actual known timeout auto-retries.
        var retry = knownTransient && category == "download_timeout" && attempt.DownloadAttemptCount < 2;
        ReleaseReservation(attempt);
        attempt.State = retry ? "retry_wait" : category == "download_cancelled" ? "paused" : "failed";
        attempt.FailureCategory = category;
        attempt.ClaimToken = null;
        attempt.LeaseUntil = null;
        attempt.RetryNotBefore = retry ? clock.UtcNow.AddSeconds(5) : null;
        attempt.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
