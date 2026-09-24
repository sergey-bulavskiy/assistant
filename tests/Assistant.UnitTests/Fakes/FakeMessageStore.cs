using Assistant.Application.Messages;
using Assistant.Application.Telegram;

namespace Assistant.UnitTests.Fakes;

public class FakeMessageStore : IMessageStore
{
    public List<(long BotId, long UpdateId, IncomingMessage? Message)> Calls { get; } = new();

    private StoreResult _nextResult = new(StoreOutcome.Stored, 1);

    public void SetNextResult(StoreResult result) => _nextResult = result;

    public Task EnsureBotStateAsync(BotIdentity identity, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<long> GetLastUpdateIdAsync(long botId, CancellationToken cancellationToken) => Task.FromResult(0L);

    public Task<StoreResult> StoreAsync(long botId, long updateId, IncomingMessage? message, CancellationToken cancellationToken)
    {
        Calls.Add((botId, updateId, message));
        return Task.FromResult(_nextResult);
    }
}
