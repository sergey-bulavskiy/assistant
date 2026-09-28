using Assistant.Application.Telegram;

namespace Assistant.Infrastructure.Telegram;

public interface ITelegramClientFactory
{
    /// <summary>Call once per bot and hold/reuse the result for that bot's lifetime; do not call
    /// this per poll-loop iteration, as created clients are not cached or pooled.</summary>
    ITelegramClient Create(string token);
}
