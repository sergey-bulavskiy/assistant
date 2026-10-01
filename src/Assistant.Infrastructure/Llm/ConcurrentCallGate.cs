namespace Assistant.Infrastructure.Llm;

/// <summary>Process-wide cap on calls in flight across every model/family (spec 3.1:
/// LLM_MAX_CONCURRENT_CALLS). A singleton so it's shared across every scoped LlmGateway instance.</summary>
public class ConcurrentCallGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public ConcurrentCallGate(int maxConcurrentCalls)
    {
        _semaphore = new SemaphoreSlim(maxConcurrentCalls, maxConcurrentCalls);
    }

    public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

    public void Release() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}
