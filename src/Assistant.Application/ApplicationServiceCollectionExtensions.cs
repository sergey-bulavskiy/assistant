using Assistant.Application.Common;
using Assistant.Application.Health;
using Assistant.Application.Health.Documents;
using Assistant.Application.Messages;
using Assistant.Application.Vet;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<UpdateHandler>();
        services.AddScoped<IGeneralAssistant, GeneralAssistant>();
        services.AddScoped<IHealthAssistant, HealthAssistant>();
        services.AddScoped<HealthDocumentProcessor>();
        services.AddSingleton<HealthDocumentExecutionGate>();
        services.AddScoped<IVetAssistant, VetAssistant>();
        services.AddSingleton<FailureNoticeThrottle>();
        return services;
    }
}
