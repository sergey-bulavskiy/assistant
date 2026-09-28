using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Infrastructure.Common;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace Assistant.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AssistantDbContext>((_, options) =>
        {
            var connectionString = configuration.GetConnectionString("Assistant")
                ?? throw new InvalidOperationException("Connection string 'ConnectionStrings:Assistant' is not configured.");
            AssistantDbContext.Configure(options, connectionString);
        });

        services.AddScoped<IMessageStore, MessageStore>();
        services.AddScoped<ICurrentFamily, CurrentFamily>();

        services.AddSingleton<ITokenEncryptor>(sp =>
            new TokenEncryptor(sp.GetRequiredService<IOptions<BotOptions>>().Value.TokenEncryptionKey));

        // Request URLs for every Telegram Bot API call embed the bot token
        // (https://api.telegram.org/bot<token>/method). The default HttpClient logging handlers log
        // request URIs at Information level, which would leak the token into application logs —
        // RemoveAllLoggers() strips those handlers from this named client only.
        services.AddHttpClient("telegram").RemoveAllLoggers();

        services.AddSingleton<ITelegramClientFactory, TelegramClientFactory>();

        return services;
    }
}
