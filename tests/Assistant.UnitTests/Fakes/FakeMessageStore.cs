using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;

namespace Assistant.UnitTests.Fakes;

public class FakeMessageStore : IMessageStore
{
    private long _nextMessageDbId = 1;

    public List<(long BotId, long UpdateId, IncomingMessage? Message)> Calls { get; } = new();

    public List<(long BotId, long ChatId, int? TopicId, string ChatType, int TelegramMessageId, string Text)> OutgoingMessages { get; } = new();

    private StoreResult _nextResult = new(StoreOutcome.Stored, 1);

    public void SetNextResult(StoreResult result) => _nextResult = result;

    public Task EnsureBotStateAsync(BotIdentity identity, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<long> GetLastUpdateIdAsync(long botId, CancellationToken cancellationToken) => Task.FromResult(0L);

    public Task<StoreResult> StoreAsync(long botId, long updateId, IncomingMessage? message, CancellationToken cancellationToken)
    {
        Calls.Add((botId, updateId, message));
        return Task.FromResult(_nextResult);
    }

    public Task StoreOutgoingAsync(
        long botId, long chatId, int? topicId, string chatType, int telegramMessageId, string text, CancellationToken cancellationToken)
    {
        OutgoingMessages.Add((botId, chatId, topicId, chatType, telegramMessageId, text));
        _nextMessageDbId++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContextMessage>> GetRecentContextAsync(
        long botId, long chatId, int? topicId, long? afterMessageId, int maxMessages, CancellationToken cancellationToken)
    {
        // Mirrors the real store closely enough for unit tests: outgoing messages stored via
        // StoreOutgoingAsync above are the only source here (this fake never sees StoreAsync's
        // inbound text, which unit tests exercising context building don't need), in insertion
        // order (a stand-in for messages.id), filtered to this (bot, chat, topic), optionally
        // cut off after afterMessageId (an index into insertion order), capped at maxMessages most
        // recent, oldest first.
        var all = OutgoingMessages
            .Select((m, index) => (Row: m, Id: (long)(index + 1)))
            .Where(x => x.Row.BotId == botId && x.Row.ChatId == chatId && x.Row.TopicId == topicId)
            .ToList();

        if (afterMessageId is { } cutoffId)
        {
            all = all.Where(x => x.Id > cutoffId).ToList();
        }

        IReadOnlyList<ContextMessage> result = all
            .OrderByDescending(x => x.Id)
            .Take(maxMessages)
            .OrderBy(x => x.Id)
            .Select(x => new ContextMessage(MessageDirection.Out, null, x.Row.Text, DateTimeOffset.UtcNow))
            .ToList();

        return Task.FromResult(result);
    }
}
