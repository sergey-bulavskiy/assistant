using System.Collections.Concurrent;
using Assistant.Application.Common;

namespace Assistant.Infrastructure.Llm;

public class ModelAvailability : IModelAvailability
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _unavailableUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly IClock _clock;

    public ModelAvailability(IClock clock)
    {
        _clock = clock;
    }

    public bool IsAvailable(string modelName) =>
        !_unavailableUntil.TryGetValue(modelName, out var until) || until <= _clock.UtcNow;

    public DateTimeOffset? RetryAt(string modelName) =>
        _unavailableUntil.TryGetValue(modelName, out var until) && until > _clock.UtcNow ? until : null;

    // Never shortens an existing mark: a later, shorter cooldown for the same model (e.g. a second
    // failure racing a first with a longer backoff) must not make it available sooner than an
    // already-recorded mark promised.
    public void MarkUnavailable(string modelName, DateTimeOffset until) =>
        _unavailableUntil.AddOrUpdate(modelName, until, (_, old) => old > until ? old : until);

    public void MarkAvailable(string modelName) =>
        _unavailableUntil.TryRemove(modelName, out _);
}
