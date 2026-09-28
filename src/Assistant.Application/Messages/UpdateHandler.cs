using Assistant.Application.Common;
using Assistant.Application.Telegram;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Messages;

public class UpdateHandler
{
    private readonly IMessageStore _store;
    private readonly BuildInfo _buildInfo;
    private readonly IClock _clock;
    private readonly ILogger<UpdateHandler> _logger;

    public UpdateHandler(
        IMessageStore store,
        BuildInfo buildInfo,
        IClock clock,
        ILogger<UpdateHandler> logger)
    {
        _store = store;
        _buildInfo = buildInfo;
        _clock = clock;
        _logger = logger;
    }

    public async Task HandleAsync(ReceivingBot bot, ITelegramClient telegramClient, IncomingUpdate update, CancellationToken cancellationToken)
    {
        var message = update.Message;
        var botId = bot.TelegramBotId;
        var botUsername = bot.Username;

        var result = await _store.StoreAsync(botId, update.UpdateId, message, cancellationToken);
        _logger.LogInformation("update {UpdateId} processed with outcome {Outcome}", update.UpdateId, result.Outcome);

        var reply = ReplyPolicy.Decide(message, result, botUsername, () => VersionText.Format(_buildInfo, _clock.UtcNow));
        if (reply is null || message is null)
        {
            return;
        }

        try
        {
            await telegramClient.SendTextAsync(message.ChatId, message.TopicId, reply, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("failed to send reply: {ExceptionType}", ex.GetType().Name);
        }
    }
}
