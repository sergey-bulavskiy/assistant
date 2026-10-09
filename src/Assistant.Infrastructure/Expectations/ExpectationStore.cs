using Assistant.Application.Common;
using Assistant.Application.Expectations;
using Assistant.Application.Families;
using Assistant.Application.Reminders;
using Assistant.Domain.Expectations;
using Assistant.Domain.Vet;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Expectations;

public sealed class ExpectationStore(AssistantDbContext db, ICurrentFamily family, IClock clock) : IExpectationStore
{
    private readonly NonurgentRules rules = new(db, family);
    private readonly ExpectedEventQuery facts = new(db);
    private async Task<T?> FreshAsync<T>(T? row, CancellationToken ct) where T : class
    {
        if (row == null) return null;
        await db.Entry(row).ReloadAsync(ct);
        return db.Entry(row).State == EntityState.Detached ? null : row;
    }
    private IQueryable<Expectation> Rows(ReminderScope s) => db.Set<Expectation>().Where(x =>
        x.FamilyId == s.FamilyId && x.BotDbId == s.BotDbId && x.BotId == s.BotId && x.Role == s.Role
        && x.ChatId == s.ChatId && x.TopicId == s.TopicId && x.ChatType == s.ChatType && x.ActorUserId == s.ActorUserId);
    internal static ReminderScope Scope(Expectation e) => new(e.FamilyId, e.BotDbId, e.BotId,
        e.Role, e.ChatId, e.TopicId, e.ChatType, e.ActorUserId);

    internal static async Task<ExpectationVersion> VersionAsync(AssistantDbContext db, Expectation e,
        DateOnly date, CancellationToken ct)
    {
        var current = await db.Set<ExpectationVersion>().AsNoTracking().SingleAsync(x =>
            x.ExpectationId == e.Id && x.Number == e.CurrentVersion, ct);
        if (e.NextVersion is not { } next) return current;
        var upcoming = await db.Set<ExpectationVersion>().AsNoTracking().SingleAsync(x =>
            x.ExpectationId == e.Id && x.Number == next, ct);
        return upcoming.EffectiveFrom <= date ? upcoming : current;
    }

    internal static async Task RecalculateAsync(AssistantDbContext db, Expectation e, CancellationToken ct)
    {
        if (e.Status != "active" || e.NextDate is not { } date) { e.DueAt = null; return; }
        var version = await VersionAsync(db, e, date, ct);
        e.DueAt = ExpectationTimePolicy.Window(date, version.DeadlineMinute,
            version.GraceMinutes, e.OffsetMinutes).Due;
    }

    internal static async Task PromoteAsync(AssistantDbContext db, Expectation e, DateOnly today, CancellationToken ct)
    {
        if (e.NextVersion is not { } next) return;
        var version = await db.Set<ExpectationVersion>().AsNoTracking().SingleAsync(x =>
            x.ExpectationId == e.Id && x.Number == next, ct);
        if (version.EffectiveFrom <= today) { e.CurrentVersion = next; e.NextVersion = null; }
    }

    public async Task<ExpectationIntake> ExecuteAsync(ReminderScope s, int sourceMessageId,
        ExpectationCommand command, CancellationToken ct)
    {
        rules.Check(s.FamilyId);
        if (sourceMessageId <= 0 || s.Role is not ("health" or "vet")) return new(s, "invalid", Result: "unavailable");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await rules.LockAsync(s.FamilyId, s.BotId, ct);
        if (!await rules.AuthorizedAsync(s, ct)) return new(s, "invalid", Result: "unavailable");
        if (command.Kind == "list") return new(s, "list");
        var receipt = await db.Set<ExpectationReceipt>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.BotId == s.BotId && x.ChatId == s.ChatId && x.SourceMessageId == sourceMessageId, ct);
        if (receipt != null) return receipt.FamilyId == s.FamilyId && receipt.ActorUserId == s.ActorUserId
            && receipt.TopicId == s.TopicId ? new(s, receipt.DraftId == null ? "result" : "preview",
                receipt.ExpectationId, receipt.DraftId, receipt.Result) : new(s, "invalid", Result: "unavailable");
        // Receipts are bounded to 60 days, but an old stored Telegram source must never enroll again.
        if (await db.Messages.AsNoTracking().AnyAsync(x => x.FamilyId == s.FamilyId && x.BotId == s.BotId
            && x.ChatId == s.ChatId && x.TelegramMessageId == sourceMessageId, ct))
            return new(s, "result", Result: "unavailable");
        var now = clock.UtcNow;
        Expectation? e = null;
        ExpectationDraft? draft = null;
        var result = "unavailable";
        if (command.Kind == "create" && command.EventType is { } type && ExpectationParser.ValidType(s.Role, type)
            && command.DeadlineMinute is { } minute && command.GraceMinutes is { } grace)
        {
            var preferences = await rules.PreferencesAsync(s.FamilyId, s.ActorUserId, ct);
            ExpectationTimePolicy.Validate(minute, grace, preferences.OffsetMinutes);
            long? profile = s.Role == "health"
                ? await db.HealthProfiles.AsNoTracking().Where(x => x.FamilyId == s.FamilyId && x.BotId == s.BotDbId)
                    .Select(x => (long?)x.Id).SingleOrDefaultAsync(ct)
                : await db.Set<VetProfile>().AsNoTracking().Where(x => x.FamilyId == s.FamilyId && x.BotDbId == s.BotDbId
                    && x.Name != null && x.Name != "").Select(x => (long?)x.Id).SingleOrDefaultAsync(ct);
            if (profile == null) result = "profile_required";
            else
            {
                var duplicate = await db.Set<Expectation>().SingleOrDefaultAsync(x => x.FamilyId == s.FamilyId
                    && x.BotDbId == s.BotDbId && x.BotId == s.BotId && x.Role == s.Role && x.ChatId == s.ChatId
                    && x.TopicId == s.TopicId && x.ProfileId == profile && x.EventType == type
                    && (x.Status == "draft" || x.Status == "active" || x.Status == "paused"), ct);
                duplicate = await FreshAsync(duplicate, ct);
                if (duplicate is { Status: "draft" } && duplicate.DraftExpiresAt <= now)
                {
                    duplicate.Status = "expired"; duplicate.UpdatedAt = now;
                    await db.Set<ExpectationDraft>().Where(x => x.ExpectationId == duplicate.Id && x.Status == "pending")
                        .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "expired").SetProperty(x => x.UpdatedAt, now), ct);
                    await db.SaveChangesAsync(ct); duplicate = null;
                }
                if (duplicate != null) result = "duplicate";
                else if (!await rules.HasCapacityAsync(s, now, ct)) result = "capacity";
                else
                {
                    e = new() { Id = Guid.NewGuid(), FamilyId = s.FamilyId, BotDbId = s.BotDbId, BotId = s.BotId,
                        Role = s.Role, ChatId = s.ChatId, TopicId = s.TopicId, ChatType = s.ChatType, ActorUserId = s.ActorUserId,
                        ProfileId = profile.Value, EventType = type, OffsetMinutes = preferences.OffsetMinutes,
                        CreatedAt = now, UpdatedAt = now,
                        DraftExpiresAt = ExpectationTimePolicy.Window(ExpectationTimePolicy.Tomorrow(now, preferences.OffsetMinutes),
                            0, 0, preferences.OffsetMinutes).Start };
                    db.Add(e);
                    draft = NewDraft(e, "create", minute, grace, now); db.Add(draft); result = "preview";
                }
            }
        }
        else if (command.Id is { } id)
        {
            e = await FreshAsync(await Rows(s).SingleOrDefaultAsync(x => x.Id == id, ct), ct);
            if (e != null && e.Status is "draft" or "active" or "paused")
            {
                if (e.CurrentVersion > 0) await PromoteAsync(db, e, ExpectationTimePolicy.LocalDate(now, e.OffsetMinutes), ct);
                if (command.Kind is "pause" or "cancel")
                {
                    if (command.Kind == "cancel" || e.Status == "active")
                    {
                        e.Status = command.Kind == "cancel" ? "cancelled" : "paused";
                        e.Revision++; e.DueAt = null; e.NextDate = null; e.UpdatedAt = now;
                        await RetireDraftsAsync(e.Id, "retired", now, ct);
                        var outcome = command.Kind == "cancel" ? "suppressed-cancelled" : "suppressed-paused";
                        await db.Set<ExpectationOccurrence>().Where(x => x.ExpectationId == e.Id && x.Outcome == "pending")
                            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Outcome, outcome).SetProperty(x => x.UpdatedAt, now), ct);
                        result = e.LastAttemptId != null ? command.Kind + "_started" : command.Kind;
                    }
                    else if (command.Kind == "pause" && e.Status == "paused") result = "pause";
                }
                else if (command.Kind == "edit" && e.Status == "active"
                    || command.Kind == "resume" && e.Status == "paused")
                {
                    var versionNumber = e.NextVersion ?? e.CurrentVersion;
                    var version = await db.Set<ExpectationVersion>().AsNoTracking().SingleAsync(x =>
                        x.ExpectationId == e.Id && x.Number == versionNumber, ct);
                    var deadline = command.Kind == "edit" ? command.DeadlineMinute!.Value : version.DeadlineMinute;
                    var proposedGrace = command.Kind == "edit" ? command.GraceMinutes!.Value : version.GraceMinutes;
                    ExpectationTimePolicy.Validate(deadline, proposedGrace, e.OffsetMinutes);
                    await RetireDraftsAsync(e.Id, "retired", now, ct);
                    draft = NewDraft(e, command.Kind, deadline, proposedGrace, now); db.Add(draft); result = "preview";
                }
            }
        }
        db.Add(new ExpectationReceipt { FamilyId = s.FamilyId, BotId = s.BotId, ChatId = s.ChatId,
            SourceMessageId = sourceMessageId, TopicId = s.TopicId, ActorUserId = s.ActorUserId,
            ExpectationId = e?.Id, DraftId = draft?.Id, Result = result, CreatedAt = now });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(s, draft == null ? "result" : "preview", e?.Id, draft?.Id, result);
    }

    private static ExpectationDraft NewDraft(Expectation e, string kind, int minute, int grace, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), FamilyId = e.FamilyId, ExpectationId = e.Id, Kind = kind,
            ExpectedRevision = e.Revision, ExpectedCurrentVersion = e.CurrentVersion,
            DeadlineMinute = minute, GraceMinutes = grace,
            EffectiveFrom = ExpectationTimePolicy.Tomorrow(now, e.OffsetMinutes), CreatedAt = now, UpdatedAt = now,
            ExpiresAt = ExpectationTimePolicy.Window(ExpectationTimePolicy.Tomorrow(now, e.OffsetMinutes), 0, 0, e.OffsetMinutes).Start };

    private Task RetireDraftsAsync(Guid id, string status, DateTimeOffset now, CancellationToken ct) =>
        db.Set<ExpectationDraft>().Where(x => x.ExpectationId == id && x.Status == "pending")
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, status).SetProperty(x => x.UpdatedAt, now), ct);

    private static bool Valid(Expectation e, ExpectationDraft d, DateTimeOffset now) => d.Status == "pending"
        && e.Revision == d.ExpectedRevision && e.CurrentVersion == d.ExpectedCurrentVersion
        && !ExpectationTimePolicy.PreviewExpired(d.CreatedAt, d.EffectiveFrom, e.OffsetMinutes, now)
        && (d.Kind == "create" && e.Status == "draft" || d.Kind == "edit" && e.Status == "active"
            || d.Kind == "resume" && e.Status == "paused");

    public async Task<ExpectationPreview?> BeginPreviewAsync(ReminderScope s, Guid draftId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await rules.LockAsync(s.FamilyId, s.BotId, ct);
        if (!await rules.AuthorizedAsync(s, ct)) return null;
        var d = await FreshAsync(await db.Set<ExpectationDraft>().SingleOrDefaultAsync(x => x.Id == draftId, ct), ct);
        if (d == null) return null;
        var e = await FreshAsync(await Rows(s).SingleOrDefaultAsync(x => x.Id == d.ExpectationId, ct), ct);
        if (e == null || d.PreviewStarted || !Valid(e, d, clock.UtcNow)) return null;
        var subject = await facts.SubjectAsync(e, ct); if (subject == null) return null;
        d.PreviewStarted = true; d.UpdatedAt = clock.UtcNow;
        var p = await rules.PreferencesAsync(s.FamilyId, s.ActorUserId, ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(e.Id, d.Id, subject, e.EventType, d.DeadlineMinute, d.GraceMinutes, e.OffsetMinutes, d.EffectiveFrom, p);
    }

    public async Task BindPreviewAsync(ReminderScope s, Guid draftId, int messageId, CancellationToken ct)
    {
        if (messageId <= 0) throw new ArgumentException("Invalid expectation preview.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await rules.LockAsync(s.FamilyId, s.BotId, ct);
        if (!await rules.AuthorizedAsync(s, ct)) return;
        var d = await FreshAsync(await db.Set<ExpectationDraft>().SingleOrDefaultAsync(x => x.Id == draftId, ct), ct);
        if (d == null) return;
        var e = await FreshAsync(await Rows(s).SingleOrDefaultAsync(x => x.Id == d.ExpectationId, ct), ct);
        if (e == null || !d.PreviewStarted || d.PreviewMessageId != null || !Valid(e, d, clock.UtcNow)) return;
        d.PreviewMessageId = messageId; d.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task<string> ResolveAsync(ReminderScope s, Guid draftId, int messageId, bool save, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await rules.LockAsync(s.FamilyId, s.BotId, ct);
        if (!await rules.AuthorizedAsync(s, ct)) return "unavailable";
        var d = await FreshAsync(await db.Set<ExpectationDraft>().SingleOrDefaultAsync(x => x.Id == draftId, ct), ct);
        if (d == null || messageId <= 0 || d.PreviewMessageId != messageId) return "unavailable";
        var e = await FreshAsync(await Rows(s).SingleOrDefaultAsync(x => x.Id == d.ExpectationId, ct), ct);
        if (e == null) return "unavailable";
        if (d.Status == "saved") return e.Status == "active" ? "saved" : "unavailable";
        if (!Valid(e, d, clock.UtcNow) || await facts.SubjectAsync(e, ct) == null) return "expired";
        var now = clock.UtcNow;
        d.Status = save ? "saved" : "cancelled"; d.UpdatedAt = now;
        if (!save)
        {
            if (d.Kind == "create") { e.Status = "cancelled"; e.Revision++; e.UpdatedAt = now; }
        }
        else
        {
            var number = ++e.LastVersion;
            db.Add(new ExpectationVersion { FamilyId = e.FamilyId, ExpectationId = e.Id, Number = number,
                DeadlineMinute = d.DeadlineMinute, GraceMinutes = d.GraceMinutes,
                EffectiveFrom = d.EffectiveFrom, CreatedAt = now });
            // Save the version before querying it to calculate a new cursor; the outer transaction owns both.
            await db.SaveChangesAsync(ct);
            if (d.Kind == "edit") e.NextVersion = number;
            else
            {
                e.CurrentVersion = number; e.NextVersion = null; e.Status = "active";
                e.FirstDate ??= d.EffectiveFrom; e.NextDate = d.EffectiveFrom;
            }
            e.Revision++; e.UpdatedAt = now;
            await RecalculateAsync(db, e, ct);
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return save ? "saved" : "cancelled";
    }

    public async Task<IReadOnlyList<ExpectationItem>> ListAsync(ReminderScope s, CancellationToken ct)
    {
        if (!await rules.AuthorizedAsync(s, ct)) return [];
        var live = await Rows(s).AsNoTracking().Where(x => x.Status == "draft" || x.Status == "active" || x.Status == "paused")
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(20).ToListAsync(ct);
        if (live.Count < 20) live.AddRange(await Rows(s).AsNoTracking().Where(x =>
                x.Status != "draft" && x.Status != "active" && x.Status != "paused")
            .OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id).Take(20 - live.Count).ToListAsync(ct));
        var result = new List<ExpectationItem>();
        foreach (var e in live)
        {
            var version = e.CurrentVersion == 0 ? null : await db.Set<ExpectationVersion>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.ExpectationId == e.Id && x.Number == (e.NextVersion ?? e.CurrentVersion), ct);
            var d = version == null ? await db.Set<ExpectationDraft>().AsNoTracking().Where(x => x.ExpectationId == e.Id)
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct) : null;
            result.Add(new(e.Id, await facts.SubjectAsync(e, ct) ?? "профиль недоступен", e.EventType, e.Status,
                version?.DeadlineMinute ?? d?.DeadlineMinute ?? 0, version?.GraceMinutes ?? d?.GraceMinutes ?? 0,
                e.OffsetMinutes, version?.EffectiveFrom ?? d?.EffectiveFrom, e.LastDate, e.LastOutcome,
                e.SkippedFrom, e.SkippedThrough));
        }
        return result;
    }
}
