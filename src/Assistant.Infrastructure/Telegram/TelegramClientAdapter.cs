using Assistant.Application.Telegram;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Assistant.Infrastructure.Telegram;

public class TelegramClientAdapter : ITelegramClient
{
    private readonly ITelegramBotClient _client;

    public TelegramClientAdapter(ITelegramBotClient client)
    {
        _client = client;
    }

    public async Task<BotIdentity> GetMeAsync(CancellationToken cancellationToken)
    {
        var me = await _client.GetMe(cancellationToken);
        return new BotIdentity(me.Id, me.Username ?? string.Empty);
    }

    public async Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var updates = await _client.GetUpdates(
            offset: checked((int)offset),
            limit: 100,
            timeout: timeoutSeconds,
            allowedUpdates: new[] { UpdateType.Message, UpdateType.EditedMessage },
            cancellationToken: cancellationToken);

        return updates.Select(TelegramUpdateMapper.Map).ToArray();
    }

    public Task SendTextAsync(long chatId, int? topicId, string text, CancellationToken cancellationToken) =>
        _client.SendMessage(chatId: chatId, text: text, messageThreadId: topicId, cancellationToken: cancellationToken);
}
