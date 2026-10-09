using Assistant.Application.Common;
using Assistant.Application.Expectations;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Application.Reminders;
using Assistant.Domain.Expectations;
using Assistant.Domain.Reminders;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Reminders;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Expectations;

public sealed class NonurgentDispatchStore(AssistantDbContext db, ICurrentFamily family, IClock clock)
    : INonurgentDispatchStore
{
    private readonly NonurgentRules rules = new(db, family);
    private readonly ExpectedEventQuery facts = new(db);
    private readonly ReminderStore reminders = new(db, family, clock);

    public async Task<IReadOnlyList<NonurgentCandidate>> SelectAsync(ReceivingBot bot, CancellationToken ct)
    {
        if (bot.FamilyId is not { } f) return [];
        rules.Check(f); var now = clock.UtcNow;
        var ordinary = db.Set<Reminder>().AsNoTracking().Where(x => x.FamilyId == f && x.BotDbId == bot.BotDbId
            && x.BotId == bot.TelegramBotId && x.Role == bot.Role && x.Status == "active" && x.DueAt <= now)
            .Select(x => new { Kind = "reminder", x.Id, x.DueAt });
        var checks = db.Set<Expectation>().AsNoTracking().Where(x => x.FamilyId == f && x.BotDbId == bot.BotDbId
            && x.BotId == bot.TelegramBotId && x.Role == bot.Role && x.Status == "active" && x.DueAt <= now)
            .Select(x => new { Kind = "expectation", x.Id, DueAt = x.DueAt!.Value });
        var rows = await ordinary.Concat(checks).OrderBy(x => x.DueAt).ThenBy(x => x.Kind).ThenBy(x => x.Id)
            .Take(200).ToListAsync(ct);
        return rows.Select(x => new NonurgentCandidate(x.Kind, x.Id, x.DueAt)).ToArray();
    }

    public async Task<NonurgentDispatch?> ClaimAsync(ReceivingBot bot, NonurgentCandidate candidate, CancellationToken ct)
    {
        if (candidate.Kind == "reminder")
        {
            var r = await reminders.ClaimSpecificAsync(bot, candidate.Id, ct);
            return r == null ? null : new("reminder", r.ReminderId, r.AttemptId, r.ChatId, r.TopicId, r.Text);
        }
        if (candidate.Kind != "expectation" || bot.FamilyId is not { } f || bot.Role is not ("health" or "vet")) return null;
        rules.Check(f);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await rules.LockAsync(f, bot.TelegramBotId, ct);
        await ExpectedEventOrder.LockAsync(db, f, bot.BotDbId, bot.Role, ct);
        var e = await db.Set<Expectation>().SingleOrDefaultAsync(x => x.FamilyId == f && x.Id == candidate.Id
            && x.BotDbId == bot.BotDbId && x.BotId == bot.TelegramBotId && x.Role == bot.Role, ct);
        if (e == null || e.Status != "active" || e.NextDate is not { } cursor) return null;
        var now = clock.UtcNow;
        var today = ExpectationTimePolicy.LocalDate(now, e.OffsetMinutes);
        if (!await rules.AuthorizedAsync(ExpectationStore.Scope(e), ct) || await facts.SubjectAsync(e, ct) == null)
        {
            e.Status = "retired"; e.Revision++; e.NextDate = null; e.DueAt = null;
            e.LastOutcome = "suppressed-authorization"; e.UpdatedAt = now;
            await db.Set<ExpectationDraft>().Where(x => x.ExpectationId == e.Id && x.Status == "pending")
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "retired").SetProperty(x => x.UpdatedAt, now), ct);
            await db.Set<ExpectationOccurrence>().Where(x => x.ExpectationId == e.Id && x.Outcome == "pending")
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Outcome, "suppressed-authorization")
                    .SetProperty(x => x.UpdatedAt, now), ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null;
        }
        if (cursor < today)
        {
            e.SkippedFrom = cursor; e.SkippedThrough = today.AddDays(-1);
            e.LastDate = today.AddDays(-1); e.LastOutcome = "skipped-stale";
            await db.Set<ExpectationOccurrence>().Where(x => x.ExpectationId == e.Id && x.LocalDate < today && x.Outcome == "pending")
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Outcome, "skipped-stale").SetProperty(x => x.UpdatedAt, now), ct);
            e.NextDate = cursor = today;
        }
        await ExpectationStore.PromoteAsync(db, e, today, ct);
        await ExpectationStore.RecalculateAsync(db, e, ct);
        if (cursor > today || e.FirstDate > today || e.DueAt > now)
        { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null; }
        var version = await ExpectationStore.VersionAsync(db, e, cursor, ct);
        var window = ExpectationTimePolicy.Window(cursor, version.DeadlineMinute, version.GraceMinutes, e.OffsetMinutes);
        var o = await db.Set<ExpectationOccurrence>().SingleOrDefaultAsync(x => x.ExpectationId == e.Id && x.LocalDate == cursor, ct);
        if (o == null)
        {
            o = new() { FamilyId = f, ExpectationId = e.Id, LocalDate = cursor, Version = version.Number,
                WindowStart = window.Start, DueAt = window.Due, ExpiresAt = window.Expires, UpdatedAt = now };
            db.Add(o);
        }
        if (o.Outcome != "pending")
        {
            e.NextDate = today.AddDays(1); await ExpectationStore.RecalculateAsync(db, e, ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null;
        }
        // UTC bounds are inclusive for facts, but the eligibility end is exclusive.
        if (now >= o.ExpiresAt) o.Outcome = "skipped-stale";
        else
        {
            var match = await facts.MatchAsync(e, o, ct);
            if (match != null)
            {
                o.Outcome = "satisfied"; o.MatchedEventId = match;
                o.MatchedEventKind = e.Role;
            }
            else if (ReminderTimePolicy.IsQuiet(now, await rules.PreferencesAsync(f, e.ActorUserId, ct))
                || !await rules.HasBudgetAsync(f, e.Role, now, ct))
            { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null; }
        }
        NonurgentDispatch? dispatch = null;
        if (o.Outcome == "pending")
        {
            var attempt = new ExpectationAttempt { Id = Guid.NewGuid(), FamilyId = f, Role = e.Role,
                ExpectationId = e.Id, LocalDate = cursor, StartedAt = now };
            db.Add(attempt); o.Outcome = "dispatch-unknown";
            e.LastAttemptId = attempt.Id; e.LastAttemptAt = now; e.LastTelegramMessageId = null;
            var subject = await facts.SubjectAsync(e, ct) ?? "профиль";
            var text = $"На момент проверки нет подтверждённой записи: {subject}, {e.EventType}, "
                + $"за {cursor:yyyy-MM-dd} с 00:00 до {ExpectationTimePolicy.Minute(version.DeadlineMinute + version.GraceMinutes)} "
                + $"UTC{ExpectationTimePolicy.Offset(e.OffsetMinutes)}. Проверяется только наличие записи; это не означает, что действие не выполнено.";
            dispatch = new("expectation", e.Id, attempt.Id, e.ChatId, e.TopicId, text);
        }
        o.UpdatedAt = now; e.LastDate = cursor; e.LastOutcome = o.Outcome; e.UpdatedAt = now;
        e.NextDate = today.AddDays(1); await ExpectationStore.RecalculateAsync(db, e, ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return dispatch;
    }

    public async Task CompleteAsync(ReceivingBot bot, NonurgentDispatch dispatch, int messageId, CancellationToken ct)
    {
        if (dispatch.Kind == "reminder")
        {
            await reminders.CompleteAsync(bot, new(dispatch.Id, dispatch.AttemptId, dispatch.ChatId,
                dispatch.TopicId, dispatch.Text), messageId, ct); return;
        }
        if (dispatch.Kind != "expectation" || bot.FamilyId is not { } f || messageId <= 0) return;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await rules.LockAsync(f, bot.TelegramBotId, ct);
        var e = await db.Set<Expectation>().SingleOrDefaultAsync(x => x.FamilyId == f && x.Id == dispatch.Id
            && x.BotDbId == bot.BotDbId && x.BotId == bot.TelegramBotId && x.Role == bot.Role, ct);
        var a = await db.Set<ExpectationAttempt>().SingleOrDefaultAsync(x => x.FamilyId == f
            && x.Id == dispatch.AttemptId && x.ExpectationId == dispatch.Id && x.Outcome == "unknown", ct);
        if (e == null || a == null) return;
        var o = await db.Set<ExpectationOccurrence>().SingleAsync(x => x.ExpectationId == e.Id && x.LocalDate == a.LocalDate, ct);
        a.Outcome = "sent"; a.TelegramMessageId = messageId;
        o.Outcome = "sent"; o.UpdatedAt = clock.UtcNow;
        if (e.LastAttemptId == a.Id && e.LastDate == a.LocalDate)
        { e.LastOutcome = "sent"; e.LastTelegramMessageId = messageId; e.UpdatedAt = clock.UtcNow; }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task CleanupAsync(ReceivingBot bot, CancellationToken ct)
    {
        await reminders.CleanupAsync(bot, ct);
        if (bot.FamilyId is not { } f) return;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await rules.LockAsync(f, bot.TelegramBotId, ct);
        var now = clock.UtcNow; var old = now.AddDays(-60);
        var owned = db.Set<Expectation>().Where(x => x.FamilyId == f && x.BotDbId == bot.BotDbId);
        var expired = await owned.Where(x => x.Status == "draft" && x.DraftExpiresAt <= now)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(100).ToListAsync(ct);
        foreach (var e in expired) { e.Status = "expired"; e.UpdatedAt = now; }
        var drafts = await db.Set<ExpectationDraft>().Where(x => x.FamilyId == f && owned.Any(e => e.Id == x.ExpectationId)
            && x.Status == "pending" && x.ExpiresAt <= now).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Take(100).ToListAsync(ct);
        foreach (var d in drafts) { d.Status = "expired"; d.UpdatedAt = now; }
        await db.SaveChangesAsync(ct);
        var attempts = await db.Set<ExpectationAttempt>().Where(x => x.FamilyId == f && x.StartedAt <= old
            && owned.Any(e => e.Id == x.ExpectationId)).OrderBy(x => x.StartedAt).ThenBy(x => x.Id).Take(100).Select(x => x.Id).ToListAsync(ct);
        await db.Set<ExpectationAttempt>().Where(x => attempts.Contains(x.Id)).ExecuteDeleteAsync(ct);
        var occurrences = await db.Set<ExpectationOccurrence>().Where(x => x.FamilyId == f && x.UpdatedAt <= old
            && x.Outcome != "pending" && owned.Any(e => e.Id == x.ExpectationId)
            && !db.Set<ExpectationAttempt>().Any(a => a.ExpectationId == x.ExpectationId && a.LocalDate == x.LocalDate))
            .OrderBy(x => x.UpdatedAt).ThenBy(x => x.ExpectationId).ThenBy(x => x.LocalDate).Take(100).ToListAsync(ct);
        db.RemoveRange(occurrences); await db.SaveChangesAsync(ct);
        var oldDrafts = await db.Set<ExpectationDraft>().Where(x => x.FamilyId == f && x.UpdatedAt <= old
            && x.Status != "pending" && owned.Any(e => e.Id == x.ExpectationId))
            .OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id).Take(100).ToListAsync(ct);
        db.RemoveRange(oldDrafts); await db.SaveChangesAsync(ct);
        var versions = await db.Set<ExpectationVersion>().Where(x => x.FamilyId == f && x.CreatedAt <= old
            && owned.Any(e => e.Id == x.ExpectationId && (e.Status != "active" && e.Status != "paused" && e.UpdatedAt <= old
                || (e.Status == "active" || e.Status == "paused") && x.Number != e.CurrentVersion
                    && x.Number != e.NextVersion && x.EffectiveFrom < DateOnly.FromDateTime(old.UtcDateTime)))
            && !db.Set<ExpectationOccurrence>().Any(o => o.ExpectationId == x.ExpectationId && o.Version == x.Number))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.ExpectationId).ThenBy(x => x.Number).Take(100).ToListAsync(ct);
        db.RemoveRange(versions); await db.SaveChangesAsync(ct);
        var terminal = await owned.Where(x => x.Status != "active" && x.Status != "paused" && x.Status != "draft"
            && x.UpdatedAt <= old && !db.Set<ExpectationVersion>().Any(v => v.ExpectationId == x.Id)
            && !db.Set<ExpectationDraft>().Any(d => d.ExpectationId == x.Id)
            && !db.Set<ExpectationOccurrence>().Any(o => o.ExpectationId == x.Id))
            .OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id).Take(100).ToListAsync(ct);
        db.RemoveRange(terminal);
        var receipts = await db.Set<ExpectationReceipt>().Where(x => x.FamilyId == f && x.BotId == bot.TelegramBotId
            && x.CreatedAt <= old).OrderBy(x => x.CreatedAt).ThenBy(x => x.SourceMessageId).Take(100).ToListAsync(ct);
        db.RemoveRange(receipts); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
