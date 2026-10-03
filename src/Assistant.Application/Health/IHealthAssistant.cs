using Assistant.Application.Messages;
using Assistant.Application.Telegram;

namespace Assistant.Application.Health;

public interface IHealthAssistant
{
    Task HandleAsync(ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken);

    /// <summary>A tap on one of this bot's buttons. The caller (UpdateHandler) has already checked
    /// that the tapping user is an approved member of the bot's family and that the place is approved.
    /// Always answers the callback.</summary>
    Task HandleCallbackAsync(ReceivingBot bot, ITelegramClient telegramClient, CallbackQueryInfo callback, CancellationToken cancellationToken);
}
