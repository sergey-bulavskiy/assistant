using Assistant.Application.Telegram;

namespace Assistant.Application.Messages;

public interface IGeneralAssistant
{
    Task HandleAsync(ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken);
}
