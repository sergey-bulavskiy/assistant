using Assistant.Application.Health;

namespace Assistant.UnitTests.Fakes;

/// <summary>In-memory pending_records with the real store's rules: ids from 1, a row leaves
/// "pending" once, and a throwing inSameTransaction leaves it pending.</summary>
public class FakePendingRecordStore : IPendingRecordStore
{
    private long _nextId = 1;

    public Dictionary<long, PendingRecordInfo> Rows { get; } = new();

    public List<(long FamilyId, long ProfileId, NewPendingRecord Record)> Added { get; } = new();

    /// <summary>CreatedAt of rows added from now on.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Parse("2030-02-07T10:00:00Z");

    /// <summary>When set, AddAsync throws this instead of storing.</summary>
    public Exception? ThrowOnAdd { get; set; }

    public Task<long> AddAsync(long familyId, long profileId, NewPendingRecord record, CancellationToken cancellationToken)
    {
        if (ThrowOnAdd is { } exception)
        {
            throw exception;
        }

        var id = _nextId++;
        Added.Add((familyId, profileId, record));
        Rows[id] = new PendingRecordInfo(
            id, profileId, record.SourceMessageId, record.BotId, record.ChatId, record.TopicId, record.TelegramMessageId, null,
            record.RequestedByUserId, record.Events, record.AlertedRuleKeys, "pending", CreatedAt);
        return Task.FromResult(id);
    }

    public Task SetPromptMessageAsync(long familyId, long id, int promptMessageId, CancellationToken cancellationToken)
    {
        Rows[id] = Rows[id] with { PromptMessageId = promptMessageId };
        return Task.CompletedTask;
    }

    public Task<PendingRecordInfo?> FindAsync(long familyId, long id, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.GetValueOrDefault(id));

    public Task<IReadOnlyList<PendingRecordInfo>> FindPendingByTelegramMessageAsync(
        long familyId, long botId, long chatId, int telegramMessageId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingRecordInfo>>(Rows.Values
            .Where(p => p.BotId == botId && p.ChatId == chatId && p.Status == "pending"
                && (p.TelegramMessageId == telegramMessageId || p.PromptMessageId == telegramMessageId))
            .OrderBy(p => p.Id)
            .ToList());

    public Task<PendingRecordInfo?> FindLatestPendingOfUserAsync(
        long familyId, long botId, long chatId, int? topicId, long userId, DateTimeOffset createdAfter, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.Values
            .Where(p => p.BotId == botId && p.ChatId == chatId && p.TopicId == topicId && p.RequestedByUserId == userId
                && p.Status == "pending" && p.CreatedAt >= createdAfter)
            .OrderByDescending(p => p.Id)
            .FirstOrDefault());

    public async Task<bool> TryResolveAsync(
        long familyId, long id, string status, long? resolvedByUserId, Func<CancellationToken, Task>? inSameTransaction,
        CancellationToken cancellationToken)
    {
        if (!Rows.TryGetValue(id, out var row) || row.Status != "pending")
        {
            return false;
        }

        if (inSameTransaction is not null)
        {
            // A throw leaves the row pending, like the rolled-back transaction.
            await inSameTransaction(cancellationToken);
        }

        Rows[id] = row with { Status = status };
        return true;
    }
}
