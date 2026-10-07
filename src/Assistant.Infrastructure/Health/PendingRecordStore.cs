using System.Text.Json;
using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Health;
using Assistant.Domain.Health;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Health.Documents;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Health;

/// <summary>pending_records. Fails closed like EventStore: every method first checks that the
/// request's ICurrentFamily is the same family, on top of the family query filter and explicit
/// FamilyId predicates. Never IgnoreQueryFilters() here, and never use this store from a fresh DI
/// scope. The status change is a conditional UPDATE (ExecuteUpdate, nothing tracked), so a
/// redelivered or second tap changes nothing.</summary>
public class PendingRecordStore : IPendingRecordStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly AssistantDbContext _db;
    private readonly ICurrentFamily _currentFamily;
    private readonly IClock _clock;

    public PendingRecordStore(AssistantDbContext db, ICurrentFamily currentFamily, IClock clock)
    {
        _db = db;
        _currentFamily = currentFamily;
        _clock = clock;
    }

    public async Task<long> AddAsync(long familyId, long profileId, NewPendingRecord record, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);
        if (!await _db.HealthProfiles.AnyAsync(p => p.Id == profileId && p.FamilyId == familyId, cancellationToken))
        {
            throw new InvalidOperationException("Health profile not found in this family.");
        }

        var documentSource = await HealthDocumentSourceLock.IsDocumentSourceAsync(_db, familyId, profileId, record.SourceMessageId, cancellationToken);
        await using var sourceTransaction = documentSource && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (documentSource && record.SourceMessageId is { } documentMessageId)
        {
            if (!await HealthDocumentSourceLock.LockAsync(_db, familyId, documentMessageId, cancellationToken)
                || await HealthDocumentSourceLock.IsDeletedAsync(_db, familyId, profileId, documentMessageId, cancellationToken))
                throw new InvalidOperationException("Document source is no longer active.");
        }

        var row = new PendingRecord
        {
            FamilyId = familyId,
            ProfileId = profileId,
            SourceMessageId = record.SourceMessageId,
            BotId = record.BotId,
            ChatId = record.ChatId,
            TopicId = record.TopicId,
            TelegramMessageId = record.TelegramMessageId,
            RequestedByUserId = record.RequestedByUserId,
            Events = JsonSerializer.Serialize(
                record.Events.Select(e => new EventJson(e.Type, e.OccurredAt, e.OccurredAtSource, e.PayloadJson)).ToList(), JsonOptions),
            AlertedRuleKeys = record.AlertedRuleKeys.ToArray(),
            Status = PendingRecordStatuses.Pending,
            CreatedAt = _clock.UtcNow
        };
        _db.PendingRecords.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            // The request's context is shared: never leave this row tracked for the next save.
            _db.Entry(row).State = EntityState.Detached;
        }

        if (sourceTransaction is not null) await sourceTransaction.CommitAsync(cancellationToken);
        return row.Id;
    }

    public async Task SetPromptMessageAsync(long familyId, long id, int promptMessageId, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);
        await _db.PendingRecords
            .Where(p => p.Id == id && p.FamilyId == familyId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.PromptMessageId, promptMessageId), cancellationToken);
    }

    public async Task<PendingRecordInfo?> FindAsync(long familyId, long id, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);
        var row = await _db.PendingRecords.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.FamilyId == familyId, cancellationToken);
        return row is null ? null : ToInfo(row);
    }

    public async Task<IReadOnlyList<PendingRecordInfo>> FindPendingByTelegramMessageAsync(
        long familyId, long botId, long chatId, int telegramMessageId, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);
        var rows = await _db.PendingRecords.AsNoTracking()
            .Where(p => p.FamilyId == familyId && p.BotId == botId && p.ChatId == chatId && p.Status == PendingRecordStatuses.Pending
                && (p.TelegramMessageId == telegramMessageId || p.PromptMessageId == telegramMessageId))
            .OrderBy(p => p.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(ToInfo).ToArray();
    }

    public async Task<PendingRecordInfo?> FindLatestPendingOfUserAsync(
        long familyId, long botId, long chatId, int? topicId, long userId, DateTimeOffset createdAfter, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);
        var row = await _db.PendingRecords.AsNoTracking()
            .Where(p => p.FamilyId == familyId && p.BotId == botId && p.ChatId == chatId && p.TopicId == topicId
                && p.RequestedByUserId == userId && p.Status == PendingRecordStatuses.Pending && p.CreatedAt >= createdAfter)
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ToInfo(row);
    }

    public async Task<bool> TryResolveAsync(
        long familyId, long id, string status, long? resolvedByUserId, Func<CancellationToken, Task>? inSameTransaction,
        CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);
        if (status == PendingRecordStatuses.Pending)
        {
            throw new ArgumentException("A row can only leave the pending status.", nameof(status));
        }

        var now = _clock.UtcNow;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var pending = await _db.PendingRecords.AsNoTracking()
            .Where(p => p.Id == id && p.FamilyId == familyId && p.Status == PendingRecordStatuses.Pending)
            .Select(p => new { p.ProfileId, p.SourceMessageId }).SingleOrDefaultAsync(cancellationToken);
        if (pending is null) return false;
        if (pending.SourceMessageId is { } sourceId && await HealthDocumentSourceLock.IsDocumentSourceAsync(
                _db, familyId, pending.ProfileId, sourceId, cancellationToken))
        {
            if (!await HealthDocumentSourceLock.LockAsync(_db, familyId, sourceId, cancellationToken)
                || await HealthDocumentSourceLock.IsDeletedAsync(_db, familyId, pending.ProfileId, sourceId, cancellationToken))
                return false;
        }
        var changed = await PendingRecordTransitions.ResolveAsync(_db, familyId, id, status, resolvedByUserId, now, cancellationToken);
        if (changed != 1)
        {
            // Already decided (or not this family's row): disposing the transaction rolls it back.
            return false;
        }

        if (inSameTransaction is not null)
        {
            await inSameTransaction(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal static PendingRecordInfo ToInfo(PendingRecord row)
    {
        var events = JsonSerializer.Deserialize<List<EventJson>>(row.Events, JsonOptions) ?? new List<EventJson>();
        return new PendingRecordInfo(
            row.Id,
            row.ProfileId,
            row.SourceMessageId,
            row.BotId,
            row.ChatId,
            row.TopicId,
            row.TelegramMessageId,
            row.PromptMessageId,
            row.RequestedByUserId,
            events.Select(e => new NewHealthEvent(e.Type, e.OccurredAt.ToUniversalTime(), e.OccurredAtSource, e.PayloadJson)).ToArray(),
            row.AlertedRuleKeys,
            row.Status,
            row.CreatedAt);
    }

    // A null or different FamilyId both mean "not this family's request scope".
    private void EnsureFamilyScope(long familyId)
    {
        if (_currentFamily.FamilyId != familyId)
        {
            throw new InvalidOperationException("Health data is only accessed on a request scope of the same family.");
        }
    }

    /// <summary>One event inside pending_records.events. The payload stays a JSON string, so its
    /// exact text survives Postgres's jsonb formatting.</summary>
    private sealed record EventJson(string Type, DateTimeOffset OccurredAt, string OccurredAtSource, string PayloadJson);
}
