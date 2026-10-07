using Assistant.Application.Telegram;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Vet;

internal sealed class VetReplies(ILogger logger)
{
    public async Task<IReadOnlyList<(int MessageId, string Text)>> SendAsync(ITelegramClient client,
        IncomingMessage message, string text, CancellationToken ct)
    {
        var sent = new List<(int, string)>();
        foreach (var part in ReplySplitter.Split(text))
        {
            try
            {
                var id = await client.SendTextAsync(message.ChatId, message.TopicId, part,
                    message.ChatType == "private" ? null : message.MessageId, ct);
                sent.Add((id, part));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Vet reply failed: {ExceptionType}", ex.GetType().Name);
                break;
            }
        }
        return sent;
    }

    public async Task<int?> ReviewAsync(ITelegramClient client, IncomingMessage message,
        long id, int revision, string text, bool canAccept, CancellationToken ct)
    {
        try
        {
            var buttons = canAccept
                ? new[] { new InlineButton("Сохранить", $"v:a:{id}:{revision}"), new InlineButton("Отменить", $"v:d:{id}:{revision}") }
                : new[] { new InlineButton("Отменить", $"v:d:{id}:{revision}") };
            return await client.SendTextWithButtonsAsync(message.ChatId, message.TopicId, text,
                buttons, message.MessageId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Vet review failed: {ExceptionType}", ex.GetType().Name);
            return null;
        }
    }

    public async Task CallbackAsync(ITelegramClient client, string id, string? text, CancellationToken ct)
    {
        try { await client.AnswerCallbackAsync(id, text, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { logger.LogWarning("Vet callback failed: {ExceptionType}", ex.GetType().Name); }
    }
}
