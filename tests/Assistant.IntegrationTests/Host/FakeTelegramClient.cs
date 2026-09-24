using Assistant.Application.Telegram;

namespace Assistant.IntegrationTests.Host;

public class FakeTelegramClient : ITelegramClient
{
    private readonly object _lock = new();
    private readonly List<IncomingUpdate> _updates = new();
    private readonly List<(long ChatId, int? TopicId, string Text)> _sentMessages = new();
    private int _getMeFailuresRemaining;
    private readonly BotIdentity _identity = new(999, "test_bot");

    /// <summary>
    /// One-shot switch simulating Telegram redelivering updates the offset has already moved past
    /// (a rare but real Telegram Bot API behavior). When set, the *next* <see cref="GetUpdatesAsync"/>
    /// call returns every enqueued update regardless of <c>offset</c>, then automatically reverts to
    /// normal offset filtering — it does not cause an unbounded resend loop.
    /// </summary>
    public bool IgnoreOffset { get; set; }

    public IReadOnlyList<(long ChatId, int? TopicId, string Text)> SentMessages
    {
        get
        {
            lock (_lock)
            {
                return _sentMessages.ToArray();
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

    public Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IncomingUpdate[] result;
            if (IgnoreOffset)
            {
                result = _updates.OrderBy(u => u.UpdateId).ToArray();
                IgnoreOffset = false;
            }
            else
            {
                result = _updates.Where(u => u.UpdateId >= offset).OrderBy(u => u.UpdateId).ToArray();
            }

            return Task.FromResult<IReadOnlyList<IncomingUpdate>>(result);
        }
    }

    public Task SendTextAsync(long chatId, int? topicId, string text, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _sentMessages.Add((chatId, topicId, text));
        }

        return Task.CompletedTask;
    }
}
