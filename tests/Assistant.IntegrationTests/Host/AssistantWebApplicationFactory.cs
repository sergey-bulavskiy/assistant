using Assistant.Application.Common;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Host;
using Assistant.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Assistant.IntegrationTests.Host;

public class AssistantWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly PollingSettings _pollingSettings;

    public FakeTelegramClient TelegramClient { get; } = new();

    public EnsureBotStateFailureInjector EnsureBotStateFailures { get; } = new();

    public PoisonUpdateInjector PoisonUpdate { get; } = new();

    public TestClock Clock { get; } = new();

    public AssistantWebApplicationFactory(string connectionString, PollingSettings? pollingSettings = null)
    {
        _connectionString = connectionString;
        _pollingSettings = pollingSettings ?? new PollingSettings(
            MinBackoff: TimeSpan.FromMilliseconds(20),
            MaxBackoff: TimeSpan.FromMilliseconds(200),
            LongPollTimeoutSeconds: 1,
            PoisonUpdateFailureCap: 2);
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
                ["ALLOWED_USER_IDS"] = "111,222",
                ["Database:MigrationMaxAttempts"] = "3",
                ["Database:MigrationRetryDelaySeconds"] = "0.2"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITelegramClient>();
            services.AddSingleton<ITelegramClient>(TelegramClient);

            services.RemoveAll<PollingSettings>();
            services.AddSingleton(_pollingSettings);

            // I2: a controllable clock lets PollingHealth tests push "now" past the healthy
            // window deterministically instead of waiting it out for real (see TestClock).
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);

            // Decorate the real IMessageStore so tests can inject a transient failure into
            // EnsureBotStateAsync (see EnsureBotStateFailureInjector / FlakyMessageStore) and a
            // permanently poisonous update (see PoisonUpdateInjector / PoisonMessageStore) — every
            // other member always delegates straight through to the real store.
            services.RemoveAll<IMessageStore>();
            services.AddScoped<IMessageStore>(sp =>
            {
                var db = sp.GetRequiredService<AssistantDbContext>();
                var clock = sp.GetRequiredService<IClock>();
                var inner = new MessageStore(db, clock);
                var flaky = new FlakyMessageStore(inner, EnsureBotStateFailures);
                return new PoisonMessageStore(flaky, PoisonUpdate);
            });
        });
    }
}
