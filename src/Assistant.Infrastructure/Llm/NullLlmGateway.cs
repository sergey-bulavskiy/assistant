using Assistant.Application.Llm;

namespace Assistant.Infrastructure.Llm;

/// <summary>Registered instead of LlmGateway when LLM config is off or invalid (Task 1/9) so every
/// call site gets a normal Refused(NotConfigured) result instead of a null-check.</summary>
public class NullLlmGateway : ILlmGateway
{
    public bool IsEnabled => false;

    public IReadOnlyList<ModelStatus> DescribeModels() => Array.Empty<ModelStatus>();

    public bool IsKnownModel(string name) => false;

    public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(LlmResult.Refused(LlmRefusalReason.NotConfigured));
}
