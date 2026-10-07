using Assistant.Application.Common;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Infrastructure.Vet.Photos;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Infrastructure.Vet;

public static class VetServiceCollectionExtensions
{
    public static IServiceCollection AddVetPersistence(this IServiceCollection services)
    {
        services.AddScoped<IVetProfileStore, VetProfileStore>();
        services.AddScoped<VetDiaryStore>();
        services.AddScoped<IVetDiaryStore>(sp => sp.GetRequiredService<VetDiaryStore>());
        services.AddScoped<IVetPhotoDiaryStore>(sp => sp.GetRequiredService<VetDiaryStore>());
        services.AddScoped<IVetPhotoReversalStore>(sp => sp.GetRequiredService<VetDiaryStore>());
        services.AddSingleton(new VetPhotoCapacity());
        services.AddSingleton<IVetPhotoImageDecoder, VetPhotoImageDecoder>();
        services.AddScoped<VetPhotoStore>();
        services.AddScoped<IVetPhotoCommandReceiptStore>(sp => sp.GetRequiredService<VetPhotoStore>());
        services.AddScoped<IVetPhotoRunReviewSelectionStore>(sp => sp.GetRequiredService<VetPhotoStore>());
        services.AddScoped<IVetPhotoRunEvidenceStore>(sp => sp.GetRequiredService<VetPhotoStore>());
        services.AddScoped<IVetPhotoRunStore>(sp => sp.GetRequiredService<VetPhotoStore>());
        services.AddScoped<IVetPhotoArchiveDispatchStore>(sp => sp.GetRequiredService<VetPhotoStore>());
        services.AddScoped<IVetPhotoDispatchStore>(sp => sp.GetRequiredService<VetPhotoStore>());
        services.AddScoped<IVetPhotoExtractionStore>(sp => sp.GetRequiredService<VetPhotoStore>());
        services.AddScoped<IVetPhotoPresentationStore>(sp => sp.GetRequiredService<VetPhotoStore>());
        services.AddScoped<IVetPhotoWorkflowStore>(sp => sp.GetRequiredService<VetPhotoStore>());
        services.AddScoped<IVetPhotoBindingStore>(sp => sp.GetRequiredService<VetPhotoStore>());
        services.AddScoped<IVetPhotoArchiveStore>(sp => sp.GetRequiredService<VetPhotoStore>());
        services.AddSingleton(sp =>
        {
            var config = sp.GetService<LlmConfig>();
            var subscription = config is not null && config.Models.Count > 0
                && config.Models.Concat(config.FastModels).All(m => m.ProviderPrefix == "codex-cli");
            return new VetRuntimeOptions(subscription, config?.MaxContextMessages ?? 20, config?.MaxInputChars ?? 20000);
        });
        services.AddSingleton(sp => new VetPhotoRuntimeOptions(sp.GetRequiredService<VetRuntimeOptions>().SubscriptionOnly));
        services.AddSingleton<VetPhotoExecutionGate>();
        services.AddSingleton<IVetPhotoBackgroundLoop, VetPhotoBackgroundLoop>();
        return services;
    }
}
