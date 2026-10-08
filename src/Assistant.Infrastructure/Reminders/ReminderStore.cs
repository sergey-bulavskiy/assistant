using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Application.Reminders;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Domain.Reminders;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Reminders;

public sealed class ReminderStore(AssistantDbContext db, ICurrentFamily family, IClock clock) : IReminderStore
{
    private IQueryable<Reminder> Rows(ReminderScope s) => db.Set<Reminder>().Where(x =>
        x.FamilyId == s.FamilyId && x.BotDbId == s.BotDbId && x.BotId == s.BotId
        && x.Role == s.Role && x.ChatId == s.ChatId && x.TopicId == s.TopicId && x.ActorUserId == s.ActorUserId);
    private static ReminderScope Scope(Reminder r) => new(r.FamilyId, r.BotDbId, r.BotId, r.Role,
        r.ChatId, r.TopicId, r.ChatType, r.ActorUserId);
    private static ReminderItem Item(Reminder r) => new(r.Id, r.Text, r.Status, r.DueAt, r.DailyMinute,
        r.OffsetMinutes, r.CreatedAt, r.LastOutcome, r.LastAttemptAt);
    private void CheckFamily(long id)
    {
        if (id <= 0 || family.FamilyId != id) throw new InvalidOperationException("Reminder scope unavailable.");
    }
    private async Task<bool> AuthorizedAsync(ReminderScope s, CancellationToken ct)
    {
        CheckFamily(s.FamilyId);
        if (s.ActorUserId <= 0 || s.Role is not ("general" or "health" or "vet")) return false;
        if (!await db.Bots.AnyAsync(x => x.Id == s.BotDbId && x.TelegramBotId == s.BotId
            && x.FamilyId == s.FamilyId && x.Role == s.Role && x.Status == BotStatus.Active, ct)) return false;
        if (!await db.FamilyMembers.AnyAsync(x => x.FamilyId == s.FamilyId
            && x.TelegramUserId == s.ActorUserId && x.Status == FamilyMemberStatus.Approved, ct)) return false;
        return s.ChatType == "private" ? s.ChatId == s.ActorUserId && s.TopicId == null
            : s.ChatType is "group" or "supergroup" && await db.Places.AnyAsync(x => x.BotId == s.BotDbId
                && x.ChatId == s.ChatId && x.TopicId == s.TopicId && x.Status == PlaceStatus.Approved, ct);
    }
    private async Task AuthorizeAsync(ReminderScope s, CancellationToken ct)
    {
        if (!await AuthorizedAsync(s, ct)) throw new InvalidOperationException("Reminder scope unavailable.");
    }
    // Dedicated two-int advisory namespace. Family-wide first; bot lock second, matching existing bot writers.
    private async Task LockAsync(long familyId, long botId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(61008, hashtext({familyId.ToString(System.Globalization.CultureInfo.InvariantCulture)}))", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({botId})", ct);
    }
    private async Task<ReminderPreferences> PreferencesAsync(long familyId, long actor, CancellationToken ct)
    {
        var row = await db.Set<ReminderPreference>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.FamilyId == familyId && x.ActorUserId == actor, ct);
        return row == null ? new() : new(row.OffsetMinutes, row.QuietStartMinute, row.QuietEndMinute);
    }
    public async Task<ReminderPreferences> GetPreferencesAsync(ReminderScope s, CancellationToken ct)
    {
        await AuthorizeAsync(s, ct); return await PreferencesAsync(s.FamilyId, s.ActorUserId, ct);
    }
    public async Task SetPreferencesAsync(ReminderScope s, int sourceMessageId, ReminderPreferences p, CancellationToken ct)
    {
        if (sourceMessageId <= 0 || p.OffsetMinutes is < -720 or > 840
            || p.QuietStartMinute is < 0 or >= 1440 || p.QuietEndMinute is < 0 or >= 1440
            || p.QuietStartMinute == p.QuietEndMinute) throw new ArgumentException("Invalid reminder settings.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(s.FamilyId, s.BotId, ct); await AuthorizeAsync(s, ct);
        if (await db.Set<ReminderSettingsReceipt>().AnyAsync(x => x.BotId == s.BotId
            && x.ChatId == s.ChatId && x.SourceMessageId == sourceMessageId, ct)) return;
        var row = await db.Set<ReminderPreference>().SingleOrDefaultAsync(x => x.FamilyId == s.FamilyId
            && x.ActorUserId == s.ActorUserId, ct);
        if (row == null)
        {
            row = new() { FamilyId = s.FamilyId, ActorUserId = s.ActorUserId };
            db.Add(row);
        }
        row.OffsetMinutes = p.OffsetMinutes; row.QuietStartMinute = p.QuietStartMinute; row.QuietEndMinute = p.QuietEndMinute;
        db.Add(new ReminderSettingsReceipt { FamilyId = s.FamilyId, BotId = s.BotId, ChatId = s.ChatId,
            SourceMessageId = sourceMessageId, ActorUserId = s.ActorUserId, CreatedAt = clock.UtcNow });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<ReminderItem?> AdmitAsync(ReminderScope s, int sourceMessageId, ReminderRequest request, CancellationToken ct)
    {
        if (sourceMessageId <= 0 || request.Text.Length is < 1 or > 500 || request.Text.Any(char.IsControl)
            || request.OffsetMinutes is < -720 or > 840 || request.DailyMinute is < 0 or >= 1440)
            throw new ArgumentException("Invalid reminder request.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(s.FamilyId, s.BotId, ct); await AuthorizeAsync(s, ct);
        var prior = await db.Set<Reminder>().SingleOrDefaultAsync(x => x.BotId == s.BotId
            && x.ChatId == s.ChatId && x.SourceMessageId == sourceMessageId, ct);
        if (prior != null)
        {
            if (prior.FamilyId != s.FamilyId || prior.BotDbId != s.BotDbId || prior.TopicId != s.TopicId
                || prior.ActorUserId != s.ActorUserId) throw new InvalidOperationException("Reminder source unavailable.");
            return Item(prior);
        }
        var now = clock.UtcNow;
        if (request.DueAt.Offset != TimeSpan.Zero || request.DueAt <= now || request.DueAt > now.AddDays(366)) return null;
        await db.Set<Reminder>().Where(x => x.FamilyId == s.FamilyId && x.BotDbId == s.BotDbId
            && x.Status == "draft" && x.CreatedAt <= now.AddHours(-24))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "skipped").SetProperty(x => x.UpdatedAt, now), ct);
        if (await Rows(s).CountAsync(x => x.Status == "draft" || x.Status == "active", ct) >= 20
            || await db.Set<Reminder>().CountAsync(x => x.FamilyId == s.FamilyId && x.BotDbId == s.BotDbId
                && (x.Status == "draft" || x.Status == "active"), ct) >= 200) return null;
        var row = new Reminder { Id = Guid.NewGuid(), FamilyId = s.FamilyId, BotDbId = s.BotDbId,
            BotId = s.BotId, Role = s.Role, ChatId = s.ChatId, TopicId = s.TopicId, ChatType = s.ChatType,
            ActorUserId = s.ActorUserId, SourceMessageId = sourceMessageId, Text = request.Text,
            DueAt = request.DueAt, DailyMinute = request.DailyMinute, OffsetMinutes = request.OffsetMinutes,
            CreatedAt = now, UpdatedAt = now };
        db.Add(row); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Item(row);
    }
    public async Task<IReadOnlyList<ReminderItem>> ListAsync(ReminderScope s, CancellationToken ct)
    {
        await AuthorizeAsync(s, ct);
        var live = await Rows(s).AsNoTracking().Where(x => x.Status == "draft" || x.Status == "active")
            .OrderBy(x => x.DueAt).ThenBy(x => x.Id).Take(20).ToListAsync(ct);
        if (live.Count < 20) live.AddRange(await Rows(s).AsNoTracking().Where(x => x.Status != "draft" && x.Status != "active")
            .OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id).Take(20 - live.Count).ToListAsync(ct));
        return live.Select(Item).ToList();
    }
    public async Task<ReminderItem?> BeginPreviewAsync(ReminderScope s, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(s.FamilyId, s.BotId, ct); await AuthorizeAsync(s, ct);
        var row = await Rows(s).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row == null || row.Status != "draft" || row.PreviewStarted || ReminderTimePolicy.IsDraftExpired(row.CreatedAt, clock.UtcNow)) return null;
        row.PreviewStarted = true; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Item(row);
    }
    public async Task BindPreviewAsync(ReminderScope s, Guid id, int messageId, CancellationToken ct)
    {
        if (messageId <= 0) throw new ArgumentException("Invalid preview.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(s.FamilyId, s.BotId, ct); await AuthorizeAsync(s, ct);
        await Rows(s).Where(x => x.Id == id && x.Status == "draft" && x.PreviewStarted && x.PreviewMessageId == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.PreviewMessageId, messageId), ct);
        await tx.CommitAsync(ct);
    }
    public async Task<string> SaveAsync(ReminderScope s, Guid id, int previewMessageId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(s.FamilyId, s.BotId, ct); await AuthorizeAsync(s, ct);
        var row = await Rows(s).SingleOrDefaultAsync(x => x.Id == id && x.PreviewMessageId == previewMessageId, ct);
        if (row == null || previewMessageId <= 0) return "unavailable";
        if (row.Status == "active") return "saved";
        if (row.Status != "draft" || ReminderTimePolicy.IsDraftExpired(row.CreatedAt, clock.UtcNow)
            || row.DueAt <= clock.UtcNow) return "expired";
        row.Status = "active"; row.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return "saved";
    }
    public async Task<string> CancelAsync(ReminderScope s, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(s.FamilyId, s.BotId, ct); await AuthorizeAsync(s, ct);
        var row = await Rows(s).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row == null) return "unavailable";
        var began = row.LastAttemptAt != null;
        if (row.Status is "draft" or "active" or "unknown")
        {
            row.Status = "cancelled"; row.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        return began ? "cancelled_started" : "cancelled";
    }
    public async Task<ReminderDispatch?> ClaimDueAsync(ReceivingBot bot, CancellationToken ct)
    {
        if (bot.FamilyId is not { } familyId) return null;
        CheckFamily(familyId);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(familyId, bot.TelegramBotId, ct);
        var now = clock.UtcNow;
        var candidates = await db.Set<Reminder>().Where(x => x.FamilyId == familyId && x.BotDbId == bot.BotDbId
            && x.BotId == bot.TelegramBotId && x.Role == bot.Role && x.Status == "active" && x.DueAt <= now)
            .OrderBy(x => x.DueAt).ThenBy(x => x.Id).Take(200).ToListAsync(ct);
        foreach (var row in candidates)
        {
            if (!await AuthorizedAsync(Scope(row), ct))
            {
                row.Status = "cancelled"; row.UpdatedAt = now; continue;
            }
            if (ReminderTimePolicy.IsStale(row.DueAt, now))
            {
                row.LastOutcome = "skipped"; row.UpdatedAt = now;
                if (row.DailyMinute is { } daily) row.DueAt = ReminderTimePolicy.NextDaily(now, daily, row.OffsetMinutes);
                else row.Status = "skipped";
                continue;
            }
            var preferences = await PreferencesAsync(familyId, row.ActorUserId, ct);
            if (ReminderTimePolicy.IsQuiet(now, preferences)) continue;
            var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            if (await db.Set<ReminderAttempt>().CountAsync(x => x.FamilyId == familyId && x.Role == bot.Role
                && x.StartedAt >= day && x.StartedAt < day.AddDays(1), ct) >= 10) continue;
            var attempt = new ReminderAttempt { Id = Guid.NewGuid(), ReminderId = row.Id, FamilyId = familyId,
                Role = row.Role, OccurrenceDueAt = row.DueAt, StartedAt = now };
            db.Add(attempt); row.LastOutcome = "unknown"; row.LastAttemptAt = now;
            row.LastTelegramMessageId = null; row.UpdatedAt = now;
            if (row.DailyMinute is { } minute) row.DueAt = ReminderTimePolicy.NextDaily(now, minute, row.OffsetMinutes);
            else row.Status = "unknown";
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(row.Id, attempt.Id, row.ChatId, row.TopicId, "⏰ Напоминание: " + row.Text);
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null;
    }
    public async Task CompleteAsync(ReceivingBot bot, ReminderDispatch dispatch, int telegramMessageId, CancellationToken ct)
    {
        if (bot.FamilyId is not { } familyId || telegramMessageId <= 0) throw new InvalidOperationException("Reminder completion unavailable.");
        CheckFamily(familyId);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(familyId, bot.TelegramBotId, ct);
        var row = await db.Set<Reminder>().SingleOrDefaultAsync(x => x.Id == dispatch.ReminderId && x.FamilyId == familyId
            && x.BotDbId == bot.BotDbId && x.BotId == bot.TelegramBotId && x.Role == bot.Role, ct);
        var attempt = await db.Set<ReminderAttempt>().SingleOrDefaultAsync(x => x.Id == dispatch.AttemptId
            && x.ReminderId == dispatch.ReminderId && x.FamilyId == familyId, ct);
        if (row == null || attempt == null || attempt.Outcome != "unknown") return;
        attempt.Outcome = "sent"; attempt.TelegramMessageId = telegramMessageId;
        if (row.LastAttemptAt == attempt.StartedAt)
        {
            row.LastOutcome = "sent"; row.LastTelegramMessageId = telegramMessageId;
            if (row.Status == "unknown") row.Status = "sent";
            row.UpdatedAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task CleanupAsync(ReceivingBot bot, CancellationToken ct)
    {
        if (bot.FamilyId is not { } familyId) return;
        CheckFamily(familyId);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(familyId, bot.TelegramBotId, ct);
        var now = clock.UtcNow; var old = now.AddDays(-60);
        await db.Set<Reminder>().Where(x => x.FamilyId == familyId && x.BotDbId == bot.BotDbId && x.Status == "draft"
            && x.CreatedAt <= now.AddHours(-24)).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "skipped")
                .SetProperty(x => x.UpdatedAt, now), ct);
        var terminal = await db.Set<Reminder>().Where(x => x.FamilyId == familyId && x.BotDbId == bot.BotDbId
            && x.Status != "draft" && x.Status != "active" && x.UpdatedAt <= old).OrderBy(x => x.Id).Select(x => x.Id).Take(100).ToListAsync(ct);
        await db.Set<Reminder>().Where(x => terminal.Contains(x.Id)).ExecuteDeleteAsync(ct);
        var attempts = await db.Set<ReminderAttempt>().Where(x => x.FamilyId == familyId && x.StartedAt <= old)
            .OrderBy(x => x.StartedAt).Select(x => x.Id).Take(100).ToListAsync(ct);
        await db.Set<ReminderAttempt>().Where(x => attempts.Contains(x.Id)).ExecuteDeleteAsync(ct);
        var receipts = await db.Set<ReminderSettingsReceipt>().Where(x => x.FamilyId == familyId && x.CreatedAt <= old)
            .OrderBy(x => x.CreatedAt).Take(100).ToListAsync(ct);
        db.RemoveRange(receipts); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
