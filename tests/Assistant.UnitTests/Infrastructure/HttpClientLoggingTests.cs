using Assistant.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Assistant.UnitTests.Infrastructure;

public class HttpClientLoggingTests
{
    [Fact]
    public void Telegram_named_client_has_no_logging_handlers()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Assistant"] = "Host=localhost;Database=unused",
            })
            .Build();

        services.AddInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        var handler = handlerFactory.CreateHandler("telegram");

        var current = handler;
        while (current is DelegatingHandler delegating)
        {
            delegating.GetType().Name.ShouldNotContain("Logging");
            current = delegating.InnerHandler;
        }
    }
}
