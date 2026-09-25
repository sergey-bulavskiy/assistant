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

    public Task<StoreResult> StoreAsync(long botId, long updateId, IncomingMessage? message, CancellationToken cancellationToken)
    {
        if (_injector.ShouldFail(updateId, message is not null))
        {
            throw new InvalidOperationException("simulated poison update failure");
        }

        return _inner.StoreAsync(botId, updateId, message, cancellationToken);
    }
}
