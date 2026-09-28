using Assistant.Application.Telegram;

namespace Assistant.UnitTests.Fakes;

public class FakeTelegramClient : ITelegramClient
{
    public List<(long ChatId, int? TopicId, string Text)> Sent { get; } = new();

    public List<(long ChatId, int MessageId, IReadOnlyList<InlineButton> Buttons)> ButtonEdits { get; } = new();

    public List<(string CallbackQueryId, string? Text)> AnsweredCallbacks { get; } = new();

    public bool ThrowOnSend { get; set; }

    private int _nextSentMessageId = 1;

    public Task<BotIdentity> GetMeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new BotIdentity(999, "test_bot"));

    public Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(
        long offset, int timeoutSeconds, IReadOnlyList<UpdateKind> allowedUpdates, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IncomingUpdate>>(Array.Empty<IncomingUpdate>());

    public Task SendTextAsync(long chatId, int? topicId, string text, CancellationToken cancellationToken)
    {
        if (ThrowOnSend)
        {
            throw new InvalidOperationException("simulated send failure");
        }

        Sent.Add((chatId, topicId, text));
        return Task.CompletedTask;
    }

    public Task<int> SendTextWithButtonsAsync(
        long chatId, int? topicId, string text, IReadOnlyList<InlineButton> buttons, CancellationToken cancellationToken)
    {
        Sent.Add((chatId, topicId, text));
        return Task.FromResult(_nextSentMessageId++);
    }

    public Task EditMessageButtonsAsync(long chatId, int messageId, IReadOnlyList<InlineButton> buttons, CancellationToken cancellationToken)
    {
        ButtonEdits.Add((chatId, messageId, buttons));
        return Task.CompletedTask;
    }

    public Task EditMessageTextAsync(long chatId, int messageId, string text, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AnswerCallbackAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
    {
        AnsweredCallbacks.Add((callbackQueryId, text));
        return Task.CompletedTask;
    }

    public Task<string> GetManagedBotTokenAsync(long managedBotUserId, CancellationToken cancellationToken) =>
        Task.FromResult("test-managed-bot-token");
}
