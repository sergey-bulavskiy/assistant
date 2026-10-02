using Assistant.Application.Messages;
using Assistant.Application.Telegram;

namespace Assistant.IntegrationTests.Host;

/// <summary>
/// Decorates an <see cref="IMessageStore"/> to simulate a permanently poisonous update (see
/// <see cref="PoisonUpdateInjector"/>). Every other member always delegates.
/// </summary>
public sealed class PoisonMessageStore : IMessageStore
{
    private readonly IMessageStore _inner;
    private readonly PoisonUpdateInjector _injector;

    public PoisonMessageStore(IMessageStore inner, PoisonUpdateInjector injector)
    {
        _inner = inner;
        _injector = injector;
    }

    public Task EnsureBotStateAsync(BotIdentity identity, CancellationToken cancellationToken) =>
        _inner.EnsureBotStateAsync(identity, cancellationToken);

    public Task<long> GetLastUpdateIdAsync(long botId, CancellationToken cancellationToken) =>
        _inner.GetLastUpdateIdAsync(botId, cancellationToken);

    public Task<bool> RebaseOffsetIfIdleAsync(long botId, DateTimeOffset idleBefore, CancellationToken cancellationToken) =>
        _inner.RebaseOffsetIfIdleAsync(botId, idleBefore, cancellationToken);

    public Task<StoreResult> StoreAsync(long botId, long updateId, IncomingMessage? message, CancellationToken cancellationToken)
    {
        if (_injector.ShouldFail(updateId, message is not null))
        {
            throw new InvalidOperationException("simulated poison update failure");
        }

        return _inner.StoreAsync(botId, updateId, message, cancellationToken);
    }

    public Task StoreOutgoingAsync(
        long botId, long chatId, int? topicId, string chatType, int telegramMessageId, string text, CancellationToken cancellationToken) =>
        _inner.StoreOutgoingAsync(botId, chatId, topicId, chatType, telegramMessageId, text, cancellationToken);

    public Task<IReadOnlyList<ContextMessage>> GetRecentContextAsync(
        long botId, long chatId, int? topicId, long? afterMessageId, long? beforeMessageId, int maxMessages, CancellationToken cancellationToken) =>
        _inner.GetRecentContextAsync(botId, chatId, topicId, afterMessageId, beforeMessageId, maxMessages, cancellationToken);
}
