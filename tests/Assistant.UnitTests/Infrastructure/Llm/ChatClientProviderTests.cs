using Assistant.Infrastructure.Llm;
using Microsoft.Extensions.AI;

namespace Assistant.UnitTests.Infrastructure.Llm;

public class ChatClientProviderTests
{
    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void Returns_the_client_registered_for_a_known_prefix()
    {
        var client = new StubChatClient();
        var provider = new ChatClientProvider(new Dictionary<string, IChatClient> { ["claude-cli"] = client });

        provider.GetClient("claude-cli").ShouldBeSameAs(client);
    }

    [Fact]
    public void An_unregistered_prefix_throws()
    {
        var provider = new ChatClientProvider(new Dictionary<string, IChatClient>());

        Should.Throw<InvalidOperationException>(() => provider.GetClient("unknown"));
    }

    [Fact]
    public void GetClient_matches_the_registered_prefix_regardless_of_case()
    {
        var client = new StubChatClient();
        var provider = new ChatClientProvider(new Dictionary<string, IChatClient> { ["claude-cli"] = client });

        provider.GetClient("CLAUDE-CLI").ShouldBeSameAs(client);
    }
}
