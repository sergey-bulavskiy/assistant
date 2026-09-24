using Assistant.Application.Telegram;
using Assistant.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Assistant.IntegrationTests.Host;

public class AssistantWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public FakeTelegramClient TelegramClient { get; } = new();

    public AssistantWebApplicationFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // R2: tests must not paper over DI lifetime mistakes (e.g. a singleton accidentally
        // capturing a scoped service) — run with scope validation on regardless of hosting
        // environment defaults.
        builder.UseEnvironment("Development");
        builder.UseDefaultServiceProvider((_, options) =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Assistant"] = _connectionString,
                ["TELEGRAM_BOT_TOKEN"] = "test-token",
                ["ALLOWED_USER_IDS"] = "111,222"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITelegramClient>();
            services.AddSingleton<ITelegramClient>(TelegramClient);

            services.RemoveAll<PollingSettings>();
            services.AddSingleton(new PollingSettings(
                MinBackoff: TimeSpan.FromMilliseconds(20),
                MaxBackoff: TimeSpan.FromMilliseconds(200),
                LongPollTimeoutSeconds: 1));
        });
    }
}
