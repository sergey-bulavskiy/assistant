using Microsoft.Extensions.AI;

namespace Assistant.Infrastructure.Llm;

public class ChatClientProvider : IChatClientProvider
{
    private readonly IReadOnlyDictionary<string, IChatClient> _clientsByPrefix;

    public ChatClientProvider(IReadOnlyDictionary<string, IChatClient> clientsByPrefix)
    {
        _clientsByPrefix = new Dictionary<string, IChatClient>(clientsByPrefix, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> RegisteredPrefixes => _clientsByPrefix.Keys.ToArray();

    public IChatClient GetClient(string providerPrefix) =>
        _clientsByPrefix.TryGetValue(providerPrefix, out var client)
            ? client
            : throw new InvalidOperationException($"No IChatClient registered for provider prefix '{providerPrefix}'.");
}
