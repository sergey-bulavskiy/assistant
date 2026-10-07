using System.Collections.Concurrent;
using Assistant.Application.Common;

namespace Assistant.Application.Health.Documents;

public sealed class HealthDocumentExecutionGate(IClock clock)
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _passes = new();

    public async Task<IDisposable> EnterAsync(long botId, CancellationToken token)
    {
        var gate = _gates.GetOrAdd(botId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        return new Releaser(gate);
    }

    // Called only while holding this bot's gate.
    public bool BeginRecovery(long botId)
    {
        var now = clock.UtcNow;
        if (_passes.TryGetValue(botId, out var previous) && now - previous < HealthDocumentLimits.RecoveryInterval)
            return false;
        _passes[botId] = now;
        return true;
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            gate.Release();
        }
    }
}
