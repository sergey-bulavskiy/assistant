using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Microsoft.Extensions.Logging;

namespace Assistant.Infrastructure.Manager;

public class ManagerUpdateHandler : IManagerUpdateHandler
{
    private readonly ILogger<ManagerUpdateHandler> _logger;

    public ManagerUpdateHandler(ILogger<ManagerUpdateHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(ReceivingBot managerBot, ITelegramClient telegramClient, IncomingUpdate update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery is { } callback)
        {
            // A later task adds real resolution (place/user approvals, settings actions).
            await telegramClient.AnswerCallbackAsync(callback.CallbackQueryId, "Пока не реализовано", cancellationToken);
            return;
        }

        if (update.Message is { Kind: not Assistant.Domain.Messages.MessageKind.Service } message && message.Text is not null)
        {
            // Later tasks add /claim, /newbot, /settings.
            _logger.LogInformation("manager received an unhandled command");
            await telegramClient.SendTextAsync(message.ChatId, message.TopicId, "Неизвестная команда.", cancellationToken);
        }
    }
}
