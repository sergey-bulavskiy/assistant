using Microsoft.Extensions.AI;

namespace Assistant.Infrastructure.Llm;

/// <summary>Wraps an SDK-provided IChatClient, translating that provider's quota/rate-limit
/// exception into ModelLimitReachedException; everything else passes through unchanged.
///
/// Streaming is never used by LlmGateway (M3a/M3b) and is only delegated, not translated: a
/// provider's 429/limit error on a streaming call surfaces mid-enumeration (inside
/// `IAsyncEnumerable<ChatResponseUpdate>.MoveNextAsync`, not from `GetStreamingResponseAsync`
/// itself, which returns before any HTTP call happens), so this class's try/catch-and-translate
/// shape does not cover it. If a future milestone turns streaming on, `GetStreamingResponseAsync`
/// needs its own translation (e.g. wrapping the enumerator) -- don't assume this class already
/// handles it.</summary>
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
