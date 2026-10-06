using System.Globalization;
using Assistant.Application.Telegram;
using Microsoft.Extensions.Logging;

namespace Assistant.Application.Health;

/// <summary>Telegram sends shared by the parts of the health assistant: fixed-text replies, the ✍
/// reaction and clearing it. A send failure returns false and a reaction failure is ignored;
/// both log only the exception type. Cancellation during a send propagates.</summary>
internal sealed class HealthReplies
{
    private readonly ILogger _logger;

    public HealthReplies(ILogger logger)
    {
        _logger = logger;
    }

    // Command replies follow the General assistant: a Telegram reply in groups, a plain message in
    // private chats. Clarifications and the failure notice always quote the message (quote: true),
    // in private chats too. Fixed bot texts, so never stored as conversation. Returns false when the
    // send failed (already logged).
    public Task<bool> ReplyAsync(
        ITelegramClient telegramClient, IncomingMessage message, string text, CancellationToken cancellationToken, bool quote = false) =>
        SendAsync(
            telegramClient, message.ChatId, message.TopicId, text,
            quote || message.ChatType != "private" ? message.MessageId : null, cancellationToken);

    /// <summary>Fixed text to a chat/topic, optionally as a reply. False when a part failed.</summary>
    public async Task<bool> SendAsync(
        ITelegramClient telegramClient, long chatId, int? topicId, string text, int? replyToMessageId, CancellationToken cancellationToken)
    {
        foreach (var part in ReplySplitter.Split(text))
        {
            try
            {
                await telegramClient.SendTextAsync(chatId, topicId, part, replyToMessageId, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("failed to send health reply: {ExceptionType}", ex.GetType().Name);
                return false;
            }
        }
        return true;
    }

    // ✍, or 👍 once if the chat refuses ✍. A failed reaction never undoes the recorded events.
    public async Task MarkRecordedAsync(ITelegramClient telegramClient, long chatId, int messageId, CancellationToken cancellationToken)
    {
        try
        {
            await telegramClient.SetReactionAsync(chatId, messageId, HealthAssistant.RecordedReaction, cancellationToken);
            return;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("recorded reaction refused, trying the fallback: {ExceptionType}", ex.GetType().Name);
        }

        try
        {
            await telegramClient.SetReactionAsync(chatId, messageId, HealthAssistant.FallbackReaction, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("failed to set the recorded reaction: {ExceptionType}", ex.GetType().Name);
        }
    }

    // A message whose every event is gone loses its ✍. Deleting never "un-sends" anything else.
    public async Task ClearReactionsAsync(ITelegramClient telegramClient, IReadOnlyList<MessageRef> messages, CancellationToken cancellationToken)
    {
        foreach (var source in messages)
        {
            try
            {
                await telegramClient.SetReactionAsync(source.ChatId, source.TelegramMessageId, null, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("failed to clear a reaction: {ExceptionType}", ex.GetType().Name);
            }
        }
    }

    /// <summary>Answers a button tap (a short notice, or null for none). A failure is only logged.</summary>
    public async Task AnswerCallbackAsync(ITelegramClient telegramClient, string callbackQueryId, string? text, CancellationToken cancellationToken)
    {
        try
        {
            await telegramClient.AnswerCallbackAsync(callbackQueryId, text, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("failed to answer a callback: {ExceptionType}", ex.GetType().Name);
        }
    }

    public static string DeletedText(DeletedEvents deleted) =>
        "Удалено: " + string.Join("; ", deleted.Events.Select(e => $"#{e.Id.ToString(CultureInfo.InvariantCulture)} {HealthEventText.Describe(e)}")) + ".";
}
