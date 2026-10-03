using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Health;
using Assistant.Domain.Health;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Health;

/// <summary>events. Fails closed like HealthProfileStore: every method first checks that the
/// request's ICurrentFamily is the same family, on top of the family query filter and explicit
/// FamilyId/ProfileId predicates. Soft delete only; deleted rows are never returned. Never
/// IgnoreQueryFilters() here, and never use this store from a fresh DI scope.</summary>
public class EventStore : IEventStore
{
    private readonly AssistantDbContext _db;
    private readonly ICurrentFamily _currentFamily;
    private readonly IClock _clock;

    public EventStore(AssistantDbContext db, ICurrentFamily currentFamily, IClock clock)
    {
        _db = db;
        _currentFamily = currentFamily;
        _clock = clock;
    }

    public async Task<IReadOnlyList<HealthEventInfo>> AddAsync(
        long familyId, long profileId, HealthEventSource source, IReadOnlyList<NewHealthEvent> events, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);
        if (events.Count == 0)
        {
            return Array.Empty<HealthEventInfo>();
        }

        var subjectTag = await GetSubjectTagAsync(familyId, profileId, cancellationToken);
        var now = _clock.UtcNow;
        var rows = events.Select(e => NewRow(familyId, profileId, subjectTag, source, e, now)).ToList();

        _db.Events.AddRange(rows);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // The request's context is shared: never leave failed rows tracked for the next save.
            foreach (var row in rows)
            {
                _db.Entry(row).State = EntityState.Detached;
            }

            throw;
        }

        return rows.Select(ToInfo).ToArray();
    }

    public async Task<IReadOnlyList<HealthEventInfo>> GetActiveAsync(
        long familyId, long profileId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);

        return await _db.Events.AsNoTracking()
            .Where(e => e.FamilyId == familyId && e.ProfileId == profileId && e.DeletedAt == null
                && e.OccurredAt >= fromUtc && e.OccurredAt < toUtc)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .Select(e => new HealthEventInfo(e.Id, e.Type, e.OccurredAt, e.Payload, e.SourceMessageId))
            .ToListAsync(cancellationToken);
    }

    public async Task<ReplacedEvents> ReplaceMessageEventsAsync(
        long familyId, long profileId, HealthEventSource source, IReadOnlyList<NewHealthEvent> events, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);
        if (source.MessageDbId is not { } messageDbId)
        {
            throw new ArgumentException("An edited message needs its messages.id.", nameof(source));
        }

        // Checked first, also when nothing is added: another family's profile is refused before any change.
        var subjectTag = await GetSubjectTagAsync(familyId, profileId, cancellationToken);

        var earlier = await _db.Events
            .Where(e => e.FamilyId == familyId && e.ProfileId == profileId && e.DeletedAt == null
                && e.SourceMessageId == messageDbId && e.BotId == source.BotId && e.ChatId == source.ChatId)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken);

        var now = _clock.UtcNow;
        var unmatched = new List<HealthEvent>(earlier);
        var current = new List<HealthEvent>();
        var added = new List<HealthEvent>();
        foreach (var e in events)
        {
            // An unchanged event keeps its row and id, so its safety alert claim still stops a second alert.
            var same = unmatched.FirstOrDefault(r =>
                r.Type == e.Type && r.OccurredAt == e.OccurredAt && HealthEventPayloads.SameJson(r.Payload, e.PayloadJson));
            if (same is not null)
            {
                unmatched.Remove(same);
                current.Add(same);
                continue;
            }

            var row = NewRow(familyId, profileId, subjectTag, source, e, now);
            added.Add(row);
            current.Add(row);
        }

        foreach (var row in unmatched)
        {
            row.DeletedAt = now;
            row.DeleteReason = EventDeleteReasons.Edit;
            row.UpdatedAt = now;
        }

        _db.Events.AddRange(added);
        try
        {
            // One save: the deletes and the inserts commit together, so a failure keeps the earlier events.
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // The request's context is shared: never leave failed changes tracked for the next save.
            foreach (var row in added.Concat(unmatched))
            {
                _db.Entry(row).State = EntityState.Detached;
            }

            throw;
        }

        return new ReplacedEvents(current.Select(ToInfo).ToArray(), events.Count - added.Count, unmatched.Select(ToInfo).ToArray());
    }

    public async Task<DeletedEvents> DeleteLatestOfUserAsync(
        long familyId, long profileId, long botId, long chatId, int? topicId, long userId, DateTimeOffset createdAfter, string reason,
        CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);

        var latestSource = await _db.Events.AsNoTracking()
            .Where(e => e.FamilyId == familyId && e.ProfileId == profileId && e.DeletedAt == null
                && e.BotId == botId && e.ChatId == chatId && e.TopicId == topicId
                && e.RecordedByUserId == userId && e.SourceMessageId != null && e.CreatedAt >= createdAfter)
            .OrderByDescending(e => e.SourceMessageId)
            .Select(e => e.SourceMessageId)
            .FirstOrDefaultAsync(cancellationToken);
        if (latestSource is null)
        {
            return DeletedEvents.None;
        }

        var rows = await _db.Events
            .Where(e => e.FamilyId == familyId && e.ProfileId == profileId && e.DeletedAt == null && e.SourceMessageId == latestSource)
            .ToListAsync(cancellationToken);
        return await SoftDeleteAsync(familyId, rows, reason, cancellationToken);
    }

    public async Task<DeletedEvents> DeleteBySourceTelegramMessageAsync(
        long familyId, long profileId, long botId, long chatId, int telegramMessageId, string reason, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);

        // messages.bot_id is the Telegram bot id, like events.bot_id.
        var sourceIds = _db.Messages
            .Where(m => m.BotId == botId && m.ChatId == chatId && m.TelegramMessageId == telegramMessageId)
            .Select(m => m.Id);
        var rows = await _db.Events
            .Where(e => e.FamilyId == familyId && e.ProfileId == profileId && e.DeletedAt == null
                && e.SourceMessageId != null && sourceIds.Contains(e.SourceMessageId.Value))
            .ToListAsync(cancellationToken);
        return await SoftDeleteAsync(familyId, rows, reason, cancellationToken);
    }

    public async Task<DeletedEvents> DeleteByIdAsync(long familyId, long profileId, long eventId, string reason, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);

        var rows = await _db.Events
            .Where(e => e.Id == eventId && e.FamilyId == familyId && e.ProfileId == profileId && e.DeletedAt == null)
            .ToListAsync(cancellationToken);
        return await SoftDeleteAsync(familyId, rows, reason, cancellationToken);
    }

    private async Task<DeletedEvents> SoftDeleteAsync(long familyId, List<HealthEvent> rows, string reason, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return DeletedEvents.None;
        }

        var now = _clock.UtcNow;
        foreach (var row in rows)
        {
            row.DeletedAt = now;
            row.DeleteReason = reason;
            row.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);

        var sourceIds = rows.Where(r => r.SourceMessageId != null).Select(r => r.SourceMessageId!.Value).Distinct().ToArray();
        var stillActive = await _db.Events.AsNoTracking()
            .Where(e => e.FamilyId == familyId && e.DeletedAt == null && e.SourceMessageId != null && sourceIds.Contains(e.SourceMessageId.Value))
            .Select(e => e.SourceMessageId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var withoutEvents = sourceIds.Except(stillActive).ToArray();
        var messages = await _db.Messages.AsNoTracking()
            .Where(m => withoutEvents.Contains(m.Id))
            .OrderBy(m => m.Id)
            .Select(m => new MessageRef(m.ChatId, m.TelegramMessageId))
            .ToListAsync(cancellationToken);

        return new DeletedEvents(rows.OrderBy(r => r.Id).Select(ToInfo).ToArray(), messages);
    }

    private async Task<string> GetSubjectTagAsync(long familyId, long profileId, CancellationToken cancellationToken) =>
        await _db.HealthProfiles.AsNoTracking()
            .Where(p => p.Id == profileId && p.FamilyId == familyId)
            .Select(p => p.SubjectTag)
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException("Health profile not found in this family.");

    private static HealthEvent NewRow(
        long familyId, long profileId, string subjectTag, HealthEventSource source, NewHealthEvent e, DateTimeOffset now) =>
        new()
        {
            FamilyId = familyId,
            ProfileId = profileId,
            Type = e.Type,
            SubjectTag = subjectTag,
            OccurredAt = e.OccurredAt,
            OccurredAtSource = e.OccurredAtSource,
            Payload = e.PayloadJson,
            Flags = e.Flags?.ToArray() ?? Array.Empty<string>(),
            SourceMessageId = source.MessageDbId,
            BotId = source.BotId,
            ChatId = source.ChatId,
            TopicId = source.TopicId,
            RecordedByUserId = source.UserId,
            CreatedAt = now,
            UpdatedAt = now
        };

    // A null or different FamilyId both mean "not this family's request scope".
    private void EnsureFamilyScope(long familyId)
    {
        if (_currentFamily.FamilyId != familyId)
        {
            throw new InvalidOperationException("Health data is only accessed on a request scope of the same family.");
        }
    }

    private static HealthEventInfo ToInfo(HealthEvent row) =>
        new(row.Id, row.Type, row.OccurredAt, row.Payload, row.SourceMessageId);
}
