using Assistant.Application.Telegram;

namespace Assistant.IntegrationTests.Host;

public class FakeTelegramClient : ITelegramClient
{
    private readonly object _lock = new();
    private readonly List<IncomingUpdate> _updates = new();
    private readonly List<(long ChatId, int? TopicId, string Text, int? ReplyToMessageId)> _sentMessages = new();
    private readonly List<(string Text, IReadOnlyList<InlineButton> Buttons)> _sentButtons = new();
    private readonly List<(string CallbackQueryId, string? Text)> _answeredCallbacks = new();
    private readonly List<(long ChatId, int? TopicId, string Action)> _chatActionsSent = new();
    private int _getMeFailuresRemaining;
    private int _nextSentMessageId = 1;
    private readonly BotIdentity _identity = new(999, "test_bot");

    public string? ManagedBotTokenToReturn { get; set; } = "test-managed-bot-token";

    /// <summary>
    /// One-shot switch simulating Telegram redelivering updates the offset has already moved past
    /// (a rare but real Telegram Bot API behavior). When set, the *next* <see cref="GetUpdatesAsync"/>
    /// call returns every enqueued update regardless of <c>offset</c>, then automatically reverts to
    /// normal offset filtering — it does not cause an unbounded resend loop.
    /// </summary>
    public bool IgnoreOffset { get; set; }

    public IReadOnlyList<(long ChatId, int? TopicId, string Text, int? ReplyToMessageId)> SentMessages
    {
        get
        {
            lock (_lock)
            {
                return _sentMessages.ToArray();
            }
        }
    }

    /// <summary>Buttons of every message sent with <see cref="SendTextWithButtonsAsync"/>, by text.</summary>
    public IReadOnlyList<(string Text, IReadOnlyList<InlineButton> Buttons)> SentButtons
    {
        get
        {
            lock (_lock)
            {
                return _sentButtons.ToArray();
            }
        }
    }

    public IReadOnlyList<(string CallbackQueryId, string? Text)> AnsweredCallbacks
    {
        get
        {
            lock (_lock)
            {
                return _answeredCallbacks.ToArray();
            }
        }
    }

    public IReadOnlyList<(long ChatId, int? TopicId, string Action)> ChatActionsSent
    {
        get
        {
            lock (_lock)
            {
                return _chatActionsSent.ToArray();
            }
        }
    }

    public void FailGetMeTimes(int times)
    {
        lock (_lock)
        {
            _getMeFailuresRemaining = times;
        }
    }

    public void EnqueueUpdate(IncomingUpdate update)
    {
        lock (_lock)
        {
            _updates.Add(update);
        }
    }

    public Task<BotIdentity> GetMeAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_getMeFailuresRemaining > 0)
            {
                _getMeFailuresRemaining--;
                throw new InvalidOperationException("simulated getMe failure");
            }

            return Task.FromResult(_identity);
        }
    }

    public async Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(
        long offset, int timeoutSeconds, IReadOnlyList<UpdateKind> allowedUpdates, CancellationToken cancellationToken)
    {
        IncomingUpdate[] result;
        lock (_lock)
        {
            if (IgnoreOffset)
            {
                result = _updates.OrderBy(u => u.UpdateId).ToArray();
                IgnoreOffset = false;
            }
            else
            {
                result = _updates.Where(u => u.UpdateId >= offset).OrderBy(u => u.UpdateId).ToArray();
            }
        }

        if (result.Length == 0)
        {
            // The real Telegram long-poll blocks until an update arrives or the poll times out.
            // Returning instantly here made PollingService spin at full speed with nothing to do,
            // hammering Postgres for the lifetime of every test host — this delay stands in for
            // that blocking wait. Cancellation propagates normally (no try/catch): the caller's
            // own cancellation handling deals with it.
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return result;
    }

    public Task<int> SendTextAsync(long chatId, int? topicId, string text, int? replyToMessageId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _sentMessages.Add((chatId, topicId, text, replyToMessageId));
            return Task.FromResult(_nextSentMessageId++);
        }
    }

    public Task SendChatActionAsync(long chatId, int? topicId, string action, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _chatActionsSent.Add((chatId, topicId, action));
        }

        return Task.CompletedTask;
    }

    public Task<int> SendTextWithButtonsAsync(
        long chatId, int? topicId, string text, IReadOnlyList<InlineButton> buttons, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _sentMessages.Add((chatId, topicId, text, null));
            _sentButtons.Add((text, buttons));
            return Task.FromResult(_nextSentMessageId++);
        }
    }

    public Task EditMessageButtonsAsync(long chatId, int messageId, IReadOnlyList<InlineButton> buttons, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task EditMessageTextAsync(long chatId, int messageId, string text, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AnswerCallbackAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _answeredCallbacks.Add((callbackQueryId, text));
        }

        return Task.CompletedTask;
    }

    public Task<string> GetManagedBotTokenAsync(long managedBotUserId, CancellationToken cancellationToken) =>
        Task.FromResult(ManagedBotTokenToReturn ?? throw new InvalidOperationException("no managed bot token configured"));
}
