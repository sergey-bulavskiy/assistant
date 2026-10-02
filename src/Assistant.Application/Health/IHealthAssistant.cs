using Assistant.Application.Messages;
using Assistant.Application.Telegram;

namespace Assistant.Application.Health;

public interface IHealthAssistant
{
    Task HandleAsync(ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken);
}
