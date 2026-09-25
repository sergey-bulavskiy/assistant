namespace Assistant.Host;

public class PollingHealth
{
    private static readonly TimeSpan HealthyWindow = TimeSpan.FromMinutes(2);
    private readonly object _lock = new();
    private DateTimeOffset? _lastSuccessAt;

    public void MarkSuccess(DateTimeOffset at)
    {
        lock (_lock)
        {
            _lastSuccessAt = at;
        }
    }

    public bool IsHealthy(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _lastSuccessAt.HasValue && (now - _lastSuccessAt.Value) < HealthyWindow;
        }
    }
}
