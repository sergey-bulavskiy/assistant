using Assistant.Application.Telegram;

namespace Assistant.Application.Messages;

public enum StoreOutcome
{
    Stored,
    Updated,
    Duplicate,
    OffsetOnly,
    AlreadyProcessed
}

public record StoreResult(StoreOutcome Outcome, long? MessageDbId);

public interface IMessageStore
{
    Task EnsureBotStateAsync(BotIdentity identity, CancellationToken cancellationToken);

    Task<long> GetLastUpdateIdAsync(long botId, CancellationToken cancellationToken);

    Task<StoreResult> StoreAsync(long botId, long updateId, IncomingMessage? message, CancellationToken cancellationToken);
}
