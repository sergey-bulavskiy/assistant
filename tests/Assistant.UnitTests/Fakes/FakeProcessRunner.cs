using Assistant.Infrastructure.Llm;

namespace Assistant.UnitTests.Fakes;

public class FakeProcessRunner : IProcessRunner
{
    public ProcessRunRequest? LastRequest { get; private set; }

    public Func<ProcessRunRequest, ProcessRunResult> Handler { get; set; } =
        _ => throw new InvalidOperationException("FakeProcessRunner.Handler was not set by the test.");

    /// <summary>When set, takes priority over <see cref="Handler"/> -- lets a test hand back a task
    /// that does not complete until it chooses to (e.g. a TaskCompletionSource), to prove a caller
    /// does not block waiting for it.</summary>
    public Func<ProcessRunRequest, Task<ProcessRunResult>>? AsyncHandler { get; set; }

    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return AsyncHandler is not null ? AsyncHandler(request) : Task.FromResult(Handler(request));
    }
}
