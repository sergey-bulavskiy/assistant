using Microsoft.Extensions.AI;

namespace Assistant.Infrastructure.Llm;

/// <summary>Wraps an SDK-provided IChatClient, translating that provider's quota/rate-limit
/// exception into ModelLimitReachedException; everything else passes through unchanged. Streaming is
/// never used by LlmGateway (M3a) and is only delegated, not translated.</summary>
public class LimitTranslatingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly Func<Exception, ModelLimitReachedException?> _translate;

    public LimitTranslatingChatClient(IChatClient inner, Func<Exception, ModelLimitReachedException?> translate)
    {
        _inner = inner;
        _translate = translate;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inner.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (_translate(ex) is not null)
        {
            throw _translate(ex)!;
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
