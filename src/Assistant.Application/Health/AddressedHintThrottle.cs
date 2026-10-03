namespace Assistant.Application.Health;

/// <summary>At most one "what to send me" hint per chat/topic of a bot per <see cref="Interval"/>.
/// Its own instance, separate from the failure notice throttle. In memory (singleton): a restart
/// forgets it, which at worst sends one extra hint.</summary>
public sealed class AddressedHintThrottle
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly Dictionary<(long BotId, long ChatId, int? TopicId), DateTimeOffset> _lastSent = new();

    /// <summary>Number of places currently remembered (diagnostics and tests).</summary>
    public int TrackedPlaces
    {
        get
        {
            lock (_lock)
            {
                return _lastSent.Count;
            }
        }
    }

    /// <summary>True, and remembers <paramref name="now"/>, when no hint went to this place in the
    /// last Interval. <paramref name="telegramBotId"/> is the Telegram bot id.</summary>
    public bool TryAcquire(long telegramBotId, long chatId, int? topicId, DateTimeOffset now)
    {
        var key = (telegramBotId, chatId, topicId);
        lock (_lock)
        {
            // Drop expired keys so the dictionary cannot grow without bound.
            foreach (var expired in _lastSent.Where(p => now - p.Value >= Interval).Select(p => p.Key).ToList())
            {
                _lastSent.Remove(expired);
            }

            if (_lastSent.TryGetValue(key, out var last) && now - last < Interval)
            {
                return false;
            }

            _lastSent[key] = now;
            return true;
        }
    }
}
