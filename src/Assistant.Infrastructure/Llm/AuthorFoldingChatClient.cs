using Microsoft.Extensions.AI;

namespace Assistant.Infrastructure.Llm;

/// <summary>Group authorship for the direct API providers (review nit): <c>claude-cli</c> gets group
/// authorship as the CLI's own <c>&lt;msg author="..."&gt;</c> framing (spec §8.4, read directly off
/// <see cref="ChatMessage.AuthorName"/> by <c>ClaudeCliChatClient</c>) -- the Anthropic/OpenAI SDKs'
/// Messages APIs have no equivalent per-message author field, so for those two providers this class
/// folds <see cref="ChatMessage.AuthorName"/> into the text itself as <c>[author]: text</c> before the
/// message ever reaches the SDK. Used only by <see cref="AnthropicChatClientFactory"/> and
/// <see cref="OpenAiChatClientFactory"/>, never by the claude-cli pipeline.</summary>
public class AuthorFoldingChatClient : IChatClient
{
    private readonly IChatClient _inner;

    public AuthorFoldingChatClient(IChatClient inner)
    {
        _inner = inner;
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.GetResponseAsync(Fold(messages), options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.GetStreamingResponseAsync(Fold(messages), options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();

    // Gateway diagnostics use the same transformation to capture exactly what the API client sees.
    internal static ChatMessage FoldMessage(ChatMessage message) =>
        string.IsNullOrEmpty(message.AuthorName)
            ? message
            : new ChatMessage(message.Role, $"[{message.AuthorName}]: {message.Text}");

    private static IEnumerable<ChatMessage> Fold(IEnumerable<ChatMessage> messages) => messages.Select(FoldMessage);
}
