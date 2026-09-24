using Assistant.Application.Messages;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        return services;
    }
}
