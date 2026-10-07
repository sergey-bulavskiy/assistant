using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore : IVetPhotoDispatchStore
{
    public async Task<VetPhotoInputRevision?> ReadInputAsync(VetDiaryScope scope, Guid sourceId,
        Guid inputRevisionId, long actorUserId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        if (!await ImageActorAsync(scope, actorUserId, ct)
            || await ImageSourceAsync(scope, sourceId, ct) is null) return null;
        return await DispatchInputs(scope.FamilyId, scope.BotDbId, scope.TelegramBotId)
            .SingleOrDefaultAsync(i => i.SourceId == sourceId && i.Id == inputRevisionId
                && i.ChatId == scope.ChatId && i.TopicId == scope.TopicId, ct);
    }

    public async Task<VetPhotoImageResult?> ReadStoredImageAsync(VetDiaryScope scope, Guid sourceId,
        Guid inputRevisionId, Guid? scheduledAttemptKey, long actorUserId, CancellationToken ct)
    {
        await CheckAsync(scope, ct);
        if (!await ImageActorAsync(scope, actorUserId, ct)
            || await ImageSourceAsync(scope, sourceId, ct) is null
            || !await Scoped<VetPhotoInputRevision>(scope).AnyAsync(i => i.Id == inputRevisionId && i.SourceId == sourceId, ct)) return null;
        var attempts = Scoped<VetPhotoAttempt>(scope);
        var windows = Scoped<VetPhotoRunWindow>(scope);
        var runs = Scoped<VetPhotoRun>(scope);
        var query = Scoped<VetPhotoExtraction>(scope).AsNoTracking().Where(e => e.SourceId == sourceId && e.InputRevisionId == inputRevisionId);
        if (scheduledAttemptKey is { } key)
            query = query.Where(e => e.AttemptId == key && attempts.Any(a =>
                a.Id == key && a.SourceId == sourceId && a.InputRevisionId == inputRevisionId && a.Kind == "image" && a.RunWindowId != null
                && windows.Any(w => w.Id == a.RunWindowId
                    && runs.Any(r => r.Id == w.RunId && r.ActorUserId == a.ActorUserId))));
        else query = query.Where(e => attempts.Any(a => a.Id == e.AttemptId
            && a.SourceId == sourceId && a.InputRevisionId == inputRevisionId && a.RunWindowId == null));
        var extraction = await query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).FirstOrDefaultAsync(ct);
        if (extraction == null) return null;
        var candidate = await Scoped<VetPhotoCandidate>(scope).AsNoTracking().SingleAsync(c => c.SourceId == sourceId && c.CandidateOrdinal == 0, ct);
        var parsed = VetPhotoInterpretationParser.Parse(extraction.StructuredJson, sourceId, inputRevisionId);
        var delta = scheduledAttemptKey != null || candidate.EventId != null || candidate.ManuallyCorrected || candidate.RequiresExplicitRestoration
            || candidate.State is "saved" or "linked" or "excluded" or "cancelled" or "deleted"
            ? new VetPhotoCandidateDelta(candidate.Id, candidate.Revision, sourceId, inputRevisionId, extraction.Id,
                candidate.RequiresExplicitRestoration || candidate.State is "excluded" or "cancelled" or "deleted") : null;
        return new(parsed == null ? VetPhotoImageStatus.InvalidResult : delta != null ? VetPhotoImageStatus.ProposedDelta
            : VetPhotoImageStatus.Existing, Extraction: extraction, Delta: delta);
    }

    private IQueryable<VetPhotoSource> DispatchSources(long familyId, long botDbId, long telegramBotId) =>
        db.Set<VetPhotoSource>().AsNoTracking().Where(s => s.FamilyId == familyId && s.BotDbId == botDbId
            && s.TelegramBotId == telegramBotId && s.SourceSlot == 1 && s.SourceMessageDbId != null
            && db.Messages.Any(m => m.Id == s.SourceMessageDbId && m.FamilyId == familyId && m.BotId == telegramBotId
                && m.ChatId == s.ChatId && m.TopicId == s.TopicId && m.TelegramMessageId == s.TelegramMessageId
                && m.UserId == s.SourceAuthorUserId && m.ChatType == s.ChatType && m.SentAt == s.SentAt
                && m.Direction == MessageDirection.In && (m.Kind == MessageKind.Photo || m.Kind == MessageKind.Document)));

    private IQueryable<VetPhotoInputRevision> DispatchInputs(long familyId, long botDbId, long telegramBotId) =>
        db.Set<VetPhotoInputRevision>().AsNoTracking().Where(i => i.FamilyId == familyId && i.BotDbId == botDbId
            && i.TelegramBotId == telegramBotId && db.Set<VetPhotoSource>().Any(s => s.Id == i.SourceId
                && s.FamilyId == familyId && s.BotDbId == botDbId && s.TelegramBotId == telegramBotId
                && s.ChatId == i.ChatId && s.TopicId == i.TopicId
                && (i.TextInputRevisionId == null || db.Set<VetTextSourceRevision>().Any(t => t.Id == i.TextInputRevisionId
                    && t.FamilyId == familyId && t.BotDbId == botDbId
                    && (t.State == "written" || t.State == "completed" || t.State == "failed" || t.State == "paused")
                    && db.Set<VetTextSource>().Any(text => text.Id == t.SourceId && text.FamilyId == familyId
                        && text.BotDbId == botDbId && text.TelegramBotId == telegramBotId && text.ChatId == i.ChatId
                        && text.TopicId == i.TopicId && text.SourceSlot == 0 && text.TelegramMessageId == s.TelegramMessageId
                        && text.SourceAuthorUserId == s.SourceAuthorUserId)))));

    public Task<IReadOnlyList<VetPhotoWork>> GetDueAsync(long familyId, long botDbId, int limit, CancellationToken ct)
        => ReadDueAsync(familyId, botDbId, limit, retainedOnly: false, ct: ct);

    public Task<IReadOnlyList<VetPhotoWork>> GetRetainedDueAsync(long familyId, long botDbId, int limit, CancellationToken ct)
        => ReadDueAsync(familyId, botDbId, limit, retainedOnly: true, ct: ct);

    private async Task<IReadOnlyList<VetPhotoWork>> ReadDueAsync(long familyId, long botDbId, int limit, bool retainedOnly, CancellationToken ct)
    {
        _guard.Family(familyId);
        ForgetPhotoSnapshots();
        if (limit is < 1 or > 5) throw new InvalidOperationException("Photo work limit is invalid.");
        await RepairObviousRunsAsync(familyId, botDbId, ct);
        var bot = await RepairDownloadsAsync(familyId, botDbId, ct);
        if (bot == null) return [];
        var now = clock.UtcNow;
        var inputs = DispatchInputs(familyId, botDbId, bot.TelegramBotId);
        var sources = DispatchSources(familyId, botDbId, bot.TelegramBotId).Where(s => s.State != "late" && s.State != "full"
            && s.BatchId != null && db.Set<VetPhotoBatch>().Any(b => b.Id == s.BatchId && b.FamilyId == familyId
                && b.BotDbId == botDbId && b.TelegramBotId == bot.TelegramBotId && b.ChatId == s.ChatId && b.TopicId == s.TopicId
                && b.State != "cancelled" && (b.State != "completed" || s.CurrentOrdinal > 1))
            && db.FamilyMembers.Any(m => m.FamilyId == familyId && m.TelegramUserId == s.SourceAuthorUserId && m.Status == FamilyMemberStatus.Approved)
            && (s.ChatType == "private" && s.ChatId == s.SourceAuthorUserId && s.TopicId == null
                || s.ChatType != "private" && db.Places.Any(p => p.BotId == botDbId && p.ChatId == s.ChatId
                    && p.TopicId == s.TopicId && p.Status == PlaceStatus.Approved))
            && db.Set<VetPhotoCandidate>().Any(c => c.SourceId == s.Id && c.FamilyId == familyId && c.BotDbId == botDbId
                && c.TelegramBotId == bot.TelegramBotId && c.ChatId == s.ChatId && c.TopicId == s.TopicId && c.CandidateOrdinal == 0
                && c.State != "cancelled" && c.State != "excluded" && c.State != "deleted" && !c.RequiresExplicitRestoration)
            && inputs.Any(i => i.Id == s.CurrentInputRevisionId
                && i.SourceId == s.Id && i.ChatId == s.ChatId && i.TopicId == s.TopicId
                && (i.ReportedSize == null || i.ReportedSize <= VetPhotoImageLimits.MaxEncodedBytes)));
        var attempts = db.Set<VetPhotoAttempt>().AsNoTracking().Where(a => a.FamilyId == familyId
            && a.BotDbId == botDbId && a.TelegramBotId == bot.TelegramBotId);
        var references = db.Set<VetPhotoOriginalReference>().AsNoTracking().Where(r => r.FamilyId == familyId
            && r.BotDbId == botDbId && r.TelegramBotId == bot.TelegramBotId && r.State == "retained");
        var results = db.Set<VetPhotoExtraction>().AsNoTracking().Where(e => e.FamilyId == familyId
            && e.BotDbId == botDbId && e.TelegramBotId == bot.TelegramBotId);
        var current = await sources.Where(s => !results.Any(e => e.SourceId == s.Id && e.InputRevisionId == s.CurrentInputRevisionId
                && e.ChatId == s.ChatId && e.TopicId == s.TopicId)
            && (!retainedOnly || references.Any(r => r.InputRevisionId == s.CurrentInputRevisionId
                && r.ChatId == s.ChatId && r.TopicId == s.TopicId))
            && (!retainedOnly && attempts.Any(a => a.SourceId == s.Id && a.InputRevisionId == s.CurrentInputRevisionId
                && a.ChatId == s.ChatId && a.TopicId == s.TopicId && a.Kind == "download"
                && (a.State == "queued" || a.State == "retry_wait" && a.RetryNotBefore <= now
                    || a.State == "downloading" && a.LeaseUntil <= now))
                || references.Any(r => r.InputRevisionId == s.CurrentInputRevisionId && r.ChatId == s.ChatId && r.TopicId == s.TopicId)
                    && !attempts.Any(a => a.SourceId == s.Id && a.InputRevisionId == s.CurrentInputRevisionId
                        && a.ChatId == s.ChatId && a.TopicId == s.TopicId && a.Kind == "image" && a.RunWindowId == null
                        && (a.State == "returned" || a.State == "failed" || a.State == "unknown"
                            || a.State == "claimed" && a.LeaseUntil > now || a.State == "dispatched" && a.LeaseUntil > now))))
            .OrderBy(s => s.AdmittedAt).ThenBy(s => s.Id).Take(limit).Select(s => new VetPhotoWork(
                new(familyId, botDbId, bot.TelegramBotId, s.ChatId, s.TopicId), s.Id, s.CurrentInputRevisionId, s.SourceAuthorUserId, null)).ToListAsync(ct);
        if (current.Count == limit) return current;
        // Validate a bounded candidate scan; invalid manifests become durable holds.
        var visited = new List<Guid>();
        for (var scanned = 0; scanned < MaxRunRepairsPerPoll && current.Count < limit;)
        {
        var scheduled = await (from attempt in attempts
            join window in db.Set<VetPhotoRunWindow>().AsNoTracking() on attempt.RunWindowId equals (Guid?)window.Id
            join run in db.Set<VetPhotoRun>().AsNoTracking() on window.RunId equals run.Id
            join source in DispatchSources(familyId, botDbId, bot.TelegramBotId) on attempt.SourceId equals source.Id
            join approval in db.Set<VetPhotoReview>().AsNoTracking() on run.SelectionReviewId equals approval.Id
            join profile in db.Set<VetProfile>().AsNoTracking() on approval.ProfileId equals (long?)profile.Id
            where window.FamilyId == familyId && window.BotDbId == botDbId && window.TelegramBotId == bot.TelegramBotId
                && run.FamilyId == familyId && run.BotDbId == botDbId && run.TelegramBotId == bot.TelegramBotId
                && attempt.ChatId == source.ChatId && attempt.TopicId == source.TopicId
                && window.ChatId == source.ChatId && window.TopicId == source.TopicId && run.ChatId == source.ChatId && run.TopicId == source.TopicId
                && (run.State == "approved" || run.State == "running") && run.CancelledAt == null && run.ModelName == "gpt-6.1-sol"
                && (window.State == "queued" || window.State == "running") && window.Ordinal == run.NextWindowOrdinal
                && !visited.Contains(attempt.Id)
                && approval.FamilyId == familyId && approval.BotDbId == botDbId && approval.TelegramBotId == bot.TelegramBotId
                && approval.ChatId == run.ChatId && approval.TopicId == run.TopicId && approval.State == "accepted"
                && approval.Kind == "reextract_selection" && approval.DecisionActorUserId == run.ActorUserId && approval.CompletePreviewDelivered
                && approval.SelectionJson == run.SelectionJson && profile.FamilyId == familyId && profile.BotDbId == botDbId
                && profile.Revision == approval.ProfileRevision
                && !attempts.Any(other => other.Id != attempt.Id && other.SourceId == attempt.SourceId && other.InputRevisionId == attempt.InputRevisionId
                    && other.ChatId == attempt.ChatId && other.TopicId == attempt.TopicId && other.Kind == "image"
                    && (other.State == "claimed" || other.State == "dispatched"))
                && attempt.Kind == "image" && attempt.ActorUserId == run.ActorUserId
                && attempt.ExpectedCurrentInputId == source.CurrentInputRevisionId && attempt.ExpectedSourceOrdinal == source.CurrentOrdinal
                && (attempt.State == "queued" || attempt.State == "claimed" && attempt.LeaseUntil <= now
                    || attempt.State == "dispatched" && attempt.LeaseUntil <= now)
                && inputs.Any(i => i.SourceId == source.Id && i.Id == attempt.InputRevisionId
                    && i.ChatId == source.ChatId && i.TopicId == source.TopicId)
                && db.FamilyMembers.Any(m => m.FamilyId == familyId && m.TelegramUserId == run.ActorUserId && m.Status == FamilyMemberStatus.Approved)
                && (source.ChatType == "private" && source.ChatId == run.ActorUserId && source.TopicId == null
                    || source.ChatType != "private" && db.Places.Any(p => p.BotId == botDbId && p.ChatId == source.ChatId
                        && p.TopicId == source.TopicId && p.Status == PlaceStatus.Approved))
            orderby run.CreatedAt, window.Ordinal, attempt.CreatedAt, attempt.Id
            select new { Attempt = attempt, Source = source, RunId = run.Id }).Take(Math.Min(5, MaxRunRepairsPerPoll - scanned)).ToListAsync(ct);
        if (scheduled.Count == 0) break;
        foreach (var row in scheduled)
        {
            visited.Add(row.Attempt.Id); scanned++;
            var scope = new VetDiaryScope(familyId, botDbId, bot.TelegramBotId, row.Source.ChatId, row.Source.TopicId);
            if (!await ScheduledSelectionAsync(scope, row.Source, row.Attempt, row.Attempt.ActorUserId, ct))
            { await RepairScheduledRunAsync(scope, row.RunId, row.Attempt.ActorUserId, ct); continue; }
            var window = await Scoped<VetPhotoRunWindow>(scope).AsNoTracking().SingleAsync(w => w.Id == row.Attempt.RunWindowId, ct);
            var run = await Scoped<VetPhotoRun>(scope).AsNoTracking().SingleAsync(r => r.Id == window.RunId, ct);
            var review = await Scoped<VetPhotoReview>(scope).AsNoTracking().SingleAsync(r => r.Id == run.SelectionReviewId, ct);
            var profile = await WorkflowProfileAsync(scope, ct);
            if (profile == null || review.ProfileId != profile.Id || review.ProfileRevision != profile.Revision)
            { await RepairScheduledRunAsync(scope, run.Id, row.Attempt.ActorUserId, ct); continue; }
            var others = await OtherImageAttemptsAsync(scope, row.Source.Id, row.Attempt.InputRevisionId, row.Attempt.Id, ct);
            if (others.LiveCalls > 0) continue;
            if (!await RunRecoveryCurrentAsync(scope, run, ct))
            { await RepairScheduledRunAsync(scope, run.Id, row.Attempt.ActorUserId, ct); continue; }
            current.Add(new(scope, row.Source.Id, row.Attempt.InputRevisionId, row.Attempt.ActorUserId, row.Attempt.Id));
            if (current.Count == limit) break;
        }
        }
        return current;
    }

    private async Task<Assistant.Domain.Bots.Bot?> RepairDownloadsAsync(long familyId, long botDbId, CancellationToken ct)
    {
        var before = TrackedBefore();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await CapacityLockAsync(ct);
            await _guard.LockAsync(familyId, botDbId, ct);
            await _guard.BotAsync(familyId, botDbId, null, ct);
            var bot = await db.Bots.AsNoTracking().SingleOrDefaultAsync(b => b.Id == botDbId && b.FamilyId == familyId && b.Status == BotStatus.Active, ct);
            if (bot == null) { await tx.CommitAsync(ct); return null; }
            var now = clock.UtcNow;
            var repair = await (from attempt in db.Set<VetPhotoAttempt>()
                join source in db.Set<VetPhotoSource>() on attempt.SourceId equals source.Id
                join input in db.Set<VetPhotoInputRevision>() on attempt.InputRevisionId equals input.Id
                where attempt.FamilyId == familyId && attempt.BotDbId == botDbId && attempt.TelegramBotId == bot.TelegramBotId
                    && source.FamilyId == familyId && source.BotDbId == botDbId && source.TelegramBotId == bot.TelegramBotId
                    && input.FamilyId == familyId && input.BotDbId == botDbId && input.TelegramBotId == bot.TelegramBotId && input.SourceId == source.Id
                    && attempt.ChatId == source.ChatId && attempt.TopicId == source.TopicId && input.ChatId == source.ChatId && input.TopicId == source.TopicId
                    && attempt.Kind == "download"
                    && (input.ReportedSize > VetPhotoImageLimits.MaxEncodedBytes
                        && (attempt.State == "queued" || attempt.State == "retry_wait" || attempt.State == "downloading" && attempt.LeaseUntil <= now)
                        || attempt.State == "downloading" && attempt.LeaseUntil <= now
                        || (attempt.State == "queued" || attempt.State == "retry_wait" || attempt.State == "downloading" && attempt.LeaseUntil <= now)
                            && input.ReusesImageInputId != null && db.Set<VetPhotoOriginalReference>().Any(r =>
                                r.FamilyId == familyId && r.BotDbId == botDbId && r.TelegramBotId == bot.TelegramBotId
                                && r.ChatId == input.ChatId && r.TopicId == input.TopicId && r.InputRevisionId == input.ReusesImageInputId
                                && r.State == "deleted"))
                orderby attempt.CreatedAt, attempt.Id
                select new { Attempt = attempt, Oversized = input.ReportedSize > VetPhotoImageLimits.MaxEncodedBytes,
                    OriginalDeleted = input.ReusesImageInputId != null && db.Set<VetPhotoOriginalReference>().Any(r =>
                        r.FamilyId == familyId && r.BotDbId == botDbId && r.TelegramBotId == bot.TelegramBotId
                        && r.ChatId == input.ChatId && r.TopicId == input.TopicId && r.InputRevisionId == input.ReusesImageInputId
                        && r.State == "deleted") }).Take(5).ToListAsync(ct);
            foreach (var row in repair)
            {
                ReleaseReservation(row.Attempt);
                var terminal = row.Oversized || row.OriginalDeleted || row.Attempt.DownloadAttemptCount >= 2;
                row.Attempt.State = terminal ? "failed" : "retry_wait";
                row.Attempt.FailureCategory = row.Oversized ? "encoded_image_too_large"
                    : row.OriginalDeleted ? "original_deleted" : "download_timeout";
                row.Attempt.ClaimToken = null; row.Attempt.LeaseUntil = null;
                row.Attempt.RetryNotBefore = terminal ? null : now;
                row.Attempt.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return bot;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None); DetachOwned(before); throw;
        }
    }
}
