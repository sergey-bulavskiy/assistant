using Assistant.Application.Health;

namespace Assistant.UnitTests.Fakes;

public class FakeEventStore : IEventStore
{
    private long _nextId = 1;

    public List<(long FamilyId, long ProfileId, HealthEventSource Source, IReadOnlyList<NewHealthEvent> Events)> Added { get; } = new();

    public List<HealthEventInfo> ActiveEvents { get; } = new();

    public List<HealthEventInfo> Notes { get; } = new();
    public (long FamilyId, long ProfileId, string? Tag, DateTimeOffset Now, int Limit)? LastNotesQuery { get; private set; }

    public Task<IReadOnlyList<HealthEventInfo>> GetNotesAsync(
        long familyId, long profileId, string? normalizedTag, DateTimeOffset nowUtc, int limit,
        CancellationToken cancellationToken)
    {
        LastNotesQuery = (familyId, profileId, normalizedTag, nowUtc, limit);
        IReadOnlyList<HealthEventInfo> result = Notes
            .Where(note => normalizedTag is null
                || HealthEventPayloads.TryDeserialize<NotePayload>(note.PayloadJson)?.Tags.Contains(normalizedTag) == true)
            .OrderByDescending(note => note.OccurredAt).ThenByDescending(note => note.Id).Take(limit).ToArray();
        return Task.FromResult(result);
    }

    public (long FamilyId, long ProfileId, DateTimeOffset From, DateTimeOffset To)? LastRange { get; private set; }

    public List<DeleteCall> DeleteCalls { get; } = new();

    public DeletedEvents NextDeleted { get; set; } = DeletedEvents.None;

    /// <summary>When set, AddAsync throws this instead of storing.</summary>
    public Exception? ThrowOnAdd { get; set; }

    public List<(long FamilyId, long ProfileId, HealthEventSource Source, IReadOnlyList<NewHealthEvent> Events)> Replaced { get; } = new();

    /// <summary>Earlier events of the message that ReplaceMessageEventsAsync reports as deleted.</summary>
    public List<HealthEventInfo> ReplaceDeletes { get; } = new();

    /// <summary>Input index → id of the earlier event that ReplaceMessageEventsAsync reports as kept
    /// (unchanged); every other input gets a new id.</summary>
    public Dictionary<int, long> ReplaceKeeps { get; } = new();

    /// <summary>When set, ReplaceMessageEventsAsync throws this instead of replacing.</summary>
    public Exception? ThrowOnReplace { get; set; }

    public Task<ReplacedEvents> ReplaceMessageEventsAsync(
        long familyId, long profileId, HealthEventSource source, IReadOnlyList<NewHealthEvent> events, CancellationToken cancellationToken)
    {
        if (ThrowOnReplace is { } exception)
        {
            throw exception;
        }

        Replaced.Add((familyId, profileId, source, events));
        var kept = 0;
        var current = new List<HealthEventInfo>();
        for (var i = 0; i < events.Count; i++)
        {
            long id;
            if (ReplaceKeeps.TryGetValue(i, out var keptId))
            {
                id = keptId;
                kept++;
            }
            else
            {
                id = _nextId++;
            }

            current.Add(new HealthEventInfo(id, events[i].Type, events[i].OccurredAt, events[i].PayloadJson, source.MessageDbId));
        }

        return Task.FromResult(new ReplacedEvents(current, kept, ReplaceDeletes.ToList()));
    }

    public Task<IReadOnlyList<HealthEventInfo>> AddAsync(
        long familyId, long profileId, HealthEventSource source, IReadOnlyList<NewHealthEvent> events, CancellationToken cancellationToken)
    {
        if (ThrowOnAdd is { } exception)
        {
            throw exception;
        }

        Added.Add((familyId, profileId, source, events));
        IReadOnlyList<HealthEventInfo> saved = events
            .Select(e => new HealthEventInfo(_nextId++, e.Type, e.OccurredAt, e.PayloadJson, source.MessageDbId))
            .ToList();
        return Task.FromResult(saved);
    }

    public Task<IReadOnlyList<HealthEventInfo>> GetActiveAsync(
        long familyId, long profileId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        LastRange = (familyId, profileId, fromUtc, toUtc);
        return Task.FromResult<IReadOnlyList<HealthEventInfo>>(ActiveEvents.ToList());
    }

    /// <summary>Returned by FindLatestSourceMessageOfUserAsync.</summary>
    public long? LatestSourceMessageId { get; set; }

    public Task<long?> FindLatestSourceMessageOfUserAsync(
        long familyId, long profileId, long botId, long chatId, int? topicId, long userId, DateTimeOffset createdAfter,
        CancellationToken cancellationToken) =>
        Task.FromResult(LatestSourceMessageId);

    public Task<DeletedEvents> DeleteLatestOfUserAsync(
        long familyId, long profileId, long botId, long chatId, int? topicId, long userId, DateTimeOffset createdAfter, string reason,
        CancellationToken cancellationToken)
    {
        DeleteCalls.Add(new DeleteCall("latest", familyId, profileId, botId, chatId, topicId, userId, createdAfter, null, null, reason));
        return Task.FromResult(NextDeleted);
    }

    public Task<DeletedEvents> DeleteBySourceTelegramMessageAsync(
        long familyId, long profileId, long botId, long chatId, int telegramMessageId, string reason, CancellationToken cancellationToken,
        DateTimeOffset? createdAfter = null)
    {
        DeleteCalls.Add(new DeleteCall("message", familyId, profileId, botId, chatId, null, null, createdAfter, telegramMessageId, null, reason));
        return Task.FromResult(NextDeleted);
    }

    public Task<DeletedEvents> DeleteByIdAsync(long familyId, long profileId, long eventId, string reason, CancellationToken cancellationToken)
    {
        DeleteCalls.Add(new DeleteCall("id", familyId, profileId, null, null, null, null, null, null, eventId, reason));
        return Task.FromResult(NextDeleted);
    }

    public record DeleteCall(
        string Kind, long FamilyId, long ProfileId, long? BotId, long? ChatId, int? TopicId, long? UserId, DateTimeOffset? CreatedAfter,
        int? TelegramMessageId, long? EventId, string Reason);
}
