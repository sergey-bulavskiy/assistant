using Microsoft.Extensions.AI;

namespace Assistant.IntegrationTests.Llm;

/// <summary>An IChatClient the test scripts one call at a time with a canned response or exception
/// to throw. The real Claude Code CLI is never invoked by any automated test (see Task 6) -- this is
/// the fake LlmGateway's own tests call through IChatClientProvider.</summary>
public class ScriptedChatClient : IChatClient
{
    private readonly Queue<Func<ChatResponse>> _script = new();

    public List<string?> RequestedModelIds { get; } = new();

    public void EnqueueResponse(string text, int inputTokens = 1, int outputTokens = 1) =>
        _script.Enqueue(() => new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens }
        });

    public void EnqueueException(Exception exception) =>
        _script.Enqueue(() => throw exception);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        RequestedModelIds.Add(options?.ModelId);
        if (_script.Count == 0)
        {
            throw new InvalidOperationException("ScriptedChatClient called more times than scripted.");
        }

        return Task.FromResult(_script.Dequeue()());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("not used by LlmGateway");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
