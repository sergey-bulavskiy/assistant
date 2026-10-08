using Assistant.Application.Common;
using Assistant.Application.Health;
using Assistant.Application.Health.Documents;
using Assistant.Application.Messages;
using Assistant.Application.Memory;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<UpdateHandler>();
        services.AddScoped<Assistant.Application.Reminders.IReminderAssistant, Assistant.Application.Reminders.ReminderAssistant>();
        services.AddScoped<IGeneralAssistant, GeneralAssistant>();
        services.AddScoped<GeneralMemoryService>();
        services.AddScoped<IHealthAssistant, HealthAssistant>();
        services.AddScoped<HealthDocumentProcessor>();
        services.AddSingleton<HealthDocumentExecutionGate>();
        services.AddScoped<IVetAssistant, VetAssistant>();
        services.AddScoped<IVetPhotoAssistant, VetPhotoAssistant>();
        services.AddScoped<IVetPhotoApplicationOperations, VetPhotoApplicationOperations>();
        services.AddScoped<IVetPhotoRunApplication, VetPhotoRunApplication>();
        services.AddScoped<IVetPhotoReversalApplication, VetPhotoReversalApplication>();
        services.AddScoped<IVetPhotoDiaryOperationRouter, VetPhotoDiaryOperationRouter>();
        services.AddScoped<VetPhotoReviewComposer>();
        services.AddScoped<VetPhotoDispositionComposer>();
        services.AddScoped<VetPhotoProcessor>();
        services.AddSingleton<FailureNoticeThrottle>();
        return services;
    }
}
