using Microsoft.Extensions.AI;

namespace Assistant.Infrastructure.Llm;

public class ChatClientProvider : IChatClientProvider
{
    private readonly IReadOnlyDictionary<string, IChatClient> _clientsByKey;

    public ChatClientProvider(IReadOnlyDictionary<string, IChatClient> clientsByKey)
    {
        _clientsByKey = new Dictionary<string, IChatClient>(clientsByKey, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> RegisteredPrefixes =>
        _clientsByKey.Keys.Select(k => k.Split(':', 2)[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public IChatClient GetClient(string providerPrefix, string modelName)
    {
        var compositeKey = $"{providerPrefix}:{modelName}";
        if (_clientsByKey.TryGetValue(compositeKey, out var perModelClient))
        {
            return perModelClient;
        }

        if (_clientsByKey.TryGetValue(providerPrefix, out var sharedClient))
        {
            return sharedClient;
        }

        throw new InvalidOperationException($"No IChatClient registered for '{compositeKey}' or bare prefix '{providerPrefix}'.");
    }
}
