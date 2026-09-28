using Assistant.Application.Telegram;

namespace Assistant.Infrastructure.Telegram;

public interface ITelegramClientFactory
{
    ITelegramClient Create(string token);
}
