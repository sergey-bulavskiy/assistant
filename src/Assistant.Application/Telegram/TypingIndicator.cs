namespace Assistant.Application.Telegram;

/// <summary>Best-effort "typing" indicator while a model call runs: sent every 4 seconds until the
/// token is cancelled. A failed send never affects the reply and is not logged.</summary>
public static class TypingIndicator
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);

    public static async Task RunAsync(ITelegramClient telegramClient, long chatId, int? topicId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await telegramClient.SendChatActionAsync(chatId, topicId, "typing", cancellationToken);
            }
            catch (Exception)
            {
                // Best-effort: not logged, it would only add noise every 4 s while Telegram is unreachable.
            }

            try
            {
                await Task.Delay(Interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
