using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Health;
using Assistant.Application.Health.Documents;
using Assistant.Application.Telegram;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Health.Documents;

public sealed class HealthDocumentStore(
    AssistantDbContext db, ICurrentFamily currentFamily, IClock clock) : IHealthDocumentStore
{
    public async Task<HealthDocumentAdmissionInfo?> AdmitAsync(
        HealthDocumentScope scope, IncomingMessage message, long updateId, CancellationToken token)
    {
        await ValidateScopeAsync(scope, token);
        if (!HealthDocumentCandidate.IsValid(message)) return null;
        var attachment = message.Document!;
        var candidate = new HealthDocumentAdmissionInfo(
            Guid.NewGuid(), scope, message.ChatId, message.TopicId, message.ChatType,
            message.MessageId, message.UserId, message.SentAt.ToUniversalTime(), attachment,
            message.Text, null, "admitted", 0, null, false, false);
        if (!await GrantsAllowAsync(candidate, token)) return null;
        var now = clock.UtcNow.ToUniversalTime();
        var fileName = HealthDocumentCandidate.FileNameMetadata(attachment.FileName);
        var mime = HealthDocumentCandidate.Metadata(attachment.MimeType);
        var size = attachment.FileSize is >= 0 ? attachment.FileSize : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO health_document_admissions
                (id, family_id, profile_id, bot_db_id, telegram_bot_id, chat_id, topic_id,
                 chat_type, telegram_message_id, sender_user_id, first_update_id, sent_at,
                 file_id, file_unique_id, file_name, mime_type, file_size, caption,
                 source_message_id, status, failure_reason, attempt_count, next_attempt_at,
                 lease_id, lease_expires_at, reaction_attempted, notice_attempted,
                 created_at, updated_at, deleted_at)
            VALUES ({candidate.Id}, {scope.FamilyId}, {scope.ProfileId}, {scope.BotDbId},
                {scope.TelegramBotId}, {message.ChatId}, CAST({message.TopicId} AS integer),
                {message.ChatType}, {message.MessageId}, CAST({message.UserId} AS bigint),
                {updateId}, {candidate.SentAt}, {attachment.FileId},
                CAST({attachment.FileUniqueId} AS text), CAST({fileName} AS text),
                CAST({mime} AS text), CAST({size} AS bigint), CAST({message.Text} AS text),
                NULL, 'admitted', NULL, 0, NULL, NULL, NULL, FALSE, FALSE, {now}, {now}, NULL)
            ON CONFLICT (family_id, telegram_bot_id, chat_id, telegram_message_id) DO NOTHING
            """, token);
        var row = await Admissions(scope).AsNoTracking().SingleOrDefaultAsync(
            a => a.ChatId == message.ChatId && a.TelegramMessageId == message.MessageId, token);
        if (row is null || row.TopicId != message.TopicId || row.ChatType != message.ChatType
            || row.SenderUserId != message.UserId || row.SentAt != candidate.SentAt
            || row.FileId != attachment.FileId || row.FileUniqueId != attachment.FileUniqueId
            || row.FileName != fileName || row.MimeType != mime || row.FileSize != size
            || row.Caption != message.Text)
            return null;
        // A tombstone is returned for identification, never revived or processed.
        return ToAdmission(row);
    }

    public async Task<HealthDocumentAdmissionInfo?> FindAsync(
        HealthDocumentScope scope, long chatId, int? topicId, int messageId, CancellationToken token)
    {
        await ValidateScopeAsync(scope, token);
        var row = await Admissions(scope).AsNoTracking().SingleOrDefaultAsync(
            a => a.ChatId == chatId && a.TopicId == topicId && a.TelegramMessageId == messageId, token);
        return row is null ? null : ToAdmission(row);
    }

    public async Task<HealthDocumentAdmissionInfo?> BindAsync(
        HealthDocumentScope scope, Guid admissionId, long? messageDbId, CancellationToken token)
    {
        await ValidateScopeAsync(scope, token);
        var row = await Admissions(scope).AsNoTracking().SingleOrDefaultAsync(a => a.Id == admissionId, token);
        if (row is null) return null;
        if (row.DeletedAt != null) return ToAdmission(row);
        var info = ToAdmission(row);
        if (!await GrantsAllowAsync(info, token))
        {
            await PauseAsync(info, token);
            return await ReadAdmissionAsync(scope, admissionId, token);
        }
        var sourceId = await SourceQuery(row).Where(m => messageDbId == null || m.Id == messageDbId)
            .Select(m => (long?)m.Id).SingleOrDefaultAsync(token);
        if (sourceId is null) return info;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (!await HealthDocumentSourceLock.LockAsync(db, scope.FamilyId, sourceId.Value, token)) return info;
        await Admissions(scope).Where(a => a.Id == admissionId && a.DeletedAt == null
                && (a.SourceMessageId == null || a.SourceMessageId == sourceId))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.SourceMessageId, sourceId)
                .SetProperty(a => a.UpdatedAt, clock.UtcNow.ToUniversalTime()), token);
        await transaction.CommitAsync(token);
        return await ReadAdmissionAsync(scope, admissionId, token);
    }

    public async Task<IReadOnlyList<HealthDocumentAdmissionInfo>> GetDueAsync(
        HealthDocumentScope scope, CancellationToken token)
    {
        await ValidateScopeAsync(scope, token);
        var now = clock.UtcNow.ToUniversalTime();
        var documents = Documents(scope);
        var rows = await Admissions(scope).AsNoTracking()
            .Where(a => a.DeletedAt == null && (a.LeaseId == null || a.LeaseExpiresAt <= now)
                && (a.NextAttemptAt == null || a.NextAttemptAt <= now)
                && (a.Status != "completed" || !a.ReactionAttempted
                    || !a.NoticeAttempted && documents.Any(d => d.AdmissionId == a.Id
                        && d.DeletedAt == null && (d.TextStatus != "read" || d.TextTruncated))))
            .OrderBy(a => a.NextAttemptAt ?? a.CreatedAt).ThenBy(a => a.Id)
            .Take(HealthDocumentLimits.RecoveryBatchSize).ToListAsync(token);
        return rows.Select(ToAdmission).ToArray();
    }

    public async Task<bool> IsAuthorizedAsync(HealthDocumentAdmissionInfo admission, CancellationToken token)
    {
        await ValidateScopeAsync(admission.Scope, token);
        var current = await Admissions(admission.Scope).AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == admission.Id && a.DeletedAt == null, token);
        return current is not null && SameIdentity(current, admission)
            && await GrantsAllowAsync(ToAdmission(current), token);
    }

    public async Task PauseAsync(HealthDocumentAdmissionInfo admission, CancellationToken token)
    {
        await ValidateScopeAsync(admission.Scope, token);
        var now = clock.UtcNow.ToUniversalTime();
        await Admissions(admission.Scope).Where(a => a.Id == admission.Id && a.DeletedAt == null
                && (a.LeaseId == null || a.LeaseExpiresAt <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "paused")
                .SetProperty(a => a.NextAttemptAt, (DateTimeOffset?)(now + HealthDocumentLimits.RecoveryInterval))
                .SetProperty(a => a.LeaseId, (Guid?)null).SetProperty(a => a.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(a => a.UpdatedAt, now), token);
    }

    public async Task DeferAsync(HealthDocumentAdmissionInfo admission, CancellationToken token)
    {
        await ValidateScopeAsync(admission.Scope, token);
        var now = clock.UtcNow.ToUniversalTime();
        await Admissions(admission.Scope).Where(a => a.Id == admission.Id && a.DeletedAt == null
                && (a.LeaseId == null || a.LeaseExpiresAt <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.NextAttemptAt,
                    (DateTimeOffset?)(now + HealthDocumentLimits.RecoveryInterval))
                .SetProperty(a => a.UpdatedAt, now), token);
    }

    public async Task<HealthDocumentLease?> TryClaimAsync(
        HealthDocumentAdmissionInfo admission, CancellationToken token)
    {
        await ValidateScopeAsync(admission.Scope, token);
        if (!await IsAuthorizedAsync(admission, token))
        {
            await PauseAsync(admission, token);
            return null;
        }
        var sourceId = admission.SourceMessageId;
        if (sourceId is null) return null;
        var scope = admission.Scope;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (!await HealthDocumentSourceLock.LockAsync(db, scope.FamilyId, sourceId.Value, token)) return null;
        var row = await Admissions(scope).AsNoTracking().SingleOrDefaultAsync(
            a => a.Id == admission.Id && a.DeletedAt == null && a.SourceMessageId == sourceId, token);
        if (row is null || !SameIdentity(row, admission)
            || !await SourceQuery(row).AnyAsync(m => m.Id == sourceId, token)
            || !await GrantsAllowAsync(ToAdmission(row), token)) return null;
        var now = clock.UtcNow.ToUniversalTime();
        var document = await Documents(scope).AsNoTracking().Where(d => d.AdmissionId == row.Id)
            .Select(d => new { d.Id, d.DeletedAt, d.TextStatus }).SingleOrDefaultAsync(token);
        if (document?.DeletedAt != null) return null;
        var leaseId = Guid.NewGuid();
        var phase = document is null || document.TextStatus == "processing" ? "processing" : "completed";
        var claimed = await Admissions(scope).Where(a => a.Id == row.Id && a.DeletedAt == null
                && a.SourceMessageId == sourceId && (a.LeaseId == null || a.LeaseExpiresAt <= now)
                && (a.NextAttemptAt == null || a.NextAttemptAt <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LeaseId, (Guid?)leaseId)
                .SetProperty(a => a.LeaseExpiresAt, (DateTimeOffset?)(now + HealthDocumentLimits.LeaseLifetime))
                .SetProperty(a => a.Status, phase).SetProperty(a => a.UpdatedAt, now), token);
        if (claimed != 1) return null;
        if (document is null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO documents
                    (family_id, profile_id, admission_id, source_message_id, telegram_file_id,
                     file_name, mime_type, size_bytes, caption, text, posted_by_user_id, posted_at,
                     text_status, text_failure_reason, text_truncated, created_at, updated_at, deleted_at)
                VALUES ({scope.FamilyId}, {scope.ProfileId}, {row.Id}, {sourceId.Value}, {row.FileId},
                    CAST({row.FileName} AS text), CAST({row.MimeType} AS text), CAST({row.FileSize} AS bigint),
                    CAST({row.Caption} AS text), NULL, CAST({row.SenderUserId} AS bigint), {row.SentAt},
                    'processing', NULL, FALSE, {now}, {now}, NULL)
                ON CONFLICT (family_id, profile_id, source_message_id) DO NOTHING
                """, token);
        }
        var current = await Admissions(scope).AsNoTracking().SingleAsync(a => a.Id == row.Id, token);
        var result = await InfoQuery(scope).SingleOrDefaultAsync(d => d.AdmissionId == row.Id, token);
        if (result is null) return null;
        await transaction.CommitAsync(token);
        return new HealthDocumentLease(leaseId, ToAdmission(current), ToInfo(result));
    }

    public async Task<int?> TryBeginAttemptAsync(HealthDocumentLease lease, CancellationToken token)
    {
        await ValidateScopeAsync(lease.Admission.Scope, token);
        if (!await IsAuthorizedAsync(lease.Admission, token)) return null;
        var now = clock.UtcNow.ToUniversalTime();
        var documents = Documents(lease.Admission.Scope);
        var changed = await Owned(lease, now).Where(a => a.AttemptCount < HealthDocumentLimits.MaxAttempts
                && documents.Any(d => d.AdmissionId == a.Id
                    && d.DeletedAt == null && d.TextStatus == "processing"))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AttemptCount, a => a.AttemptCount + 1)
                .SetProperty(a => a.UpdatedAt, now), token);
        if (changed != 1) return null;
        return await Owned(lease, now).Select(a => (int?)a.AttemptCount).SingleOrDefaultAsync(token);
    }

    public async Task<bool> RenewAsync(HealthDocumentLease lease, CancellationToken token)
    {
        await ValidateScopeAsync(lease.Admission.Scope, token);
        var now = clock.UtcNow.ToUniversalTime();
        if (!await IsAuthorizedAsync(lease.Admission, token))
        {
            await Owned(lease, now).ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "paused")
                .SetProperty(a => a.NextAttemptAt, (DateTimeOffset?)(now + HealthDocumentLimits.RecoveryInterval))
                .SetProperty(a => a.LeaseId, (Guid?)null).SetProperty(a => a.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(a => a.UpdatedAt, now), token);
            return false;
        }
        return await Owned(lease, now).ExecuteUpdateAsync(s => s.SetProperty(a => a.LeaseExpiresAt,
                (DateTimeOffset?)(now + HealthDocumentLimits.LeaseLifetime))
            .SetProperty(a => a.UpdatedAt, now), token) == 1;
    }

    public async Task ReleaseAsync(HealthDocumentLease lease, CancellationToken token)
    {
        await ValidateScopeAsync(lease.Admission.Scope, token);
        // Cancellation cleanup may release an expired lease, but never another owner's lease.
        await Admissions(lease.Admission.Scope).Where(a => a.Id == lease.Admission.Id
                && a.DeletedAt == null && a.LeaseId == lease.LeaseId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LeaseId, (Guid?)null)
                .SetProperty(a => a.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(a => a.UpdatedAt, clock.UtcNow.ToUniversalTime()), token);
    }

    public async Task<bool> FinishAsync(
        HealthDocumentLease lease, DocumentTextExtraction? result, string textStatus,
        string? failureReason, DateTimeOffset? retryAt, CancellationToken token)
    {
        await ValidateScopeAsync(lease.Admission.Scope, token);
        ValidateResult(result, textStatus, failureReason, retryAt);
        retryAt = retryAt?.ToUniversalTime();
        if (!await IsAuthorizedAsync(lease.Admission, token)) return false;
        var sourceId = lease.Admission.SourceMessageId;
        if (sourceId is null) return false;
        var scope = lease.Admission.Scope;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (!await HealthDocumentSourceLock.LockAsync(db, scope.FamilyId, sourceId.Value, token)) return false;
        var now = clock.UtcNow.ToUniversalTime();
        if (!await Owned(lease, now).AnyAsync(token)) return false;
        var text = textStatus == "read" ? result!.Text : null;
        var truncated = textStatus == "read" && result!.Truncated;
        var changedDocument = await Documents(scope).Where(d => d.Id == lease.Document.Id
                && d.AdmissionId == lease.Admission.Id && d.SourceMessageId == sourceId
                && d.DeletedAt == null && d.TextStatus == "processing")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Text, text)
                .SetProperty(d => d.TextStatus, textStatus).SetProperty(d => d.TextFailureReason, failureReason)
                .SetProperty(d => d.TextTruncated, truncated).SetProperty(d => d.UpdatedAt, now), token);
        if (changedDocument != 1) return false;
        var phase = retryAt is null ? "completed" : "admitted";
        var changedAdmission = await Owned(lease, now)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, phase)
                .SetProperty(a => a.FailureReason, failureReason).SetProperty(a => a.NextAttemptAt, retryAt)
                .SetProperty(a => a.LeaseId, (Guid?)null).SetProperty(a => a.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(a => a.UpdatedAt, now), token);
        if (changedAdmission != 1) return false;
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task<bool> TryClaimDeliveryAsync(
        HealthDocumentAdmissionInfo admission, bool reaction, CancellationToken token)
    {
        await ValidateScopeAsync(admission.Scope, token);
        if (!await IsAuthorizedAsync(admission, token)) return false;
        var documents = Documents(admission.Scope);
        var query = Admissions(admission.Scope).Where(a => a.Id == admission.Id && a.DeletedAt == null
            && documents.Any(d => d.AdmissionId == a.Id && d.DeletedAt == null));
        var now = clock.UtcNow.ToUniversalTime();
        if (reaction)
            return await query.Where(a => !a.ReactionAttempted)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ReactionAttempted, true)
                    .SetProperty(a => a.UpdatedAt, now), token) == 1;
        return await query.Where(a => !a.NoticeAttempted)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.NoticeAttempted, true)
                .SetProperty(a => a.UpdatedAt, now), token) == 1;
    }

    public async Task<bool> HasAcknowledgedDocumentAsync(
        HealthDocumentScope scope, long chatId, int messageId, CancellationToken token)
    {
        await ValidateScopeAsync(scope, token);
        var documents = Documents(scope);
        return await Admissions(scope).AnyAsync(a => a.ChatId == chatId && a.TelegramMessageId == messageId
            && a.DeletedAt == null && a.ReactionAttempted
            && documents.Any(d => d.AdmissionId == a.Id && d.DeletedAt == null), token);
    }

    public async Task<IReadOnlyList<HealthDocumentInfo>> GetLatestAsync(
        HealthDocumentScope scope, CancellationToken token)
    {
        await ValidateScopeAsync(scope, token);
        var rows = await InfoQuery(scope).OrderByDescending(d => d.PostedAt).ThenByDescending(d => d.Id)
            .Take(10).ToListAsync(token);
        return rows.Select(ToInfo).ToArray();
    }

    public async Task<HealthDocumentInfo?> GetDocumentAsync(
        HealthDocumentScope scope, Guid admissionId, CancellationToken token)
    {
        await ValidateScopeAsync(scope, token);
        var row = await InfoQuery(scope).SingleOrDefaultAsync(d => d.AdmissionId == admissionId, token);
        return row is null ? null : ToInfo(row);
    }

    public async Task<HealthDocumentContextSnapshot> GetContextAsync(
        HealthDocumentScope scope, CancellationToken token)
    {
        await ValidateScopeAsync(scope, token);
        var rows = await InfoQuery(scope).OrderByDescending(d => d.PostedAt).ThenByDescending(d => d.Id)
            .ToListAsync(token);
        var remaining = HealthDocumentLimits.ContextCharacters;
        var result = new List<HealthDocumentInfo>(rows.Count);
        foreach (var row in rows)
        {
            string? text = null;
            var contextTruncated = false;
            if (row.TextStatus == "read" && remaining > 0)
            {
                // SQL substring bounds transferred text; no full document body is materialized.
                var take = remaining + 1;
                var candidate = await Documents(scope).Where(d => d.Id == row.Id && d.DeletedAt == null
                        && d.TextStatus == "read" && d.Text != null)
                    .Select(d => new { Text = d.Text!.Substring(0, take), Truncated = d.Text!.Length > take })
                    .SingleOrDefaultAsync(token);
                text = candidate?.Text;
                contextTruncated = candidate?.Truncated ?? false;
                if (text is not null)
                {
                    // PostgreSQL substring counts Unicode characters, .NET budgets UTF-16 units.
                    // Keep the sentinel within the latter bound without splitting a surrogate pair.
                    if (text.Length > take)
                    {
                        var end = take;
                        if (end > 0 && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
                        text = text[..end];
                        contextTruncated = true;
                    }
                    remaining = Math.Max(0, remaining - text.Length);
                }
            }
            result.Add(ToInfo(row) with { Text = text, ContextTextTruncated = contextTruncated });
        }
        return new HealthDocumentContextSnapshot(result);
    }

    public async Task<HealthDocumentSourceDeletion> DeleteSourceAsync(
        HealthDocumentScope scope, long chatId, int? topicId, int messageId,
        long? actorId, CancellationToken token)
    {
        await ValidateScopeAsync(scope, token);
        var row = await Admissions(scope).AsNoTracking().SingleOrDefaultAsync(a => a.ChatId == chatId
            && a.TopicId == topicId && a.TelegramMessageId == messageId, token);
        if (row is null || row.DeletedAt != null) return HealthDocumentSourceDeletion.None;
        // Command authorization belongs to the current actor/place. Original sender revocation
        // must not stop an approved member from deleting a retained source.
        var sourceId = row.SourceMessageId ?? await SourceQuery(row).Select(m => (long?)m.Id).SingleOrDefaultAsync(token);
        if (sourceId is not null && !await SourceQuery(row).AnyAsync(m => m.Id == sourceId, token))
            return HealthDocumentSourceDeletion.None;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (sourceId is not null && !await HealthDocumentSourceLock.LockAsync(db, scope.FamilyId, sourceId.Value, token))
            return HealthDocumentSourceDeletion.None;
        var now = clock.UtcNow.ToUniversalTime();
        var deleted = await Admissions(scope).Where(a => a.Id == row.Id && a.DeletedAt == null
                && (a.SourceMessageId == null || a.SourceMessageId == sourceId))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.SourceMessageId, sourceId)
                .SetProperty(a => a.DeletedAt, (DateTimeOffset?)now).SetProperty(a => a.Status, "deleted")
                .SetProperty(a => a.LeaseId, (Guid?)null).SetProperty(a => a.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(a => a.NextAttemptAt, (DateTimeOffset?)null).SetProperty(a => a.UpdatedAt, now), token);
        if (deleted != 1) return HealthDocumentSourceDeletion.None;
        if (sourceId is null)
        {
            // A never-bound admission cannot have document/events/pending source effects.
            // Never query SourceMessageId == null, which would match unrelated manual records.
            await transaction.CommitAsync(token);
            return new HealthDocumentSourceDeletion(true, DeletedEvents.None, []);
        }
        await Documents(scope).Where(d => d.AdmissionId == row.Id && d.SourceMessageId == sourceId
                && d.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeletedAt, (DateTimeOffset?)now)
                .SetProperty(d => d.UpdatedAt, now), token);
        var eventQuery = db.Events.Where(e => e.FamilyId == scope.FamilyId && e.ProfileId == scope.ProfileId
            && e.BotId == scope.TelegramBotId && e.ChatId == chatId && e.TopicId == topicId
            && e.SourceMessageId == sourceId && e.DeletedAt == null);
        var events = await eventQuery.AsNoTracking().OrderBy(e => e.Id)
            .Select(e => new HealthEventInfo(e.Id, e.Type, e.OccurredAt, e.Payload, e.SourceMessageId)).ToListAsync(token);
        await eventQuery.ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, (DateTimeOffset?)now)
            .SetProperty(e => e.DeleteReason, EventDeleteReasons.Del).SetProperty(e => e.UpdatedAt, now), token);
        var pending = await db.PendingRecords.AsNoTracking().Where(p => p.FamilyId == scope.FamilyId
                && p.ProfileId == scope.ProfileId && p.BotId == scope.TelegramBotId
                && p.ChatId == chatId && p.TopicId == topicId && p.SourceMessageId == sourceId
                && p.Status == PendingRecordStatuses.Pending).OrderBy(p => p.Id).ToListAsync(token);
        var closed = new List<PendingRecordInfo>();
        foreach (var item in pending)
        {
            if (await PendingRecordTransitions.ResolveAsync(db, scope.FamilyId, item.Id,
                    PendingRecordStatuses.Declined, actorId, now, token) == 1)
                closed.Add(PendingRecordStore.ToInfo(item) with { Status = PendingRecordStatuses.Declined });
        }
        var activeEvent = await db.Events.AnyAsync(e => e.FamilyId == scope.FamilyId
            && e.SourceMessageId == sourceId && e.DeletedAt == null, token);
        var activeDocument = await db.HealthDocuments.AnyAsync(d => d.FamilyId == scope.FamilyId
            && d.SourceMessageId == sourceId && d.DeletedAt == null, token);
        IReadOnlyList<MessageRef> clear = activeEvent || activeDocument ? [] : [new(chatId, messageId)];
        await transaction.CommitAsync(token);
        // Only these source rows are detached; never clear the shared context's unrelated work.
        foreach (var tracked in db.ChangeTracker.Entries<HealthEvent>().Where(e => e.Entity.FamilyId == scope.FamilyId
                     && e.Entity.ProfileId == scope.ProfileId && e.Entity.SourceMessageId == sourceId).ToArray())
            tracked.State = EntityState.Detached;
        return new HealthDocumentSourceDeletion(true, new DeletedEvents(events, clear), closed);
    }

    private async Task ValidateScopeAsync(HealthDocumentScope scope, CancellationToken token)
    {
        EnsureFamily(scope.FamilyId);
        if (!await db.HealthProfiles.AnyAsync(p => p.Id == scope.ProfileId && p.FamilyId == scope.FamilyId
                && p.BotId == scope.BotDbId, token)
            || !await db.Bots.AnyAsync(b => b.Id == scope.BotDbId && b.FamilyId == scope.FamilyId
                && b.TelegramBotId == scope.TelegramBotId && b.Role == "health", token))
            throw new InvalidOperationException("Health document scope does not belong to this family and bot.");
    }

    private void EnsureFamily(long familyId)
    {
        if (currentFamily.FamilyId != familyId)
            throw new InvalidOperationException("Health data is only accessed on a request scope of the same family.");
    }

    private IQueryable<HealthDocumentAdmission> Admissions(HealthDocumentScope scope)
    {
        EnsureFamily(scope.FamilyId);
        return db.HealthDocumentAdmissions.Where(a => a.FamilyId == scope.FamilyId && a.ProfileId == scope.ProfileId
            && a.BotDbId == scope.BotDbId && a.TelegramBotId == scope.TelegramBotId);
    }

    private IQueryable<HealthDocument> Documents(HealthDocumentScope scope)
    {
        EnsureFamily(scope.FamilyId);
        return db.HealthDocuments.Where(d => d.FamilyId == scope.FamilyId && d.ProfileId == scope.ProfileId);
    }

    private IQueryable<HealthDocumentAdmission> Owned(HealthDocumentLease lease, DateTimeOffset now) =>
        Admissions(lease.Admission.Scope).Where(a => a.Id == lease.Admission.Id && a.DeletedAt == null
            && a.SourceMessageId == lease.Admission.SourceMessageId && a.LeaseId == lease.LeaseId
            && a.LeaseExpiresAt > now);

    private IQueryable<StoredMessage> SourceQuery(HealthDocumentAdmission row) => db.Messages.AsNoTracking()
        .Where(m => m.FamilyId == row.FamilyId && m.BotId == row.TelegramBotId
            && m.ChatId == row.ChatId && m.TopicId == row.TopicId && m.TelegramMessageId == row.TelegramMessageId
            && m.Direction == MessageDirection.In && m.Kind == MessageKind.Document
            && m.UserId == row.SenderUserId && m.ChatType == row.ChatType && m.SentAt == row.SentAt);

    private async Task<bool> GrantsAllowAsync(HealthDocumentAdmissionInfo admission, CancellationToken token)
    {
        var scope = admission.Scope;
        EnsureFamily(scope.FamilyId);
        if (!await db.Bots.AnyAsync(b => b.Id == scope.BotDbId && b.FamilyId == scope.FamilyId
                && b.TelegramBotId == scope.TelegramBotId && b.Role == "health" && b.Status == BotStatus.Active, token)
            || !await db.Families.AnyAsync(f => f.Id == scope.FamilyId, token)
            || !await db.HealthProfiles.AnyAsync(p => p.Id == scope.ProfileId && p.FamilyId == scope.FamilyId
                && p.BotId == scope.BotDbId, token)) return false;
        if (admission.SenderUserId is { } user && !await db.FamilyMembers.AnyAsync(m => m.FamilyId == scope.FamilyId
                && m.TelegramUserId == user && m.Status == FamilyMemberStatus.Approved, token)) return false;
        if (admission.ChatType == "private")
            return admission.SenderUserId is { } sender && sender == admission.ChatId;
        return admission.ChatType is "group" or "supergroup" && await db.Places.AnyAsync(p => p.BotId == scope.BotDbId
            && p.ChatId == admission.ChatId && p.TopicId == admission.TopicId && p.Status == PlaceStatus.Approved, token);
    }

    private async Task<HealthDocumentAdmissionInfo?> ReadAdmissionAsync(
        HealthDocumentScope scope, Guid id, CancellationToken token)
    {
        var row = await Admissions(scope).AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, token);
        return row is null ? null : ToAdmission(row);
    }

    private IQueryable<InfoRow> InfoQuery(HealthDocumentScope scope) =>
        from d in Documents(scope).AsNoTracking()
        join a in Admissions(scope).AsNoTracking() on d.AdmissionId equals a.Id
        where d.DeletedAt == null && a.DeletedAt == null
        select new InfoRow
        {
            Id = d.Id, AdmissionId = d.AdmissionId, SourceMessageId = d.SourceMessageId,
            PostedAt = d.PostedAt, FileName = d.FileName, Caption = d.Caption,
            TextStatus = d.TextStatus, FailureReason = d.TextFailureReason,
            TextTruncated = d.TextTruncated, AdmissionStatus = a.Status, NextAttemptAt = a.NextAttemptAt
        };

    private static HealthDocumentInfo ToInfo(InfoRow row) => new(row.Id, row.SourceMessageId, row.PostedAt,
        row.FileName, row.Caption, row.TextStatus, row.FailureReason, row.TextTruncated,
        row.AdmissionStatus, row.NextAttemptAt);

    private static HealthDocumentAdmissionInfo ToAdmission(HealthDocumentAdmission row) => new(
        row.Id, new(row.FamilyId, row.ProfileId, row.BotDbId, row.TelegramBotId), row.ChatId, row.TopicId,
        row.ChatType, row.TelegramMessageId, row.SenderUserId, row.SentAt,
        new(row.FileId, row.FileUniqueId, row.FileName, row.MimeType, row.FileSize),
        row.Caption, row.SourceMessageId, row.Status, row.AttemptCount, row.NextAttemptAt,
        row.ReactionAttempted, row.NoticeAttempted);

    private static bool SameIdentity(HealthDocumentAdmission row, HealthDocumentAdmissionInfo info) =>
        row.ChatId == info.ChatId && row.TopicId == info.TopicId && row.ChatType == info.ChatType
        && row.TelegramMessageId == info.TelegramMessageId && row.SenderUserId == info.SenderUserId
        && row.SentAt == info.SentAt && row.FileId == info.Attachment.FileId
        && row.FileUniqueId == info.Attachment.FileUniqueId && row.FileName == info.Attachment.FileName
        && row.MimeType == info.Attachment.MimeType && row.FileSize == info.Attachment.FileSize && row.Caption == info.Caption;

    private static void ValidateResult(
        DocumentTextExtraction? result, string status, string? reason, DateTimeOffset? retryAt)
    {
        if (status is not ("read" or "metadata_only" or "failed" or "processing"))
            throw new ArgumentException("Invalid document text status.", nameof(status));
        if (reason is not (null or "unsupported_format" or "too_large" or "invalid_pdf" or "encrypted_pdf"
            or "binary_content" or "invalid_encoding" or "no_readable_text" or "unavailable" or "timeout" or "interrupted"))
            throw new ArgumentException("Invalid document failure category.", nameof(reason));
        if (status == "read" && (result is null || string.IsNullOrWhiteSpace(result.Text)
                || result.Text.Length > HealthDocumentLimits.MaxTextCharacters || result.FailureReason != null || reason != null)
            || status != "read" && result?.Text != null
            || (status == "processing") != (retryAt != null)
            || status != "read" && reason == null)
            throw new ArgumentException("Inconsistent document finalization.");
    }

    private sealed class InfoRow
    {
        public long Id { get; init; }
        public Guid AdmissionId { get; init; }
        public long SourceMessageId { get; init; }
        public DateTimeOffset PostedAt { get; init; }
        public string? FileName { get; init; }
        public string? Caption { get; init; }
        public string TextStatus { get; init; } = "";
        public string? FailureReason { get; init; }
        public bool TextTruncated { get; init; }
        public string AdmissionStatus { get; init; } = "";
        public DateTimeOffset? NextAttemptAt { get; init; }
    }
}
