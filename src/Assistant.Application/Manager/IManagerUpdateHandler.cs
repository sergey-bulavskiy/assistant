using Assistant.Application.Messages;
using Assistant.Application.Telegram;

namespace Assistant.Application.Manager;

/// <summary>Handles every update received by the manager bot (commands, callback-query button
/// taps, managed_bot events): /claim, /newbot, /settings and the approval/settings buttons.
/// UpdateHandler routes every manager-bot update here.</summary>
public interface IManagerUpdateHandler
{
    Task HandleAsync(ReceivingBot managerBot, ITelegramClient telegramClient, IncomingUpdate update, CancellationToken cancellationToken);
}
