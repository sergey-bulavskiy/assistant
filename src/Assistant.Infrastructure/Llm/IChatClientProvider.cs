using Microsoft.Extensions.AI;

namespace Assistant.Infrastructure.Llm;

/// <summary>Model entry -> IChatClient, keyed by the catalog entry's provider prefix (the part of
/// LLM_MODELS before the colon). Providers register by prefix at DI composition time; an entry
/// whose prefix has no registered provider fails validation at startup (Task 9).</summary>
public interface IChatClientProvider
{
    IReadOnlyCollection<string> RegisteredPrefixes { get; }

    IChatClient GetClient(string providerPrefix);
}
