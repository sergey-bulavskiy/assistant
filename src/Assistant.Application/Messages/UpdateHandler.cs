using Assistant.Application.Common;
using Assistant.Application.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Assistant.Application.Messages;

public class UpdateHandler
{
    private readonly IMessageStore _store;
    private readonly ITelegramClient _telegramClient;
    private readonly IOptions<BotOptions> _options;
    private readonly BuildInfo _buildInfo;
    private readonly IClock _clock;
    private readonly ILogger<UpdateHandler> _logger;

    public UpdateHandler(
        IMessageStore store,
        ITelegramClient telegramClient,
        IOptions<BotOptions> options,
        BuildInfo buildInfo,
        IClock clock,
        ILogger<UpdateHandler> logger)
    {
        _store = store;
        _telegramClient = telegramClient;
        _options = options;
        _buildInfo = buildInfo;
        _clock = clock;
        _logger = logger;
    }

    public async Task HandleAsync(long botId, string botUsername, IncomingUpdate update, CancellationToken cancellationToken)
    {
        var message = update.Message;

        if (message is not null && !(message.UserId.HasValue && _options.Value.AllowedUserIds.Contains(message.UserId.Value)))
        {
            _logger.LogInformation("ignored update from non-allowed user {UserId}", message.UserId);
            await _store.StoreAsync(botId, update.UpdateId, null, cancellationToken);
            return;
        }

        var result = await _store.StoreAsync(botId, update.UpdateId, message, cancellationToken);
        _logger.LogInformation("update {UpdateId} processed with outcome {Outcome}", update.UpdateId, result.Outcome);

        var reply = ReplyPolicy.Decide(message, result, botUsername, () => VersionText.Format(_buildInfo, _clock.UtcNow));
        if (reply is null || message is null)
        {
            return;
        }

        try
        {
            await _telegramClient.SendTextAsync(message.ChatId, message.TopicId, reply, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("failed to send reply: {ExceptionType}", ex.GetType().Name);
        }
    }
}
