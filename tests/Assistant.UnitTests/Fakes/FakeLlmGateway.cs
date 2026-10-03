using Assistant.Application.Llm;

namespace Assistant.UnitTests.Fakes;

public class FakeLlmGateway : ILlmGateway
{
    public bool IsEnabled { get; set; } = true;

    public List<ModelStatus> Models { get; set; } = new() { new ModelStatus("sonnet", true, null), new ModelStatus("haiku", true, null) };

    public LlmResult NextResult { get; set; } = LlmResult.Answered("test answer", "sonnet");

    /// <summary>When set, CompleteAsync waits for this task before returning NextResult, so a test
    /// can observe what happens while the model call is in flight.</summary>
    public Task? WaitBeforeAnswering { get; set; }

    /// <summary>When set, CompleteAsync records the request and then throws this.</summary>
    public Exception? ThrowOnComplete { get; set; }

    /// <summary>Results returned one per call, in order; NextResult once it is empty.</summary>
    public Queue<LlmResult> Results { get; } = new();

    /// <summary>1-based call number → exception thrown by that call after it is recorded.</summary>
    public Dictionary<int, Exception> ThrowOnCall { get; } = new();

    public List<LlmRequest> Requests { get; } = new();

    public LlmRequest? LastRequest => Requests.LastOrDefault();

    public IReadOnlyList<ModelStatus> DescribeModels() => Models;

    public bool IsKnownModel(string name) => Models.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (ThrowOnCall.TryGetValue(Requests.Count, out var callException))
        {
            throw callException;
        }

        if (ThrowOnComplete is { } exception)
        {
            throw exception;
        }

        if (WaitBeforeAnswering is { } wait)
        {
            await wait.WaitAsync(cancellationToken);
        }

        return Results.Count > 0 ? Results.Dequeue() : NextResult;
    }
}
