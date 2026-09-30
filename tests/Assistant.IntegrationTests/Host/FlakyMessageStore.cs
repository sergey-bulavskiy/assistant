using Assistant.Application.Messages;
using Assistant.Application.Telegram;

namespace Assistant.IntegrationTests.Host;

/// <summary>
/// Decorates the real <see cref="IMessageStore"/> to simulate a transient DB failure on the first N
/// calls to <see cref="EnsureBotStateAsync"/> (per <see cref="EnsureBotStateFailureInjector"/>), then
/// delegates normally. Every other member always delegates.
/// </summary>
public sealed class FlakyMessageStore : IMessageStore
{
    private readonly IMessageStore _inner;
    private readonly EnsureBotStateFailureInjector _injector;

    public FlakyMessageStore(IMessageStore inner, EnsureBotStateFailureInjector injector)
    {
        _inner = inner;
        _injector = injector;
    }

    public Task EnsureBotStateAsync(BotIdentity identity, CancellationToken cancellationToken)
    {
        if (_injector.ShouldFail())
        {
            throw new InvalidOperationException("simulated transient EnsureBotState failure");
        }

        return _inner.EnsureBotStateAsync(identity, cancellationToken);
    }

    public Task<long> GetLastUpdateIdAsync(long botId, CancellationToken cancellationToken) =>
        _inner.GetLastUpdateIdAsync(botId, cancellationToken);

    public Task<StoreResult> StoreAsync(long botId, long updateId, IncomingMessage? message, CancellationToken cancellationToken) =>
        _inner.StoreAsync(botId, updateId, message, cancellationToken);

    public Task StoreOutgoingAsync(
        long botId, long chatId, int? topicId, string chatType, int telegramMessageId, string text, CancellationToken cancellationToken) =>
        _inner.StoreOutgoingAsync(botId, chatId, topicId, chatType, telegramMessageId, text, cancellationToken);

    public Task<IReadOnlyList<ContextMessage>> GetRecentContextAsync(
        long botId, long chatId, int? topicId, long? afterMessageId, int maxMessages, CancellationToken cancellationToken) =>
        _inner.GetRecentContextAsync(botId, chatId, topicId, afterMessageId, maxMessages, cancellationToken);
}
