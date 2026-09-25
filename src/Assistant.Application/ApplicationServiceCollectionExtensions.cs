using Assistant.Application.Common;
using Assistant.Application.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<UpdateHandler>();
        return services;
    }
}
