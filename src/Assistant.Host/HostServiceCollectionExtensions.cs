using Assistant.Application.Common;
using Microsoft.Extensions.Options;

namespace Assistant.Host;

public static class HostServiceCollectionExtensions
{
    public static IServiceCollection AddAssistantHost(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BotOptions>()
            .Configure<IConfiguration>((options, config) =>
            {
                options.ManagerToken = config["TELEGRAM_MANAGER_BOT_TOKEN"] ?? string.Empty;
                options.TokenEncryptionKey = config["TOKEN_ENCRYPTION_KEY"] ?? string.Empty;
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<BotOptions>, BotOptionsValidator>();

        services.AddSingleton(sp => BuildInfo.FromEnvironment(sp.GetRequiredService<IClock>()));

        return services;
    }
}
