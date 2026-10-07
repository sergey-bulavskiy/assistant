using Assistant.Application.Telegram;

namespace Assistant.UnitTests.Fakes;

public class FakeTelegramClient : ITelegramClient
{
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<string> DownloadedFiles { get; } = new();

    public async Task<long> DownloadFileAsync(string fileId, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DownloadedFiles.Enqueue(fileId);
        if (!Files.TryGetValue(fileId, out var bytes))
            throw new TelegramFileDownloadException(TelegramFileDownloadFailure.Unavailable);
        if (bytes.LongLength > maxBytes)
            throw new TelegramFileDownloadException(TelegramFileDownloadFailure.TooLarge);
        await destination.WriteAsync(bytes, cancellationToken);
        return bytes.LongLength;
    }

    public List<(long ChatId, int? TopicId, string Text, int? ReplyToMessageId)> Sent { get; } = new();

    public List<(long ChatId, int MessageId, IReadOnlyList<InlineButton> Buttons)> ButtonEdits { get; } = new();

    /// <summary>Every SendTextWithButtonsAsync call with the id it returned (also listed in Sent).</summary>
    public List<(long ChatId, int? TopicId, string Text, IReadOnlyList<InlineButton> Buttons, int? ReplyToMessageId, int MessageId)> ButtonMessages { get; } = new();

    public List<(long ChatId, int MessageId, string Text)> TextEdits { get; } = new();

    public List<(long ChatId, int? TopicId, string Action)> ChatActionsSent { get; } = new();

    public List<(string CallbackQueryId, string? Text)> AnsweredCallbacks { get; } = new();

    public List<(long ChatId, int MessageId, string? Emoji)> Reactions { get; } = new();

    /// <summary>The first N SetReactionAsync calls throw (and are not recorded); each throw
    /// decrements it.</summary>
    public int FailReactionTimes { get; set; }

    public bool ThrowOnSend { get; set; }

    public Exception? SendFailure { get; set; }

    public bool ThrowOnChatAction { get; set; }

    /// <summary>When set, SendTextAsync throws for this 1-based call number only (earlier and later
    /// sends succeed).</summary>
    public int? ThrowOnSendNumber { get; set; }

    private int _sendCalls;

    private int _nextSentMessageId = 1;

    public Task<BotIdentity> GetMeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new BotIdentity(999, "test_bot"));

    public Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(
        long offset, int timeoutSeconds, IReadOnlyList<UpdateKind> allowedUpdates, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IncomingUpdate>>(Array.Empty<IncomingUpdate>());

    public Task<int> SendTextAsync(long chatId, int? topicId, string text, int? replyToMessageId, CancellationToken cancellationToken)
    {
        _sendCalls++;
        if (ThrowOnSend || _sendCalls == ThrowOnSendNumber)
        {
            throw SendFailure ?? new InvalidOperationException("simulated send failure");
        }

        Sent.Add((chatId, topicId, text, replyToMessageId));
        return Task.FromResult(_nextSentMessageId++);
    }

    public Task SendChatActionAsync(long chatId, int? topicId, string action, CancellationToken cancellationToken)
    {
        ChatActionsSent.Add((chatId, topicId, action));
        if (ThrowOnChatAction)
        {
            throw new InvalidOperationException("simulated chat action failure");
        }

        return Task.CompletedTask;
    }

    public Task SetReactionAsync(long chatId, int messageId, string? emoji, CancellationToken cancellationToken)
    {
        if (FailReactionTimes > 0)
        {
            FailReactionTimes--;
            throw new InvalidOperationException("simulated reaction failure");
        }

        Reactions.Add((chatId, messageId, emoji));
        return Task.CompletedTask;
    }

    public Task<int> SendTextWithButtonsAsync(
        long chatId, int? topicId, string text, IReadOnlyList<InlineButton> buttons, int? replyToMessageId, CancellationToken cancellationToken)
    {
        _sendCalls++;
        if (ThrowOnSend || _sendCalls == ThrowOnSendNumber)
        {
            throw SendFailure ?? new InvalidOperationException("simulated send failure");
        }

        var messageId = _nextSentMessageId++;
        Sent.Add((chatId, topicId, text, replyToMessageId));
        ButtonMessages.Add((chatId, topicId, text, buttons, replyToMessageId, messageId));
        return Task.FromResult(messageId);
    }

    public Task EditMessageButtonsAsync(long chatId, int messageId, IReadOnlyList<InlineButton> buttons, CancellationToken cancellationToken)
    {
        ButtonEdits.Add((chatId, messageId, buttons));
        return Task.CompletedTask;
    }

    public Task EditMessageTextAsync(long chatId, int messageId, string text, CancellationToken cancellationToken)
    {
        TextEdits.Add((chatId, messageId, text));
        return Task.CompletedTask;
    }

    public Task AnswerCallbackAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
    {
        AnsweredCallbacks.Add((callbackQueryId, text));
        return Task.CompletedTask;
    }

    public Task<string> GetManagedBotTokenAsync(long managedBotUserId, CancellationToken cancellationToken) =>
        Task.FromResult("test-managed-bot-token");
}
