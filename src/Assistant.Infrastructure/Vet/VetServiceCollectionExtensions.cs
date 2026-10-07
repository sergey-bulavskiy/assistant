using Assistant.Application.Common;
using Assistant.Application.Vet;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Infrastructure.Vet;

public static class VetServiceCollectionExtensions
{
    public static IServiceCollection AddVetPersistence(this IServiceCollection services)
    {
        services.AddScoped<IVetProfileStore, VetProfileStore>();
        services.AddScoped<IVetDiaryStore, VetDiaryStore>();
        services.AddSingleton(sp =>
        {
            var config = sp.GetService<LlmConfig>();
            var subscription = config is not null && config.Models.Count > 0
                && config.Models.Concat(config.FastModels).All(m => m.ProviderPrefix == "codex-cli");
            return new VetRuntimeOptions(subscription, config?.MaxContextMessages ?? 20, config?.MaxInputChars ?? 20000);
        });
        return services;
    }
}
