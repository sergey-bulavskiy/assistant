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

    public void MarkUnavailable(string modelName, DateTimeOffset until) =>
        _unavailableUntil[modelName] = until;

    public void MarkAvailable(string modelName) =>
        _unavailableUntil.TryRemove(modelName, out _);
}
