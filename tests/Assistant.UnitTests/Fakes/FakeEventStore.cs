using Assistant.Application.Health;

namespace Assistant.UnitTests.Fakes;

public class FakeEventStore : IEventStore
{
    private long _nextId = 1;

    public List<(long FamilyId, long ProfileId, HealthEventSource Source, IReadOnlyList<NewHealthEvent> Events)> Added { get; } = new();

    public List<HealthEventInfo> ActiveEvents { get; } = new();

    public (long FamilyId, long ProfileId, DateTimeOffset From, DateTimeOffset To)? LastRange { get; private set; }

    public List<DeleteCall> DeleteCalls { get; } = new();

    public DeletedEvents NextDeleted { get; set; } = DeletedEvents.None;

    /// <summary>When set, AddAsync throws this instead of storing.</summary>
    public Exception? ThrowOnAdd { get; set; }

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

    public Task<DeletedEvents> DeleteLatestOfUserAsync(
        long familyId, long profileId, long botId, long chatId, int? topicId, long userId, DateTimeOffset createdAfter, string reason,
        CancellationToken cancellationToken)
    {
        DeleteCalls.Add(new DeleteCall("latest", familyId, profileId, botId, chatId, topicId, userId, createdAfter, null, null, reason));
        return Task.FromResult(NextDeleted);
    }

    public Task<DeletedEvents> DeleteBySourceTelegramMessageAsync(
        long familyId, long profileId, long botId, long chatId, int telegramMessageId, string reason, CancellationToken cancellationToken)
    {
        DeleteCalls.Add(new DeleteCall("message", familyId, profileId, botId, chatId, null, null, null, telegramMessageId, null, reason));
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
