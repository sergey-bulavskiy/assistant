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
                options.Token = config["TELEGRAM_BOT_TOKEN"] ?? string.Empty;
                options.AllowedUserIdsRaw = config["ALLOWED_USER_IDS"] ?? string.Empty;
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<BotOptions>, BotOptionsValidator>();

        services.AddSingleton(sp => BuildInfo.FromEnvironment(sp.GetRequiredService<IClock>()));
        services.AddSingleton<PollingHealth>();
        services.AddSingleton(PollingSettings.Default);
        services.AddHostedService<PollingService>();

        return services;
    }
}
