using System.Globalization;
using Assistant.Application.Families;
using Assistant.Application.Reminders;
using Assistant.Domain.Bots;
using Assistant.Domain.Expectations;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Domain.Reminders;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Expectations;

internal sealed class NonurgentRules(AssistantDbContext db, ICurrentFamily family)
{
    public void Check(long familyId)
    {
        if (familyId <= 0 || family.FamilyId != familyId)
            throw new InvalidOperationException("Nonurgent scope unavailable.");
    }

    public async Task LockAsync(long familyId, long telegramBotId, CancellationToken ct)
    {
        Check(familyId);
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Nonurgent transaction unavailable.");
        var key = familyId.ToString(CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(61008, hashtext({key}))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({telegramBotId})", ct);
    }

    public async Task<bool> AuthorizedAsync(ReminderScope s, CancellationToken ct)
    {
        Check(s.FamilyId);
        if (s.ActorUserId <= 0 || s.Role is not ("general" or "health" or "vet")) return false;
        if (!await db.Bots.AsNoTracking().AnyAsync(x => x.Id == s.BotDbId && x.FamilyId == s.FamilyId
            && x.TelegramBotId == s.BotId && x.Role == s.Role && x.Status == BotStatus.Active, ct)) return false;
        if (!await db.FamilyMembers.AsNoTracking().AnyAsync(x => x.FamilyId == s.FamilyId
            && x.TelegramUserId == s.ActorUserId && x.Status == FamilyMemberStatus.Approved, ct)) return false;
        return s.ChatType == "private" ? s.ChatId == s.ActorUserId && s.TopicId == null
            : s.ChatType is "group" or "supergroup" && await db.Places.AsNoTracking().AnyAsync(x =>
                x.BotId == s.BotDbId && x.ChatId == s.ChatId && x.TopicId == s.TopicId
                && x.Status == PlaceStatus.Approved, ct);
    }

    public async Task<ReminderPreferences> PreferencesAsync(long familyId, long actor, CancellationToken ct)
    {
        Check(familyId);
        var p = await db.Set<ReminderPreference>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.FamilyId == familyId && x.ActorUserId == actor, ct);
        return p == null ? new() : new(p.OffsetMinutes, p.QuietStartMinute, p.QuietEndMinute);
    }

    public async Task<bool> HasBudgetAsync(long familyId, string role, DateTimeOffset now, CancellationToken ct)
    {
        Check(familyId);
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Nonurgent transaction unavailable.");
        var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var end = start.AddDays(1);
        var reminders = await db.Set<ReminderAttempt>().CountAsync(x => x.FamilyId == familyId
            && x.Role == role && x.StartedAt >= start && x.StartedAt < end, ct);
        var expectations = await db.Set<ExpectationAttempt>().CountAsync(x => x.FamilyId == familyId
            && x.Role == role && x.StartedAt >= start && x.StartedAt < end, ct);
        return reminders + expectations < 10;
    }

    public async Task<bool> HasCapacityAsync(ReminderScope s, DateTimeOffset now, CancellationToken ct)
    {
        Check(s.FamilyId);
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Nonurgent transaction unavailable.");
        // Expired drafts are excluded atomically even when cleanup still has another bounded batch.
        var cutoff = now.AddHours(-24);
        var reminders = db.Set<Reminder>().Where(x => x.FamilyId == s.FamilyId && x.BotDbId == s.BotDbId
            && (x.Status == "active" || x.Status == "draft" && x.CreatedAt > cutoff));
        var expectations = db.Set<Expectation>().Where(x => x.FamilyId == s.FamilyId && x.BotDbId == s.BotDbId
            && (x.Status == "active" || x.Status == "paused" || x.Status == "draft" && x.DraftExpiresAt > now));
        if (await reminders.CountAsync(ct) + await expectations.CountAsync(ct) >= 200) return false;
        var reminderPlace = reminders.Where(x => x.BotId == s.BotId && x.Role == s.Role
            && x.ChatId == s.ChatId && x.TopicId == s.TopicId && x.ActorUserId == s.ActorUserId);
        var expectationPlace = expectations.Where(x => x.BotId == s.BotId && x.Role == s.Role
            && x.ChatId == s.ChatId && x.TopicId == s.TopicId && x.ActorUserId == s.ActorUserId);
        return await reminderPlace.CountAsync(ct) + await expectationPlace.CountAsync(ct) < 20;
    }
}
