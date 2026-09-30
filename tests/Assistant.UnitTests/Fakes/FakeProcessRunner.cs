using Assistant.Infrastructure.Llm;

namespace Assistant.UnitTests.Fakes;

public class FakeProcessRunner : IProcessRunner
{
    public ProcessRunRequest? LastRequest { get; private set; }

    public Func<ProcessRunRequest, ProcessRunResult> Handler { get; set; } =
        _ => throw new InvalidOperationException("FakeProcessRunner.Handler was not set by the test.");

    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(Handler(request));
    }
}
