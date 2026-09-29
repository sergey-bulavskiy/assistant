using Assistant.Application.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;

namespace Assistant.Infrastructure.Telegram;

// One ITelegramBotClient (and therefore one TelegramClientAdapter) per bot: each active bots row
// polls with its own token, so a single shared singleton client (M1's approach) no longer fits.
// The "telegram" named HttpClient (RemoveAllLoggers'd in InfrastructureServiceCollectionExtensions
// so request URLs — which embed the token — are never logged) is still reused for every instance.
public class TelegramClientFactory : ITelegramClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;

    public TelegramClientFactory(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public ITelegramClient Create(string token)
    {
        var httpClient = _httpClientFactory.CreateClient("telegram");
        var client = new TelegramBotClient(new TelegramBotClientOptions(token), httpClient);
        return new TelegramClientAdapter(client);
    }
}
