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
    public void A_composite_provider_model_key_is_preferred_over_the_bare_prefix()
    {
        var perModel = new StubChatClient();
        var shared = new StubChatClient();
        var provider = new ChatClientProvider(new Dictionary<string, IChatClient>
        {
            ["anthropic:claude-haiku-4-5"] = perModel,
            ["claude-cli"] = shared
        });

        provider.GetClient("anthropic", "claude-haiku-4-5").ShouldBeSameAs(perModel);
    }

    [Fact]
    public void A_bare_prefix_registration_still_serves_every_model_of_that_prefix()
    {
        var shared = new StubChatClient();
        var provider = new ChatClientProvider(new Dictionary<string, IChatClient> { ["claude-cli"] = shared });

        provider.GetClient("claude-cli", "sonnet").ShouldBeSameAs(shared);
        provider.GetClient("claude-cli", "haiku").ShouldBeSameAs(shared);
    }

    [Fact]
    public void Neither_a_composite_nor_a_bare_registration_throws()
    {
        var provider = new ChatClientProvider(new Dictionary<string, IChatClient>());

        Should.Throw<InvalidOperationException>(() => provider.GetClient("openai", "gpt-6-luna"));
    }

    [Fact]
    public void GetClient_matches_the_registered_prefix_regardless_of_case()
    {
        var client = new StubChatClient();
        var provider = new ChatClientProvider(new Dictionary<string, IChatClient> { ["claude-cli"] = client });

        provider.GetClient("CLAUDE-CLI", "sonnet").ShouldBeSameAs(client);
    }
}
