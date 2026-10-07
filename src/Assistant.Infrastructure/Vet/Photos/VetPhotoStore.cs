using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore(
    AssistantDbContext db, ICurrentFamily current, IClock clock,
    VetPhotoCapacity capacity, IVetPhotoImageDecoder decoder) : IVetPhotoArchiveStore
{
    private readonly VetStoreGuard _guard = new(db, current);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IQueryable<T> Scoped<T>(VetDiaryScope scope) where T : class =>
        db.Set<T>().Where(x => EF.Property<long>(x, "FamilyId") == scope.FamilyId
            && EF.Property<long>(x, "BotDbId") == scope.BotDbId
            && EF.Property<long>(x, "TelegramBotId") == scope.TelegramBotId
            && EF.Property<long>(x, "ChatId") == scope.ChatId
            && EF.Property<int?>(x, "TopicId") == scope.TopicId);

    private T InScope<T>(T value, VetDiaryScope scope) where T : class
    {
        var entry = db.Entry(value);
        entry.Property("FamilyId").CurrentValue = scope.FamilyId;
        entry.Property("BotDbId").CurrentValue = scope.BotDbId;
        entry.Property("TelegramBotId").CurrentValue = scope.TelegramBotId;
        entry.Property("ChatId").CurrentValue = scope.ChatId;
        entry.Property("TopicId").CurrentValue = scope.TopicId;
        return value;
    }

    private Task CheckAsync(VetDiaryScope scope, CancellationToken ct)
    {
        ForgetPhotoSnapshots();
        return _guard.BotAsync(scope.FamilyId, scope.BotDbId, scope.TelegramBotId, ct);
    }

    private void ForgetPhotoSnapshots()
    {
        foreach (var entry in db.ChangeTracker.Entries().ToArray())
        {
            if (entry.Entity.GetType().Namespace != typeof(VetPhotoSource).Namespace) continue;
            if (entry.State != EntityState.Unchanged)
                throw new InvalidOperationException("Photo context has unfinished changes.");
            entry.State = EntityState.Detached;
        }
    }

    private async Task<bool> ActorAsync(
        VetDiaryScope scope, long actor, CancellationToken ct, string? incomingChatType = null)
    {
        if (actor <= 0 || !await db.FamilyMembers.AnyAsync(m => m.FamilyId == scope.FamilyId
                && m.TelegramUserId == actor && m.Status == FamilyMemberStatus.Approved, ct))
            return false;
        if (incomingChatType == "private" && scope.ChatId == actor) return true;
        if (scope.ChatId == actor && await Scoped<VetPhotoSource>(scope).AnyAsync(
                s => s.ChatType == "private" && s.SourceAuthorUserId == actor, ct))
            return true;
        return await db.Places.AnyAsync(p => p.BotId == scope.BotDbId
            && p.ChatId == scope.ChatId && p.TopicId == scope.TopicId
            && p.Status == PlaceStatus.Approved, ct);
    }

    private Task<bool> OwnerAsync(VetDiaryScope scope, long actor, CancellationToken ct) =>
        db.FamilyMembers.AnyAsync(m => m.FamilyId == scope.FamilyId
            && m.TelegramUserId == actor && m.Status == FamilyMemberStatus.Approved
            && m.IsOwner, ct);

    private async Task CapacityLockAsync(CancellationToken ct)
    {
        if (!capacity.IsValid) throw new InvalidOperationException("Photo capacity is invalid.");
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('vet-photo-capacity-v1', 0))", ct);
    }

    private async Task<VetPhotoCapacityTotals> TotalsLockedAsync(CancellationToken ct)
    {
        // Cross-family access is confined to six numeric aggregates. Never project private rows.
        var bytes = await db.Database.SqlQueryRaw<long>(
            "SELECT COALESCE(SUM(actual_bytes), 0)::bigint AS \"Value\" FROM vet_photo_blobs WHERE content IS NOT NULL")
            .SingleAsync(ct);
        var reservedBytes = await db.Database.SqlQueryRaw<long>(
            "SELECT COALESCE(SUM(reserved_bytes), 0)::bigint AS \"Value\" FROM vet_photo_attempts WHERE reserved_bytes > 0")
            .SingleAsync(ct);
        var inputs = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*)::bigint AS \"Value\" FROM vet_photo_original_references WHERE state = 'retained'")
            .SingleAsync(ct);
        var reservedInputs = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*)::bigint AS \"Value\" FROM vet_photo_attempts WHERE reserved_input_slot")
            .SingleAsync(ct);
        var results = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*)::bigint AS \"Value\" FROM vet_photo_extractions")
            .SingleAsync(ct);
        var reservedResults = await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*)::bigint AS \"Value\" FROM vet_photo_attempts WHERE reserved_result_slot")
            .SingleAsync(ct);
        return new(bytes, reservedBytes, inputs, reservedInputs, results, reservedResults);
    }

    public async Task<VetPhotoCapacityTotals> GetCapacityAsync(
        VetDiaryScope scope, long actorUserId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await CapacityLockAsync(ct);
        await _guard.LockAsync(scope.FamilyId, scope.BotDbId, ct);
        if (!await ActorAsync(scope, actorUserId, ct))
            throw new InvalidOperationException("Photo actor is not approved.");
        var totals = await TotalsLockedAsync(ct);
        await tx.CommitAsync(ct);
        return totals;
    }

    private HashSet<object> TrackedBefore() =>
        db.ChangeTracker.Entries().Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);

    private void DetachOwned(HashSet<object> before)
    {
        foreach (var entry in db.ChangeTracker.Entries().ToArray())
            if (!before.Contains(entry.Entity)
                && (entry.Entity.GetType().Namespace == typeof(VetPhotoSource).Namespace
                    || entry.Entity is Assistant.Domain.Vet.VetProfile))
                entry.State = EntityState.Detached;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static bool HasCompletePreview(VetPhotoReview review) => ReviewDelivered(review);

    private static bool ReviewDelivered(VetPhotoReview review)
    {
        if (!review.CompletePreviewDelivered || review.AcceptancePromptMessageId is not > 0
            || review.PageCount is <= 0 or > VetPhotoReviewBounds.MaxPages
            || review.Fingerprint != Hash(Encoding.UTF8.GetBytes(review.SelectionJson)))
            return false;
        try
        {
            var pages = JsonSerializer.Deserialize<string[]>(review.PreviewPagesJson, Json) ?? [];
            var deliveries = JsonSerializer.Deserialize<VetPhotoPageDelivery[]>(review.DeliveredPagesJson, Json) ?? [];
            if (pages.Length != review.PageCount || deliveries.Length != review.PageCount
                || deliveries.Any(d => d is null)
                || deliveries.Select(d => d.PageIndex).Distinct().Count() != review.PageCount)
                return false;
            for (var i = 0; i < pages.Length; i++)
            {
                if (pages[i] is not { Length: > 0 and <= 3500 }) return false;
                var delivery = deliveries.SingleOrDefault(d => d.PageIndex == i);
                if (delivery is null || delivery.MessageId <= 0
                    || delivery.TextHash != Hash(Encoding.UTF8.GetBytes(pages[i])))
                    return false;
            }
            return deliveries.Single(d => d.PageIndex == pages.Length - 1).MessageId
                == review.AcceptancePromptMessageId;
        }
        catch (JsonException) { return false; }
    }

    private static void ReleaseReservation(VetPhotoAttempt attempt)
    {
        attempt.ReservedBytes = 0;
        attempt.ReservedInputSlot = false;
        attempt.ReservedResultSlot = false;
    }

    private async Task InvalidateBatchAsync(
        VetDiaryScope scope, Guid? batchId, CancellationToken ct)
    {
        if (batchId is not { } id) return;
        await Scoped<VetPhotoBatch>(scope).Where(b => b.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(b => b.ReviewRevision, b => b.ReviewRevision + 1)
                .SetProperty(b => b.UpdatedAt, clock.UtcNow), ct);
        await Scoped<VetPhotoReview>(scope).Where(r => r.BatchId == id && r.State == "preview")
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, "stale")
                .SetProperty(r => r.CompletePreviewDelivered, false), ct);
    }
}
