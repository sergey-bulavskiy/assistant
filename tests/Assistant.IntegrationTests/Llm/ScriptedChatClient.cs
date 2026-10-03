using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Assistant.IntegrationTests.Llm;

/// <summary>An IChatClient the test scripts one call at a time with a canned response or exception
/// to throw. The real Claude Code CLI is never invoked by any automated test (see Task 6) -- this is
/// the fake LlmGateway's own tests call through IChatClientProvider.</summary>
public class ScriptedChatClient : IChatClient
{
    private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _script = new();

    public List<string?> RequestedModelIds { get; } = new();

    /// <summary>The messages of every call, in order (system prompt first).</summary>
    public List<List<ChatMessage>> RequestedMessages { get; } = new();

    public void EnqueueResponse(string text, int inputTokens = 1, int outputTokens = 1) =>
        _script.Enqueue(_ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens }
        }));

    /// <summary>Enqueues a response with only one of input/output token counts reported (the other
    /// left null), to simulate a provider that returns partial usage.</summary>
    public void EnqueueResponsePartialUsage(string text, int? inputTokens, int? outputTokens) =>
        _script.Enqueue(_ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens }
        }));

    /// <summary>Enqueues a response carrying a reported cost via
    /// <see cref="Assistant.Infrastructure.Llm.LlmResponseKeys.ReportedCostUsd"/>, using
    /// <paramref name="asJsonElement"/> to simulate the value arriving as a <c>JsonElement</c> (as it
    /// would after round-tripping through JSON) rather than a native <c>decimal</c>.</summary>
    public void EnqueueResponseWithCost(string text, decimal cost, bool asJsonElement = false) =>
        _script.Enqueue(_ =>
        {
            object costValue = asJsonElement
                ? JsonSerializer.SerializeToElement(cost)
                : cost;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 },
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [Assistant.Infrastructure.Llm.LlmResponseKeys.ReportedCostUsd] = costValue
                }
            });
        });

    public void EnqueueException(Exception exception) =>
        _script.Enqueue(_ => throw exception);

    /// <summary>Waits <paramref name="delay"/> honouring the per-call cancellation token (the
    /// gateway's own call timeout, or the caller's token) before returning the response -- used to
    /// simulate a slow provider call for timeout/cancellation tests.</summary>
    public void EnqueueDelayedResponse(TimeSpan delay, string text) =>
        _script.Enqueue(async ct =>
        {
            await Task.Delay(delay, ct);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        });

    private readonly TaskCompletionSource _hangStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a call has reached an <see cref="EnqueueHang"/> step — await it before
    /// cancelling, rather than a fixed delay, so the cancellation really lands mid-call.</summary>
    public Task HangStarted => _hangStarted.Task;

    /// <summary>Never completes on its own -- only cancellation (the gateway's own timeout or the
    /// caller's token) ends the call.</summary>
    public void EnqueueHang() =>
        _script.Enqueue(async ct =>
        {
            _hangStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable: EnqueueHang only ends via cancellation.");
        });

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        RequestedMessages.Add(messages.ToList());
        RequestedModelIds.Add(options?.ModelId);
        if (_script.Count == 0)
        {
            throw new InvalidOperationException("ScriptedChatClient called more times than scripted.");
        }

        return _script.Dequeue()(cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("not used by LlmGateway");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
