using System.Globalization;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Expectations;

internal static class ExpectedEventOrder
{
    // A wider-than-place lock deliberately orders every fact mutation for this bot.
    // Take after existing source/Vet locks. Never take those locks after this one.
    public static Task LockAsync(AssistantDbContext db, long familyId, long botDbId,
        string role, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null || familyId <= 0 || botDbId <= 0
            || role is not ("health" or "vet"))
            throw new InvalidOperationException("Expected-event ordering unavailable.");
        var key = string.Create(CultureInfo.InvariantCulture, $"{familyId}:{botDbId}:{role}");
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(61009, hashtext({key}))", ct);
    }

    public static async Task LockHealthAsync(AssistantDbContext db, long familyId,
        long profileId, CancellationToken ct)
    {
        var botDbId = await db.HealthProfiles.AsNoTracking()
            .Where(x => x.FamilyId == familyId && x.Id == profileId)
            .Select(x => (long?)x.BotId).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Expected-event profile unavailable.");
        await LockAsync(db, familyId, botDbId, "health", ct);
    }
}
