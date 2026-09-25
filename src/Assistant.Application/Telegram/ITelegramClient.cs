namespace Assistant.Application.Telegram;

public interface ITelegramClient
{
    Task<BotIdentity> GetMeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken cancellationToken);

    Task SendTextAsync(long chatId, int? topicId, string text, CancellationToken cancellationToken);
}
