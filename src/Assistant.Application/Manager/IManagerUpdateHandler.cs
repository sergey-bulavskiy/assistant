using Assistant.Application.Messages;
using Assistant.Application.Telegram;

namespace Assistant.Application.Manager;

/// <summary>Handles every update received by the manager bot (commands, callback-query button
/// taps, managed_bot events). Built incrementally: this task wires the call from UpdateHandler;
/// /claim, /newbot, callback resolution and /settings are added in later tasks.</summary>
public interface IManagerUpdateHandler
{
    Task HandleAsync(ReceivingBot managerBot, ITelegramClient telegramClient, IncomingUpdate update, CancellationToken cancellationToken);
}
