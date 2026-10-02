using Assistant.Application.Telegram;

namespace Assistant.Application.Messages;

public interface IGeneralAssistant
{
    /// <param name="replyToAll">The place row's reply_to_all flag (groups/topics only; UpdateHandler
    /// passes false for private chats). When true, unaddressed ordinary text messages are answered
    /// too, and a gateway refusal for such a message is not posted.</param>
    Task HandleAsync(
        ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken, bool replyToAll = false);
}
