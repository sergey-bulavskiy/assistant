using Assistant.Application.Messages;
using Assistant.Application.Health.Documents;
using Assistant.Application.Telegram;

namespace Assistant.Application.Health;

public interface IHealthAssistant
{
    // Typed pre-offset/recovery siblings; defaults preserve implementations without document support.
    Task<HealthDocumentAdmissionInfo?> AdmitDocumentAsync(ReceivingBot bot, IncomingMessage message, long updateId, CancellationToken token)
        => Task.FromResult<HealthDocumentAdmissionInfo?>(null);
    Task ResumeDocumentsAsync(ReceivingBot bot, ITelegramClient client, CancellationToken token) => Task.CompletedTask;

    Task HandleAsync(ReceivingBot bot, ITelegramClient telegramClient, IncomingMessage message, StoreResult storeResult, CancellationToken cancellationToken, bool replyToAll = false);

    /// <summary>A tap on one of this bot's buttons. The caller (UpdateHandler) has already checked
    /// that the tapping user is an approved member of the bot's family and that the place is approved.
    /// Always answers the callback.</summary>
    Task HandleCallbackAsync(ReceivingBot bot, ITelegramClient telegramClient, CallbackQueryInfo callback, CancellationToken cancellationToken);
}
