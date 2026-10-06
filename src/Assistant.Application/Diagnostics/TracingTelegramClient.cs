using System.Threading;
using Assistant.Application.Telegram;

namespace Assistant.Application.Diagnostics;

/// <summary>Records only text transport calls made inside an admitted role-bot dispatch.</summary>
public sealed class TracingTelegramClient(ITraceSession trace, ITelegramClient inner) : ITelegramClient
{
    private static readonly AsyncLocal<(int? Index, int? Count, Guid? AttemptId)?> CurrentPart = new();

    public static IDisposable ForAttempt(Guid? attemptId)
    {
        var previous = CurrentPart.Value;
        CurrentPart.Value = (previous?.Index, previous?.Count, attemptId);
        return new PartScope(previous);
    }

    public static IDisposable ForPart(int index, int count)
    {
        var previous = CurrentPart.Value;
        CurrentPart.Value = (index, count, previous?.AttemptId);
        return new PartScope(previous);
    }

    private sealed class PartScope((int? Index, int? Count, Guid? AttemptId)? previous) : IDisposable
    {
        public void Dispose() => CurrentPart.Value = previous;
    }

    public Task<BotIdentity> GetMeAsync(CancellationToken cancellationToken) => inner.GetMeAsync(cancellationToken);
    public Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, IReadOnlyList<UpdateKind> allowedUpdates, CancellationToken cancellationToken) =>
        inner.GetUpdatesAsync(offset, timeoutSeconds, allowedUpdates, cancellationToken);
    public Task SendChatActionAsync(long chatId, int? topicId, string action, CancellationToken cancellationToken) =>
        inner.SendChatActionAsync(chatId, topicId, action, cancellationToken);
    public Task SetReactionAsync(long chatId, int messageId, string? emoji, CancellationToken cancellationToken) =>
        inner.SetReactionAsync(chatId, messageId, emoji, cancellationToken);
    public Task EditMessageButtonsAsync(long chatId, int messageId, IReadOnlyList<InlineButton> buttons, CancellationToken cancellationToken) =>
        inner.EditMessageButtonsAsync(chatId, messageId, buttons, cancellationToken);
    public Task AnswerCallbackAsync(string callbackQueryId, string? text, CancellationToken cancellationToken) =>
        inner.AnswerCallbackAsync(callbackQueryId, text, cancellationToken);
    public Task<string> GetManagedBotTokenAsync(long managedBotUserId, CancellationToken cancellationToken) =>
        inner.GetManagedBotTokenAsync(managedBotUserId, cancellationToken);

    public Task<int> SendTextAsync(long chatId, int? topicId, string text, int? replyToMessageId, CancellationToken cancellationToken) =>
        SendAsync("send_text", text, () => inner.SendTextAsync(chatId, topicId, text, replyToMessageId, cancellationToken));

    public Task<int> SendTextWithButtonsAsync(long chatId, int? topicId, string text, IReadOnlyList<InlineButton> buttons, int? replyToMessageId, CancellationToken cancellationToken) =>
        SendAsync("send_text_with_buttons", text, () => inner.SendTextWithButtonsAsync(chatId, topicId, text, buttons, replyToMessageId, cancellationToken));

    public async Task EditMessageTextAsync(long chatId, int messageId, string text, CancellationToken cancellationToken)
    {
        await RecordAsync("edit_text", "attempted", "normal", text, messageId);
        try
        {
            await inner.EditMessageTextAsync(chatId, messageId, text, cancellationToken);
            await RecordAsync("edit_text", "sent", "normal", text, messageId);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync("edit_text", ex, text, messageId);
            throw;
        }
    }

    private async Task<int> SendAsync(string operation, string text, Func<Task<int>> send)
    {
        await RecordAsync(operation, "attempted", "normal", text, null);
        try
        {
            var messageId = await send();
            await RecordAsync(operation, "sent", "normal", text, messageId);
            return messageId;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(operation, ex, text, null);
            throw;
        }
    }

    private Task RecordFailureAsync(string operation, Exception ex, string text, int? messageId) =>
        ex is OperationCanceledException or TimeoutException
            ? RecordAsync(operation, "unknown", "delivery_timeout", text, messageId)
            : RecordAsync(operation, "failed", "delivery_failure", text, messageId);

    private Task RecordAsync(string operation, string outcome, string reason, string text, int? messageId)
    {
        var part = CurrentPart.Value;
        return TraceSafety.RecordAsync(trace, new TraceEventData(
            "delivery", outcome, reason, AttemptId: part?.AttemptId, Text: text,
            Sent: outcome == "sent" ? true : outcome == "failed" ? false : null,
            PartIndex: part?.Index, PartCount: part?.Count, TelegramMessageId: messageId,
            Operation: operation));
    }
}
