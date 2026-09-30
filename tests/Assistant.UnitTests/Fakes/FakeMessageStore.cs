using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;

namespace Assistant.UnitTests.Fakes;

public class FakeMessageStore : IMessageStore
{
    private long _nextMessageId;

    private sealed record StoredRow(long Id, long BotId, long ChatId, int? TopicId, MessageDirection Direction, string Text);

    // Both StoreAsync and StoreOutgoingAsync append here, sharing one id sequence, so
    // GetRecentContextAsync below reads a single ordered timeline -- the same shape the real
    // (messages.id-ordered) store has -- and tests of /new cutoffs and beforeMessageId behave
    // the same against the fake as against the real store.
    private readonly List<StoredRow> _rows = new();

    public List<(long BotId, long UpdateId, IncomingMessage? Message)> Calls { get; } = new();

    public List<(long BotId, long ChatId, int? TopicId, string ChatType, int TelegramMessageId, string Text)> OutgoingMessages { get; } = new();

    private StoreResult _nextResult = new(StoreOutcome.Stored, 1);

    public void SetNextResult(StoreResult result) => _nextResult = result;

    public Task EnsureBotStateAsync(BotIdentity identity, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<long> GetLastUpdateIdAsync(long botId, CancellationToken cancellationToken) => Task.FromResult(0L);

    public Task<StoreResult> StoreAsync(long botId, long updateId, IncomingMessage? message, CancellationToken cancellationToken)
    {
        Calls.Add((botId, updateId, message));

        if (message is { Kind: MessageKind.Text, Text: { } text })
        {
            _rows.Add(new StoredRow(++_nextMessageId, botId, message.ChatId, message.TopicId, MessageDirection.In, text));
        }

        return Task.FromResult(_nextResult);
    }

    public Task StoreOutgoingAsync(
        long botId, long chatId, int? topicId, string chatType, int telegramMessageId, string text, CancellationToken cancellationToken)
    {
        OutgoingMessages.Add((botId, chatId, topicId, chatType, telegramMessageId, text));
        _rows.Add(new StoredRow(++_nextMessageId, botId, chatId, topicId, MessageDirection.Out, text));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContextMessage>> GetRecentContextAsync(
        long botId, long chatId, int? topicId, long? afterMessageId, long? beforeMessageId, int maxMessages, CancellationToken cancellationToken)
    {
        if (maxMessages <= 0)
        {
            return Task.FromResult<IReadOnlyList<ContextMessage>>(Array.Empty<ContextMessage>());
        }

        var query = _rows
            .Where(r => r.BotId == botId && r.ChatId == chatId && r.TopicId == topicId && !r.Text.StartsWith('/'));

        if (afterMessageId is { } afterId)
        {
            query = query.Where(r => r.Id > afterId);
        }

        if (beforeMessageId is { } beforeId)
        {
            query = query.Where(r => r.Id < beforeId);
        }

        IReadOnlyList<ContextMessage> result = query
            .OrderByDescending(r => r.Id)
            .Take(maxMessages)
            .OrderBy(r => r.Id)
            .Select(r => new ContextMessage(r.Direction, null, r.Text, DateTimeOffset.UtcNow))
            .ToList();

        return Task.FromResult(result);
    }
}
