using System.Text.Json;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Vet;
using Assistant.Domain.Vet;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet;

public sealed class VetProfileStore(
    AssistantDbContext db, ICurrentFamily current, IFamilyOwnership ownership, IClock clock) : IVetProfileStore
{
    private readonly VetStoreGuard _guard = new(db, current);

    public async Task<VetProfile> GetOrCreateAsync(long familyId, long botDbId, CancellationToken ct)
    {
        _guard.Family(familyId);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await _guard.LockAsync(familyId, botDbId, ct);
        var profile = await db.Set<VetProfile>().SingleOrDefaultAsync(p => p.FamilyId == familyId && p.BotDbId == botDbId, ct);
        if (profile is null)
        {
            profile = new VetProfile { FamilyId = familyId, BotDbId = botDbId, UpdatedAt = clock.UtcNow };
            db.Add(profile);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return profile;
    }

    public async Task<VetProfileResult> UpdateAsync(long familyId, long botDbId, long actorUserId,
        int expectedRevision, IReadOnlyList<VetProfileChange> changes, CancellationToken ct)
    {
        _guard.Family(familyId);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await _guard.LockAsync(familyId, botDbId, ct);
        var profile = await db.Set<VetProfile>().AsTracking()
            .SingleAsync(p => p.FamilyId == familyId && p.BotDbId == botDbId, ct);
        await db.Entry(profile).ReloadAsync(ct);
        if (!await ownership.IsApprovedOwnerAsync(familyId, actorUserId, ct))
            return new(false, false, profile);
        if (profile.Revision != expectedRevision)
            return new(false, true, profile);
        if (changes.Count is < 1 or > 7 || changes.Select(c => c.Field).Distinct().Count() != changes.Count
            || changes.Any(c => !VetProfileValidation.IsValid(c.Field, c.Value)))
            return new(false, false, profile);
        var provenance = JsonSerializer.Deserialize<Dictionary<string, FieldProvenance>>(profile.FieldProvenanceJson)!;
        var changed = false;
        foreach (var change in changes)
        {
            var property = typeof(VetProfile).GetProperty(change.Field)!;
            if (Equals(property.GetValue(profile), change.Value)) continue;
            property.SetValue(profile, change.Value);
            provenance[change.Field] = new(actorUserId, clock.UtcNow,
                change.Field == nameof(VetProfile.ReportedVetGuidance) ? "owner-reported veterinarian" : "owner");
            changed = true;
        }
        if (changed)
        {
            profile.Revision++;
            profile.UpdatedAt = clock.UtcNow;
            profile.FieldProvenanceJson = JsonSerializer.Serialize(provenance);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new(true, false, profile);
    }

    private sealed record FieldProvenance(long ActorUserId, DateTimeOffset UpdatedAt, string Source);
}
