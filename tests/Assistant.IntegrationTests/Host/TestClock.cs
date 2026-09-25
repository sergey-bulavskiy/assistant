using Assistant.Application.Common;

namespace Assistant.IntegrationTests.Host;

/// <summary>
/// An <see cref="IClock"/> that otherwise tracks real wall-clock time (like <see cref="SystemClock"/>)
/// but can be pushed forward on demand. Used to make assertions about <c>PollingHealth</c>'s
/// multi-minute healthy window deterministic — advancing past it takes one call, not an actual wait.
/// </summary>
public sealed class TestClock : IClock
{
    private readonly object _lock = new();
    private TimeSpan _offset = TimeSpan.Zero;

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_lock)
            {
                return DateTimeOffset.UtcNow + _offset;
            }
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _offset += by;
        }
    }
}
