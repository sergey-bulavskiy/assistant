using Microsoft.Extensions.AI;

namespace Assistant.Infrastructure.Llm;

/// <summary>Model entry -> IChatClient. A provider that binds one model per client instance at
/// construction time (Anthropic/OpenAI's official SDK adapters -- Decision 1 in the M3b plan)
/// registers one client per (prefix, modelName) pair; a provider whose single client already reads
/// the model from ChatOptions.ModelId per call (claude-cli) registers once under the bare prefix,
/// serving every model of that prefix. GetClient tries the composite key first, then the bare
/// prefix, so both styles coexist.</summary>
public interface IChatClientProvider
{
    IReadOnlyCollection<string> RegisteredPrefixes { get; }

    IChatClient GetClient(string providerPrefix, string modelName);
}
